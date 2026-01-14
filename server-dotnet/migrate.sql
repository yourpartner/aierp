-- ---------------------------------------
-- 数据库迁移脚本（PostgreSQL）：
-- - 启用 pgcrypto（用于 gen_random_uuid）
-- - 创建公司、结构定义、凭证与主数据表
-- - 关键字段采用"生成列"（方案B）+ 索引/唯一约束
-- - 写入最小的 jsonstructures 种子（voucher / businesspartner）
-- ---------------------------------------
-- Extensions
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Helper immutable wrappers to satisfy generated column immutability checks
-- 日期（header.postingDate -> date）
CREATE OR REPLACE FUNCTION fn_jsonb_header_posting_date(p jsonb)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p->'header'->>'postingDate') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
      THEN (p->'header'->>'postingDate')::date
    ELSE NULL
  END;
$$;

-- remindAt(Unix 秒) -> timestamptz（不可变）
CREATE OR REPLACE FUNCTION fn_jsonb_remind_at(p jsonb)
RETURNS timestamptz
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p->>'remindAt') ~ '^[0-9]+(\.[0-9]+)?$'
      THEN (TIMESTAMP WITH TIME ZONE 'epoch' + ((p->>'remindAt')::double precision) * INTERVAL '1 second')
    ELSE NULL
  END;
$$;

-- 通用：payload->>'key' 转 numeric（若非数值则返回 NULL）
CREATE OR REPLACE FUNCTION fn_jsonb_numeric(p jsonb, key text)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~ '^[-+]?[0-9]+(\.[0-9]+)?$'
      THEN (p ->> key)::numeric
    ELSE NULL
  END;
$$;

-- 通用：payload->>'key' 转 date（若格式不符则返回 NULL）
CREATE OR REPLACE FUNCTION fn_jsonb_date(p jsonb, key text)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p ->> key) ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
      THEN (p ->> key)::date
    ELSE NULL
  END;
$$;

-- 通用：timestamptz 转日期（按 UTC 截断，保证不可变）
CREATE OR REPLACE FUNCTION fn_timestamptz_date(ts timestamptz)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT (ts AT TIME ZONE 'UTC')::date;
$$;

-- 通用：payload->>'key' 提取年月（YYYY-MM）字符串
CREATE OR REPLACE FUNCTION fn_jsonb_month(p jsonb, key text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN fn_jsonb_date(p, key) IS NULL THEN NULL
    ELSE to_char(fn_jsonb_date(p, key), 'YYYY-MM')
  END;
$$;

-- 取文本（header.voucherType）
CREATE OR REPLACE FUNCTION fn_jsonb_header_voucher_type(p jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT p->'header'->>'voucherType';
$$;

-- 取文本（header.voucherNo）
CREATE OR REPLACE FUNCTION fn_jsonb_header_voucher_no(p jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT p->'header'->>'voucherNo';
$$;

-- flags.customer as boolean
CREATE OR REPLACE FUNCTION fn_jsonb_flags_customer(p jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT (p->'flags'->>'customer') = 'true';
$$;

-- flags.vendor as boolean
CREATE OR REPLACE FUNCTION fn_jsonb_flags_vendor(p jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT (p->'flags'->>'vendor') = 'true';
$$;

-- openItem boolean
CREATE OR REPLACE FUNCTION fn_jsonb_open_item(p jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT (p->>'openItem') = 'true';
$$;

-- Companies
CREATE TABLE IF NOT EXISTS companies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL UNIQUE,
  name TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- schemas: 统一的结构定义（支持公司级与全局）
CREATE TABLE IF NOT EXISTS schemas (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NULL,
  name TEXT NOT NULL,
  version INTEGER NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  schema JSONB NOT NULL,
  ui JSONB,
  query JSONB,
  core_fields JSONB,
  validators JSONB,
  numbering JSONB,
  ai_hints JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, name, version)
);

-- 兼容迁移：若历史存在 jsonstructures，则将其拷贝为全局 schemas（company_code=NULL）
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='jsonstructures') THEN
    INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
    SELECT NULL, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints
    FROM jsonstructures js
    ON CONFLICT (company_code, name, version) DO NOTHING;
  END IF;
END $$;

-- 凭证编号序列表：按 (company_code, yymm) 分段计数，生成 yymm+6
CREATE TABLE IF NOT EXISTS voucher_sequences (
  company_code TEXT NOT NULL,
  yymm TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, yymm)
);

-- 物料编号序列表：按 (company_code, yymm) 分段计数，生成 MATyymm#####
CREATE TABLE IF NOT EXISTS material_sequences (
  company_code TEXT NOT NULL,
  yymm TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, yymm)
);

-- 仓库编号序列表：按 (company_code, yymm) 分段计数，生成 WHyymm####
CREATE TABLE IF NOT EXISTS warehouse_sequences (
  company_code TEXT NOT NULL,
  yymm TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, yymm)
);

-- 会计凭证：
-- - payload 内 header.postingDate/voucherType/voucherNo 映射为生成列
-- - 支持按公司+日期/类型查询；公司+编号唯一
CREATE TABLE IF NOT EXISTS vouchers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  posting_date DATE GENERATED ALWAYS AS (fn_jsonb_header_posting_date(payload)) STORED,
  voucher_type TEXT GENERATED ALWAYS AS (fn_jsonb_header_voucher_type(payload)) STORED,
  voucher_no TEXT GENERATED ALWAYS AS (fn_jsonb_header_voucher_no(payload)) STORED
);

CREATE INDEX IF NOT EXISTS idx_vouchers_company_posting_date ON vouchers(company_code, posting_date);
CREATE INDEX IF NOT EXISTS idx_vouchers_company_voucher_type ON vouchers(company_code, voucher_type);
CREATE UNIQUE INDEX IF NOT EXISTS uq_vouchers_company_voucher_no ON vouchers(company_code, voucher_no);

ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS created_by TEXT GENERATED ALWAYS AS (payload->'header'->>'createdBy') STORED;
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS updated_by TEXT GENERATED ALWAYS AS (payload->'header'->>'updatedBy') STORED;
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS created_by_employee TEXT GENERATED ALWAYS AS (payload->'header'->>'createdByEmployee') STORED;
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS updated_by_employee TEXT GENERATED ALWAYS AS (payload->'header'->>'updatedByEmployee') STORED;

-- 电子保存/附件检索辅助字段（最低复杂度实现）：
-- - has_attachments: 是否存在附件（便于“仅检索电子凭证”）
-- - primary_partner_code: 从凭证行提取首个 vendorId/customerId（便于按取引先检索）
-- - amount_total: 借方合计（便于按金额区间检索）
CREATE OR REPLACE FUNCTION fn_jsonb_voucher_primary_partner(p jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT COALESCE(
    (SELECT NULLIF(btrim(COALESCE(line->>'vendorId', line->>'vendorCode', line->>'vendor_id', line->>'vendor_code', '')), '')
     FROM jsonb_array_elements(
       CASE WHEN jsonb_typeof(p->'lines')='array' THEN p->'lines' ELSE '[]'::jsonb END
     ) AS line
     WHERE NULLIF(btrim(COALESCE(line->>'vendorId', line->>'vendorCode', line->>'vendor_id', line->>'vendor_code', '')), '') IS NOT NULL
     LIMIT 1),
    (SELECT NULLIF(btrim(COALESCE(line->>'customerId', line->>'customerCode', line->>'customer_id', line->>'customer_code', '')), '')
     FROM jsonb_array_elements(
       CASE WHEN jsonb_typeof(p->'lines')='array' THEN p->'lines' ELSE '[]'::jsonb END
     ) AS line
     WHERE NULLIF(btrim(COALESCE(line->>'customerId', line->>'customerCode', line->>'customer_id', line->>'customer_code', '')), '') IS NOT NULL
     LIMIT 1),
    NULLIF(btrim(COALESCE(p->'header'->>'partnerCode', p->'header'->>'vendorId', p->'header'->>'customerId', '')), '')
  );
$$;

CREATE OR REPLACE FUNCTION fn_jsonb_voucher_amount_total(p jsonb)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT COALESCE((
    SELECT SUM(
      CASE
        WHEN upper(COALESCE(line->>'drcr', line->>'side', '')) IN ('DR','DEBIT')
             AND (line->>'amount') ~ '^-?[0-9]+(\.[0-9]+)?$'
          THEN (line->>'amount')::numeric
        ELSE 0
      END
    )
    FROM jsonb_array_elements(
      CASE WHEN jsonb_typeof(p->'lines')='array' THEN p->'lines' ELSE '[]'::jsonb END
    ) AS line
  ), 0);
$$;

ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS has_attachments boolean
    GENERATED ALWAYS AS (
      (jsonb_typeof(payload->'attachments')='array' AND jsonb_array_length(payload->'attachments') > 0)
    ) STORED;
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS primary_partner_code text
    GENERATED ALWAYS AS (fn_jsonb_voucher_primary_partner(payload)) STORED;
ALTER TABLE vouchers
  ADD COLUMN IF NOT EXISTS amount_total numeric(18,2)
    GENERATED ALWAYS AS (fn_jsonb_voucher_amount_total(payload)) STORED;

CREATE INDEX IF NOT EXISTS idx_vouchers_company_has_attachments ON vouchers(company_code, has_attachments) WHERE has_attachments = true;
CREATE INDEX IF NOT EXISTS idx_vouchers_company_primary_partner ON vouchers(company_code, primary_partner_code) WHERE primary_partner_code IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_vouchers_company_amount_total ON vouchers(company_code, amount_total);

-- 业务伙伴：客户/供应商合并，flag_customer/flag_vendor 为生成列
CREATE TABLE IF NOT EXISTS businesspartners (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  partner_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  flag_customer BOOLEAN GENERATED ALWAYS AS (fn_jsonb_flags_customer(payload)) STORED,
  flag_vendor BOOLEAN GENERATED ALWAYS AS (fn_jsonb_flags_vendor(payload)) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_bp_company_code ON businesspartners(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_bp_company_name ON businesspartners(company_code, name);
CREATE INDEX IF NOT EXISTS idx_bp_company_flags ON businesspartners(company_code, flag_customer, flag_vendor);

-- 合作伙伴编号序列表：按 company_code 递增
CREATE TABLE IF NOT EXISTS bp_sequences (
  company_code TEXT PRIMARY KEY,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ===============================
-- CRM 模块表结构（联系人/商谈/报价/受注/活动）
-- ===============================

-- 联系人（隶属合作伙伴，可多语言）
CREATE TABLE IF NOT EXISTS contacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  contact_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  email TEXT GENERATED ALWAYS AS (payload->>'email') STORED,
  mobile TEXT GENERATED ALWAYS AS (payload->>'mobile') STORED,
  language TEXT GENERATED ALWAYS AS (payload->>'language') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_contacts_company_code ON contacts(company_code, contact_code);
CREATE INDEX IF NOT EXISTS idx_contacts_company_partner ON contacts(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_contacts_company_email ON contacts(company_code, email);

-- 商谈（机会）
CREATE TABLE IF NOT EXISTS deals (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  deal_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  owner_user_id TEXT GENERATED ALWAYS AS (payload->>'ownerUserId') STORED,
  stage TEXT GENERATED ALWAYS AS (payload->>'stage') STORED, -- 潜在/提案/成约/开票
  expected_amount NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'expectedAmount')) STORED,
  expected_close_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'expectedCloseDate')) STORED,
  source TEXT GENERATED ALWAYS AS (payload->>'source') STORED, -- 介绍/网站/展会
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_deals_company_code ON deals(company_code, deal_code);
CREATE INDEX IF NOT EXISTS idx_deals_company_stage ON deals(company_code, stage);
CREATE INDEX IF NOT EXISTS idx_deals_company_owner ON deals(company_code, owner_user_id);
CREATE INDEX IF NOT EXISTS idx_deals_company_expected_date ON deals(company_code, expected_close_date);

-- 报价（Quote）
CREATE TABLE IF NOT EXISTS quotes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  quote_no TEXT GENERATED ALWAYS AS (payload->>'quoteNo') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  amount_total NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'amountTotal')) STORED,
  valid_until DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'validUntil')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED -- draft/sent/accepted/rejected
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_quotes_company_no ON quotes(company_code, quote_no);
CREATE INDEX IF NOT EXISTS idx_quotes_company_partner ON quotes(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_quotes_company_status ON quotes(company_code, status);

-- 受注（Sales Order）
CREATE TABLE IF NOT EXISTS sales_orders (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  so_no TEXT GENERATED ALWAYS AS (payload->>'soNo') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  amount_total NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'amountTotal')) STORED,
  order_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'orderDate')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED -- draft/confirmed/invoiced/cancelled
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_sos_company_no ON sales_orders(company_code, so_no);
CREATE INDEX IF NOT EXISTS idx_sos_company_partner ON sales_orders(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_sos_company_status ON sales_orders(company_code, status);

-- 納品書（Delivery Note）
CREATE TABLE IF NOT EXISTS delivery_notes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  delivery_no TEXT GENERATED ALWAYS AS (payload->>'deliveryNo') STORED,
  sales_order_no TEXT GENERATED ALWAYS AS (payload->>'salesOrderNo') STORED,
  customer_code TEXT GENERATED ALWAYS AS (payload->>'customerCode') STORED,
  delivery_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'deliveryDate')) STORED,
  print_status TEXT GENERATED ALWAYS AS (payload->>'printStatus') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_delivery_notes_company_no ON delivery_notes(company_code, delivery_no);
CREATE INDEX IF NOT EXISTS idx_delivery_notes_company_so ON delivery_notes(company_code, sales_order_no);
CREATE INDEX IF NOT EXISTS idx_delivery_notes_company_print ON delivery_notes(company_code, print_status);

-- 活动（邮件/电话/会议/任务），含提醒
CREATE TABLE IF NOT EXISTS activities (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  activity_type TEXT GENERATED ALWAYS AS (payload->>'type') STORED, -- email/phone/meeting/task
  subject TEXT GENERATED ALWAYS AS (payload->>'subject') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  contact_code TEXT GENERATED ALWAYS AS (payload->>'contactCode') STORED,
  owner_user_id TEXT GENERATED ALWAYS AS (payload->>'ownerUserId') STORED,
  due_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'dueDate')) STORED,
  remind_at TIMESTAMPTZ GENERATED ALWAYS AS (fn_jsonb_remind_at(payload)) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED -- open/done/cancelled
);
CREATE INDEX IF NOT EXISTS idx_acts_company_due ON activities(company_code, due_date);
CREATE INDEX IF NOT EXISTS idx_acts_company_owner ON activities(company_code, owner_user_id);
CREATE INDEX IF NOT EXISTS idx_acts_company_type ON activities(company_code, activity_type);

-- 会计科目：含 PL/BS 分类与未清项管理标记（openItem）
CREATE TABLE IF NOT EXISTS accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  account_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  pl_bs_type TEXT GENERATED ALWAYS AS (payload->>'category') STORED,
  open_item_mgmt BOOLEAN GENERATED ALWAYS AS (fn_jsonb_open_item(payload)) STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_accounts_company_code ON accounts(company_code, account_code);
CREATE INDEX IF NOT EXISTS idx_accounts_company_name ON accounts(company_code, name);

ALTER TABLE accounts
  ADD COLUMN IF NOT EXISTS fs_bs_group TEXT GENERATED ALWAYS AS (payload->>'fsBalanceGroup') STORED;

ALTER TABLE accounts
  ADD COLUMN IF NOT EXISTS fs_pl_group TEXT GENERATED ALWAYS AS (payload->>'fsProfitGroup') STORED;

CREATE INDEX IF NOT EXISTS idx_accounts_fs_bs_group ON accounts(company_code, fs_bs_group) WHERE fs_bs_group IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_accounts_fs_pl_group ON accounts(company_code, fs_pl_group) WHERE fs_pl_group IS NOT NULL;

INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "6610",
    "name": "雑費",
    "category": "PL",
    "openItem": false,
    "fieldRules": {
      "customerId": "hidden",
      "vendorId": "hidden",
      "employeeId": "hidden",
      "departmentId": "hidden",
      "paymentDate": "hidden"
    }
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO NOTHING;

INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "1410",
    "name": "仮払金",
    "category": "BS",
    "openItem": false,
    "fieldRules": {
      "customerId": "hidden",
      "vendorId": "hidden",
      "employeeId": "hidden",
      "departmentId": "hidden",
      "paymentDate": "hidden"
    }
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO NOTHING;

-- 财务报表分组定义
CREATE TABLE IF NOT EXISTS fs_nodes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  statement TEXT GENERATED ALWAYS AS (payload->>'statement') STORED,
  node_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  parent_code TEXT GENERATED ALWAYS AS (payload->>'parentCode') STORED,
  sort_order INT GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'order') AND (payload->>'order') ~ '^-?[0-9]+$'
        THEN (payload->>'order')::int
      ELSE NULL
    END
  ) STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_fs_nodes_company_code ON fs_nodes(company_code, node_code);
CREATE INDEX IF NOT EXISTS idx_fs_nodes_statement ON fs_nodes(company_code, statement, sort_order);

ALTER TABLE fs_nodes
  ADD COLUMN IF NOT EXISTS name_ja TEXT GENERATED ALWAYS AS (payload->>'nameJa') STORED;

ALTER TABLE fs_nodes
  ADD COLUMN IF NOT EXISTS name_en TEXT GENERATED ALWAYS AS (payload->>'nameEn') STORED;

ALTER TABLE fs_nodes
  ADD COLUMN IF NOT EXISTS is_subtotal BOOLEAN GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'isSubtotal') THEN (payload->>'isSubtotal')::boolean
      ELSE FALSE
    END
  ) STORED;

-- 总账月度汇总（供财务报表使用）
DROP MATERIALIZED VIEW IF EXISTS mv_gl_monthly;
CREATE MATERIALIZED VIEW mv_gl_monthly AS
SELECT
  v.company_code,
  date_trunc('month', (v.payload->'header'->>'postingDate')::date)::date AS period_month,
  COALESCE(v.payload->'header'->>'currency', 'JPY') AS currency,
  line->>'accountCode' AS account_code,
  SUM(CASE WHEN line->>'drcr' = 'DR' THEN (line->>'amount')::numeric ELSE 0 END) AS debit_amount,
  SUM(CASE WHEN line->>'drcr' = 'CR' THEN (line->>'amount')::numeric ELSE 0 END) AS credit_amount,
  SUM(CASE WHEN line->>'drcr' = 'DR' THEN (line->>'amount')::numeric ELSE -(line->>'amount')::numeric END) AS net_amount
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') AS line
WHERE (v.payload->'header'->>'postingDate') ~ '^\d{4}-\d{2}-\d{2}$'
  AND (line ? 'accountCode') AND (line ? 'amount') AND (line ? 'drcr')
GROUP BY 1,2,3,4;

CREATE UNIQUE INDEX IF NOT EXISTS idx_mv_gl_monthly ON mv_gl_monthly(company_code, period_month, currency, account_code);

-- 部门层级：parent_department_code 可空
CREATE TABLE IF NOT EXISTS departments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  department_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  parent_department_code TEXT GENERATED ALWAYS AS (payload->>'parentCode') STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_departments_company_code ON departments(company_code, department_code);
CREATE INDEX IF NOT EXISTS idx_departments_company_name ON departments(company_code, name);

-- 员工：关联部门编码
CREATE TABLE IF NOT EXISTS employees (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  employee_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  department_code TEXT GENERATED ALWAYS AS (payload->>'departmentCode') STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_employees_company_code ON employees(company_code, employee_code);
CREATE INDEX IF NOT EXISTS idx_employees_company_name ON employees(company_code, name);

CREATE TABLE IF NOT EXISTS payroll_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  policy_id UUID,
  period_month TEXT NOT NULL,
  run_type TEXT NOT NULL DEFAULT 'manual',
  status TEXT NOT NULL DEFAULT 'draft',
  total_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  diff_summary JSONB,
  metadata JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_payroll_runs_company_policy_month_type
  ON payroll_runs(company_code, COALESCE(policy_id, '00000000-0000-0000-0000-000000000000'::uuid), period_month, run_type);

CREATE TABLE IF NOT EXISTS payroll_run_entries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id UUID NOT NULL REFERENCES payroll_runs(id) ON DELETE CASCADE,
  company_code TEXT NOT NULL,
  employee_id UUID NOT NULL,
  employee_code TEXT,
  employee_name TEXT,
  department_code TEXT,
  total_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  payroll_sheet JSONB NOT NULL,
  accounting_draft JSONB NOT NULL,
  diff_summary JSONB,
  metadata JSONB,
  voucher_id UUID,
  voucher_no TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_run ON payroll_run_entries(run_id);
CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_emp ON payroll_run_entries(company_code, employee_id);
CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_voucher ON payroll_run_entries(company_code, voucher_no);
CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_voucher_id ON payroll_run_entries(company_code, voucher_id);
ALTER TABLE payroll_run_entries ADD COLUMN IF NOT EXISTS metadata JSONB;

CREATE TABLE IF NOT EXISTS payroll_run_traces (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id UUID NOT NULL REFERENCES payroll_runs(id) ON DELETE CASCADE,
  entry_id UUID NOT NULL REFERENCES payroll_run_entries(id) ON DELETE CASCADE,
  employee_id UUID NOT NULL,
  trace JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_payroll_run_traces_run ON payroll_run_traces(run_id);
CREATE INDEX IF NOT EXISTS idx_payroll_run_traces_entry ON payroll_run_traces(entry_id);

CREATE TABLE IF NOT EXISTS employee_sequences (
  company_code TEXT PRIMARY KEY,
  last_number BIGINT NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 公司级设置：存储工作时间等配置（JSONB）
CREATE TABLE IF NOT EXISTS company_settings (
  company_code TEXT PRIMARY KEY,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS scheduler_tasks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB,
  next_run_at TIMESTAMPTZ,
  last_run_at TIMESTAMPTZ,
  locked_by TEXT,
  locked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE scheduler_tasks ALTER COLUMN payload SET NOT NULL;

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='nl_spec')
     AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='plan')
     AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='schedule')
     AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='status')
     AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='result') THEN
    ALTER TABLE scheduler_tasks ADD COLUMN IF NOT EXISTS payload JSONB;
    UPDATE scheduler_tasks
    SET payload = jsonb_build_object(
      'nlSpec', nl_spec,
      'plan', plan,
      'schedule', schedule,
      'status', status,
      'result', result
    )
    WHERE payload IS NULL;
    ALTER TABLE scheduler_tasks DROP COLUMN IF EXISTS nl_spec;
    ALTER TABLE scheduler_tasks DROP COLUMN IF EXISTS plan;
    ALTER TABLE scheduler_tasks DROP COLUMN IF EXISTS schedule;
    ALTER TABLE scheduler_tasks DROP COLUMN IF EXISTS status;
    ALTER TABLE scheduler_tasks DROP COLUMN IF EXISTS result;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='status') THEN
    ALTER TABLE scheduler_tasks ADD COLUMN status TEXT GENERATED ALWAYS AS (COALESCE(payload->>'status','pending')) STORED;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='nl_spec') THEN
    ALTER TABLE scheduler_tasks ADD COLUMN nl_spec TEXT GENERATED ALWAYS AS (payload->>'nlSpec') STORED;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_scheduler_tasks_company_status ON scheduler_tasks(company_code, status);
CREATE INDEX IF NOT EXISTS idx_scheduler_tasks_next_run ON scheduler_tasks(company_code, next_run_at);

-- 工时 Timesheets：员工本人录入（不在表单中显式选择员工），按日期/项目记录
CREATE TABLE IF NOT EXISTS timesheets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 生成列：便于过滤/排序
  timesheet_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'date')) STORED,
  month TEXT GENERATED ALWAYS AS (fn_jsonb_month(payload, 'date')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  project_code TEXT GENERATED ALWAYS AS (payload->>'projectCode') STORED,
  created_by TEXT GENERATED ALWAYS AS (payload->>'creatorUserId') STORED
);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_date ON timesheets(company_code, timesheet_date DESC);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_status ON timesheets(company_code, status);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_creator ON timesheets(company_code, created_by);

-- 工时月度提交 Timesheet Submissions：按员工+月份汇总，用于审批（保持 payload JSONB 体系）
CREATE TABLE IF NOT EXISTS timesheet_submissions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 生成列：便于过滤/排序
  month TEXT GENERATED ALWAYS AS (payload->>'month') STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  created_by TEXT GENERATED ALWAYS AS (payload->>'creatorUserId') STORED,
  employee_code TEXT GENERATED ALWAYS AS (payload->>'employeeCode') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_timesheet_submissions_company_user_month ON timesheet_submissions(company_code, created_by, month);
CREATE INDEX IF NOT EXISTS idx_timesheet_submissions_company_month ON timesheet_submissions(company_code, month);
CREATE INDEX IF NOT EXISTS idx_timesheet_submissions_company_status ON timesheet_submissions(company_code, status);
CREATE INDEX IF NOT EXISTS idx_timesheet_submissions_company_creator ON timesheet_submissions(company_code, created_by);

-- 开放项投影：记录可清账明细（来自凭证行）
CREATE TABLE IF NOT EXISTS open_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  voucher_id UUID NOT NULL,
  voucher_line_no INTEGER NOT NULL,
  account_code TEXT NOT NULL,
  partner_id TEXT,
  currency TEXT,
  doc_date DATE,
  original_amount NUMERIC(18,2) NOT NULL,
  residual_amount NUMERIC(18,2) NOT NULL,
  cleared_flag BOOLEAN NOT NULL DEFAULT FALSE,
  cleared_at TIMESTAMPTZ,
  cleared_by TEXT,
  refs JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_oi_company_residual ON open_items(company_code) WHERE residual_amount > 0;
CREATE INDEX IF NOT EXISTS idx_oi_company_partner ON open_items(company_code, partner_id);
CREATE INDEX IF NOT EXISTS idx_oi_company_account ON open_items(company_code, account_code);

-- 银行
CREATE TABLE IF NOT EXISTS banks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  bank_code TEXT GENERATED ALWAYS AS (payload->>'bankCode') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
ALTER TABLE banks DROP COLUMN IF EXISTS company_code;
DROP INDEX IF EXISTS uq_banks_company_bank_code;
DROP INDEX IF EXISTS idx_banks_company_name;
CREATE UNIQUE INDEX IF NOT EXISTS uq_banks_bank_code ON banks(bank_code);
CREATE INDEX IF NOT EXISTS idx_banks_name ON banks(name);

-- 支店
CREATE TABLE IF NOT EXISTS branches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  bank_code TEXT GENERATED ALWAYS AS (payload->>'bankCode') STORED,
  branch_code TEXT GENERATED ALWAYS AS (payload->>'branchCode') STORED,
  branch_name TEXT GENERATED ALWAYS AS (payload->>'branchName') STORED
);
ALTER TABLE branches DROP COLUMN IF EXISTS company_code;
DROP INDEX IF EXISTS uq_branches_company_bank_branch;
DROP INDEX IF EXISTS idx_branches_company_branch_name;
CREATE UNIQUE INDEX IF NOT EXISTS uq_branches_bank_branch ON branches(bank_code, branch_code);
CREATE INDEX IF NOT EXISTS idx_branches_branch_name ON branches(branch_name);

-- Moneytree 导入支持
CREATE TABLE IF NOT EXISTS moneytree_import_batches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  requested_by TEXT,
  total_rows INTEGER DEFAULT 0,
  inserted_rows INTEGER DEFAULT 0,
  skipped_rows INTEGER DEFAULT 0,
  error TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_moneytree_import_batches_company ON moneytree_import_batches(company_code, created_at DESC);

-- Moneytree 导入明细
CREATE TABLE IF NOT EXISTS moneytree_transactions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  batch_id UUID NOT NULL REFERENCES moneytree_import_batches(id) ON DELETE CASCADE,
  company_code TEXT NOT NULL,
  transaction_date DATE,
  deposit_amount NUMERIC(18,2),
  withdrawal_amount NUMERIC(18,2),
  balance NUMERIC(18,2),
  currency TEXT,
  bank_name TEXT,
  account_name TEXT,
  account_number TEXT,
  description TEXT,
  voucher_id UUID,
  voucher_no TEXT,
  rule_id UUID,
  rule_title TEXT,
  posting_status TEXT NOT NULL DEFAULT 'pending',
  posting_error TEXT,
  posting_run_id UUID,
  cleared_open_item_id UUID,
  hash TEXT NOT NULL,
  row_sequence INTEGER,
  imported_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, hash)
);

-- 兜底：确保唯一约束存在
DO $$
BEGIN
    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'moneytree_transactions') THEN
        -- 确保 row_sequence 列存在
        IF NOT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'moneytree_transactions' AND column_name = 'row_sequence') THEN
            ALTER TABLE moneytree_transactions ADD COLUMN row_sequence INTEGER DEFAULT 0;
        END IF;

        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint 
            WHERE conname = 'uq_moneytree_transactions_company_hash' 
              AND conrelid = 'moneytree_transactions'::regclass
        ) THEN
            -- 生产库可能已经存在重复数据，直接加 UNIQUE 会失败并导致整份 migrate.sql 被中断（随后在程序里被 catch 吞掉）。
            -- 这里先按 (company_code, hash) 去重：保留最新导入的一条，其余删除。
            BEGIN
                DELETE FROM moneytree_transactions t
                USING (
                    SELECT id
                    FROM (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY company_code, hash
                                   ORDER BY imported_at DESC, created_at DESC, id DESC
                               ) AS rn
                        FROM moneytree_transactions
                    ) ranked
                    WHERE ranked.rn > 1
                ) d
                WHERE t.id = d.id;
            EXCEPTION WHEN OTHERS THEN
                RAISE NOTICE 'dedupe moneytree_transactions skipped: %', SQLERRM;
            END;

            BEGIN
                ALTER TABLE moneytree_transactions ADD CONSTRAINT uq_moneytree_transactions_company_hash UNIQUE (company_code, hash);
            EXCEPTION WHEN OTHERS THEN
                RAISE NOTICE 'add constraint uq_moneytree_transactions_company_hash skipped: %', SQLERRM;
            END;
        END IF;
    END IF;
    
    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'payroll_deadlines') THEN
        IF NOT EXISTS (
            SELECT 1 FROM pg_constraint 
            WHERE conname = 'uq_payroll_deadlines_company_month' 
              AND conrelid = 'payroll_deadlines'::regclass
        ) THEN
            -- 同理：生产库可能已有重复 (company_code, period_month)，先去重再加 UNIQUE。
            BEGIN
                DELETE FROM payroll_deadlines p
                USING (
                    SELECT id
                    FROM (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY company_code, period_month
                                   ORDER BY updated_at DESC, created_at DESC, id DESC
                               ) AS rn
                        FROM payroll_deadlines
                    ) ranked
                    WHERE ranked.rn > 1
                ) d
                WHERE p.id = d.id;
            EXCEPTION WHEN OTHERS THEN
                RAISE NOTICE 'dedupe payroll_deadlines skipped: %', SQLERRM;
            END;

            BEGIN
                ALTER TABLE payroll_deadlines ADD CONSTRAINT uq_payroll_deadlines_company_month UNIQUE (company_code, period_month);
            EXCEPTION WHEN OTHERS THEN
                RAISE NOTICE 'add constraint uq_payroll_deadlines_company_month skipped: %', SQLERRM;
            END;
        END IF;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_company_date ON moneytree_transactions(company_code, transaction_date DESC);
CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_sequence ON moneytree_transactions (company_code, transaction_date, row_sequence);
CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_voucher ON moneytree_transactions(company_code, voucher_id);
CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_status ON moneytree_transactions(company_code, posting_status, transaction_date DESC);
CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_run ON moneytree_transactions(company_code, posting_run_id, transaction_date DESC);

-- ---------------------------------------
-- 薪资计算期限管理
-- ---------------------------------------
CREATE TABLE IF NOT EXISTS payroll_deadlines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    period_month TEXT NOT NULL,              -- YYYY-MM
    deadline_at TIMESTAMPTZ NOT NULL,
    warning_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'pending',  -- pending, warning_sent, completed, overdue, cancelled
    completed_at TIMESTAMPTZ,
    notified_at TIMESTAMPTZ,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(company_code, period_month)
);
CREATE INDEX IF NOT EXISTS idx_payroll_deadlines_status ON payroll_deadlines(company_code, status, deadline_at);
-- Moneytree 自动记账规则
CREATE TABLE IF NOT EXISTS moneytree_posting_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  priority INTEGER NOT NULL DEFAULT 100,
  matcher JSONB NOT NULL,
  action JSONB NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  created_by TEXT,
  updated_by TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_moneytree_posting_rules_company ON moneytree_posting_rules(company_code, is_active, priority);
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS title TEXT;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS description TEXT;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS priority INTEGER DEFAULT 100;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS matcher JSONB NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS action JSONB NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS created_by TEXT;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS updated_by TEXT;
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NOT NULL DEFAULT now();
ALTER TABLE moneytree_posting_rules ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

DO $$
BEGIN
  -- 更新或创建振込手数料规则（添加消费税拆分支持）
  IF EXISTS (
    SELECT 1 FROM moneytree_posting_rules WHERE company_code = 'JP01' AND title = '振込手数料-雑費'
  ) THEN
    UPDATE moneytree_posting_rules
    SET description = '振込手数料を雑費で処理。支払と配対できない場合のフォールバック用。消費税10%を自動拆分。',
        action = '{
          "debitAccount": "6610",
          "creditAccount": "{bankAccount}",
          "summaryTemplate": "振込手数料 {description}",
          "postingDate": "transactionDate",
          "debitNote": "{description}",
          "creditNote": "{description}",
          "inputTaxAccountCode": "1410",
          "bankFeeAccountCode": "6610"
        }'::jsonb,
        updated_at = now()
    WHERE company_code = 'JP01' AND title = '振込手数料-雑費';
  ELSE
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
      'JP01',
      '振込手数料-雑費',
      '振込手数料を雑費で処理。支払と配対できない場合のフォールバック用。消費税10%を自動拆分。',
      10,
      '{"descriptionContains":["振込手数料"]}'::jsonb,
      '{
        "debitAccount": "6610",
        "creditAccount": "{bankAccount}",
        "summaryTemplate": "振込手数料 {description}",
        "postingDate": "transactionDate",
        "debitNote": "{description}",
        "creditNote": "{description}",
        "inputTaxAccountCode": "1410",
        "bankFeeAccountCode": "6610"
      }'::jsonb,
      TRUE,
      'system',
      'system'
    );
  END IF;

  DELETE FROM moneytree_posting_rules
  WHERE company_code = 'JP01'
    AND title IN ('BOOKING.COM入金', 'Travel Partner入金');

  IF EXISTS (
    SELECT 1 FROM moneytree_posting_rules WHERE company_code = 'JP01' AND title = 'OTAプラットフォーム入金'
  ) THEN
    UPDATE moneytree_posting_rules
    SET matcher = '{"descriptionRegex":"(BOOKING|AIRBNB|EXPEDIA|TRIP\\.COM|CTRIP|RAKUTEN|JTB|TRAVEL PARTNER|EXCHANGE JAPAN)"}'::jsonb,
        action = '{
          "debitAccount": "{bankAccount}",
          "creditAccount": "1100",
          "summaryTemplate": "OTA入金 {description}",
          "postingDate": "transactionDate",
          "settlement": {
            "enabled": true,
            "line": "credit",
            "platformGroup": "OTA",
            "requireMatch": false,
            "tolerance": 1000
          }
        }'::jsonb,
        updated_at = now()
    WHERE company_code = 'JP01'
      AND title = 'OTAプラットフォーム入金';
  ELSE
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
      'JP01',
      'OTAプラットフォーム入金',
      '主要 OTA からの入金を統一ルールで処理し自動消込',
      20,
      '{"descriptionRegex":"(BOOKING|AIRBNB|EXPEDIA|TRIP\\.COM|CTRIP|RAKUTEN|JTB|TRAVEL PARTNER|EXCHANGE JAPAN)"}'::jsonb,
      '{
        "debitAccount": "{bankAccount}",
        "creditAccount": "1100",
        "summaryTemplate": "OTA入金 {description}",
        "postingDate": "transactionDate",
        "settlement": {
          "enabled": true,
          "line": "credit",
          "platformGroup": "OTA",
          "requireMatch": false,
          "tolerance": 1000
        }
      }'::jsonb,
      TRUE,
      'system',
      'system'
    );
  END IF;
END $$;

-- 会计期间开关
CREATE TABLE IF NOT EXISTS accounting_periods (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  period_start DATE GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'periodStart') AND (payload->>'periodStart') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
             THEN fn_jsonb_date(payload, 'periodStart')
      ELSE NULL
    END
  ) STORED,
  period_end DATE GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'periodEnd') AND (payload->>'periodEnd') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
             THEN fn_jsonb_date(payload, 'periodEnd')
      ELSE NULL
    END
  ) STORED,
  is_open BOOLEAN GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'isOpen') THEN (payload->>'isOpen')::boolean
      ELSE TRUE
    END
  ) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_accounting_periods_company_range ON accounting_periods(company_code, period_start, period_end);
CREATE INDEX IF NOT EXISTS idx_accounting_periods_company_dates ON accounting_periods(company_code, period_start, period_end);

-- インボイス登録番号（適格請求書発行事業者）マスタ
CREATE TABLE IF NOT EXISTS invoice_issuers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  registration_no TEXT NOT NULL,
  name TEXT,
  name_kana TEXT,
  effective_from DATE,
  effective_to DATE,
  last_synced_at TIMESTAMPTZ,
  source TEXT,
  payload JSONB DEFAULT '{}'::jsonb
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_invoice_issuers_no ON invoice_issuers(registration_no);

-- voucher schema 增补：允许インボイス登録番号相关字段
UPDATE schemas
SET schema = jsonb_set(
              jsonb_set(
                jsonb_set(
                  jsonb_set(
                    jsonb_set(
                      jsonb_set(
                        jsonb_set(schema,
                          '{properties,header,properties,invoiceRegistrationNo}',
                          '{"type":"string","pattern":"^T\\d{13}$","maxLength":14}'::jsonb,
                          true
                        ),
                        '{properties,header,properties,invoiceRegistrationStatus}',
                        '{"type":["string","null"]}'::jsonb,
                        true
                      ),
                      '{properties,header,properties,invoiceRegistrationName}',
                      '{"type":["string","null"]}'::jsonb,
                      true
                    ),
                    '{properties,header,properties,invoiceRegistrationCheckedAt}',
                    '{"type":["string","null"],"format":"date-time"}'::jsonb,
                    true
                  ),
                  '{properties,header,properties,invoiceRegistrationEffectiveFrom}',
                  '{"type":["string","null"],"format":"date"}'::jsonb,
                  true
                ),
                '{properties,header,properties,invoiceRegistrationEffectiveTo}',
                '{"type":["string","null"],"format":"date"}'::jsonb,
                true
              ),
              '{properties,header,properties,invoiceRegistrationNote}',
              '{"type":["string","null"]}'::jsonb,
              true
           ),
    updated_at = now()
WHERE name='voucher';

-- AI 会话与消息表（ChatKit 持久化）
CREATE TABLE IF NOT EXISTS ai_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  user_id TEXT NOT NULL,
  title TEXT,
  state JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_sessions_company_user ON ai_sessions(company_code, user_id);
ALTER TABLE ai_sessions ADD COLUMN IF NOT EXISTS state JSONB NOT NULL DEFAULT '{}'::jsonb;

CREATE TABLE IF NOT EXISTS ai_messages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL,
  role TEXT NOT NULL, -- 'user' | 'assistant' | 'system' | 'tool'
  content TEXT,
  payload JSONB,
  task_id UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
ALTER TABLE ai_messages ADD COLUMN IF NOT EXISTS task_id UUID;
CREATE INDEX IF NOT EXISTS idx_ai_messages_session ON ai_messages(session_id, created_at);
CREATE INDEX IF NOT EXISTS idx_ai_messages_task ON ai_messages(task_id, created_at);

CREATE TABLE IF NOT EXISTS ai_accounting_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  keywords TEXT[] NOT NULL DEFAULT '{}',
  account_code TEXT,
  account_name TEXT,
  note TEXT,
  priority INT NOT NULL DEFAULT 100,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  options JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_accounting_rules_company ON ai_accounting_rules(company_code, is_active, priority);

-- ---------------------------------------
-- AI 任务管理 (Unified Tasks)
-- ---------------------------------------
CREATE TABLE IF NOT EXISTS ai_tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID,                         -- AI 会话 ID（可选）
    company_code TEXT NOT NULL,
    task_type TEXT NOT NULL,                 -- 'invoice', 'sales_order', 'payroll', 'alert', 等
    status TEXT NOT NULL DEFAULT 'pending',  -- 'pending', 'in_progress', 'completed', 'cancelled', 'failed'
    title TEXT,
    summary TEXT,
    
    -- 通用用户字段
    user_id TEXT,                            -- 创建者/所属用户
    target_user_id TEXT,                     -- 目标用户（用于通知）
    assigned_user_id TEXT,                   -- 已分配用户
    
    -- 类型特定的数据都放在 payload 中
    payload JSONB DEFAULT '{}'::jsonb,
    metadata JSONB DEFAULT '{}'::jsonb,
    
    -- 时间戳
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    completed_by TEXT
);

CREATE INDEX IF NOT EXISTS idx_ai_tasks_session ON ai_tasks(session_id);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_company_type ON ai_tasks(company_code, task_type);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_company_status ON ai_tasks(company_code, status);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_target_user ON ai_tasks(company_code, target_user_id, status);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_created ON ai_tasks(created_at DESC);

-- 数据迁移逻辑 (仅在旧表存在时运行)
DO $$
BEGIN
    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'ai_invoice_tasks') THEN
        INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, payload, metadata, created_at, updated_at, completed_at)
        SELECT 
            id, session_id, company_code, 'invoice', COALESCE(status, 'pending'), COALESCE(file_name, 'Invoice Task'), summary, user_id,
            jsonb_build_object(
                'fileId', file_id,
                'documentSessionId', document_session_id,
                'fileName', file_name,
                'contentType', content_type,
                'fileSize', size,
                'blobName', blob_name,
                'documentLabel', document_label,
                'storedPath', null,
                'analysis', analysis
            ),
            COALESCE(metadata, '{}'::jsonb), created_at, updated_at, completed_at
        FROM ai_invoice_tasks
        ON CONFLICT (id) DO NOTHING;
    END IF;

    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'ai_sales_order_tasks') THEN
        INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, payload, metadata, created_at, updated_at, completed_at)
        SELECT 
            id, session_id, company_code, 'sales_order', COALESCE(status, 'pending'), COALESCE(sales_order_no, 'Sales Order Task'), summary, user_id,
            jsonb_build_object(
                'salesOrderId', sales_order_id,
                'salesOrderNo', sales_order_no,
                'customerCode', customer_code,
                'customerName', customer_name
            ) || COALESCE(payload, '{}'::jsonb),
            COALESCE(metadata, '{}'::jsonb), created_at, updated_at, completed_at
        FROM ai_sales_order_tasks
        ON CONFLICT (id) DO NOTHING;
    END IF;

    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'ai_payroll_tasks') THEN
        INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, target_user_id, payload, metadata, created_at, updated_at, completed_at)
        SELECT 
            id, session_id, company_code, 'payroll', COALESCE(status, 'pending'), COALESCE(employee_name, 'Payroll Task'), summary, null, target_user_id,
            jsonb_build_object(
                'runId', run_id,
                'entryId', entry_id,
                'employeeId', employee_id,
                'employeeCode', employee_code,
                'employeeName', employee_name,
                'periodMonth', period_month,
                'diffSummary', diff_summary
            ),
            COALESCE(metadata, '{}'::jsonb), created_at, updated_at, completed_at
        FROM ai_payroll_tasks
        ON CONFLICT (id) DO NOTHING;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS ai_messages_archive (
  id UUID PRIMARY KEY,
  session_id UUID NOT NULL,
  role TEXT NOT NULL,
  content TEXT,
  payload JSONB,
  task_id UUID,
  created_at TIMESTAMPTZ NOT NULL,
  archived_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
ALTER TABLE ai_messages_archive ADD COLUMN IF NOT EXISTS task_id UUID;
CREATE INDEX IF NOT EXISTS idx_ai_messages_archive_session ON ai_messages_archive(session_id, created_at);
CREATE INDEX IF NOT EXISTS idx_ai_messages_archive_task ON ai_messages_archive(task_id, created_at);

-- HR/Payroll 配置实体（骨架表，后续由 schema 驱动）
CREATE TABLE IF NOT EXISTS employment_types (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  type_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  is_active BOOLEAN GENERATED ALWAYS AS ((payload->>'isActive')='true') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_employment_types_company_code ON employment_types(company_code, type_code);
CREATE INDEX IF NOT EXISTS idx_employment_types_company_active ON employment_types(company_code, is_active);

-- payroll_items 已废弃（自然语言薪资构成替代）

CREATE TABLE IF NOT EXISTS payroll_policies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  policy_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_payroll_policies_company_code ON payroll_policies(company_code, policy_code);

-- 法规数据集（示例结构）：统一以 law_rates 存储三险费率
-- kind: 'health' | 'pension' | 'employment'
-- key:  健康保险=都道府県，雇佣保险=事业区分；厚生年金可为空
-- 区间：按 min_amount/max_amount 匹配（半开半闭区间 [min, max)）
-- 生效期：按 [effective_from, effective_to] 匹配月份日期
CREATE TABLE IF NOT EXISTS law_rates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NULL,
  kind TEXT NOT NULL,
  key TEXT NULL,
  min_amount NUMERIC(18,2) NULL,
  max_amount NUMERIC(18,2) NULL,
  rate NUMERIC(18,6) NOT NULL,
  effective_from DATE NOT NULL,
  effective_to DATE NULL,
  version TEXT NULL,
  note TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_law_rates_lookup ON law_rates(company_code, kind, key, effective_from, effective_to);

-- 源泉所得税 月額表（甲欄）等の速算表（電子計算機特例用）
-- カテゴリー例: 'monthly_ko'
-- 税額 = max(0, 課税給与所得金額(B) * rate - deduction)
-- rate は復興特別所得税(102.1%)を内包した合算率を格納
CREATE TABLE IF NOT EXISTS withholding_rates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NULL,
  category TEXT NOT NULL,
  min_amount NUMERIC(18,2) NULL,
  max_amount NUMERIC(18,2) NULL,
  rate NUMERIC(18,6) NOT NULL,
  deduction NUMERIC(18,2) NOT NULL DEFAULT 0,
  effective_from DATE NOT NULL,
  effective_to DATE NULL,
  version TEXT NULL,
  note TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_withholding_rates_lookup ON withholding_rates(company_code, category, effective_from, effective_to);

-- 认证与权限表
CREATE TABLE IF NOT EXISTS users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  employee_code TEXT NOT NULL,
  password_hash TEXT NOT NULL,
  name TEXT,
  dept_id TEXT,
  user_type TEXT DEFAULT 'internal', -- 'internal' | 'external'
  employee_id UUID, -- 关联员工ID（外部用户为空）
  email TEXT,
  phone TEXT,
  is_active BOOLEAN DEFAULT true,
  last_login_at TIMESTAMPTZ,
  external_type TEXT, -- 'tax_accountant' | 'auditor' | 'client' 等
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ DEFAULT now(),
  UNIQUE(company_code, employee_code)
);

CREATE INDEX IF NOT EXISTS idx_users_employee_id ON users(employee_id) WHERE employee_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_users_user_type ON users(company_code, user_type);
CREATE INDEX IF NOT EXISTS idx_users_is_active ON users(company_code, is_active);

CREATE TABLE IF NOT EXISTS roles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT,
  role_code TEXT NOT NULL,
  role_name TEXT,
  description TEXT,
  role_type TEXT DEFAULT 'custom', -- 'builtin' | 'custom' | 'ai_generated'
  is_active BOOLEAN DEFAULT true,
  source_prompt TEXT, -- AI生成角色时保存的原始描述
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ DEFAULT now(),
  UNIQUE(company_code, role_code)
);

CREATE INDEX IF NOT EXISTS idx_roles_is_active ON roles(company_code, is_active);
CREATE INDEX IF NOT EXISTS idx_roles_role_type ON roles(company_code, role_type);

CREATE TABLE IF NOT EXISTS user_roles (
  user_id UUID NOT NULL,
  role_id UUID NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(user_id, role_id)
);

-- 角色能力（capabilities），用于非 schema 绑定的跨页面权限
CREATE TABLE IF NOT EXISTS role_caps (
  role_id UUID NOT NULL,
  cap TEXT NOT NULL,
  PRIMARY KEY(role_id, cap)
);

-- 功能模块定义表（系统级）
CREATE TABLE IF NOT EXISTS permission_modules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  module_code TEXT NOT NULL UNIQUE,
  module_name JSONB NOT NULL,
  icon TEXT,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 功能菜单/页面定义表
CREATE TABLE IF NOT EXISTS permission_menus (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  module_code TEXT NOT NULL,
  menu_key TEXT NOT NULL UNIQUE,
  menu_name JSONB NOT NULL,
  menu_path TEXT,
  caps_required TEXT[],
  caps_all_required TEXT[],
  parent_menu_key TEXT,
  description JSONB,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_permission_menus_module ON permission_menus(module_code);
CREATE INDEX IF NOT EXISTS idx_permission_menus_parent ON permission_menus(parent_menu_key);

-- 能力（Capability）定义表
CREATE TABLE IF NOT EXISTS permission_caps (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  cap_code TEXT NOT NULL UNIQUE,
  cap_name JSONB NOT NULL,
  module_code TEXT NOT NULL,
  cap_type TEXT DEFAULT 'action',
  is_sensitive BOOLEAN DEFAULT false,
  description JSONB,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_permission_caps_module ON permission_caps(module_code);
CREATE INDEX IF NOT EXISTS idx_permission_caps_type ON permission_caps(cap_type);

-- 数据范围权限表
CREATE TABLE IF NOT EXISTS role_data_scopes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  role_id UUID NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  entity_type TEXT NOT NULL,
  scope_type TEXT NOT NULL,
  scope_filter JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(role_id, entity_type)
);

CREATE INDEX IF NOT EXISTS idx_role_data_scopes_role ON role_data_scopes(role_id);
CREATE INDEX IF NOT EXISTS idx_role_data_scopes_entity ON role_data_scopes(entity_type);

-- AI角色生成日志
CREATE TABLE IF NOT EXISTS ai_role_generations (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  role_id UUID REFERENCES roles(id) ON DELETE SET NULL,
  user_prompt TEXT NOT NULL,
  ai_response JSONB,
  status TEXT DEFAULT 'pending',
  applied_at TIMESTAMPTZ,
  applied_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_ai_role_generations_company ON ai_role_generations(company_code);
CREATE INDEX IF NOT EXISTS idx_ai_role_generations_role ON ai_role_generations(role_id);
CREATE INDEX IF NOT EXISTS idx_ai_role_generations_status ON ai_role_generations(status);

-- 种子：voucher/businesspartner 的结构定义（可在应用内编辑/版本化）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'voucher', 1, TRUE,
 '{"type":"object","properties":{"header":{"type":"object","properties":{"companyCode":{"type":"string"},"postingDate":{"type":"string","format":"date"},"voucherType":{"type":"string","enum":["GL","AP","AR","AA","SA","IN","OT"]},"voucherNo":{"type":"string"},"summary":{"type":"string"}},"required":["companyCode","postingDate","voucherType"]},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"accountCode":{"type":"string"},"debit":{"type":"integer"},"credit":{"type":"integer"},"vendorId":{"type":["string","null"]},"customerId":{"type":["string","null"]},"departmentId":{"type":["string","null"]},"employeeId":{"type":["string","null"]},"tax":{"type":["object","null"],"properties":{"rate":{"type":"number"},"amount":{"type":"integer"}}}},"required":["lineNo","accountCode","debit","credit"]}}},"required":["header","lines"]}',
 '{"list":{"columns":["posting_date","voucher_type","voucher_no"]},"form":{"layout":[]}}',
 '{"filters":["posting_date","voucher_type","voucher_no","lines[].employeeId"],"sorts":["posting_date","voucher_no"]}',
 '{"coreFields":[{"name":"posting_date","path":"header.postingDate","type":"date","index":{"strategy":"generated_column","unique":false}},{"name":"voucher_type","path":"header.voucherType","type":"string","index":{"strategy":"generated_column","unique":false}},{"name":"voucher_no","path":"header.voucherNo","type":"string","index":{"strategy":"generated_column","unique":true,"scope":["company_code"]}}]}',
 '["voucher_balance_check"]'::jsonb,
 '{"strategy":"yymm6","targetPath":"header.voucherNo"}'::jsonb,
 '{"displayNames":{"ja":"仕訳","zh":"会计凭证","en":"Voucher"},"synonyms":["凭证","仕訳","会计分录","journal"],"typeMap":{"工资凭证":"SA","給与仕訳":"SA","入金":"IN","出金":"OT"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Schema: 通知规则运行记录（只读列表）
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'notification_rule_run', 1, TRUE,
  '{"type":"object","properties":{"company_code":{"type":"string"},"policy_id":{"type":"string"},"rule_key":{"type":"string"},"last_run_at":{"type":"string","format":"date-time"}},"required":[]}',
  '{"list":{"columns":["company_code","policy_id","rule_key","last_run_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"company_code","label":"公司","span":8,"props":{"readonly":true}},{"field":"policy_id","label":"策略ID","span":8,"props":{"readonly":true}},{"field":"rule_key","label":"规则键","span":8,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"last_run_at","label":"上次运行","span":8,"props":{"readonly":true}}]}]}}',
  '{"filters":["company_code","policy_id","rule_key","last_run_at"],"sorts":["last_run_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Schema: 通知发送日志（只读列表）
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'notification_log', 1, TRUE,
  '{"type":"object","properties":{"company_code":{"type":"string"},"policy_id":{"type":["string","null"]},"rule_key":{"type":["string","null"]},"user_id":{"type":"string"},"related_entity":{"type":["string","null"]},"related_id":{"type":["string","null"]},"sent_at":{"type":"string","format":"date-time"},"sent_day":{"type":"string","format":"date"}},"required":[]}',
  '{"list":{"columns":["sent_at","policy_id","rule_key","user_id","related_entity","related_id"]},"form":{"layout":[{"type":"grid","cols":[{"field":"sent_at","label":"发送时间","span":8,"props":{"readonly":true}},{"field":"sent_day","label":"发送日","span":6,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"policy_id","label":"策略ID","span":8,"props":{"readonly":true}},{"field":"rule_key","label":"规则键","span":8,"props":{"readonly":true}},{"field":"user_id","label":"用户ID","span":8,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"related_entity","label":"对象","span":8,"props":{"readonly":true}},{"field":"related_id","label":"对象ID","span":16,"props":{"readonly":true}}]}]}}',
  '{"filters":["sent_day","policy_id","rule_key","user_id","related_entity"],"sorts":["sent_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: CRM schemas（contact/deal/quote/sales_order/activity）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
  ('JP01','contact', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"partnerCode":{"type":"string"},"email":{"type":["string","null"],"format":"email"},"mobile":{"type":["string","null"]},"language":{"type":["string","null"],"enum":["zh","ja","en",null]},"status":{"type":["string","null"],"enum":["active","inactive",null]}},"required":["name","partnerCode"]}',
  '{"list":{"columns":["contact_code","name","partner_code","email","status"]},"form":{"layout":[]}}',
  '{"filters":["contact_code","name","partner_code","email","status"],"sorts":["name"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
  ('JP01','deal', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"partnerCode":{"type":"string"},"ownerUserId":{"type":["string","null"]},"stage":{"type":"string","enum":["prospect","proposal","won","invoiced"]},"expectedAmount":{"type":["number","null"]},"expectedCloseDate":{"type":["string","null"],"format":"date"},"source":{"type":["string","null"],"enum":["referral","website","expo",null]},"status":{"type":["string","null"]}},"required":["code","partnerCode","stage"]}',
  '{"list":{"columns":["deal_code","partner_code","stage","expected_amount","expected_close_date","source"]},"form":{"layout":[]}}',
  '{"filters":["deal_code","partner_code","stage","owner_user_id","expected_close_date","source"],"sorts":["expected_close_date"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
  ('JP01','quote', 1, TRUE,
  '{"type":"object","properties":{"quoteNo":{"type":"string"},"partnerCode":{"type":"string"},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"item":{"type":"string"},"qty":{"type":"number"},"uom":{"type":["string","null"]},"price":{"type":"number"},"amount":{"type":"number"}},"required":["lineNo","item","qty","price"]}},"amountTotal":{"type":"number"},"validUntil":{"type":["string","null"],"format":"date"},"status":{"type":"string","enum":["draft","sent","accepted","rejected"]}},"required":["quoteNo","partnerCode","amountTotal","status"]}',
  '{"list":{"columns":["quote_no","partner_code","amount_total","valid_until","status"]},"form":{"layout":[]}}',
  '{"filters":["quote_no","partner_code","status"],"sorts":["quote_no"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
  ('JP01','sales_order', 1, TRUE,
  '{"type":"object","properties":{"soNo":{"type":"string"},"partnerCode":{"type":"string"},"amountTotal":{"type":"number"},"orderDate":{"type":"string","format":"date"},"status":{"type":"string","enum":["draft","confirmed","invoiced","cancelled"]}},"required":["soNo","partnerCode","amountTotal","orderDate"]}',
  '{"list":{"columns":["so_no","partner_code","amount_total","order_date","status"]},"form":{"layout":[]}}',
  '{"filters":["so_no","partner_code","status"],"sorts":["order_date"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
  ('JP01','activity', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"type":{"type":"string","enum":["email","phone","meeting","task"]},"subject":{"type":"string"},"dueDate":{"type":["string","null"],"format":"date"},"owner":{"type":["string","null"]},"status":{"type":["string","null"],"enum":["open","done","cancelled"]}},"required":["code","type"]}',
  '{"list":{"columns":["activity_type","subject","due_date","owner","status"]},"form":{"layout":[]}}',
  '{"filters":["activity_type","due_date","status"],"sorts":["due_date"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- 审批待办：统一投影表（任意实体的审批步骤分发）
CREATE TABLE IF NOT EXISTS approval_tasks (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  entity TEXT NOT NULL,
  object_id UUID NOT NULL,
  step_no INTEGER NOT NULL,
  step_name TEXT,
  approver_user_id TEXT NOT NULL,
  approver_email TEXT NULL,
  status TEXT NOT NULL DEFAULT 'pending', -- pending/approved/rejected
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_approvals_company_user ON approval_tasks(company_code, approver_user_id, status);
CREATE INDEX IF NOT EXISTS idx_approvals_company_obj ON approval_tasks(company_code, entity, object_id);

-- 证明申请：通用对象表（走 CRUD），用于存放申请表单与 AI 生成的邮件/PDF正文字段
CREATE TABLE IF NOT EXISTS certificate_requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 种子：审批待办与证明申请的 schema（用于通用搜索/表单与权限）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'approval_task', 1, TRUE,
  '{"type":"object","properties":{}}',
  '{"list":{"columns":["entity","step_no","step_name","status","created_at"]},"form":{"layout":[]}}',
  '{"filters":["approver_user_id","status","entity","created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'certificate_request', 1, TRUE,
  '{"type":"object","properties":{"employeeId":{"type":"string"},"type":{"type":"string"},"language":{"type":"string"},"purpose":{"type":"string"},"toEmail":{"type":"string"},"subject":{"type":"string"},"bodyText":{"type":"string"},"status":{"type":"string"}},"required":["employeeId","type"]}',
  '{"list":{"columns":["created_at","status"]},"form":{"layout":[]}}',
  '{"filters":["status","created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'company_setting', 1, TRUE,
  '{"type":"object","properties":{"companyName":{"type":"string"},"companyAddress":{"type":"string"},"companyRep":{"type":"string"},"workdayDefaultStart":{"type":"string","pattern":"^\\d{2}:\\d{2}$"},"workdayDefaultEnd":{"type":"string","pattern":"^\\d{2}:\\d{2}$"},"lunchMinutes":{"type":"number","minimum":0,"maximum":240}},"required":[]}',
  '{"list":{"columns":["created_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"companyName","label":"公司名称","span":12},{"field":"companyAddress","label":"公司地址","span":12},{"field":"companyRep","label":"代表者","span":6},{"field":"workdayDefaultStart","label":"上班(HH:mm)","span":6},{"field":"workdayDefaultEnd","label":"下班(HH:mm)","span":6},{"field":"lunchMinutes","label":"午休(分钟)","span":6,"props":{"type":"number"}}]}]}}',
  '{"filters":["created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- ===============================
-- 库存模块表结构（物料/仓库/仓位/批次/库存移动/现存量）
-- ===============================

-- 物料主数据
CREATE TABLE IF NOT EXISTS materials (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  base_uom TEXT GENERATED ALWAYS AS (payload->>'baseUom') STORED,
  is_batch_mgmt BOOLEAN GENERATED ALWAYS AS ((payload->>'batchManagement')='true') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_materials_company_code ON materials(company_code, material_code);
CREATE INDEX IF NOT EXISTS idx_materials_company_name ON materials(company_code, name);

-- 仓库主数据
CREATE TABLE IF NOT EXISTS warehouses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_warehouses_company_code ON warehouses(company_code, warehouse_code);
CREATE INDEX IF NOT EXISTS idx_warehouses_company_name ON warehouses(company_code, name);

-- 仓位（库位/存储位）
CREATE TABLE IF NOT EXISTS bins (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  warehouse_code TEXT GENERATED ALWAYS AS (payload->>'warehouseCode') STORED,
  bin_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_bins_company_wh_bin ON bins(company_code, warehouse_code, bin_code);
CREATE INDEX IF NOT EXISTS idx_bins_company_wh ON bins(company_code, warehouse_code);

-- 库存状态字典：如 检品中/非限制使用/客户库存 等
CREATE TABLE IF NOT EXISTS stock_statuses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_stock_statuses_company_code ON stock_statuses(company_code, status_code);

-- 批次主数据（可按物料+批次号唯一）
CREATE TABLE IF NOT EXISTS batches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  material_code TEXT GENERATED ALWAYS AS (payload->>'materialCode') STORED,
  batch_no TEXT GENERATED ALWAYS AS (payload->>'batchNo') STORED,
  mfg_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'mfgDate')) STORED,
  exp_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'expDate')) STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_batches_company_material_batch ON batches(company_code, material_code, batch_no);

-- 库存移动单（入库/出库/转储），采用 JSONB 存业务字段，生成列便于检索
CREATE TABLE IF NOT EXISTS inventory_movements (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  movement_type TEXT GENERATED ALWAYS AS (payload->>'movementType') STORED, -- IN/OUT/TRANSFER
  movement_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'movementDate')) STORED,
  from_warehouse TEXT GENERATED ALWAYS AS (payload->>'fromWarehouse') STORED,
  from_bin TEXT GENERATED ALWAYS AS (payload->>'fromBin') STORED,
  to_warehouse TEXT GENERATED ALWAYS AS (payload->>'toWarehouse') STORED,
  to_bin TEXT GENERATED ALWAYS AS (payload->>'toBin') STORED,
  reference_no TEXT GENERATED ALWAYS AS (payload->>'referenceNo') STORED
);
CREATE INDEX IF NOT EXISTS idx_inv_moves_company_date ON inventory_movements(company_code, movement_date DESC);
CREATE INDEX IF NOT EXISTS idx_inv_moves_company_type ON inventory_movements(company_code, movement_type);

-- 库存台账明细（按行展开 movements.lines）
CREATE TABLE IF NOT EXISTS inventory_ledger (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  movement_id UUID NOT NULL,
  line_no INTEGER NOT NULL,
  movement_type TEXT NOT NULL,
  movement_date DATE NOT NULL,
  material_code TEXT NOT NULL,
  quantity NUMERIC(18,6) NOT NULL,
  uom TEXT,
  from_warehouse TEXT,
  from_bin TEXT,
  to_warehouse TEXT,
  to_bin TEXT,
  batch_no TEXT,
  status_code TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_inv_ledger_company_material ON inventory_ledger(company_code, material_code, movement_date);
CREATE INDEX IF NOT EXISTS idx_inv_ledger_company_wh_bin ON inventory_ledger(company_code, to_warehouse, to_bin);

-- 现存量聚合表（便于快速查询）：物料/仓库/仓位/状态/批次 维度
CREATE TABLE IF NOT EXISTS inventory_balances (
  company_code TEXT NOT NULL,
  material_code TEXT NOT NULL,
  warehouse_code TEXT NOT NULL,
  bin_code TEXT NULL,
  status_code TEXT NULL,
  batch_no TEXT NULL,
  quantity NUMERIC(18,6) NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code, material_code, warehouse_code, bin_code, status_code, batch_no)
);
CREATE INDEX IF NOT EXISTS idx_inv_bal_company_wh ON inventory_balances(company_code, warehouse_code);

-- 最小 schema 种子：materials/warehouse/bin/stock_status/batch/inventory_movement
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'material', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"baseUom":{"type":"string"},"batchManagement":{"type":"boolean"},"price":{"type":"number"}}}',
  '{"list":{"columns":["material_code","base_uom","is_batch_mgmt"]},"form":{"layout":[{"type":"grid","cols":[{"field":"code","label":"编码","span":6},{"field":"baseUom","label":"基本单位","span":6},{"field":"batchManagement","label":"批次管理","widget":"switch","span":6}]}]}}',
  '{"filters":["material_code","is_batch_mgmt"],"sorts":["material_code"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'warehouse', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["warehouse_code","name"]}}',
  '{"filters":["warehouse_code","name"],"sorts":["warehouse_code","name"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'bin', 1, TRUE,
  '{"type":"object","properties":{"warehouseCode":{"type":"string"},"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["warehouse_code","bin_code","name"]}}',
  '{"filters":["warehouse_code","bin_code"],"sorts":["warehouse_code","bin_code"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'stock_status', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["status_code","name"]}}',
  '{"filters":["status_code","name"],"sorts":["status_code","name"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'batch', 1, TRUE,
  '{"type":"object","properties":{"materialCode":{"type":"string"},"batchNo":{"type":"string"},"mfgDate":{"type":"string","format":"date"},"expDate":{"type":"string","format":"date"}}}',
  '{"list":{"columns":["material_code","batch_no","mfg_date","exp_date"]}}',
  '{"filters":["material_code","batch_no"],"sorts":["material_code","batch_no"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'inventory_movement', 1, TRUE,
  '{"type":"object","properties":{"movementType":{"type":"string","enum":["IN","OUT","TRANSFER"]},"movementDate":{"type":"string","format":"date"},"fromWarehouse":{"type":["string","null"]},"fromBin":{"type":["string","null"]},"toWarehouse":{"type":["string","null"]},"toBin":{"type":["string","null"]},"referenceNo":{"type":["string","null"]},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"materialCode":{"type":"string"},"quantity":{"type":"number"},"uom":{"type":"string"},"batchNo":{"type":["string","null"]},"statusCode":{"type":["string","null"]}}}}}}',
  '{"list":{"columns":["movement_date","movement_type","reference_no"]}}',
  '{"filters":["movement_date","movement_type","reference_no"],"sorts":["movement_date"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'businesspartner', 1, TRUE,
 '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"flags":{"type":"object","properties":{"customer":{"type":"boolean"},"vendor":{"type":"boolean"}}},"status":{"type":"string"}},"required":["code","name"]}',
 '{"list":{"columns":["partner_code","name","flag_customer","flag_vendor","status"]},"form":{"labelWidth":"140px","layout":[{"type":"grid","cols":[{"field":"code","label":"取引先コード","span":8},{"field":"name","label":"名称","span":16}]},{"type":"grid","cols":[{"field":"flags.customer","label":"顧客区分","span":8,"widget":"switch","props":{"activeText":"あり","inactiveText":"なし"}},{"field":"flags.vendor","label":"仕入先区分","span":8,"widget":"switch","props":{"activeText":"あり","inactiveText":"なし"}},{"field":"status","label":"ステータス","span":8}]}]}}',
 '{"filters":["partner_code","name","flag_customer","flag_vendor","status"],"sorts":["partner_code","name"]}',
 '{"coreFields":[{"name":"partner_code","path":"code","type":"string","index":{"strategy":"generated_column","unique":true,"scope":["company_code"]}},{"name":"name","path":"name","type":"string","index":{"strategy":"generated_column"}},{"name":"flag_customer","path":"flags.customer","type":"boolean","index":{"strategy":"generated_column"}},{"name":"flag_vendor","path":"flags.vendor","type":"boolean","index":{"strategy":"generated_column"}},{"name":"status","path":"status","type":"string","index":{"strategy":"generated_column"}}]}',
 '[]'::jsonb,
 NULL::jsonb,
 NULL::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: HR/Payroll 基础 schema（最小骨架）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'employment_type', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"isActive":{"type":"boolean"}},"required":["code","name"]}',
  '{"list":{"columns":["type_code","name","is_active"]},"form":{"layout":[]}}',
  '{"filters":["type_code","name","is_active"],"sorts":["type_code","name"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 -- payroll_item schema 移除
 (NULL,'payroll_policy', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"rules":{"type":"array"}},"required":["code","name"]}',
  '{"list":{"columns":["policy_code","name"]},"form":{"layout":[]}}',
  '{"filters":["policy_code","name"],"sorts":["policy_code","name"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: openitem schema（用于通用搜索 DSL，便于前端查询未清项）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'openitem', 1, TRUE,
  '{"type":"object","properties":{}}',
  '{"list":{"columns":["doc_date","partner_id","account_code","currency","original_amount","residual_amount"]},"form":{"layout":[]}}',
  '{"filters":["doc_date","partner_id","account_code","currency","original_amount","residual_amount"],"sorts":["doc_date","residual_amount"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'account', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"category":{"type":"string"}},"required":["code","name","category"]}',
  '{"list":{"columns":["account_code","name","category"]},"form":{"layout":[]}}',
  '{"filters":["account_code","name","category"],"sorts":["account_code"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'openitem_projection', 1, TRUE,
  '{"type":"object","properties":{"voucher_id":{"type":"string"},"voucher_line_no":{"type":"integer"},"account_code":{"type":"string"}},"required":["voucher_id","voucher_line_no","account_code"]}',
  '{"list":{"columns":["voucher_id","voucher_line_no","account_code"]},"form":{"layout":[]}}',
  '{"filters":["voucher_id","account_code"],"sorts":["voucher_line_no"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'bank', 1, TRUE,
  '{"type":"object","properties":{"bankCode":{"type":"string"},"name":{"type":"string"}},"required":["bankCode","name"]}',
  '{"list":{"columns":["bank_code","name"]},"form":{"layout":[]}}',
  '{"filters":["bank_code","name"],"sorts":["bank_code"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'branch', 1, TRUE,
  '{"type":"object","properties":{"bankCode":{"type":"string"},"branchCode":{"type":"string"},"name":{"type":"string"}},"required":["bankCode","branchCode","name"]}',
  '{"list":{"columns":["bank_code","branch_code","name"]},"form":{"layout":[]}}',
  '{"filters":["bank_code","branch_code","name"],"sorts":["branch_code"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'accounting_period', 1, TRUE,
  '{"type":"object","properties":{"periodStart":{"type":"string","format":"date"},"periodEnd":{"type":"string","format":"date"},"isOpen":{"type":"boolean"}},"required":["periodStart","periodEnd"]}',
  '{"list":{"columns":["period_start","period_end","is_open"]},"form":{"layout":[]}}',
  '{"filters":["period_start","period_end","is_open"],"sorts":["period_start"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 ),
 (NULL,'invoice_issuer', 1, TRUE,
  '{"type":"object","properties":{"registrationNo":{"type":"string"},"name":{"type":"string"}},"required":["registrationNo","name"]}',
  '{"list":{"columns":["registration_no","name"]},"form":{"layout":[]}}',
  '{"filters":["registration_no","name"],"sorts":["registration_no"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

 
-- 推送设备令牌（用于 APNs/FCM 等）
CREATE TABLE IF NOT EXISTS device_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  user_id UUID NOT NULL,
  platform TEXT NOT NULL, -- 'ios' | 'android' | 'web'
  bundle_id TEXT,
  device_id TEXT,
  token TEXT NOT NULL,
  environment TEXT, -- 'sandbox' | 'production'
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_device_tokens_user ON device_tokens(company_code, user_id, platform);
CREATE UNIQUE INDEX IF NOT EXISTS uq_device_tokens_unique ON device_tokens(company_code, user_id, platform, device_id);

-- 通知策略（自然语言与编译后的结构）
CREATE TABLE IF NOT EXISTS notification_policies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  name TEXT NOT NULL,
  nl TEXT,
  compiled JSONB NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, name)
);

-- 通知发送日志（用于去重与限频）
CREATE TABLE IF NOT EXISTS notification_logs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  policy_id UUID NULL,
  rule_key TEXT NULL,
  user_id UUID NOT NULL,
  related_entity TEXT NULL,
  related_id UUID NULL,
  sent_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  sent_day DATE GENERATED ALWAYS AS (fn_timestamptz_date(sent_at)) STORED
);
CREATE INDEX IF NOT EXISTS idx_notification_logs_company_user_day ON notification_logs(company_code, user_id, sent_day);
CREATE INDEX IF NOT EXISTS idx_notification_logs_related ON notification_logs(company_code, related_entity, related_id, sent_day);

-- 通知规则运行记录（用于按规则的 schedule 去重/防抖）
CREATE TABLE IF NOT EXISTS notification_rule_runs (
  company_code TEXT NOT NULL,
  policy_id UUID NOT NULL,
  rule_key TEXT NOT NULL,
  last_run_at TIMESTAMPTZ NOT NULL,
  PRIMARY KEY(company_code, policy_id, rule_key)
);

-- Schema: 通知策略（用于 UI 渲染与权限控制）
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'notification_policy', 1, TRUE,
  '{"type":"object","properties":{"name":{"type":"string"},"nl":{"type":["string","null"]},"compiled":{"type":"object"},"isActive":{"type":"boolean"}},"required":["name"]}',
  '{"list":{"columns":["name","is_active","updated_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"name","label":"策略名称","span":12},{"field":"isActive","label":"启用","widget":"switch","span":4}]},{"type":"grid","cols":[{"field":"nl","label":"自然语言描述","span":24,"props":{"type":"textarea","rows":3}},{"field":"compiled","label":"编译结果(JSON)","span":24,"props":{"type":"json"}}]}]}}',
  '{"filters":["name","is_active"],"sorts":["updated_at"]}',
  '{"coreFields":[]}',
  '[]'::jsonb,
  NULL::jsonb,
  NULL::jsonb
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

CREATE TABLE IF NOT EXISTS ai_workflow_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  rule_key TEXT NOT NULL,
  title TEXT,
  description TEXT,
  instructions TEXT,
  actions JSONB NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  priority INTEGER NOT NULL DEFAULT 100,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_ai_workflow_rules_company_key ON ai_workflow_rules(company_code, rule_key);

INSERT INTO ai_workflow_rules (company_code, rule_key, title, description, instructions, actions, priority)
VALUES (
  'JP01',
  'dining.receipt',
  '餐饮发票自动记账',
  '识别为餐饮、饮食、居酒屋等消费的发票或收据时自动建凭证。',
  '当文档显示为餐饮相关消费（例如发票抬头包含餐饮、饮食、居酒屋、meal、restaurant 等）时，选择此规则。',
  jsonb_build_array(
    jsonb_build_object(
      'type','voucher.autoCreate',
      'params', jsonb_build_object(
        'header', jsonb_build_object(
          'summary','餐饮消费：{document.partnerName ?? metadata.fileName ?? ''未命名''}',
          'postingDate','{document.issueDate ?? header.postingDate ?? today}',
          'currency','{header.currency ?? ''JPY''}'
        ),
        'lines', jsonb_build_array(
          jsonb_build_object(
            'side','debit',
            'amount','{totals.grand ?? document.amount}',
            'account', jsonb_build_object('code','6200')
          ),
          jsonb_build_object(
            'side','credit',
            'amount','{totals.grand ?? document.amount}',
            'account', jsonb_build_object('code','1000')
          )
        ),
        'successMessage','餐饮发票已自动记账，会議費 / 現金。'
      )
    )
  ),
  50
)
ON CONFLICT (company_code, rule_key) DO NOTHING;

-- HR/Payroll 配置实体（骨架表，后续由 schema 驱动）
CREATE TABLE IF NOT EXISTS employment_types (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  type_code TEXT GENERATED ALWAYS AS (payload->>'code') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
  is_active BOOLEAN GENERATED ALWAYS AS ((payload->>'isActive')='true') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_employment_types_company_code ON employment_types(company_code, type_code);
CREATE INDEX IF NOT EXISTS idx_employment_types_company_active ON employment_types(company_code, is_active);

CREATE TABLE IF NOT EXISTS agent_scenarios (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  scenario_key TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  instructions TEXT,
  metadata JSONB,
  tool_hints JSONB,
  context JSONB,
  priority INTEGER NOT NULL DEFAULT 100,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (company_code, scenario_key)
);
CREATE INDEX IF NOT EXISTS idx_agent_scenarios_company_active_priority ON agent_scenarios(company_code, is_active, priority, updated_at DESC);

ALTER TABLE agent_scenarios ADD COLUMN IF NOT EXISTS metadata JSONB DEFAULT '{}'::jsonb;
UPDATE agent_scenarios SET company_code = COALESCE(company_code, 'JP01');

INSERT INTO agent_scenarios (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
 ('JP01',
  'sales_order',
  '受注登録',
  '自然言語で受注情報を整理し、必要事項を確認して受注データを作成します。',
  '1. ユーザーの依頼から顧客・送付先・品目・数量・納期を整理する。\n2. 顧客や送付先は lookup_customer で確認し、候補が複数ある場合は request_clarification で選択してもらう。\n3. 品目や価格は lookup_material で確認し、単価が不明な場合はユーザーへ確認する。\n4. 情報が揃ったら create_sales_order を一度だけ呼び出し、結果の受注番号と納期をユーザーへ回答する。\n5. エラーが返った場合は不足項目を説明し、修正後に再実行する。',
  '{
    "matcher": {
      "appliesTo": "message",
      "messageContains": ["受注", "注文", "sales order", "受注書", "受注登録", "下单", "订单"],
      "always": false
    },
    "contextMessages": [
      {
        "role": "system",
        "content": {
          "ja": "ユーザーの自然言語指示から受注情報を整理し、顧客・送付先・品目・数量・納期を確認して不足があれば request_clarification で質問してください。",
          "zh": "请根据用户的自然语言指示整理受注信息，确认客户、收货地址、品目、数量和交期，如有缺失请使用 request_clarification 询问。"
        }
      },
      {
        "role": "system",
        "content": {
          "ja": "lookup_customer と lookup_material を活用してマスタ情報を取得し、準備が整ったら create_sales_order を一度だけ実行して受注を登録してください。",
          "zh": "使用 lookup_customer 与 lookup_material 查询主数据，信息齐全后调用 create_sales_order 完成受注登记。"
        }
      }
    ]
  }'::jsonb,
  '["lookup_customer","lookup_material","create_sales_order"]'::jsonb,
  NULL,
  40,
  TRUE,
  now()
 )
ON CONFLICT (company_code, scenario_key) DO NOTHING;

INSERT INTO agent_scenarios (
    company_code,
    scenario_key,
    title,
    description,
    instructions,
    metadata,
    tool_hints,
    context,
    priority,
    is_active,
    updated_at)
VALUES (
    'JP01',
    'moneytree.rule.design',
    'Moneytree 自動記帳ルール設計',
    'Moneytree 銀行入金明細を分析し、自動記帳・消込ルールを自然言語で登録します。',
    $$あなたは経理オペレーションの自動化アシスタントです。ユーザーの述べた要件に従い、Moneytree 銀行入金データ向けの自動記帳ルールを設計し register_moneytree_rule ツールで登録します。
1. まず入金パターンを確認し、どのような摘要・金額帯・通貨・取引口座・金融機関に適用するかを整理する。
2. matcher には description/accountName/accountNumber/bankName/amountRange/currency などの条件を JSON で記述し、将来の入金を一意に特定できるよう設計する。
3. action には借方・貸方の科目コード、摘要テンプレート、伝票日ロジック、必要であれば open item 清帳設定（例：targetType='receivable', partnerCode, tolerance）を含める。
4. ルールの優先度 priority を設定し、重複適用を避ける。重要なケースほど小さな数値を設定する。
5. 不明点があれば Clarification でユーザーに確認してからルールを登録する。十分な情報が揃わない場合はルール登録を避ける。
6. register_moneytree_rule の結果をユーザーに報告し、ルールの要約（条件・アクション）をメッセージで説明する。
7. 複数のルールを一括登録する場合は bulk_register_moneytree_rule ツールを利用する。$$,
    '{}'::jsonb,
    $$["register_moneytree_rule","request_clarification"]$$::jsonb,
    NULL,
    45,
    TRUE,
    now())
ON CONFLICT (company_code, scenario_key) DO UPDATE
SET title = EXCLUDED.title,
    description = EXCLUDED.description,
    instructions = EXCLUDED.instructions,
    metadata = EXCLUDED.metadata,
    tool_hints = EXCLUDED.tool_hints,
    context = EXCLUDED.context,
    priority = EXCLUDED.priority,
    is_active = EXCLUDED.is_active,
    updated_at = now();
-- 自動支払 FB ファイル管理
CREATE TABLE IF NOT EXISTS fb_payment_files (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  file_name TEXT NOT NULL,
  file_content TEXT,
  record_type TEXT NOT NULL DEFAULT '21',
  bank_code TEXT,
  bank_name TEXT,
  branch_code TEXT,
  branch_name TEXT,
  payment_date DATE NOT NULL,
  deposit_type TEXT,
  account_number TEXT,
  account_holder TEXT,
  total_count INTEGER NOT NULL DEFAULT 0,
  total_amount NUMERIC(18,2) NOT NULL DEFAULT 0,
  line_items JSONB,
  voucher_ids JSONB,
  status TEXT NOT NULL DEFAULT 'created',
  created_by TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_fb_payment_files_company ON fb_payment_files(company_code, payment_date DESC);
CREATE INDEX IF NOT EXISTS idx_fb_payment_files_status ON fb_payment_files(company_code, status);

-- ==============================================
-- 固定资产管理模块 - 数据库表结构
-- ==============================================

-- 不可变辅助函数（用于固定资产生成列）
CREATE OR REPLACE FUNCTION fn_jsonb_bool(p jsonb, key text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN p->>key = 'true' THEN true
    WHEN p->>key = 'false' THEN false
    ELSE NULL
  END;
$$;

CREATE OR REPLACE FUNCTION fn_jsonb_int(p jsonb, key text)
RETURNS integer
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p->>key) ~ '^[0-9]+$' THEN (p->>key)::integer
    ELSE NULL
  END;
$$;

CREATE OR REPLACE FUNCTION fn_jsonb_numeric_val(p jsonb, key text)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p->>key) ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (p->>key)::numeric
    ELSE 0
  END;
$$;

CREATE OR REPLACE FUNCTION fn_jsonb_date_val(p jsonb, key text)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
  SELECT CASE
    WHEN (p->>key) ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN (p->>key)::date
    ELSE NULL
  END;
$$;

-- 1. 资产类别表 (Asset Classes)
CREATE TABLE IF NOT EXISTS asset_classes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 生成列
  class_name TEXT GENERATED ALWAYS AS (payload->>'className') STORED,
  asset_type TEXT GENERATED ALWAYS AS (
    CASE WHEN (payload->>'isTangible') = 'true' THEN 'TANGIBLE' ELSE 'INTANGIBLE' END
  ) STORED,
  acquisition_account TEXT GENERATED ALWAYS AS (payload->>'acquisitionAccount') STORED,
  disposal_account TEXT GENERATED ALWAYS AS (payload->>'disposalAccount') STORED,
  depreciation_expense_account TEXT GENERATED ALWAYS AS (payload->>'depreciationExpenseAccount') STORED,
  accumulated_depreciation_account TEXT GENERATED ALWAYS AS (payload->>'accumulatedDepreciationAccount') STORED,
  include_tax_in_depreciation BOOLEAN GENERATED ALWAYS AS (
    COALESCE(fn_jsonb_bool(payload, 'includeTaxInDepreciation'), false)
  ) STORED
);

CREATE INDEX IF NOT EXISTS idx_asset_classes_company ON asset_classes(company_code);
CREATE INDEX IF NOT EXISTS idx_asset_classes_name ON asset_classes(company_code, class_name);

-- 2. 固定资产主表 (Fixed Assets)
CREATE TABLE IF NOT EXISTS fixed_assets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 生成列
  asset_no TEXT GENERATED ALWAYS AS (payload->>'assetNo') STORED,
  asset_class_id TEXT GENERATED ALWAYS AS (payload->>'assetClassId') STORED,
  asset_name TEXT GENERATED ALWAYS AS (payload->>'assetName') STORED,
  depreciation_method TEXT GENERATED ALWAYS AS (payload->>'depreciationMethod') STORED,
  useful_life INTEGER GENERATED ALWAYS AS (fn_jsonb_int(payload, 'usefulLife')) STORED,
  acquisition_cost NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric_val(payload, 'acquisitionCost')) STORED,
  book_value NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric_val(payload, 'bookValue')) STORED,
  capitalization_date DATE GENERATED ALWAYS AS (fn_jsonb_date_val(payload, 'capitalizationDate')) STORED,
  depreciation_start_date DATE GENERATED ALWAYS AS (fn_jsonb_date_val(payload, 'depreciationStartDate')) STORED
);

CREATE INDEX IF NOT EXISTS idx_fixed_assets_company ON fixed_assets(company_code);
CREATE UNIQUE INDEX IF NOT EXISTS uq_fixed_assets_asset_no ON fixed_assets(company_code, asset_no);
CREATE INDEX IF NOT EXISTS idx_fixed_assets_class ON fixed_assets(company_code, asset_class_id);
CREATE INDEX IF NOT EXISTS idx_fixed_assets_capitalization ON fixed_assets(company_code, capitalization_date);

-- 3. 资产交易表 (Asset Transactions)
CREATE TABLE IF NOT EXISTS asset_transactions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  asset_id UUID NOT NULL REFERENCES fixed_assets(id) ON DELETE CASCADE,
  payload JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- 生成列
  transaction_type TEXT GENERATED ALWAYS AS (payload->>'transactionType') STORED,
  posting_date DATE GENERATED ALWAYS AS (fn_jsonb_date_val(payload, 'postingDate')) STORED,
  amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric_val(payload, 'amount')) STORED,
  voucher_id TEXT GENERATED ALWAYS AS (payload->>'voucherId') STORED,
  voucher_no TEXT GENERATED ALWAYS AS (payload->>'voucherNo') STORED
);

CREATE INDEX IF NOT EXISTS idx_asset_transactions_company ON asset_transactions(company_code);
CREATE INDEX IF NOT EXISTS idx_asset_transactions_asset ON asset_transactions(asset_id);
CREATE INDEX IF NOT EXISTS idx_asset_transactions_type ON asset_transactions(company_code, transaction_type);
CREATE INDEX IF NOT EXISTS idx_asset_transactions_date ON asset_transactions(company_code, posting_date);
CREATE INDEX IF NOT EXISTS idx_asset_transactions_voucher ON asset_transactions(company_code, voucher_id);

-- 4. 折旧执行记录表 (Depreciation Runs)
CREATE TABLE IF NOT EXISTS depreciation_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  year_month TEXT NOT NULL,  -- 格式: YYYY-MM
  asset_count INTEGER NOT NULL DEFAULT 0,
  voucher_id UUID,
  voucher_no TEXT,
  executed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  executed_by TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_depreciation_runs_month ON depreciation_runs(company_code, year_month);
CREATE INDEX IF NOT EXISTS idx_depreciation_runs_company ON depreciation_runs(company_code, year_month);

-- 5. 资产编号序列表
CREATE TABLE IF NOT EXISTS asset_sequences (
  company_code TEXT NOT NULL,
  last_number INTEGER NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY(company_code)
);

-- ---------------------------------------
-- 数据库迁移脚本（PostgreSQL）：
-- - 启用 pgcrypto（用于 gen_random_uuid）
-- - 创建公司、结构定义、凭证与主数据表
-- - 关键字段采用“生成列”（方案B）+ 索引/唯一约束
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
  expected_amount NUMERIC(18,2) GENERATED ALWAYS AS ((payload->>'expectedAmount')::numeric) STORED,
  expected_close_date DATE GENERATED ALWAYS AS ((payload->>'expectedCloseDate')::date) STORED,
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
  amount_total NUMERIC(18,2) GENERATED ALWAYS AS ((payload->>'amountTotal')::numeric) STORED,
  valid_until DATE GENERATED ALWAYS AS ((payload->>'validUntil')::date) STORED,
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
  amount_total NUMERIC(18,2) GENERATED ALWAYS AS ((payload->>'amountTotal')::numeric) STORED,
  order_date DATE GENERATED ALWAYS AS ((payload->>'orderDate')::date) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED -- draft/confirmed/invoiced/cancelled
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_sos_company_no ON sales_orders(company_code, so_no);
CREATE INDEX IF NOT EXISTS idx_sos_company_partner ON sales_orders(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_sos_company_status ON sales_orders(company_code, status);

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
  due_date DATE GENERATED ALWAYS AS ((payload->>'dueDate')::date) STORED,
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
  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='scheduler_tasks' AND column_name='nl_spec') THEN
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
  timesheet_date DATE GENERATED ALWAYS AS ((payload->>'date')::date) STORED,
  month TEXT GENERATED ALWAYS AS (substr(((payload->>'date')::date)::text, 1, 7)) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED,
  project_code TEXT GENERATED ALWAYS AS (payload->>'projectCode') STORED,
  created_by TEXT GENERATED ALWAYS AS (payload->>'creatorUserId') STORED
);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_date ON timesheets(company_code, timesheet_date DESC);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_status ON timesheets(company_code, status);
CREATE INDEX IF NOT EXISTS idx_timesheets_company_creator ON timesheets(company_code, created_by);

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
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  bank_code TEXT GENERATED ALWAYS AS (payload->>'bankCode') STORED,
  name TEXT GENERATED ALWAYS AS (payload->>'name') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_banks_company_bank_code ON banks(company_code, bank_code);
CREATE INDEX IF NOT EXISTS idx_banks_company_name ON banks(company_code, name);

-- 支店
CREATE TABLE IF NOT EXISTS branches (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  bank_code TEXT GENERATED ALWAYS AS (payload->>'bankCode') STORED,
  branch_code TEXT GENERATED ALWAYS AS (payload->>'branchCode') STORED,
  branch_name TEXT GENERATED ALWAYS AS (payload->>'branchName') STORED
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_branches_company_bank_branch ON branches(company_code, bank_code, branch_code);
CREATE INDEX IF NOT EXISTS idx_branches_company_branch_name ON branches(company_code, branch_name);

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
        THEN (payload->>'periodStart')::date
      ELSE NULL
    END
  ) STORED,
  period_end DATE GENERATED ALWAYS AS (
    CASE
      WHEN (payload ? 'periodEnd') AND (payload->>'periodEnd') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
        THEN (payload->>'periodEnd')::date
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
                      schema,
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
    schema = jsonb_set(
                  jsonb_set(
                    jsonb_set(schema,
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
    schema = jsonb_set(
                  schema,
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
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_sessions_company_user ON ai_sessions(company_code, user_id);

CREATE TABLE IF NOT EXISTS ai_messages (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL,
  role TEXT NOT NULL, -- 'user' | 'assistant' | 'system' | 'tool'
  content TEXT,
  payload JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_messages_session ON ai_messages(session_id, created_at);

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
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, employee_code)
);

CREATE TABLE IF NOT EXISTS roles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  role_code TEXT NOT NULL,
  role_name TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(company_code, role_code)
);

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

-- 种子：voucher/businesspartner 的结构定义（可在应用内编辑/版本化）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
 (NULL,'voucher', 1, TRUE,
 '{"type":"object","properties":{"header":{"type":"object","properties":{"companyCode":{"type":"string"},"postingDate":{"type":"string","format":"date"},"voucherType":{"type":"string","enum":["GL","AP","AR","AA","SA","IN","OT"]},"voucherNo":{"type":"string"},"summary":{"type":"string"}},"required":["companyCode","postingDate","voucherType"]},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"accountCode":{"type":"string"},"debit":{"type":"integer"},"credit":{"type":"integer"},"vendorId":{"type":["string","null"]},"customerId":{"type":["string","null"]},"departmentId":{"type":["string","null"]},"employeeId":{"type":["string","null"]},"tax":{"type":["object","null"],"properties":{"rate":{"type":"number"},"amount":{"type":"integer"}}}},"required":["lineNo","accountCode","debit","credit"]}}},"required":["header","lines"]}',
 '{"list":{"columns":["posting_date","voucher_type","voucher_no"]},"form":{"layout":[]}}',
 '{"filters":["posting_date","voucher_type","voucher_no","lines[].employeeId"],"sorts":["posting_date","voucher_no"]}',
 '{"coreFields":[{"name":"posting_date","path":"header.postingDate","type":"date","index":{"strategy":"generated_column","unique":false}},{"name":"voucher_type","path":"header.voucherType","type":"string","index":{"strategy":"generated_column","unique":false}},{"name":"voucher_no","path":"header.voucherNo","type":"string","index":{"strategy":"generated_column","unique":true,"scope":["company_code"]}}]}',
 '["voucher_balance_check"]',
 '{"strategy":"yymm6","targetPath":"header.voucherNo"}',
 '{"displayNames":{"ja":"仕訳","zh":"会计凭证","en":"Voucher"},"synonyms":["凭证","仕訳","会计分录","journal"],"typeMap":{"工资凭证":"SA","給与仕訳":"SA","入金":"IN","出金":"OT"}}'
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Schema: 通知规则运行记录（只读列表）
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'notification_rule_run', 1, TRUE,
  '{"type":"object","properties":{"company_code":{"type":"string"},"policy_id":{"type":"string"},"rule_key":{"type":"string"},"last_run_at":{"type":"string","format":"date-time"}},"required":[]}',
  '{"list":{"columns":["company_code","policy_id","rule_key","last_run_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"company_code","label":"公司","span":8,"props":{"readonly":true}},{"field":"policy_id","label":"策略ID","span":8,"props":{"readonly":true}},{"field":"rule_key","label":"规则键","span":8,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"last_run_at","label":"上次运行","span":8,"props":{"readonly":true}}]}]}}',
  '{"filters":["company_code","policy_id","rule_key","last_run_at"],"sorts":["last_run_at"]}',
  '{"coreFields":[]}'
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Schema: 通知发送日志（只读列表）
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'notification_log', 1, TRUE,
  '{"type":"object","properties":{"company_code":{"type":"string"},"policy_id":{"type":["string","null"]},"rule_key":{"type":["string","null"]},"user_id":{"type":"string"},"related_entity":{"type":["string","null"]},"related_id":{"type":["string","null"]},"sent_at":{"type":"string","format":"date-time"},"sent_day":{"type":"string","format":"date"}},"required":[]}',
  '{"list":{"columns":["sent_at","policy_id","rule_key","user_id","related_entity","related_id"]},"form":{"layout":[{"type":"grid","cols":[{"field":"sent_at","label":"发送时间","span":8,"props":{"readonly":true}},{"field":"sent_day","label":"发送日","span":6,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"policy_id","label":"策略ID","span":8,"props":{"readonly":true}},{"field":"rule_key","label":"规则键","span":8,"props":{"readonly":true}},{"field":"user_id","label":"用户ID","span":8,"props":{"readonly":true}}]},{"type":"grid","cols":[{"field":"related_entity","label":"对象","span":8,"props":{"readonly":true}},{"field":"related_id","label":"对象ID","span":16,"props":{"readonly":true}}]}]}}',
  '{"filters":["sent_day","policy_id","rule_key","user_id","related_entity"],"sorts":["sent_at"]}',
  '{"coreFields":[]}'
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: CRM schemas（contact/deal/quote/sales_order/activity）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'contact', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"partnerCode":{"type":"string"},"email":{"type":["string","null"],"format":"email"},"mobile":{"type":["string","null"]},"language":{"type":["string","null"],"enum":["zh","ja","en",null]},"status":{"type":["string","null"],"enum":["active","inactive",null]}},"required":["name","partnerCode"]}',
  '{"list":{"columns":["contact_code","name","partner_code","email","status"]},"form":{"layout":[]}}',
  '{"filters":["contact_code","name","partner_code","email","status"],"sorts":["name"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'deal', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"partnerCode":{"type":"string"},"ownerUserId":{"type":["string","null"]},"stage":{"type":"string","enum":["prospect","proposal","won","invoiced"]},"expectedAmount":{"type":["number","null"]},"expectedCloseDate":{"type":["string","null"],"format":"date"},"source":{"type":["string","null"],"enum":["referral","website","expo",null]},"status":{"type":["string","null"]}},"required":["code","partnerCode","stage"]}',
  '{"list":{"columns":["deal_code","partner_code","stage","expected_amount","expected_close_date","source"]},"form":{"layout":[]}}',
  '{"filters":["deal_code","partner_code","stage","owner_user_id","expected_close_date","source"],"sorts":["expected_close_date"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'quote', 1, TRUE,
  '{"type":"object","properties":{"quoteNo":{"type":"string"},"partnerCode":{"type":"string"},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"item":{"type":"string"},"qty":{"type":"number"},"uom":{"type":["string","null"]},"price":{"type":"number"},"amount":{"type":"number"}},"required":["lineNo","item","qty","price"]}},"amountTotal":{"type":"number"},"validUntil":{"type":["string","null"],"format":"date"},"status":{"type":"string","enum":["draft","sent","accepted","rejected"]}},"required":["quoteNo","partnerCode","amountTotal","status"]}',
  '{"list":{"columns":["quote_no","partner_code","amount_total","valid_until","status"]},"form":{"layout":[]}}',
  '{"filters":["quote_no","partner_code","status"],"sorts":["quote_no"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'sales_order', 1, TRUE,
  '{"type":"object","properties":{"soNo":{"type":"string"},"partnerCode":{"type":"string"},"orderDate":{"type":"string","format":"date"},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"item":{"type":"string"},"qty":{"type":"number"},"uom":{"type":["string","null"]},"price":{"type":"number"},"amount":{"type":"number"}},"required":["lineNo","item","qty","price"]}},"amountTotal":{"type":"number"},"status":{"type":"string","enum":["draft","confirmed","invoiced","cancelled"]}},"required":["soNo","partnerCode","orderDate","amountTotal","status"]}',
  '{"list":{"columns":["so_no","partner_code","amount_total","order_date","status"]},"form":{"layout":[]}}',
  '{"filters":["so_no","partner_code","status","order_date"],"sorts":["order_date"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'activity', 1, TRUE,
  '{"type":"object","properties":{"type":{"type":"string","enum":["email","phone","meeting","task"]},"subject":{"type":["string","null"]},"partnerCode":{"type":["string","null"]},"contactCode":{"type":["string","null"]},"ownerUserId":{"type":["string","null"]},"dueDate":{"type":["string","null"],"format":"date"},"remindAt":{"type":["number","null"]},"content":{"type":["string","null"]},"status":{"type":["string","null"],"enum":["open","done","cancelled",null]}},"required":["type"]}',
  '{"list":{"columns":["activity_type","subject","partner_code","contact_code","due_date","status"]},"form":{"layout":[]}}',
  '{"filters":["activity_type","due_date","owner_user_id","partner_code","status"],"sorts":["due_date"]}',
  '{"coreFields":[]}'
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
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'approval_task', 1, TRUE,
  '{"type":"object","properties":{}}',
  '{"list":{"columns":["entity","step_no","step_name","status","created_at"]},"form":{"layout":[]}}',
  '{"filters":["approver_user_id","status","entity","created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'certificate_request', 1, TRUE,
  '{"type":"object","properties":{"employeeId":{"type":"string"},"type":{"type":"string"},"language":{"type":"string"},"purpose":{"type":"string"},"toEmail":{"type":"string"},"subject":{"type":"string"},"bodyText":{"type":"string"},"status":{"type":"string"}},"required":["employeeId","type"]}',
  '{"list":{"columns":["created_at","status"]},"form":{"layout":[]}}',
  '{"filters":["status","created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'company_setting', 1, TRUE,
  '{"type":"object","properties":{"companyName":{"type":"string"},"companyAddress":{"type":"string"},"companyRep":{"type":"string"},"workdayDefaultStart":{"type":"string","pattern":"^\\\\d{2}:\\\\d{2}$"},"workdayDefaultEnd":{"type":"string","pattern":"^\\\\d{2}:\\\\d{2}$"},"lunchMinutes":{"type":"number","minimum":0,"maximum":240}},"required":[]}',
  '{"list":{"columns":["created_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"companyName","label":"公司名称","span":12},{"field":"companyAddress","label":"公司地址","span":12},{"field":"companyRep","label":"代表者","span":6},{"field":"workdayDefaultStart","label":"上班(HH:mm)","span":6},{"field":"workdayDefaultEnd","label":"下班(HH:mm)","span":6},{"field":"lunchMinutes","label":"午休(分钟)","span":6,"props":{"type":"number"}}]}]}}',
  '{"filters":["created_at"],"sorts":["created_at"]}',
  '{"coreFields":[]}'
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
  mfg_date DATE GENERATED ALWAYS AS ((payload->>'mfgDate')::date) STORED,
  exp_date DATE GENERATED ALWAYS AS ((payload->>'expDate')::date) STORED
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
  movement_date DATE GENERATED ALWAYS AS ((payload->>'movementDate')::date) STORED,
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
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'material', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"baseUom":{"type":"string"},"batchManagement":{"type":"boolean"},"spec":{"type":"string"},"description":{"type":"string"}}}',
  '{"list":{"columns":["material_code","name","base_uom","is_batch_mgmt"]},"form":{"layout":[{"type":"grid","cols":[{"field":"code","label":"编码","span":6},{"field":"name","label":"名称","span":10},{"field":"baseUom","label":"基本单位","span":4},{"field":"batchManagement","label":"批次管理","widget":"switch","span":4}]},{"type":"grid","cols":[{"field":"spec","label":"规格型号","span":12},{"field":"description","label":"描述","widget":"textarea","props":{"type":"textarea","rows":3},"span":24}]}]}}',
  '{"filters":["material_code","name","is_batch_mgmt"],"sorts":["material_code","name"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'warehouse', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["warehouse_code","name"]}}',
  '{"filters":["warehouse_code","name"],"sorts":["warehouse_code","name"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'bin', 1, TRUE,
  '{"type":"object","properties":{"warehouseCode":{"type":"string"},"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["warehouse_code","bin_code","name"]}}',
  '{"filters":["warehouse_code","bin_code"],"sorts":["warehouse_code","bin_code"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'stock_status', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"}}}',
  '{"list":{"columns":["status_code","name"]}}',
  '{"filters":["status_code","name"],"sorts":["status_code","name"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'batch', 1, TRUE,
  '{"type":"object","properties":{"materialCode":{"type":"string"},"batchNo":{"type":"string"},"mfgDate":{"type":"string","format":"date"},"expDate":{"type":"string","format":"date"}}}',
  '{"list":{"columns":["material_code","batch_no","mfg_date","exp_date"]}}',
  '{"filters":["material_code","batch_no"],"sorts":["material_code","batch_no"]}',
  '{"coreFields":[]}'
 ),
 (NULL,'inventory_movement', 1, TRUE,
  '{"type":"object","properties":{"movementType":{"type":"string","enum":["IN","OUT","TRANSFER"]},"movementDate":{"type":"string","format":"date"},"fromWarehouse":{"type":["string","null"]},"fromBin":{"type":["string","null"]},"toWarehouse":{"type":["string","null"]},"toBin":{"type":["string","null"]},"referenceNo":{"type":["string","null"]},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"materialCode":{"type":"string"},"quantity":{"type":"number"},"uom":{"type":"string"},"batchNo":{"type":["string","null"]},"statusCode":{"type":["string","null"]}}}}}}',
  '{"list":{"columns":["movement_date","movement_type","reference_no"]}}',
  '{"filters":["movement_date","movement_type","reference_no"],"sorts":["movement_date"]}',
  '{"coreFields":[]}'
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'businesspartner', 1, TRUE,
 '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"flags":{"type":"object","properties":{"customer":{"type":"boolean"},"vendor":{"type":"boolean"}}},"status":{"type":"string"}},"required":["code","name"]}',
 '{"list":{"columns":["partner_code","name","flag_customer","flag_vendor","status"]},"form":{"layout":[]}}',
 '{"filters":["partner_code","name","flag_customer","flag_vendor","status"],"sorts":["partner_code","name"]}',
 '{"coreFields":[{"name":"partner_code","path":"code","type":"string","index":{"strategy":"generated_column","unique":true,"scope":["company_code"]}},{"name":"name","path":"name","type":"string","index":{"strategy":"generated_column"}},{"name":"flag_customer","path":"flags.customer","type":"boolean","index":{"strategy":"generated_column"}},{"name":"flag_vendor","path":"flags.vendor","type":"boolean","index":{"strategy":"generated_column"}},{"name":"status","path":"status","type":"string","index":{"strategy":"generated_column"}}]}'
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: HR/Payroll 基础 schema（最小骨架）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'employment_type', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"isActive":{"type":"boolean"}},"required":["code","name"]}',
  '{"list":{"columns":["type_code","name","is_active"]},"form":{"layout":[]}}',
  '{"filters":["type_code","name","is_active"],"sorts":["type_code","name"]}',
  '{"coreFields":[]}'
 ),
 -- payroll_item schema 移除
 (NULL,'payroll_policy', 1, TRUE,
  '{"type":"object","properties":{"code":{"type":"string"},"name":{"type":"string"},"rules":{"type":"array"}},"required":["code","name"]}',
  '{"list":{"columns":["policy_code","name"]},"form":{"layout":[]}}',
  '{"filters":["policy_code","name"],"sorts":["policy_code","name"]}',
  '{"coreFields":[]}'
 )
ON CONFLICT (company_code, name, version) DO NOTHING;

-- Seed: openitem schema（用于通用搜索 DSL，便于前端查询未清项）
INSERT INTO schemas(company_code,name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'openitem', 1, TRUE,
  '{"type":"object","properties":{}}',
  '{"list":{"columns":["doc_date","partner_id","account_code","currency","original_amount","residual_amount"]},"form":{"layout":[]}}',
  '{"filters":["doc_date","partner_id","account_code","currency","original_amount","residual_amount"],"sorts":["doc_date","residual_amount"]}',
  '{"coreFields":[]}'
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
  sent_day DATE GENERATED ALWAYS AS (date_trunc('day', sent_at)::date) STORED
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
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields)
VALUES
 (NULL,'notification_policy', 1, TRUE,
  '{"type":"object","properties":{"name":{"type":"string"},"nl":{"type":["string","null"]},"compiled":{"type":"object"},"isActive":{"type":"boolean"}},"required":["name"]}',
  '{"list":{"columns":["name","is_active","updated_at"]},"form":{"layout":[{"type":"grid","cols":[{"field":"name","label":"策略名称","span":12},{"field":"isActive","label":"启用","widget":"switch","span":4}]},{"type":"grid","cols":[{"field":"nl","label":"自然语言描述","span":24,"props":{"type":"textarea","rows":3}},{"field":"compiled","label":"编译结果(JSON)","span":24,"props":{"type":"json"}}]}]}}',
  '{"filters":["name","is_active"],"sorts":["updated_at"]}',
  '{"coreFields":[]}'
 )
ON CONFLICT (company_code, name, version) DO NOTHING;
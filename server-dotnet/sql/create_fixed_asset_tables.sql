-- ==============================================
-- 固定资产管理模块 - 数据库表结构
-- ==============================================

-- 不可变辅助函数（用于生成列）
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

-- 注释
COMMENT ON TABLE asset_classes IS '资产类别主数据：定义资产科目映射';
COMMENT ON TABLE fixed_assets IS '固定资产主数据：资产基本信息和折旧参数';
COMMENT ON TABLE asset_transactions IS '资产交易记录：取得、折旧、除却等';
COMMENT ON TABLE depreciation_runs IS '折旧执行记录：按月执行的折旧批处理';
COMMENT ON TABLE asset_sequences IS '资产编号序列：自动生成资产编号';


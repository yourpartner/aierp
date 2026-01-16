-- 消費税申告書テーブル
-- Phase 1: データベース構造

-- ============================================
-- 1. 消費税申告書テーブル
-- ============================================
CREATE TABLE IF NOT EXISTS consumption_tax_returns (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    fiscal_year VARCHAR(7) NOT NULL,         -- '2024-03' (事業年度終了月)
    period_type VARCHAR(20) NOT NULL,        -- 'annual', 'interim_q1', 'interim_q2', 'interim_q3', 'interim_m1'~'interim_m11'
    status VARCHAR(20) DEFAULT 'draft',      -- draft, calculated, submitted, accepted
    taxation_method VARCHAR(20) NOT NULL,    -- general, simplified, special_20pct
    
    -- 計算結果 (JSON)
    calculation JSONB NOT NULL DEFAULT '{}'::jsonb,
    
    -- 申告書データ (PDF生成用)
    form_data JSONB,
    
    -- 監査
    created_at TIMESTAMPTZ DEFAULT now(),
    created_by UUID,
    created_by_name TEXT,
    updated_at TIMESTAMPTZ DEFAULT now(),
    updated_by UUID,
    submitted_at TIMESTAMPTZ,
    submitted_by UUID,
    
    CONSTRAINT uk_ctx_return UNIQUE (company_code, fiscal_year, period_type)
);

CREATE INDEX IF NOT EXISTS idx_ctx_returns_company ON consumption_tax_returns(company_code);
CREATE INDEX IF NOT EXISTS idx_ctx_returns_fiscal_year ON consumption_tax_returns(company_code, fiscal_year);
CREATE INDEX IF NOT EXISTS idx_ctx_returns_status ON consumption_tax_returns(company_code, status);

-- ============================================
-- 2. 科目の消費税区分を拡張
-- 現在: NON_TAX, INPUT_TAX, OUTPUT_TAX, TAX_ACCOUNT
-- 追加: TAXABLE_10, TAXABLE_8, EXEMPT, EXPORT, INPUT_10, INPUT_8
-- ============================================

-- accountスキーマの taxType enum を更新
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,taxType,enum}',
    '["NON_TAX", "INPUT_TAX", "OUTPUT_TAX", "TAX_ACCOUNT", "TAXABLE_10", "TAXABLE_8", "EXEMPT", "EXPORT", "INPUT_10", "INPUT_8"]'::jsonb
),
    version = version + 1,
    updated_at = now()
WHERE name = 'account' AND is_active = true;

-- ============================================
-- 3. 消費税用科目を追加（サンプル）
-- ============================================

-- 売上科目（課税10%）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "4100",
    "name": "売上高",
    "category": "PL",
    "accountType": "revenue",
    "taxType": "TAXABLE_10",
    "taxCategory": "sales",
    "description": "課税売上（標準税率10%）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 売上科目（課税8%・軽減税率）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "4110",
    "name": "売上高（軽減税率）",
    "category": "PL",
    "accountType": "revenue",
    "taxType": "TAXABLE_8",
    "taxCategory": "sales",
    "description": "課税売上（軽減税率8%）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 輸出売上（免税）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "4200",
    "name": "輸出売上高",
    "category": "PL",
    "accountType": "revenue",
    "taxType": "EXPORT",
    "taxCategory": "sales",
    "description": "輸出免税売上"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 非課税売上
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "4300",
    "name": "非課税売上高",
    "category": "PL",
    "accountType": "revenue",
    "taxType": "EXEMPT",
    "taxCategory": "sales",
    "description": "非課税売上（土地譲渡等）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 仕入科目（課税10%）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "5100",
    "name": "仕入高",
    "category": "PL",
    "accountType": "expense",
    "taxType": "INPUT_10",
    "taxCategory": "purchase",
    "description": "課税仕入（標準税率10%）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 仕入科目（課税8%）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "5110",
    "name": "仕入高（軽減税率）",
    "category": "PL",
    "accountType": "expense",
    "taxType": "INPUT_8",
    "taxCategory": "purchase",
    "description": "課税仕入（軽減税率8%）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 経費科目（課税10%）
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "6100",
    "name": "消耗品費",
    "category": "PL",
    "accountType": "expense",
    "taxType": "INPUT_10",
    "taxCategory": "purchase",
    "description": "課税仕入（標準税率10%）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 仮払消費税
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "1890",
    "name": "仮払消費税",
    "category": "BS",
    "accountType": "asset",
    "taxType": "TAX_ACCOUNT",
    "description": "仮払消費税（進項税額）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 仮受消費税
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "2890",
    "name": "仮受消費税",
    "category": "BS",
    "accountType": "liability",
    "taxType": "TAX_ACCOUNT",
    "description": "仮受消費税（売上税額）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 未払消費税
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "2891",
    "name": "未払消費税",
    "category": "BS",
    "accountType": "liability",
    "taxType": "TAX_ACCOUNT",
    "description": "未払消費税（納付予定額）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- ============================================
-- 4. 消費税申告書スキーマ
-- ============================================
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'consumption_tax_return',
  1,
  TRUE,
  '{
    "type": "object",
    "properties": {
      "fiscalYear": {"type": "string", "description": "事業年度終了月 YYYY-MM"},
      "periodType": {"type": "string", "enum": ["annual", "interim_q1", "interim_q2", "interim_q3"]},
      "taxationMethod": {"type": "string", "enum": ["general", "simplified", "special_20pct"]},
      "status": {"type": "string", "enum": ["draft", "calculated", "submitted", "accepted"]},
      "calculation": {"type": "object"},
      "formData": {"type": "object"}
    },
    "required": ["fiscalYear", "periodType", "taxationMethod"]
  }'::jsonb,
  '{
    "list": {"columns": ["fiscal_year", "period_type", "status", "taxation_method"]},
    "form": {"layout": []}
  }'::jsonb,
  '{"filters": ["fiscal_year", "status"], "sorts": ["fiscal_year"]}'::jsonb,
  NULL,
  NULL,
  NULL,
  '{"displayNames": {"ja": "消費税申告書", "zh": "消费税申报表", "en": "Consumption Tax Return"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- ============================================
-- 5. 权限能力（Capabilities）
-- ============================================
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES 
('consumption_tax:read', '{"ja":"消費税申告書参照","zh":"查看消费税申报","en":"View Consumption Tax Returns"}', 'finance', 'action', false,
 '{"ja":"消費税申告書を閲覧する権限","zh":"允许查看消费税申报","en":"Permission to view consumption tax returns"}', 40),
('consumption_tax:manage', '{"ja":"消費税申告書管理","zh":"消费税申报管理","en":"Consumption Tax Management"}', 'finance', 'action', false,
 '{"ja":"消費税申告書を作成・編集する権限","zh":"允许创建和编辑消费税申报","en":"Permission to create and edit consumption tax returns"}', 41)
ON CONFLICT (cap_code) DO NOTHING;

-- ============================================
-- 6. 菜单定义（permission_menus）
-- ============================================
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES 
('finance', 'fin.consumptionTax', '{"ja":"消費税申告書","zh":"消费税申报表","en":"Consumption Tax Return"}', '/financial/consumption-tax', ARRAY['consumption_tax:read'], 15)
ON CONFLICT (menu_key) DO NOTHING;

SELECT 'consumption_tax_returns table and related objects created successfully' as result;


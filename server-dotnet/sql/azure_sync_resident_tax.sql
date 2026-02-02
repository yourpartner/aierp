-- 1. 创建住民税明细表
CREATE TABLE IF NOT EXISTS resident_tax_schedules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    employee_id UUID NOT NULL,
    fiscal_year INTEGER NOT NULL,
    municipality_code TEXT,
    municipality_name TEXT,
    annual_amount NUMERIC(10,0) NOT NULL DEFAULT 0,
    june_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    july_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    august_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    september_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    october_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    november_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    december_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    january_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    february_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    march_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    april_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    may_amount NUMERIC(8,0) NOT NULL DEFAULT 0,
    status TEXT DEFAULT 'active',
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(company_code, employee_id, fiscal_year)
);

CREATE INDEX IF NOT EXISTS idx_resident_tax_company_year ON resident_tax_schedules(company_code, fiscal_year);
CREATE INDEX IF NOT EXISTS idx_resident_tax_employee ON resident_tax_schedules(company_code, employee_id);
CREATE INDEX IF NOT EXISTS idx_resident_tax_status ON resident_tax_schedules(company_code, status);

-- 2. 创建会计科目（住民税預り金）
INSERT INTO accounts (company_code, payload)
SELECT 'JP01', 
'{
  "code": "3185",
  "name": "住民税預り金",
  "isBank": false,
  "isCash": false,
  "taxType": "NON_TAX",
  "category": "BS",
  "openItem": true,
  "createdAt": "2026-01-27T00:00:00+00:00",
  "createdBy": "system",
  "updatedAt": "2026-01-27T00:00:00+00:00",
  "updatedBy": "system",
  "fieldRules": {
    "assetId": "hidden",
    "vendorId": "hidden",
    "customerId": "hidden",
    "employeeId": "required",
    "paymentDate": "hidden",
    "departmentId": "hidden"
  },
  "fsBalanceGroup": "BS-L-1",
  "openItemBaseline": "EMPLOYEE"
}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code = 'JP01' AND (payload->>'code') = '3185');

-- 3. 在权限系统中注册“住民税管理”菜单
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order, is_active)
VALUES (
    'hr', 
    'hr.resident_tax', 
    '{"ja": "住民税管理", "en": "Resident Tax", "zh": "住民税管理"}', 
    '/hr/resident-tax', 
    ARRAY['payroll:resident_tax'], 
    214, 
    true
)
ON CONFLICT (menu_key) DO UPDATE SET 
    menu_name = EXCLUDED.menu_name,
    menu_path = EXCLUDED.menu_path,
    display_order = EXCLUDED.display_order;

-- 4. 更新 Policy 的 DSL 规则和自然语言描述
UPDATE payroll_policies
SET payload = jsonb_set(
    jsonb_set(
        jsonb_set(
            payload,
            '{dsl,rules,RESIDENT_TAX}',
            '{"item": "RESIDENT_TAX", "type": "deduction", "formula": {"residentTax": {"source": "db"}}}'::jsonb
        ),
        '{dsl,payrollItems}',
        payload->'dsl'->'payrollItems' || '{"RESIDENT_TAX": "住民税"}'::jsonb
    ),
    '{nlText}',
    to_jsonb(payload->>'nlText' || E'\n住民税：从住民税管理数据中根据当前月份查询扣除金额。')
)
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 5. 添加会计凭证规则（DSL 级别）
UPDATE payroll_policies
SET payload = jsonb_set(
    payload,
    '{dsl,journalRules}',
    payload->'dsl'->'journalRules' || '[{
        "name": "residentTax",
        "items": [{"code": "RESIDENT_TAX"}],
        "description": "未払費用／住民税預り金",
        "debitAccount": "315",
        "creditAccount": "3185"
    }]'::jsonb
)
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 6. 同步到根级别
UPDATE payroll_policies
SET payload = payload || jsonb_build_object('journalRules', payload->'dsl'->'journalRules')
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 1. 为 permission_menus 添加 menu_key 唯一约束
ALTER TABLE permission_menus ADD CONSTRAINT permission_menus_menu_key_key UNIQUE (menu_key);

-- 2. 重新注册“住民税管理”菜单
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

-- 3. 修复 Policy 中的 RESIDENT_TAX 规则添加方式 (使用 || 而非 jsonb_set 针对不存在的 key)
UPDATE payroll_policies
SET payload = jsonb_set(
    jsonb_set(
        payload,
        '{dsl,rules}',
        (payload->'dsl'->'rules') || '{"RESIDENT_TAX": {"item": "RESIDENT_TAX", "type": "deduction", "formula": {"residentTax": {"source": "db"}}}}'::jsonb
    ),
    '{dsl,payrollItems}',
    (payload->'dsl'->'payrollItems') || '{"RESIDENT_TAX": "住民税"}'::jsonb
)
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 4. 再次确保 journalRules 同步
UPDATE payroll_policies
SET payload = payload || jsonb_build_object('journalRules', payload->'dsl'->'journalRules')
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 1. 彻底修复 Policy 结构：将 RESIDENT_TAX 正确插入 rules 数组和 payrollItems 数组
UPDATE payroll_policies
SET payload = jsonb_set(
    jsonb_set(
        jsonb_set(
            payload,
            '{dsl,rules}',
            (payload->'dsl'->'rules') || '[{
                "item": "RESIDENT_TAX",
                "type": "deduction",
                "formula": {"residentTax": {"source": "db"}},
                "rounding": {"method": "round_half_down", "precision": 0}
            }]'::jsonb
        ),
        '{dsl,payrollItems}',
        -- 过滤掉之前错误的项，重新添加正确的对象格式
        (
            SELECT jsonb_agg(item)
            FROM jsonb_array_elements(payload->'dsl'->'payrollItems') AS item
            WHERE NOT (item ? 'RESIDENT_TAX')
        ) || '[{"code": "RESIDENT_TAX", "kind": "deduction", "name": "住民税", "isActive": true}]'::jsonb
    ),
    '{nlText}',
    to_jsonb(payload->>'nlText' || E'\n住民税：从住民税管理数据中根据当前月份查询扣除金额。')
)
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 2. 确保 journalRules 包含住民税且同步到根级别
UPDATE payroll_policies
SET payload = payload || jsonb_build_object('journalRules', payload->'dsl'->'journalRules')
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

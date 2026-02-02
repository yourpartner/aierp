-- 彻底清理并重新构建 Policy 结构，确保没有任何残留的错误项
UPDATE payroll_policies
SET payload = jsonb_set(
    jsonb_set(
        jsonb_set(
            payload,
            '{dsl,rules}',
            (
                SELECT jsonb_agg(rule)
                FROM jsonb_array_elements(payload->'dsl'->'rules') AS rule
                WHERE NOT (rule ? 'RESIDENT_TAX') AND (rule->>'item' IS NULL OR rule->>'item' != 'RESIDENT_TAX')
            ) || '[{
                "item": "RESIDENT_TAX",
                "type": "deduction",
                "formula": {"residentTax": {"source": "db"}},
                "rounding": {"method": "round_half_down", "precision": 0}
            }]'::jsonb
        ),
        '{dsl,payrollItems}',
        (
            SELECT jsonb_agg(item)
            FROM jsonb_array_elements(payload->'dsl'->'payrollItems') AS item
            WHERE NOT (item ? 'RESIDENT_TAX') AND (item->>'code' IS NULL OR item->>'code' != 'RESIDENT_TAX')
        ) || '[{"code": "RESIDENT_TAX", "kind": "deduction", "name": "住民税", "isActive": true}]'::jsonb
    ),
    '{nlText}',
    to_jsonb(
        CASE 
            WHEN payload->>'nlText' LIKE '%住民税：从住民税管理数据中根据当前月份查询扣除金额。%' THEN payload->>'nlText'
            ELSE payload->>'nlText' || E'\n住民税：从住民税管理数据中根据当前月份查询扣除金额。'
        END
    )
)
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

-- 同步到根级别
UPDATE payroll_policies
SET payload = payload || jsonb_build_object('journalRules', payload->'dsl'->'journalRules')
WHERE company_code = 'JP01' AND (payload->>'isActive')::boolean = true;

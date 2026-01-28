-- 介护保险部署脚本
-- 执行前请确认连接到正确的数据库

-- 1. 添加介护保险费率（如果不存在）
INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
SELECT NULL, 'care', '東京都:care', NULL, NULL, 0.0080, '2025-03-01', NULL, 'JP-CARE-2025-03', 'Kaigo Hoken employee share - age 40-64'
WHERE NOT EXISTS (
    SELECT 1 FROM law_rates WHERE kind = 'care' AND key = '東京都:care'
);

-- 2. 删除旧的有 journalRules 的 policy（会导致介护保险不出现在会计凭证中）
-- 只删除 company_code='JP01' 且有 journalRules 但 is_active=false 的 policy
DELETE FROM payroll_policies 
WHERE company_code = 'JP01' 
  AND is_active = false 
  AND payload ? 'journalRules' 
  AND jsonb_array_length(payload->'journalRules') > 0;

-- 3. 确保活动 policy 的 rules 中包含 CARE_INS（如果缺失则添加）
UPDATE payroll_policies
SET payload = jsonb_set(
    payload,
    '{rules}',
    (payload->'rules') || '[{
        "item": "CARE_INS",
        "type": "deduction",
        "formula": {
            "rate": "policy.law.care.rate",
            "_base": {
                "charRef": "employee.baseSalaryMonth"
            }
        },
        "rounding": {
            "method": "round_half_down",
            "precision": 0
        },
        "activation": {
            "salaryDescriptionNotContains": [
                "時給",
                "时薪",
                "hourly",
                "時間給"
            ]
        }
    }]'::jsonb
)
WHERE company_code = 'JP01'
  AND is_active = true
  AND (payload::text NOT LIKE '%CARE_INS%');

-- 4. 验证结果
SELECT 'law_rates - care' as check_type, count(*) as count FROM law_rates WHERE kind = 'care';
SELECT 'policies with journalRules' as check_type, count(*) as count FROM payroll_policies WHERE company_code = 'JP01' AND payload ? 'journalRules';
SELECT 'CARE_INS in active policy' as check_type, 
       CASE WHEN payload::text LIKE '%CARE_INS%' THEN 'YES' ELSE 'NO' END as has_care_ins
FROM payroll_policies 
WHERE company_code = 'JP01' AND is_active = true;

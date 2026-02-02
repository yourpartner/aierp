-- 更新活跃的 payroll policy 添加住民税支持
-- 1. 在 rules 和 dsl.rules 中添加 RESIDENT_TAX 规则
-- 2. 在 payrollItems 中添加 RESIDENT_TAX 项目
-- 3. 在 journalRules 中添加 residentTax 会计规则
-- 4. 在 nlText 中添加住民税描述

UPDATE payroll_policies
SET payload = payload
  -- 添加 RESIDENT_TAX 规则到 rules 数组
  || jsonb_build_object('rules', 
       (payload->'rules') || '[{
         "item": "RESIDENT_TAX",
         "type": "deduction",
         "formula": {"residentTax": {"source": "db"}},
         "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}
       }]'::jsonb
     )
  -- 添加 RESIDENT_TAX 规则到 dsl.rules 数组
  || jsonb_build_object('dsl',
       payload->'dsl' || jsonb_build_object('rules',
         (payload->'dsl'->'rules') || '[{
           "item": "RESIDENT_TAX",
           "type": "deduction",
           "formula": {"residentTax": {"source": "db"}},
           "activation": {"salaryDescriptionNotContains": ["時給", "时薪", "hourly", "時間給"]}
         }]'::jsonb
       ) || jsonb_build_object('payrollItems',
         (payload->'dsl'->'payrollItems') || '[{
           "code": "RESIDENT_TAX",
           "kind": "deduction",
           "name": "住民税",
           "isActive": true
         }]'::jsonb
       ) || jsonb_build_object('journalRules',
         (payload->'dsl'->'journalRules') || '[{
           "name": "residentTax",
           "items": [{"code": "RESIDENT_TAX"}],
           "description": "未払費用／住民税預り金",
           "debitAccount": "315",
           "creditAccount": "3185"
         }]'::jsonb
       ) || jsonb_build_object('hints',
         (payload->'dsl'->'hints') || '["已生成住民税计算规则：从住民税管理数据中根据当前月份查询扣除金额。"]'::jsonb
       )
     )
WHERE company_code = 'JP01' 
  AND (payload->>'isActive')::boolean = true
  AND NOT EXISTS (
    SELECT 1 FROM jsonb_array_elements(payload->'rules') AS r 
    WHERE r->>'item' = 'RESIDENT_TAX'
  );

-- 更新 nlText 添加住民税描述
UPDATE payroll_policies
SET payload = jsonb_set(
  payload,
  '{nlText}',
  to_jsonb(
    (payload->>'nlText') || E'\n\n・住民税（item: RESIDENT_TAX）：住民税管理で登録された特別徴収税額から、当月分の控除額を自動取得する。住民税の年度は6月から翌年5月まで。会計処理は未払費用／住民税預り金で仕訳する。'
  )
)
WHERE company_code = 'JP01' 
  AND (payload->>'isActive')::boolean = true
  AND (payload->>'nlText') NOT LIKE '%住民税%';

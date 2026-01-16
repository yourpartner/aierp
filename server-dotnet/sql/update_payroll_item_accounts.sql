-- 更新 payroll_items 表中的会计科目编码
-- 将临时编码（5100, 5200, 5300）更新为新系统正确的科目编码

-- 根据旧系统 ITBANK (公司172) 的 salaryitems 配置:
-- BaseSalary: 借方832(給与手当), 贷方315(未払費用)
-- HealthInsurance: 借方315(未払費用), 贷方318 → 新系统3181(社会保険預り金)
-- EndowInsurance: 借方315(未払費用), 贷方318 → 新系统3182(厚生年金預り金)
-- HireInsurance: 借方315(未払費用), 贷方318 → 新系统3183(雇用保険預り金)
-- IncomeTax: 借方315(未払費用), 贷方318 → 新系统3184(源泉所得税預り金)

-- 新系统科目表 (JP01):
-- 832 - 給与手当
-- 315 - 未払費用
-- 3181 - 社会保険預り金 (健康保険 + 介護保険)
-- 3182 - 厚生年金預り金
-- 3183 - 雇用保険預り金
-- 3184 - 源泉所得税預り金

-- 查看当前 payroll_items 的科目设置
SELECT 
  payload->>'code' as code,
  payload->>'name' as name,
  payload->>'kind' as kind,
  payload->>'accountCode' as current_account_code
FROM payroll_items 
WHERE company_code = 'JP01';

-- 更新基本工资类项目的科目 (5100 → 832)
UPDATE payroll_items 
SET payload = jsonb_set(payload, '{accountCode}', '"832"')
WHERE company_code = 'JP01' 
  AND payload->>'accountCode' = '5100';

-- 更新社会保险类项目的科目 (5200 → 3181)
UPDATE payroll_items 
SET payload = jsonb_set(payload, '{accountCode}', '"3181"')
WHERE company_code = 'JP01' 
  AND payload->>'accountCode' = '5200';

-- 更新厚生年金类项目的科目 (5300 → 3182)
UPDATE payroll_items 
SET payload = jsonb_set(payload, '{accountCode}', '"3182"')
WHERE company_code = 'JP01' 
  AND payload->>'accountCode' = '5300';

-- 如果有其他需要更新的项目，可以按需添加:
-- 雇用保险 → 3183
-- 源泉所得税 → 3184

-- 验证更新结果
SELECT 
  payload->>'code' as code,
  payload->>'name' as name,
  payload->>'kind' as kind,
  payload->>'accountCode' as updated_account_code
FROM payroll_items 
WHERE company_code = 'JP01';


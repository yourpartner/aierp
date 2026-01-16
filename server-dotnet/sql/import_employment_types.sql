-- 导入旧系统雇佣区分数据到新系统
-- 只导入新系统需要的字段：code, name, isActive, isContractor

-- 删除现有数据（如果有的话）
DELETE FROM employment_types WHERE company_code = 'JP01';

-- 插入雇佣区分数据
-- 来源：旧系统 employeetypes 表 (CompanyID=1)
INSERT INTO employment_types (company_code, payload) VALUES
-- 正社員系列
('JP01', '{"code": "FT_INTERNAL", "name": "正社員（社内勤務）", "isActive": true, "isContractor": false}'),
('JP01', '{"code": "FT_ENGINEER", "name": "正社員（技術者）", "isActive": true, "isContractor": false}'),
('JP01', '{"code": "FT_TEMP", "name": "正社員（一時用）", "isActive": true, "isContractor": false}'),

-- 契約社員
('JP01', '{"code": "CT", "name": "契約社員", "isActive": true, "isContractor": false}'),

-- 役員
('JP01', '{"code": "EXEC", "name": "役員", "isActive": true, "isContractor": false}'),

-- 個人事業主（isContractor = true）
('JP01', '{"code": "CONTRACTOR", "name": "個人事業主", "isActive": true, "isContractor": true}'),

-- アルバイト系列
('JP01', '{"code": "PT", "name": "アルバイト", "isActive": true, "isContractor": false}'),
('JP01', '{"code": "PT_TEMP", "name": "アルバイト（一時）", "isActive": true, "isContractor": false}');

-- 验证导入结果
SELECT 
  payload->>'code' as code,
  payload->>'name' as name,
  payload->>'isContractor' as is_contractor,
  payload->>'isActive' as is_active
FROM employment_types 
WHERE company_code = 'JP01'
ORDER BY 
  CASE 
    WHEN payload->>'code' LIKE 'FT%' THEN 1
    WHEN payload->>'code' = 'CT' THEN 2
    WHEN payload->>'code' = 'EXEC' THEN 3
    WHEN payload->>'code' = 'CONTRACTOR' THEN 4
    WHEN payload->>'code' LIKE 'PT%' THEN 5
    ELSE 6
  END;


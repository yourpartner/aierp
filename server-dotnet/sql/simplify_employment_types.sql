-- 简化雇佣区分

-- 删除不需要的类型
DELETE FROM employment_types 
WHERE company_code = 'JP01' 
AND payload->>'code' IN ('FT_INTERNAL', 'FT_ENGINEER', 'FT_TEMP', 'PT_TEMP');

-- 添加简化的正社員
INSERT INTO employment_types (company_code, payload) 
VALUES ('JP01', '{"code": "FT", "name": "正社員", "isActive": true, "isContractor": false}');

-- 查看结果
SELECT payload->>'code' as code, payload->>'name' as name, payload->>'isContractor' as is_contractor
FROM employment_types WHERE company_code = 'JP01' ORDER BY code;


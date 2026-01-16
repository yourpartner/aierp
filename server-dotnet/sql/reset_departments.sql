-- 删除现有部门并创建新部门
DELETE FROM departments WHERE company_code = 'JP01';

INSERT INTO departments (id, company_code, payload) VALUES 
(gen_random_uuid(), 'JP01', '{"code": "D001", "name": "AI開発", "path": "D001", "level": 1}'),
(gen_random_uuid(), 'JP01', '{"code": "D002", "name": "ホテル運営", "path": "D002", "level": 1}'),
(gen_random_uuid(), 'JP01', '{"code": "D003", "name": "バックオフィス", "path": "D003", "level": 1}');

SELECT department_code, payload->>'name' as name, payload->>'level' as level 
FROM departments 
WHERE company_code = 'JP01' 
ORDER BY department_code;


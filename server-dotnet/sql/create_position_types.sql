-- 插入初始职务数据
INSERT INTO position_types (company_code, payload) VALUES
('JP01', '{"name": "社長", "level": 1, "isActive": true}'),
('JP01', '{"name": "部長", "level": 2, "isActive": true}'),
('JP01', '{"name": "課長", "level": 3, "isActive": true}'),
('JP01', '{"name": "主任", "level": 4, "isActive": true}'),
('JP01', '{"name": "店員", "level": 5, "isActive": true}');

SELECT payload->>'name' as name, payload->>'level' as level FROM position_types WHERE company_code = 'JP01';


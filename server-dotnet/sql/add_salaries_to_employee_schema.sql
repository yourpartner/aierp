-- 在 employee schema 中添加 salaries 字段
UPDATE schemas 
SET schema = jsonb_set(
  schema::jsonb, 
  '{properties,salaries}', 
  '{"type": "array", "items": {"type": "object", "properties": {"startDate": {"type": "string", "format": "date"}, "description": {"type": "string", "maxLength": 500}}}}'::jsonb
)
WHERE name = 'employee' AND company_code = 'JP01';

-- 验证更新结果
SELECT schema->'properties'->'salaries' as salaries_schema
FROM schemas 
WHERE name = 'employee' AND company_code = 'JP01';


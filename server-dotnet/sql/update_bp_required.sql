-- 更新取引先 schema 的必填字段
-- code 由后端自动生成，不再是必填
-- 只有 name 是必填字段（shortName 已移除，paymentTerms 为可选）

UPDATE schemas 
SET schema = jsonb_set(schema, '{required}', '["name"]'::jsonb),
    updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 显示更新结果
SELECT name, schema->'required' as required_fields FROM schemas WHERE name = 'businesspartner' AND is_active = true;


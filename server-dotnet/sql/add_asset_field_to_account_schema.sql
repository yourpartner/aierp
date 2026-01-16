-- 为 account schema 的 fieldRules 添加 assetId 字段
-- 用于控制固定资产入力

-- 1. 更新 schema 定义，添加 assetId 字段
UPDATE schemas
SET schema = jsonb_set(
  schema,
  '{properties,fieldRules,properties,assetId}',
  '{"type": "string", "enum": ["required", "optional", "hidden"]}'::jsonb
),
    version = version + 1,
    updated_at = now()
WHERE name = 'account' AND is_active = true;

-- 显示更新结果
SELECT name, version, is_active, 
       schema->'properties'->'fieldRules'->'properties' as field_rules
FROM schemas 
WHERE name = 'account' AND is_active = true;

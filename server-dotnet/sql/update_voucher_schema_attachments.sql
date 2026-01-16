-- 更新 voucher schema，添加 attachments 字段
-- 执行此脚本前请备份数据库

UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,attachments}',
    '{
        "type": "array",
        "description": "添付ファイル一覧",
        "items": {
            "type": "object",
            "properties": {
                "id": { "type": "string", "description": "ファイルID" },
                "name": { "type": "string", "description": "ファイル名" },
                "contentType": { "type": "string", "description": "MIMEタイプ" },
                "size": { "type": "integer", "description": "ファイルサイズ（バイト）" },
                "blobName": { "type": "string", "description": "Azure Blob名" },
                "url": { "type": "string", "description": "アクセスURL" },
                "uploadedAt": { "type": "string", "format": "date-time", "description": "アップロード日時" }
            },
            "required": ["id", "name"]
        }
    }'::jsonb,
    true
),
updated_at = NOW()
WHERE name = 'voucher' AND is_active = true;

-- 验证更新
SELECT id, name, schema->'properties'->'attachments' as attachments_schema
FROM schemas
WHERE name = 'voucher' AND is_active = true;

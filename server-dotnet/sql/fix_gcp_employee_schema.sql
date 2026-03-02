-- GCP 公司没有 employee schema 会导致照会接口返回 "schema not found"。
-- 从 JP01 或全局(company_code IS NULL) 复制 employee schema 到 GCP。
-- Azure schemas 表结构: id, company_code, name, schema, ui, query, core_fields, validators, numbering, ai_hints, created_at, updated_at（无 version/is_active）
INSERT INTO schemas (company_code, name, schema, ui, query, core_fields, validators, numbering, ai_hints, created_at, updated_at)
SELECT 'GCP', s.name, s.schema, s.ui, s.query, s.core_fields, s.validators, s.numbering, s.ai_hints, NOW(), NOW()
FROM schemas s
WHERE s.name = 'employee'
  AND (s.company_code = 'JP01' OR s.company_code IS NULL)
  AND NOT EXISTS (SELECT 1 FROM schemas WHERE company_code = 'GCP' AND name = 'employee')
ORDER BY s.company_code NULLS LAST
LIMIT 1;

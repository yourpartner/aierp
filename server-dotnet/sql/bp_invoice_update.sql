
-- ============================================
-- businesspartner schema: Invoice registration fields
-- ============================================

-- 1. Add invoiceRegistrationNumber to schema properties
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationNumber}',
    '{"type": ["string", "null"], "maxLength": 14}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 2. Add invoiceRegistrationStartDate to schema properties
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationStartDate}',
    '{"type": ["string", "null"], "format": "date"}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 3. Update UI configuration with invoice fields
UPDATE schemas
SET ui = jsonb_set(
    COALESCE(ui, '{}'::jsonb),
    '{form,layout}',
    (
        SELECT jsonb_agg(
            CASE 
                WHEN elem->>'label' = 'インボイス制度' THEN elem
                WHEN elem->'cols' @> '[{"field": "flags.customer"}]'::jsonb THEN 
                    jsonb_build_object(
                        'type', 'grid',
                        'cols', (elem->'cols') || '[{"field": "invoiceRegistrationNumber", "label": "インボイス登録番号", "span": 12, "props": {"placeholder": "T1234567890123", "maxlength": 14}}, {"field": "invoiceRegistrationStartDate", "label": "登録番号生効日", "span": 12, "widget": "date"}]'::jsonb
                    )
                ELSE elem
            END
        )
        FROM jsonb_array_elements(COALESCE(ui->'form'->'layout', '[]'::jsonb)) elem
    ),
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true
  AND NOT (ui->'form'->'layout')::text LIKE '%invoiceRegistrationNumber%';


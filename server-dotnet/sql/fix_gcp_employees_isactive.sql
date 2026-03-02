-- GCP 社員の payload.isActive を true に設定（在籍表示用）
UPDATE employees
SET payload = jsonb_set(
  COALESCE(payload, '{}'::jsonb),
  '{isActive}',
  'true'::jsonb,
  true
)
WHERE company_code = 'GCP'
  AND (payload->>'isActive' IS NULL OR payload->>'isActive' = 'false');

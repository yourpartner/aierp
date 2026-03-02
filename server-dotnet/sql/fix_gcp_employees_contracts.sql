-- GCP 社員にデフォルトの雇佣期間（契約）を1件追加。在籍は雇佣期間で判定するため、contracts が空だと退職になる。
-- 1件のみ追加し、既に contracts がある場合は変更しない。
UPDATE employees
SET payload = jsonb_set(
  payload,
  '{contracts}',
  '[{"employmentTypeCode":"正社員","periodFrom":"2024-01-01","periodTo":"9999-12-31","note":""}]'::jsonb
)
WHERE company_code = 'GCP'
  AND (payload->'contracts' IS NULL OR jsonb_array_length(COALESCE(payload->'contracts', '[]'::jsonb)) = 0);

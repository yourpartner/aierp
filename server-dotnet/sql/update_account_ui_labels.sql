-- 更新 account schema UI 的分组标题为日语
UPDATE schemas 
SET ui = jsonb_set(
  jsonb_set(ui, '{form,layout,1,title}', '"入力フィールド状態制御"'),
  '{form,layout,2,title}', '"銀行 / 現金"'
)
WHERE name = 'account' AND company_code = 'JP01';

-- 也更新全局 schema (如果存在)
UPDATE schemas 
SET ui = jsonb_set(
  jsonb_set(ui, '{form,layout,1,title}', '"入力フィールド状態制御"'),
  '{form,layout,2,title}', '"銀行 / 現金"'
)
WHERE name = 'account' AND company_code IS NULL;


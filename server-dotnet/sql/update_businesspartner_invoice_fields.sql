-- ============================================
-- 取引先にインボイス登録番号フィールドを追加
-- 手動実行用SQL
-- ============================================

-- 1. schema に invoiceRegistrationNumber を追加
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationNumber}',
    '{"type":["string","null"],"maxLength":14}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 2. schema に invoiceRegistrationStartDate を追加
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationStartDate}',
    '{"type":["string","null"],"format":"date"}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 3. UI設定を更新（インボイス制度セクションを追加）
-- 現在のUI設定を確認
SELECT ui FROM schemas WHERE name = 'businesspartner' AND is_active = true;

-- UI layoutにインボイスフィールドを追加
-- 既存のlayoutの適切な位置に以下のセクションを挿入する必要があります
-- {
--   "type": "divider",
--   "label": "インボイス制度"
-- },
-- {
--   "type": "grid",
--   "cols": [
--     {"field": "invoiceRegistrationNumber", "label": "インボイス登録番号", "span": 12, "props": {"placeholder": "T1234567890123", "maxlength": 14}},
--     {"field": "invoiceRegistrationStartDate", "label": "登録番号生効日", "span": 12, "widget": "date", "props": {"placeholder": "2023-10-01"}}
--   ]
-- }


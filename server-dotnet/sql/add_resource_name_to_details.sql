-- resource_name 列追加: 手入力の要員名を保持するため
-- resource_id が NULL でも resource_name で要員を表示できる

ALTER TABLE stf_juchuu_detail  ADD COLUMN IF NOT EXISTS resource_name TEXT;
ALTER TABLE stf_hatchuu_detail ADD COLUMN IF NOT EXISTS resource_name TEXT;

-- 既存データ: resource_id がある行に display_name を埋める
UPDATE stf_juchuu_detail d
   SET resource_name = r.payload->>'display_name'
  FROM stf_resources r
 WHERE d.resource_id = r.id
   AND d.resource_name IS NULL;

UPDATE stf_hatchuu_detail d
   SET resource_name = r.payload->>'display_name'
  FROM stf_resources r
 WHERE d.resource_id = r.id
   AND d.resource_name IS NULL;

-- SEHO のみ：既存凭证から70件をランダムに選び、
-- 記帳日を 2026-01-01～2026-02-15 のランダムに、ログイン日(created_at/updated_at)を記帳日の数日前にランダムで更新。他社・他データに影響なし。
DO $$
DECLARE
  r RECORD;
  new_posting_date DATE;
  days_before INT;
  new_created_at TIMESTAMPTZ;
  cnt INT := 0;
BEGIN
  FOR r IN (
    SELECT id, payload
    FROM vouchers
    WHERE company_code = 'SEHO'
    ORDER BY random()
    LIMIT 70
  )
  LOOP
    -- 記帳日: 2026-01-01 ～ 2026-02-15（46日間のランダム）
    new_posting_date := '2026-01-01'::date + (random() * 45)::int;
    -- ログイン日は記帳日の 1～7 日前をランダム
    days_before := 1 + (random() * 6)::int;
    new_created_at := (new_posting_date - days_before)::date + (random() * interval '1 day');

    UPDATE vouchers
    SET
      payload = jsonb_set(payload, '{header,postingDate}', to_jsonb(new_posting_date::text)),
      created_at = new_created_at,
      updated_at = new_created_at
    WHERE id = r.id AND company_code = 'SEHO';

    cnt := cnt + 1;
  END LOOP;
  RAISE NOTICE 'Updated % vouchers', cnt;
END $$;

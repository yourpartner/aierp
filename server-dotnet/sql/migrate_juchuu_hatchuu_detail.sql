-- =============================================================
-- 受注明細(stf_juchuu_detail) / 発注明細(stf_hatchuu_detail) 追加
-- リソース・料金条件をヘッダーから明細テーブルへ移行
-- =============================================================

-- 1. 受注明細テーブル
CREATE TABLE IF NOT EXISTS stf_juchuu_detail (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code            TEXT NOT NULL,
    juchuu_id               UUID NOT NULL REFERENCES stf_juchuu(id) ON DELETE CASCADE,
    resource_id             UUID,                        -- リソース（stf_resources.id）
    -- 請求条件（リソースごと）
    billing_rate            NUMERIC(12,2),
    billing_rate_type       TEXT DEFAULT 'monthly',      -- monthly / daily / hourly
    overtime_rate_multiplier NUMERIC(4,2) DEFAULT 1.25,
    settlement_type         TEXT DEFAULT 'range',        -- range / fixed
    settlement_lower_h      NUMERIC(6,2) DEFAULT 140,
    settlement_upper_h      NUMERIC(6,2) DEFAULT 180,
    notes                   TEXT,
    sort_order              INT DEFAULT 0,
    created_at              TIMESTAMPTZ DEFAULT now(),
    updated_at              TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_stf_juchuu_detail_juchuu   ON stf_juchuu_detail(juchuu_id);
CREATE INDEX IF NOT EXISTS idx_stf_juchuu_detail_resource ON stf_juchuu_detail(resource_id);

-- 2. 既存データを明細へ移行（resource_id が設定されている行のみ）
INSERT INTO stf_juchuu_detail (
    company_code, juchuu_id, resource_id,
    billing_rate, billing_rate_type, overtime_rate_multiplier,
    settlement_type, settlement_lower_h, settlement_upper_h
)
SELECT
    company_code, id, resource_id,
    billing_rate, billing_rate_type, overtime_rate_multiplier,
    settlement_type, settlement_lower_h, settlement_upper_h
FROM stf_juchuu
WHERE resource_id IS NOT NULL
ON CONFLICT DO NOTHING;

-- 3. ヘッダーから resource/rate 列を削除
ALTER TABLE stf_juchuu
    DROP COLUMN IF EXISTS resource_id,
    DROP COLUMN IF EXISTS billing_rate,
    DROP COLUMN IF EXISTS billing_rate_type,
    DROP COLUMN IF EXISTS overtime_rate_multiplier,
    DROP COLUMN IF EXISTS settlement_type,
    DROP COLUMN IF EXISTS settlement_lower_h,
    DROP COLUMN IF EXISTS settlement_upper_h;

-- 4. 発注明細テーブル
CREATE TABLE IF NOT EXISTS stf_hatchuu_detail (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code            TEXT NOT NULL,
    hatchuu_id              UUID NOT NULL REFERENCES stf_hatchuu(id) ON DELETE CASCADE,
    resource_id             UUID,                        -- リソース（stf_resources.id）
    -- 原価条件（リソースごと）
    cost_rate               NUMERIC(12,2),
    cost_rate_type          TEXT DEFAULT 'monthly',      -- monthly / daily / hourly
    settlement_type         TEXT DEFAULT 'range',
    settlement_lower_h      NUMERIC(6,2) DEFAULT 140,
    settlement_upper_h      NUMERIC(6,2) DEFAULT 180,
    notes                   TEXT,
    sort_order              INT DEFAULT 0,
    created_at              TIMESTAMPTZ DEFAULT now(),
    updated_at              TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_detail_hatchuu  ON stf_hatchuu_detail(hatchuu_id);
CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_detail_resource ON stf_hatchuu_detail(resource_id);

-- 5. 既存データを明細へ移行
INSERT INTO stf_hatchuu_detail (
    company_code, hatchuu_id, resource_id,
    cost_rate, cost_rate_type,
    settlement_type, settlement_lower_h, settlement_upper_h
)
SELECT
    company_code, id, resource_id,
    cost_rate, cost_rate_type,
    settlement_type, settlement_lower_h, settlement_upper_h
FROM stf_hatchuu
WHERE resource_id IS NOT NULL
ON CONFLICT DO NOTHING;

-- 6. ヘッダーから resource/rate 列を削除
ALTER TABLE stf_hatchuu
    DROP COLUMN IF EXISTS resource_id,
    DROP COLUMN IF EXISTS cost_rate,
    DROP COLUMN IF EXISTS cost_rate_type,
    DROP COLUMN IF EXISTS settlement_type,
    DROP COLUMN IF EXISTS settlement_lower_h,
    DROP COLUMN IF EXISTS settlement_upper_h;

-- =============================================================
-- 受注(juchuu) / 発注(hatchuu) テーブル移行スクリプト
-- 既存の stf_contracts データを削除し、新テーブルを作成
-- =============================================================

-- 1. 既存の契約データのみを削除（他のデータに絶対影響しない）
DELETE FROM stf_contracts;

-- 2. 受注シーケンス
CREATE SEQUENCE IF NOT EXISTS seq_juchuu_no START 1;

-- 3. 発注シーケンス
CREATE SEQUENCE IF NOT EXISTS seq_hatchuu_no START 1;

-- 4. 受注テーブル（顧客→自社）
CREATE TABLE IF NOT EXISTS stf_juchuu (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code            TEXT NOT NULL,
    juchuu_no               TEXT,                      -- 受注番号 JU-YYYY-NNNN
    client_partner_id       UUID,                      -- 顧客（businesspartners.id）
    project_id              UUID,                      -- 関連案件（stf_projects.id）
    resource_id             UUID,                      -- 稼働リソース（stf_resources.id）
    contract_type           TEXT DEFAULT 'ses',        -- ses / dispatch / contract
    status                  TEXT DEFAULT 'draft',      -- draft / active / ended / terminated
    start_date              DATE,
    end_date                DATE,
    -- 請求条件
    billing_rate            NUMERIC(12,2),
    billing_rate_type       TEXT DEFAULT 'monthly',    -- monthly / daily / hourly
    overtime_rate_multiplier NUMERIC(4,2) DEFAULT 1.25,
    settlement_type         TEXT DEFAULT 'range',      -- range / fixed
    settlement_lower_h      NUMERIC(6,2) DEFAULT 140,
    settlement_upper_h      NUMERIC(6,2) DEFAULT 180,
    -- 勤務条件
    work_location           TEXT,
    work_days               TEXT,
    work_start_time         TEXT,
    work_end_time           TEXT,
    monthly_work_hours      NUMERIC(6,2) DEFAULT 160,
    -- 注文書PDF
    attached_doc_url        TEXT,                      -- Azureストレージ上のURL
    ocr_raw_text            TEXT,                      -- OCR抽出テキスト
    notes                   TEXT,
    created_at              TIMESTAMPTZ DEFAULT now(),
    updated_at              TIMESTAMPTZ DEFAULT now(),
    payload                 JSONB DEFAULT '{}'::jsonb  -- 追加フィールド用
);

CREATE INDEX IF NOT EXISTS idx_stf_juchuu_company ON stf_juchuu(company_code);
CREATE INDEX IF NOT EXISTS idx_stf_juchuu_client ON stf_juchuu(client_partner_id);
CREATE INDEX IF NOT EXISTS idx_stf_juchuu_status ON stf_juchuu(status);
CREATE INDEX IF NOT EXISTS idx_stf_juchuu_dates ON stf_juchuu(start_date, end_date);

-- 5. 発注テーブル（自社→リソース/BP会社）
CREATE TABLE IF NOT EXISTS stf_hatchuu (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code            TEXT NOT NULL,
    hatchuu_no              TEXT,                      -- 発注番号 HT-YYYY-NNNN
    juchuu_id               UUID REFERENCES stf_juchuu(id) ON DELETE SET NULL,  -- 受注との紐付け
    resource_id             UUID,                      -- リソース（stf_resources.id）
    supplier_partner_id     UUID,                      -- BP会社（businesspartners.id）
    contract_type           TEXT DEFAULT 'ses',        -- ses / dispatch / contract
    status                  TEXT DEFAULT 'draft',      -- draft / active / ended / terminated
    start_date              DATE,
    end_date                DATE,
    -- 原価条件
    cost_rate               NUMERIC(12,2),
    cost_rate_type          TEXT DEFAULT 'monthly',    -- monthly / daily / hourly
    settlement_type         TEXT DEFAULT 'range',
    settlement_lower_h      NUMERIC(6,2) DEFAULT 140,
    settlement_upper_h      NUMERIC(6,2) DEFAULT 180,
    -- 勤務条件（発注書に記載）
    work_location           TEXT,
    work_days               TEXT,
    work_start_time         TEXT,
    work_end_time           TEXT,
    monthly_work_hours      NUMERIC(6,2) DEFAULT 160,
    -- 発注書PDF
    doc_generated_url       TEXT,                      -- 生成した発注書PDFのURL
    doc_generated_at        TIMESTAMPTZ,
    notes                   TEXT,
    created_at              TIMESTAMPTZ DEFAULT now(),
    updated_at              TIMESTAMPTZ DEFAULT now(),
    payload                 JSONB DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_company ON stf_hatchuu(company_code);
CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_juchuu ON stf_hatchuu(juchuu_id);
CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_resource ON stf_hatchuu(resource_id);
CREATE INDEX IF NOT EXISTS idx_stf_hatchuu_status ON stf_hatchuu(status);

-- 6. permission_caps / permission_menus の更新
-- 既存の staffing:contract:* 権限を juchuu / hatchuu に置き換え

-- 既存契約権限を削除
DELETE FROM permission_caps WHERE cap_code LIKE 'staffing:contract:%';
DELETE FROM permission_menus WHERE menu_path LIKE '/staffing/contracts%';

-- 受注権限
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order) VALUES
('staffing:juchuu:read',   '{"ja":"受注参照","zh":"查看受注","en":"View Orders"}',      'staffing', 'action', false, '{"ja":"受注一覧・詳細の閲覧","zh":"查看受注列表","en":"View received orders"}', 20),
('staffing:juchuu:write',  '{"ja":"受注編集","zh":"编辑受注","en":"Edit Orders"}',      'staffing', 'action', false, '{"ja":"受注の作成・編集","zh":"创建和编辑受注","en":"Create and edit received orders"}', 21),
('staffing:hatchuu:read',  '{"ja":"発注参照","zh":"查看发注","en":"View Purchase Orders"}', 'staffing', 'action', false, '{"ja":"発注一覧・詳細の閲覧","zh":"查看发注列表","en":"View purchase orders"}', 22),
('staffing:hatchuu:write', '{"ja":"発注編集","zh":"编辑发注","en":"Edit Purchase Orders"}', 'staffing', 'action', false, '{"ja":"発注の作成・編集・PDF生成","zh":"创建编辑发注和生成PDF","en":"Create, edit purchase orders and generate PDFs"}', 23)
ON CONFLICT (cap_code) DO UPDATE SET
  cap_name = EXCLUDED.cap_name,
  module_code = EXCLUDED.module_code,
  cap_type = EXCLUDED.cap_type,
  display_order = EXCLUDED.display_order;

-- 受注メニュー
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) VALUES
('staffing', 'staffing.juchuu',  '{"ja":"受注一覧","zh":"受注列表","en":"Received Orders"}',  '/staffing/juchuu',  ARRAY['staffing:juchuu:read'],  32),
('staffing', 'staffing.hatchuu', '{"ja":"発注一覧","zh":"发注列表","en":"Purchase Orders"}',  '/staffing/hatchuu', ARRAY['staffing:hatchuu:read'], 33)
ON CONFLICT (menu_key) DO UPDATE SET
  module_code = EXCLUDED.module_code,
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 管理者ロールに権限付与
INSERT INTO role_caps (role_id, cap)
SELECT r.id, c.cap_code
FROM roles r
CROSS JOIN permission_caps c
WHERE r.company_code = 'JP01'
  AND r.role_code IN ('admin', 'ADMIN')
  AND c.cap_code IN ('staffing:juchuu:read','staffing:juchuu:write','staffing:hatchuu:read','staffing:hatchuu:write')
ON CONFLICT DO NOTHING;

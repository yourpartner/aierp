-- 创建库存科目用于测试（商品）
-- 条件: BS科目 + 资产类 + 非清账管理 + 非课税
-- account_code 是生成列，从 payload->>'code' 自动生成

INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "1400",
    "name": "商品",
    "category": "BS",
    "accountType": "asset",
    "openItem": false,
    "openItemBaseline": null,
    "taxType": "NON_TAXABLE",
    "description": "商品在庫"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 1401 原材料
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "1401",
    "name": "原材料",
    "category": "BS",
    "accountType": "asset",
    "openItem": false,
    "openItemBaseline": null,
    "taxType": "NON_TAXABLE",
    "description": "原材料在庫"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- 1402 仕掛品
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "1402",
    "name": "仕掛品",
    "category": "BS",
    "accountType": "asset",
    "openItem": false,
    "openItemBaseline": null,
    "taxType": "NON_TAXABLE",
    "description": "仕掛品在庫"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;

-- =============================================
-- 買掛金科目（贷方）用于测试
-- 条件: BS负债类 + 清账基准为供应商 + 非课税
-- =============================================

-- 2100 買掛金
INSERT INTO accounts (company_code, payload)
VALUES (
  'JP01',
  '{
    "code": "2100",
    "name": "買掛金",
    "category": "BS",
    "accountType": "liability",
    "openItem": true,
    "openItemBaseline": "VENDOR",
    "taxType": "NON_TAXABLE",
    "description": "買掛金（仕入先清算）"
  }'::jsonb
)
ON CONFLICT (company_code, account_code) DO UPDATE SET
  payload = EXCLUDED.payload;


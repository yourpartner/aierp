-- FOS01 追加科目（仕訳リスト xls に出現する未登録科目）
-- 他社データへの影響なし (全て company_code='FOS01')
BEGIN;

INSERT INTO accounts (company_code, payload) VALUES
  ('FOS01', '{"code": "135", "name": "みずほ銀行", "category": "BS", "openItem": false, "isBank": true,  "isCash": false}'::jsonb),
  ('FOS01', '{"code": "136", "name": "PayPay難波",  "category": "BS", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "153", "name": "売掛金",       "category": "BS", "openItem": true,  "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "186", "name": "仮勘定",       "category": "BS", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "320", "name": "楽天未払金",   "category": "BS", "openItem": true,  "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "714", "name": "国内仕入高",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "715", "name": "仕入諸非課",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "866", "name": "事務代行費",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "867", "name": "販売手数料",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "880", "name": "交通費非課",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb),
  ('FOS01', '{"code": "881", "name": "法定福負担",   "category": "PL", "openItem": false, "isBank": false, "isCash": false}'::jsonb)
ON CONFLICT DO NOTHING;

COMMIT;

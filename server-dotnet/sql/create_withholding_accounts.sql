-- 创建预扣明细科目 (account_code is generated from payload->>'code')
INSERT INTO accounts (company_code, payload) VALUES
('JP01', '{"code": "3181", "name": "社会保険預り金", "category": "BS", "type": "LIABILITY", "normalBalance": "CR", "openItem": false}'::jsonb),
('JP01', '{"code": "3182", "name": "厚生年金預り金", "category": "BS", "type": "LIABILITY", "normalBalance": "CR", "openItem": false}'::jsonb),
('JP01', '{"code": "3183", "name": "雇用保険預り金", "category": "BS", "type": "LIABILITY", "normalBalance": "CR", "openItem": false}'::jsonb),
('JP01', '{"code": "3184", "name": "源泉所得税預り金", "category": "BS", "type": "LIABILITY", "normalBalance": "CR", "openItem": false}'::jsonb)
ON CONFLICT (company_code, account_code) DO UPDATE SET payload = EXCLUDED.payload;


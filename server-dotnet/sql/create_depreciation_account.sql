-- 创建減価償却費科目
INSERT INTO accounts(company_code, payload) 
VALUES ('1000', '{"code": "845", "name": "減価償却費", "category": "PL", "openItem": false}')
ON CONFLICT DO NOTHING;


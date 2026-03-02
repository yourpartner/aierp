-- SEHO 凭证中引用但 accounts 中不存在的科目：补建（0, 1 → 科目未決；331 → 推断为负债）
INSERT INTO accounts (company_code, payload)
SELECT 'SEHO', '{"code":"0","name":"科目未決","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code = 'SEHO' AND (payload->>'code') = '0');

INSERT INTO accounts (company_code, payload)
SELECT 'SEHO', '{"code":"1","name":"科目未決","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code = 'SEHO' AND (payload->>'code') = '1');

INSERT INTO accounts (company_code, payload)
SELECT 'SEHO', '{"code":"331","name":"未払税金等","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code = 'SEHO' AND (payload->>'code') = '331');

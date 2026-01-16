-- 检查有isBank标记的账户
SELECT account_code, payload->>'name' as name, payload->>'isBank' as is_bank, payload->'bankInfo' as bank_info
FROM accounts 
WHERE company_code = 'JP01' 
  AND COALESCE((payload->>'isBank')::boolean, false) = true
LIMIT 10;

-- 检查账户131的配置
SELECT account_code, payload->>'name' as name, payload->>'isBank' as is_bank, payload->'bankInfo' as bank_info
FROM accounts 
WHERE company_code = 'JP01' AND account_code = '131';

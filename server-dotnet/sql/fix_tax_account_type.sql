-- 修正消费税科目的 taxType 为 TAX_ACCOUNT
-- 仮払消費税 (189) 和 仮受消費税 (324) 应该是 TAX_ACCOUNT，而不是 INPUT_TAX/OUTPUT_TAX

-- 检查当前状态
SELECT company_code, account_code, payload->>'name' as name, payload->>'taxType' as tax_type 
FROM accounts 
WHERE account_code IN ('189', '324')
ORDER BY company_code, account_code;

-- 修正 taxType
UPDATE accounts 
SET payload = jsonb_set(payload, '{taxType}', '"TAX_ACCOUNT"'),
    updated_at = NOW()
WHERE account_code IN ('189', '324') 
  AND COALESCE(payload->>'taxType', '') != 'TAX_ACCOUNT';

-- 验证修正结果
SELECT company_code, account_code, payload->>'name' as name, payload->>'taxType' as tax_type 
FROM accounts 
WHERE account_code IN ('189', '324')
ORDER BY company_code, account_code;


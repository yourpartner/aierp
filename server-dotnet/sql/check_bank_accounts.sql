SELECT account_code, name, payload->>'bankName' as bank, payload->>'accountNo' as acct_no
FROM accounts 
WHERE company_code='JP01' 
AND payload->>'category' = 'asset'
AND (payload->>'bankName' IS NOT NULL OR payload->>'accountNo' IS NOT NULL);


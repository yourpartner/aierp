-- 检查最新凭证的借方科目
SELECT 
    line->>'accountCode' as account_code,
    line->>'note' as note,
    line->>'drcr' as drcr,
    line->>'amount' as amount
FROM vouchers v, jsonb_array_elements(v.payload->'lines') as line
WHERE v.voucher_no = '2512000038'
ORDER BY (line->>'lineNo')::int;


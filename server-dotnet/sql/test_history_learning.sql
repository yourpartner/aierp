-- 测试修改后的历史学习SQL（排除税金行，按金额排序）
WITH line_data AS (
    SELECT 
        v.id,
        v.voucher_no,
        v.created_at,
        line->>'accountCode' as account_code,
        line->>'note' as note,
        line->>'drcr' as drcr,
        COALESCE((line->>'isTaxLine')::boolean, false) as is_tax_line,
        COALESCE((line->>'amount')::numeric, 0) as amount,
        v.payload->'header'->>'summary' as summary
    FROM vouchers v,
         jsonb_array_elements(v.payload->'lines') as line
    WHERE v.company_code = 'JP01'
      AND v.voucher_type = 'OT'
      AND v.voucher_no != '2512000037'  -- 排除刚创建的凭证
)
SELECT 
    account_code,
    note,
    amount,
    voucher_no,
    is_tax_line
FROM line_data
WHERE drcr = 'DR'
  AND account_code != '131'
  AND is_tax_line = false
  AND (note LIKE '%カ)フジカフエウイル%' OR summary LIKE '%カ)フジカフエウイル%')
ORDER BY created_at DESC, amount DESC
LIMIT 5;

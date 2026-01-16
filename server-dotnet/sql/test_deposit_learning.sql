-- 检查凭证2510000074的类型
SELECT voucher_no, voucher_type, payload->'header'->>'summary' as summary
FROM vouchers WHERE voucher_no = '2510000074';

-- 用ILIKE搜索（不区分大小写）
WITH line_data AS (
    SELECT 
        v.id,
        v.voucher_no,
        v.voucher_type,
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
)
SELECT 
    account_code,
    voucher_type,
    note,
    amount,
    voucher_no
FROM line_data
WHERE drcr = 'DR'
  AND account_code != '131'
  AND is_tax_line = false
  AND (note ILIKE '%イメガ%' OR summary ILIKE '%イメガ%')
ORDER BY created_at DESC, amount DESC
LIMIT 10;

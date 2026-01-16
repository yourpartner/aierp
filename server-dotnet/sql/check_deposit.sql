-- 检查イメガ相关的open item（用正确的列名）
SELECT id, account_code, partner_id, original_amount, residual_amount, cleared_flag, voucher_id
FROM open_items
WHERE company_code = 'JP01'
  AND account_code = '152'
  AND residual_amount > 0
ORDER BY created_at DESC
LIMIT 10;

-- 检查BP000034的open item
SELECT id, account_code, partner_id, original_amount, residual_amount, cleared_flag
FROM open_items
WHERE company_code = 'JP01'
  AND partner_id = 'BP000034';

-- 检查凭证2510000074是否创建了open item
SELECT oi.id, oi.account_code, oi.partner_id, oi.original_amount, oi.residual_amount, oi.cleared_flag, v.voucher_no
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE v.voucher_no = '2510000074';

-- 检查凭证2512000048
SELECT id, voucher_no, voucher_type, 
       payload->'header'->>'summary' as summary,
       payload->'header'->>'postingDate' as posting_date,
       primary_partner_code
FROM vouchers WHERE voucher_no = '2512000048';

-- 检查凭证2510000074的open_item状态
SELECT oi.id, oi.account_code, oi.partner_id, oi.original_amount, oi.residual_amount, oi.cleared_flag, oi.cleared_at
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE v.voucher_no = '2510000074';

-- 检查2512000048是否创建了open_item
SELECT oi.id, oi.account_code, oi.partner_id, oi.original_amount, oi.residual_amount, oi.cleared_flag
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE v.voucher_no = '2512000048';

-- 检查清账记录（refs字段）
SELECT oi.id, oi.refs
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE v.voucher_no IN ('2510000074', '2512000048');



-- 重置测试数据

-- 1. 删除自动生成的入金凭证
DELETE FROM vouchers WHERE voucher_no = '2512000049';

-- 2. 重置银行明细为待处理状态
UPDATE moneytree_transactions
SET posting_status = 'pending', 
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL
WHERE id = '44794064-e33e-41bd-b24b-b1be3b99eac7';

-- 3. 确认重置结果
SELECT id, posting_status, voucher_id 
FROM moneytree_transactions 
WHERE id = '44794064-e33e-41bd-b24b-b1be3b99eac7';

-- 4. 确认open_item还在
SELECT oi.id, oi.account_code, oi.partner_id, oi.residual_amount, oi.cleared_flag
FROM open_items oi
JOIN vouchers v ON oi.voucher_id = v.id
WHERE v.voucher_no = '2510000074';

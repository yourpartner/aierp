-- 恢复入金交易
UPDATE moneytree_transactions 
SET posting_status = 'pending',
    voucher_id = NULL,
    voucher_no = NULL,
    posting_error = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL
WHERE id = '44794064-e33e-41bd-b24b-b1be3b99eac7';

-- 删除错误创建的凭证
DELETE FROM vouchers WHERE voucher_no = '2512000045';

-- 删除相关的open_item
DELETE FROM open_items WHERE voucher_id NOT IN (SELECT id FROM vouchers);

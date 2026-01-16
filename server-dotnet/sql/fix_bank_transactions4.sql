-- 恢复被单独记账的手续费记录状态（凭证2512000027已删除）
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE id = 'c6c09936-1a90-4134-802d-99f49a57bf4d';

-- 确认结果
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE id = 'c6c09936-1a90-4134-802d-99f49a57bf4d';


-- 恢复主交易记录状态（仅解除关联，不删除凭证）
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE id = '892d080b-e3de-4135-be52-4742c773ac1a';

-- 确认结果
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE id IN ('c6c09936-1a90-4134-802d-99f49a57bf4d', '892d080b-e3de-4135-be52-4742c773ac1a')
ORDER BY row_sequence;


-- 恢复手续费和主交易状态
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE company_code = 'JP01'
  AND voucher_no IN ('2512000030', '2512000003')
  AND transaction_date = '2025-12-10';

-- 删除新生成的手续费凭证
DELETE FROM vouchers WHERE company_code = 'JP01' AND voucher_no = '2512000030';

-- 确认结果
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no, row_sequence
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2025-12-10'
  AND row_sequence IN (15, 16)
ORDER BY row_sequence;


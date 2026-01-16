-- 查看12月10日所有银行明细
SELECT id, transaction_date, description, withdrawal_amount, deposit_amount, posting_status, voucher_no, row_sequence
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2025-12-10'
ORDER BY row_sequence;


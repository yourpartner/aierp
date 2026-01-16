-- 查看需要处理的银行明细
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2024-12-10'
ORDER BY row_sequence;


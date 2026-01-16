-- 检查手续费和主交易的账户信息是否一致
SELECT id, transaction_date, description, withdrawal_amount, 
       account_number, bank_name, row_sequence,
       COALESCE(account_number, bank_name, 'default') as group_key
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2025-12-10'
  AND row_sequence IN (15, 16)
ORDER BY row_sequence;


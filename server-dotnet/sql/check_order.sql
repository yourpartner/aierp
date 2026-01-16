SELECT id, transaction_date, created_at, withdrawal_amount, description 
FROM moneytree_transactions 
WHERE company_code = 'JP01' AND transaction_date = '2025-11-05' AND withdrawal_amount < 0
ORDER BY transaction_date, created_at;


-- Check if there's a sequence number or original order preserved
SELECT id, transaction_date, created_at, balance, withdrawal_amount, description
FROM moneytree_transactions 
WHERE company_code = 'JP01' AND transaction_date = '2025-11-05'
ORDER BY balance DESC;  -- balance can indicate original order (decreasing as payments are made)


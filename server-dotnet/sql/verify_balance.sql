-- Verify balance data and order for 2025-11-10
SELECT 
    transaction_date,
    balance,
    withdrawal_amount,
    deposit_amount,
    description,
    (LAG(balance) OVER (ORDER BY balance DESC)) as prev_balance,
    CASE 
        WHEN withdrawal_amount < 0 THEN balance - withdrawal_amount
        WHEN deposit_amount > 0 THEN balance - deposit_amount
        ELSE NULL
    END as expected_prev_balance
FROM moneytree_transactions 
WHERE company_code = 'JP01' 
  AND transaction_date = '2025-11-10'
ORDER BY balance DESC;


-- 1. Delete newly generated vouchers
DELETE FROM vouchers WHERE payload->'header'->>'voucherNo' IN ('2511000060', '2511000061');

-- 2. Restore bank transaction status
UPDATE moneytree_transactions 
SET posting_status = 'pending', 
    voucher_id = NULL, 
    voucher_no = NULL, 
    rule_id = NULL,
    rule_title = NULL,
    posting_error = NULL,
    posting_run_id = NULL
WHERE voucher_no IN ('2511000060', '2511000061');

-- 3. Reset voucher sequence
UPDATE voucher_sequences SET last_number = 59 WHERE company_code = 'JP01' AND yymm = '2511';

-- 4. Verify
SELECT id, withdrawal_amount, description, posting_status, voucher_no FROM moneytree_transactions 
WHERE company_code = 'JP01' AND transaction_date = '2025-11-05' AND withdrawal_amount < 0
ORDER BY withdrawal_amount;


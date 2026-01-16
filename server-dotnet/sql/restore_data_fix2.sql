-- 2. Restore bank transaction status
UPDATE moneytree_transactions 
SET posting_status = 'pending', 
    voucher_id = NULL, 
    voucher_no = NULL, 
    rule_id = NULL,
    rule_title = NULL,
    posting_error = NULL,
    posting_run_id = NULL
WHERE id IN ('3ba6fd71-aa48-4a59-85e4-91ae36470757', 'ea34dcd7-31fb-4ca7-9241-31bcd4ea1f2b');

-- 3. Reset voucher sequence
UPDATE voucher_sequences SET last_number = 59 WHERE company_code = 'JP01' AND yymm = '2511';


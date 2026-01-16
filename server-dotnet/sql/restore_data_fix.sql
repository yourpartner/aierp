-- 1. Delete newly generated vouchers
DELETE FROM vouchers WHERE payload->'header'->>'voucherNo' IN ('2511000060', '2511000061');

-- 2. Restore bank transaction status
UPDATE moneytree_transactions 
SET status = 'pending', 
    voucher_id = NULL, 
    voucher_no = NULL, 
    matched_rule_id = NULL,
    matched_rule_title = NULL,
    agent_message = NULL,
    posting_run_id = NULL
WHERE id IN ('3ba6fd71-aa48-4a59-85e4-91ae36470757', 'ea34dcd7-31fb-4ca7-9241-31bcd4ea1f2b');

-- 3. Reset voucher sequence
UPDATE voucher_sequences SET next_number = 60 WHERE company_code = 'JP01' AND yymm = '2511';


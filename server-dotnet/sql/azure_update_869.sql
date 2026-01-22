-- Update account 6610 to 869 in Azure
UPDATE moneytree_posting_rules 
SET action = REPLACE(action::text, '"6610"', '"869"')::jsonb 
WHERE action::text LIKE '%6610%';

-- Verify
SELECT id, title, action->>'debitAccount' as debit, action->>'bankFeeAccountCode' as fee 
FROM moneytree_posting_rules 
WHERE action::text LIKE '%869%';


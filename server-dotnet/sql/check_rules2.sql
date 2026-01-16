SELECT title, action->>'debitAccountCode' as debit, action->>'creditAccountCode' as credit
FROM moneytree_posting_rules 
WHERE company_code='JP01' 
LIMIT 10;


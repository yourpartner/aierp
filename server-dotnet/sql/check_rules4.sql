SELECT title, matcher, action
FROM moneytree_posting_rules 
WHERE company_code='JP01' 
AND title LIKE '%買掛金%';


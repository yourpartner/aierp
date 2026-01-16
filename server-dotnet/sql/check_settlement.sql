-- 检查规则的settlement配置
SELECT title, 
       action->>'debitAccount' as debit,
       action->>'creditAccount' as credit,
       action->'settlement' as settlement 
FROM moneytree_posting_rules 
WHERE company_code = 'JP01'
ORDER BY priority DESC;


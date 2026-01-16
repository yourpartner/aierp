-- 检查出金规则的settlement配置
SELECT title, 
       matcher->>'transactionType' as tx_type, 
       action->'settlement' as settlement,
       action->>'fallbackAccount' as fallback_account
FROM moneytree_posting_rules 
WHERE company_code = 'JP01' 
  AND matcher->>'transactionType' = 'withdrawal'
ORDER BY priority;

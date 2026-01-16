-- 为规则添加 transactionType 字段以区分入金和出金

-- 出金类规则添加 transactionType = "withdrawal"
UPDATE moneytree_posting_rules 
SET matcher = jsonb_set(matcher, '{transactionType}', '"withdrawal"')
WHERE company_code = 'JP01' 
  AND (title LIKE '%支払%' OR title LIKE '%返済%' OR title LIKE '%手数料%');

-- 入金类规则添加 transactionType = "deposit"  
UPDATE moneytree_posting_rules
SET matcher = jsonb_set(matcher, '{transactionType}', '"deposit"')
WHERE company_code = 'JP01'
  AND (title LIKE '%入金%' OR title LIKE '%利息%');

-- 验证更新结果
SELECT title, matcher->>'transactionType' as tx_type FROM moneytree_posting_rules WHERE company_code = 'JP01' ORDER BY priority DESC;



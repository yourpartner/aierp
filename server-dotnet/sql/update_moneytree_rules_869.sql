-- 将银行明细规则中的科目 6610 改为 869
UPDATE moneytree_posting_rules 
SET action = REPLACE(action::text, '"6610"', '"869"')::jsonb 
WHERE action::text LIKE '%6610%';

-- 验证更新结果
SELECT id, title, action FROM moneytree_posting_rules 
WHERE action::text LIKE '%869%' OR action::text LIKE '%6610%';


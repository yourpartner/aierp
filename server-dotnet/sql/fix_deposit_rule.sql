-- 撤销之前的修改，移除硬编码的accountCode
UPDATE moneytree_posting_rules 
SET action = action - 'settlement' || jsonb_build_object('settlement', 
    (action->'settlement') - 'accountCode'
)
WHERE company_code = 'JP01' 
  AND title = '入金-振込（仮受）';

-- 确认更新结果
SELECT title, action->'settlement' as settlement
FROM moneytree_posting_rules 
WHERE company_code = 'JP01' 
  AND title = '入金-振込（仮受）';

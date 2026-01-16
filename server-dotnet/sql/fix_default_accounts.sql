-- 修复默认科目设置
-- 出金（付款）：找不到正确借方科目时应该用「仮払金」(183)
-- 入金：找不到正确贷方科目时应该用「仮受金」(319)

-- 更新出金规则：買掛金(312) -> 仮払金(183)
UPDATE moneytree_posting_rules 
SET action = jsonb_set(action, '{debitAccount}', '"183"'),
    title = '出金-振込（仮払）'
WHERE company_code = 'JP01' 
  AND title = '買掛金支払-振込';

-- 更新入金规则：売掛金(152) -> 仮受金(319)
UPDATE moneytree_posting_rules 
SET action = jsonb_set(action, '{creditAccount}', '"319"'),
    title = '入金-振込（仮受）'
WHERE company_code = 'JP01' 
  AND title = '売掛金入金-振込';

-- 确认更新结果
SELECT title, 
       matcher->>'transactionType' as type,
       action->>'debitAccount' as debit, 
       action->>'creditAccount' as credit 
FROM moneytree_posting_rules 
WHERE company_code = 'JP01' 
  AND (title LIKE '%振込%' OR title LIKE '%仮%')
ORDER BY priority DESC;


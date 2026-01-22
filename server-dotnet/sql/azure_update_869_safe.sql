-- 只更新指定ID的3条记录，将科目6610改为869
-- 不会影响任何其他数据

-- 记录1: 振込手数料-雑費
UPDATE moneytree_posting_rules 
SET action = jsonb_set(jsonb_set(action, '{debitAccount}', '"869"'), '{bankFeeAccountCode}', '"869"'),
    updated_at = now()
WHERE id = '47444347-47c5-4d07-ac6a-2c92343c02ff';

-- 记录2: 通用出金（仮払金）
UPDATE moneytree_posting_rules 
SET action = jsonb_set(action, '{bankFeeAccountCode}', '"869"'),
    updated_at = now()
WHERE id = '98bb6930-9e7d-4603-bf74-b5fb3bdcfa2f';

-- 记录3: 通用出金（仮払金）
UPDATE moneytree_posting_rules 
SET action = jsonb_set(action, '{bankFeeAccountCode}', '"869"'),
    updated_at = now()
WHERE id = '5407c999-50c4-4218-a525-55ddf7f94b9f';

-- 验证更新结果（只查看这3条）
SELECT id, title, action->>'debitAccount' as debit, action->>'bankFeeAccountCode' as fee 
FROM moneytree_posting_rules 
WHERE id IN (
    '47444347-47c5-4d07-ac6a-2c92343c02ff',
    '98bb6930-9e7d-4603-bf74-b5fb3bdcfa2f',
    '5407c999-50c4-4218-a525-55ddf7f94b9f'
);


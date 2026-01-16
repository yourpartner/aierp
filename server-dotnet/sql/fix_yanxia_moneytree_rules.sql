-- 修复 yanxia 公司的 Moneytree 自动记账规则
-- 问题：出金交易错误地使用了入金规则，导致借方科目用了「仮受金」而不是「仮払金」
-- 解决：
-- 1. 为规则添加 transactionType 条件区分入金/出金
-- 2. 确保入金规则的默认贷方是仮受金(319)
-- 3. 确保出金规则的默认借方是仮払金(183)

-- 查看当前规则
SELECT id, title, priority, 
       matcher->>'transactionType' as tx_type,
       action->>'debitAccount' as debit, 
       action->>'creditAccount' as credit 
FROM moneytree_posting_rules 
WHERE company_code = 'JP01' 
ORDER BY priority DESC;

-- 1. 为出金类规则添加 transactionType = "withdrawal"
UPDATE moneytree_posting_rules 
SET matcher = jsonb_set(COALESCE(matcher, '{}'::jsonb), '{transactionType}', '"withdrawal"')
WHERE company_code = 'JP01' 
  AND (title LIKE '%支払%' OR title LIKE '%返済%' OR title LIKE '%手数料%' OR title LIKE '%出金%');

-- 2. 为入金类规则添加 transactionType = "deposit"
UPDATE moneytree_posting_rules
SET matcher = jsonb_set(COALESCE(matcher, '{}'::jsonb), '{transactionType}', '"deposit"')
WHERE company_code = 'JP01'
  AND (title LIKE '%入金%' OR title LIKE '%利息%' OR title LIKE '%OTA%' OR title LIKE '%売掛%');

-- 3. 确保有默认的入金规则（fallback到仮受金）
INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
SELECT 
    'JP01',
    '通用入金（仮受金）',
    '入金の汎用ルール。売掛金消込できない場合は仮受金で処理。',
    100,  -- 最低优先级
    '{"transactionType":"deposit"}'::jsonb,
    jsonb_build_object(
        'debitAccount', '{bankAccount}',
        'creditAccount', '319',  -- 仮受金
        'summaryTemplate', '入金 {description}',
        'postingDate', 'transactionDate',
        'voucherType', 'IN',
        'creditNote', '{description}'
    ),
    TRUE,
    'system',
    'system'
WHERE NOT EXISTS (
    SELECT 1 FROM moneytree_posting_rules 
    WHERE company_code = 'JP01' AND title = '通用入金（仮受金）'
);

-- 4. 确保有默认的出金规则（fallback到仮払金）
INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
SELECT 
    'JP01',
    '通用出金（仮払金）',
    '出金の汎用ルール。買掛金消込できない場合は仮払金で処理。',
    100,  -- 最低优先级
    '{"transactionType":"withdrawal"}'::jsonb,
    jsonb_build_object(
        'debitAccount', '183',  -- 仮払金
        'creditAccount', '{bankAccount}',
        'summaryTemplate', '出金 {description}',
        'postingDate', 'transactionDate',
        'voucherType', 'OT',
        'debitNote', '{description}',
        'bankFeeAccountCode', '6610',
        'inputTaxAccountCode', '1410'
    ),
    TRUE,
    'system',
    'system'
WHERE NOT EXISTS (
    SELECT 1 FROM moneytree_posting_rules 
    WHERE company_code = 'JP01' AND title = '通用出金（仮払金）'
);

-- 5. 更新现有出金规则，确保使用仮払金作为fallback
UPDATE moneytree_posting_rules
SET action = jsonb_set(
    jsonb_set(
        action, 
        '{settlement,fallbackAccount}', '"183"'
    ),
    '{settlement,fallbackLine}', '"debit"'
)
WHERE company_code = 'JP01'
  AND action->'settlement'->>'enabled' = 'true'
  AND matcher->>'transactionType' = 'withdrawal';

-- 6. 更新现有入金规则，确保使用仮受金作为fallback
UPDATE moneytree_posting_rules
SET action = jsonb_set(
    jsonb_set(
        action, 
        '{settlement,fallbackAccount}', '"319"'
    ),
    '{settlement,fallbackLine}', '"credit"'
)
WHERE company_code = 'JP01'
  AND action->'settlement'->>'enabled' = 'true'
  AND matcher->>'transactionType' = 'deposit';

-- 验证更新结果
SELECT id, title, priority,
       matcher->>'transactionType' as tx_type,
       action->>'debitAccount' as debit, 
       action->>'creditAccount' as credit,
       action->'settlement'->>'fallbackAccount' as fallback_account
FROM moneytree_posting_rules 
WHERE company_code = 'JP01' 
ORDER BY priority DESC, title;


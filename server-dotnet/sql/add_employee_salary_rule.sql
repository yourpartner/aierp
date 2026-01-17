-- ============================================================
-- 添加「振込出金-従業員給与」规则
-- 目的：识别摘要中包含员工姓名的振込出金，自动匹配员工并使用給与科目
-- ============================================================

-- 为 azure 公司添加规则
INSERT INTO moneytree_posting_rules (
    company_code, 
    title, 
    description, 
    priority, 
    matcher, 
    action, 
    is_active, 
    created_by, 
    updated_by
)
SELECT 
    'azure',
    '振込出金-従業員給与',
    '振込による従業員への給与支払。摘要から従業員名を特定し未払給与を消込。',
    24,  -- 给与支払(25)より少し高い優先度
    '{"descriptionRegex":"^振込\\s", "transactionType":"withdrawal"}'::jsonb,
    jsonb_build_object(
        'debitAccount', '2130',  -- 未払給与
        'creditAccount', '{bankAccount}',
        'summaryTemplate', '給与支払 {description}',
        'postingDate', 'transactionDate',
        'voucherType', 'OT',
        'counterparty', jsonb_build_object(
            'type', jsonb_build_array('employee'),
            'nameContains', jsonb_build_array('{description}'),
            'assignLine', 'debit',
            'activeOnly', true
        )
    ),
    TRUE,
    'system',
    'system'
WHERE NOT EXISTS (
    SELECT 1 FROM moneytree_posting_rules 
    WHERE company_code = 'azure' AND title = '振込出金-従業員給与'
);

-- 同时为 JP01 公司也添加相同规则
INSERT INTO moneytree_posting_rules (
    company_code, 
    title, 
    description, 
    priority, 
    matcher, 
    action, 
    is_active, 
    created_by, 
    updated_by
)
SELECT 
    'JP01',
    '振込出金-従業員給与',
    '振込による従業員への給与支払。摘要から従業員名を特定し未払給与を消込。',
    24,  -- 给与支払(25)より少し高い優先度
    '{"descriptionRegex":"^振込\\s", "transactionType":"withdrawal"}'::jsonb,
    jsonb_build_object(
        'debitAccount', '2130',  -- 未払給与
        'creditAccount', '{bankAccount}',
        'summaryTemplate', '給与支払 {description}',
        'postingDate', 'transactionDate',
        'voucherType', 'OT',
        'counterparty', jsonb_build_object(
            'type', jsonb_build_array('employee'),
            'nameContains', jsonb_build_array('{description}'),
            'assignLine', 'debit',
            'activeOnly', true
        )
    ),
    TRUE,
    'system',
    'system'
WHERE NOT EXISTS (
    SELECT 1 FROM moneytree_posting_rules 
    WHERE company_code = 'JP01' AND title = '振込出金-従業員給与'
);

-- 验证插入结果
SELECT company_code, title, priority, 
       matcher->>'descriptionRegex' as regex,
       action->>'debitAccount' as debit_account,
       action->'counterparty'->>'type' as counterparty_type
FROM moneytree_posting_rules 
WHERE title = '振込出金-従業員給与'
ORDER BY company_code;



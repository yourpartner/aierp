-- ============================================================
-- Moneytree 银行自动记账规则设置
-- 包含：OTA入金、売掛金、利息、工资支付、供应商付款、信用卡还款
-- ============================================================

-- 全局设置：记账完成后通知的用户（可在此修改）
-- 通知目标可以是：用户ID、角色代码、或邮箱
DO $$
DECLARE
    v_company TEXT := 'JP01';
    v_notify_role TEXT := 'accountant';  -- 通知会计角色
    v_bank_account TEXT := '{bankAccount}';
    v_ar_account TEXT := '1100';  -- 売掛金
    v_ap_account TEXT := '2100';  -- 買掛金
    v_unpaid_account TEXT := '2120';  -- 未払金
    v_salary_payable TEXT := '2130';  -- 未払給与
    v_interest_income TEXT := '8100';  -- 受取利息
    v_suspense_receipt TEXT := '2190';  -- 仮受金
    v_fee_account TEXT := '6610';  -- 雑費（手续费）
    v_input_tax TEXT := '1410';  -- 仮払消費税
BEGIN

-- ============================================================
-- 1. OTA平台入金规则（优先级高，先匹配）
-- ============================================================

-- 1.1 Booking.com 入金
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'OTA入金-Booking.com') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'OTA入金-Booking.com',
        'Booking.comからの売上入金を売掛金消込。未消込の場合は仮受金。',
        20,
        '{"descriptionRegex":"(BOOKING\\.?COM|ﾌﾞｯｷﾝｸﾞ|ブッキング)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', 'OTA入金 Booking.com {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'platformGroup', 'OTA',
                'requireMatch', false,
                'tolerance', 1000,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 1.2 Expedia 入金
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'OTA入金-Expedia') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'OTA入金-Expedia',
        'Expediaからの売上入金を売掛金消込。未消込の場合は仮受金。',
        20,
        '{"descriptionRegex":"(EXPEDIA|ｴｸｽﾍﾟﾃﾞｨｱ|エクスペディア)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', 'OTA入金 Expedia {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'platformGroup', 'OTA',
                'requireMatch', false,
                'tolerance', 1000,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 1.3 Ctrip/Trip.com 入金
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'OTA入金-Ctrip') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'OTA入金-Ctrip',
        'Ctrip/Trip.comからの売上入金を売掛金消込。未消込の場合は仮受金。',
        20,
        '{"descriptionRegex":"(CTRIP|TRIP\\.?COM|ｼｰﾄﾘｯﾌﾟ|シートリップ)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', 'OTA入金 Ctrip {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'platformGroup', 'OTA',
                'requireMatch', false,
                'tolerance', 1000,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 1.4 Airbnb 入金
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'OTA入金-Airbnb') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'OTA入金-Airbnb',
        'Airbnbからの売上入金を売掛金消込。未消込の場合は仮受金。',
        20,
        '{"descriptionRegex":"(AIRBNB|ｴｱﾋﾞｰ|エアビー)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', 'OTA入金 Airbnb {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'platformGroup', 'OTA',
                'requireMatch', false,
                'tolerance', 1000,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 1.5 Vacation STAY 入金
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'OTA入金-VacationSTAY') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'OTA入金-VacationSTAY',
        'Vacation STAYからの売上入金を売掛金消込。未消込の場合は仮受金。',
        20,
        '{"descriptionRegex":"(VACATION\\s*STAY|ﾊﾞｹｰｼｮﾝｽﾃｲ|バケーションステイ|ラクテンステイ|ﾗｸﾃﾝｽﾃｲ|楽天ステイ)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', 'OTA入金 Vacation STAY {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'platformGroup', 'OTA',
                'requireMatch', false,
                'tolerance', 1000,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- ============================================================
-- 2. 银行利息入金
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = '受取利息') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        '受取利息',
        '銀行預金利息の入金処理。',
        15,
        '{"descriptionRegex":"(利息|利子|ﾘｿｸ|リソク|INTEREST)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_interest_income,
            'summaryTemplate', '受取利息 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'debitNote', '預金利息',
            'creditNote', '預金利息',
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- ============================================================
-- 3. 一般売掛金入金（振込入金）
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = '売掛金入金-振込') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        '売掛金入金-振込',
        '振込による売掛金の入金。摘要から取引先を推測し消込を試行。',
        50,
        '{"descriptionRegex":"^振込\\s", "amountMin": 1}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_bank_account,
            'creditAccount', v_ar_account,
            'summaryTemplate', '売掛金入金 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'IN',
            'bankFeeAccountCode', v_fee_account,
            'counterparty', jsonb_build_object(
                'type', jsonb_build_array('customer'),
                'nameContains', jsonb_build_array('{description}'),
                'assignLine', 'credit'
            ),
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'credit',
                'useCounterparty', true,
                'requireMatch', false,
                'tolerance', 100,
                'fallbackAccount', v_suspense_receipt,
                'fallbackLine', 'credit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- ============================================================
-- 4. 支付规则
-- ============================================================

-- 4.1 员工工资支付（通过摘要识别员工）
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = '給与支払') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        '給与支払',
        '従業員への給与振込。摘要から従業員を特定し未払給与を消込。',
        25,
        '{"descriptionRegex":"(給与|給料|賃金|ｷｭｳﾖ|ｷｭｳﾘｮｳ|ﾁﾝｷﾞﾝ)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_salary_payable,
            'creditAccount', v_bank_account,
            'summaryTemplate', '給与支払 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'OT',
            'bankFeeAccountCode', v_fee_account,
            'inputTaxAccountCode', v_input_tax,
            'counterparty', jsonb_build_object(
                'type', jsonb_build_array('employee'),
                'nameContains', jsonb_build_array('{description}'),
                'assignLine', 'debit',
                'activeOnly', true
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 4.2 信用卡还款
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = 'クレジットカード返済') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        'クレジットカード返済',
        'クレジットカードの返済処理。未払金を消込。',
        30,
        '{"descriptionRegex":"(ｶｰﾄﾞ|カード|CARD|ｸﾚｼﾞｯﾄ|クレジット|CREDIT|ｱﾒｯｸｽ|アメックス|AMEX|VISA|ﾋﾞｻﾞ|ビザ|MASTER|ﾏｽﾀｰ|マスター|JCB|ｼﾞｪｰｼｰﾋﾞｰ)"}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_unpaid_account,
            'creditAccount', v_bank_account,
            'summaryTemplate', 'クレジットカード返済 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'OT',
            'bankFeeAccountCode', v_fee_account,
            'inputTaxAccountCode', v_input_tax,
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'debit',
                'requireMatch', false,
                'tolerance', 100
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- 4.3 供应商付款（振込出金）
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = '買掛金支払-振込') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        '買掛金支払-振込',
        '振込による買掛金・未払金の支払。摘要から取引先を推測し消込を試行。',
        60,
        '{"descriptionRegex":"^振込\\s", "amountMin": 1}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_ap_account,
            'creditAccount', v_bank_account,
            'summaryTemplate', '買掛金支払 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'OT',
            'bankFeeAccountCode', v_fee_account,
            'inputTaxAccountCode', v_input_tax,
            'counterparty', jsonb_build_object(
                'type', jsonb_build_array('vendor'),
                'nameContains', jsonb_build_array('{description}'),
                'assignLine', 'debit'
            ),
            'settlement', jsonb_build_object(
                'enabled', true,
                'line', 'debit',
                'useCounterparty', true,
                'requireMatch', false,
                'tolerance', 100,
                'fallbackAccount', v_unpaid_account,
                'fallbackLine', 'debit'
            ),
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

-- ============================================================
-- 5. 银行手续费（独立处理 - Fallback）
-- ============================================================
-- 更新现有规则，确保消费税拆分正确
UPDATE moneytree_posting_rules
SET description = '振込手数料を雑費で処理。支払と配対できない場合のフォールバック用。消費税10%を自動分離。',
    matcher = '{"descriptionContains":["振込手数料"]}'::jsonb,
    action = jsonb_build_object(
        'debitAccount', v_fee_account,
        'creditAccount', v_bank_account,
        'summaryTemplate', '振込手数料 {description}',
        'postingDate', 'transactionDate',
        'voucherType', 'OT',
        'debitNote', '{description}',
        'creditNote', '{description}',
        'inputTaxAccountCode', v_input_tax,
        'bankFeeAccountCode', v_fee_account,
        'notification', jsonb_build_object(
            'enabled', true,
            'targetRole', v_notify_role
        )
    ),
    updated_at = now()
WHERE company_code = v_company AND title = '振込手数料-雑費';

-- 如果不存在则创建
IF NOT EXISTS (SELECT 1 FROM moneytree_posting_rules WHERE company_code = v_company AND title = '振込手数料-雑費') THEN
    INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by)
    VALUES (
        v_company,
        '振込手数料-雑費',
        '振込手数料を雑費で処理。支払と配対できない場合のフォールバック用。消費税10%を自動分離。',
        10,
        '{"descriptionContains":["振込手数料"]}'::jsonb,
        jsonb_build_object(
            'debitAccount', v_fee_account,
            'creditAccount', v_bank_account,
            'summaryTemplate', '振込手数料 {description}',
            'postingDate', 'transactionDate',
            'voucherType', 'OT',
            'debitNote', '{description}',
            'creditNote', '{description}',
            'inputTaxAccountCode', v_input_tax,
            'bankFeeAccountCode', v_fee_account,
            'notification', jsonb_build_object(
                'enabled', true,
                'targetRole', v_notify_role
            )
        ),
        TRUE,
        'system',
        'system'
    );
END IF;

RAISE NOTICE 'Moneytree posting rules setup completed for company %', v_company;

END $$;

-- ============================================================
-- 显示所有规则
-- ============================================================
SELECT 
    title,
    description,
    priority,
    is_active,
    matcher,
    action->'notification' as notification
FROM moneytree_posting_rules
WHERE company_code = 'JP01'
ORDER BY priority, title;


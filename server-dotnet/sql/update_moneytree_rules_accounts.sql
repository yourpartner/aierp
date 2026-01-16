-- ============================================================
-- 更新 moneytree_posting_rules 中的科目编号
-- 根据科目名称从 accounts 表匹配新的科目编号
-- ============================================================

DO $$
DECLARE
    v_company TEXT := 'JP01';
    
    -- 从 accounts 表按名称查找科目编号
    v_ar_account TEXT;      -- 売掛金
    v_ap_account TEXT;      -- 買掛金
    v_unpaid_account TEXT;  -- 未払金
    v_salary_payable TEXT;  -- 未払給与 / 未払費用
    v_interest_income TEXT; -- 受取利息
    v_suspense_receipt TEXT;-- 仮受金
    v_fee_account TEXT;     -- 支払手数料 / 雑費
    v_input_tax TEXT;       -- 仮払消費税
    
    v_updated_count INT := 0;
BEGIN
    -- 查找売掛金
    SELECT account_code INTO v_ar_account FROM accounts 
    WHERE company_code = v_company 
      AND (payload->>'name' ILIKE '%売掛金%' OR payload->>'name' ILIKE '%売掛%')
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '売掛金: %', COALESCE(v_ar_account, '未找到');
    
    -- 查找買掛金
    SELECT account_code INTO v_ap_account FROM accounts 
    WHERE company_code = v_company 
      AND (payload->>'name' ILIKE '%買掛金%' OR payload->>'name' ILIKE '%買掛%')
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '買掛金: %', COALESCE(v_ap_account, '未找到');
    
    -- 查找未払金
    SELECT account_code INTO v_unpaid_account FROM accounts 
    WHERE company_code = v_company 
      AND payload->>'name' ILIKE '%未払金%'
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '未払金: %', COALESCE(v_unpaid_account, '未找到');
    
    -- 查找未払給与（也可能叫未払費用）
    SELECT account_code INTO v_salary_payable FROM accounts 
    WHERE company_code = v_company 
      AND (payload->>'name' ILIKE '%未払給与%' OR payload->>'name' ILIKE '%未払給料%' OR payload->>'name' ILIKE '%未払費用%')
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '未払給与: %', COALESCE(v_salary_payable, '未找到');
    
    -- 查找受取利息
    SELECT account_code INTO v_interest_income FROM accounts 
    WHERE company_code = v_company 
      AND payload->>'name' ILIKE '%受取利息%'
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '受取利息: %', COALESCE(v_interest_income, '未找到');
    
    -- 查找仮受金
    SELECT account_code INTO v_suspense_receipt FROM accounts 
    WHERE company_code = v_company 
      AND payload->>'name' ILIKE '%仮受金%'
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '仮受金: %', COALESCE(v_suspense_receipt, '未找到');
    
    -- 查找支払手数料（或雑費）
    SELECT account_code INTO v_fee_account FROM accounts 
    WHERE company_code = v_company 
      AND (payload->>'name' ILIKE '%支払手数料%' OR payload->>'name' ILIKE '%振込手数料%')
    ORDER BY account_code LIMIT 1;
    IF v_fee_account IS NULL THEN
        SELECT account_code INTO v_fee_account FROM accounts 
        WHERE company_code = v_company 
          AND payload->>'name' ILIKE '%雑費%'
        ORDER BY account_code LIMIT 1;
    END IF;
    RAISE NOTICE '支払手数料/雑費: %', COALESCE(v_fee_account, '未找到');
    
    -- 查找仮払消費税
    SELECT account_code INTO v_input_tax FROM accounts 
    WHERE company_code = v_company 
      AND payload->>'name' ILIKE '%仮払消費税%'
    ORDER BY account_code LIMIT 1;
    RAISE NOTICE '仮払消費税: %', COALESCE(v_input_tax, '未找到');
    
    RAISE NOTICE '----------------------------------------';
    RAISE NOTICE '开始更新 moneytree_posting_rules...';
    
    -- 更新所有规则中的 creditAccount（売掛金）
    IF v_ar_account IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{creditAccount}', to_jsonb(v_ar_account)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'creditAccount' IN ('1100', '1101', '1102', '1110', '1120');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新売掛金(creditAccount): % 条', v_updated_count;
        
        -- 同时更新 settlement.fallbackAccount 如果是売掛金相关
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{settlement,fallbackAccount}', to_jsonb(v_suspense_receipt)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->'settlement'->>'fallbackAccount' IS NOT NULL
          AND v_suspense_receipt IS NOT NULL;
    END IF;
    
    -- 更新所有规则中的 debitAccount（買掛金）
    IF v_ap_account IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{debitAccount}', to_jsonb(v_ap_account)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'debitAccount' IN ('2100', '2101', '2102', '2110');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新買掛金(debitAccount): % 条', v_updated_count;
    END IF;
    
    -- 更新未払金
    IF v_unpaid_account IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{debitAccount}', to_jsonb(v_unpaid_account)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'debitAccount' IN ('2120', '2121', '2122');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新未払金: % 条', v_updated_count;
    END IF;
    
    -- 更新未払給与
    IF v_salary_payable IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{debitAccount}', to_jsonb(v_salary_payable)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'debitAccount' IN ('2130', '2131', '2132');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新未払給与: % 条', v_updated_count;
    END IF;
    
    -- 更新受取利息
    IF v_interest_income IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{creditAccount}', to_jsonb(v_interest_income)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'creditAccount' IN ('8100', '8101', '8110');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新受取利息: % 条', v_updated_count;
    END IF;
    
    -- 更新仮受金（作为 fallback）
    IF v_suspense_receipt IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{settlement,fallbackAccount}', to_jsonb(v_suspense_receipt)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->'settlement'->>'fallbackAccount' IN ('2190', '2191', '2192');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新仮受金(fallback): % 条', v_updated_count;
    END IF;
    
    -- 更新支払手数料/雑費
    IF v_fee_account IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{bankFeeAccountCode}', to_jsonb(v_fee_account)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'bankFeeAccountCode' IN ('6610', '6611', '6620', '6630');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新bankFeeAccountCode: % 条', v_updated_count;
        
        -- 同时更新作为 debitAccount 的手续费规则
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{debitAccount}', to_jsonb(v_fee_account)),
            updated_at = now()
        WHERE company_code = v_company
          AND title ILIKE '%手数料%'
          AND action->>'debitAccount' IN ('6610', '6611', '6620', '6630');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新手数料規則debitAccount: % 条', v_updated_count;
    END IF;
    
    -- 更新仮払消費税
    IF v_input_tax IS NOT NULL THEN
        UPDATE moneytree_posting_rules
        SET action = jsonb_set(action, '{inputTaxAccountCode}', to_jsonb(v_input_tax)),
            updated_at = now()
        WHERE company_code = v_company
          AND action->>'inputTaxAccountCode' IN ('1410', '1411', '1412');
        GET DIAGNOSTICS v_updated_count = ROW_COUNT;
        RAISE NOTICE '更新inputTaxAccountCode: % 条', v_updated_count;
    END IF;
    
    RAISE NOTICE '----------------------------------------';
    RAISE NOTICE '更新完成！';
END $$;

-- 验证更新结果
SELECT 
    title,
    action->>'debitAccount' as debit,
    action->>'creditAccount' as credit,
    action->'settlement'->>'fallbackAccount' as fallback,
    action->>'bankFeeAccountCode' as fee,
    action->>'inputTaxAccountCode' as input_tax
FROM moneytree_posting_rules
WHERE company_code = 'JP01'
ORDER BY priority, title;



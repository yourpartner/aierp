-- =========================================================================
-- 银行明细记账规则迁移: moneytree_posting_rules → agent_skill_rules
-- 同时升级 bank_auto_booking skill 的 system_prompt 和 enabled_tools
-- =========================================================================

BEGIN;

-- 0. 获取 bank_auto_booking 的 skill id
DO $$
DECLARE
  v_skill_id UUID;
BEGIN
  SELECT id INTO v_skill_id FROM agent_skills WHERE skill_key = 'bank_auto_booking' LIMIT 1;
  IF v_skill_id IS NULL THEN
    RAISE EXCEPTION 'bank_auto_booking skill not found';
  END IF;

  -- 1. 删除旧的 agent_skill_rules（仅 bank_auto_booking 的）
  DELETE FROM agent_skill_rules WHERE skill_id = v_skill_id;

  -- 2. 从 moneytree_posting_rules 迁移规则（JP01 公司的活跃规则）
  INSERT INTO agent_skill_rules (id, skill_id, rule_key, name, conditions, actions, priority, is_active)
  SELECT
    gen_random_uuid(),
    v_skill_id,
    -- rule_key: 从 title 生成简洁的 key
    CASE
      WHEN title ILIKE '%振込手数料%' THEN 'bank_fee_withdrawal'
      WHEN title ILIKE '%受取利息%' THEN 'interest_income'
      WHEN title ILIKE '%VacationSTAY%' OR title ILIKE '%Vacation STAY%' THEN 'ota_deposit_vacation_stay'
      WHEN title ILIKE '%Ctrip%' THEN 'ota_deposit_ctrip'
      WHEN title ILIKE '%OTAプラットフォーム%' THEN 'ota_deposit_general'
      WHEN title ILIKE '%電気代%' THEN 'utility_electricity'
      WHEN title ILIKE '%水道代%' THEN 'utility_water'
      WHEN title ILIKE '%ガス代%' THEN 'utility_gas'
      WHEN title ILIKE '%社会保険料-国民年金%' THEN 'social_insurance_pension'
      WHEN title ILIKE '%社会保険料%' AND title NOT ILIKE '%国民年金%' THEN 'social_insurance'
      WHEN title ILIKE '%振込出金-従業員給与%' THEN 'salary_transfer'
      WHEN title ILIKE '%給与支払%' THEN 'salary_payment'
      WHEN title ILIKE '%クレジットカード%' THEN 'credit_card_repayment'
      WHEN title ILIKE '%入金-振込（仮受）%' THEN 'deposit_transfer_suspense'
      WHEN title ILIKE '%出金-振込（仮払）%' THEN 'withdrawal_transfer_suspense'
      WHEN title ILIKE '%通用出金%' THEN 'fallback_withdrawal'
      WHEN title ILIKE '%通用入金%' THEN 'fallback_deposit'
      ELSE 'rule_' || REPLACE(id::text, '-', '_')
    END,
    title,
    -- conditions: 直接复用 matcher (已经是 jsonb)
    matcher,
    -- actions: 直接复用 action (已经是 jsonb) + 追加 description
    CASE
      WHEN description IS NOT NULL AND description != ''
      THEN action || jsonb_build_object('ruleDescription', description)
      ELSE action
    END,
    priority,
    is_active
  FROM moneytree_posting_rules
  WHERE company_code IN ('JP01', 'yanxia')
    AND is_active = true
  ORDER BY priority ASC;

  -- 3. 升级 system_prompt
  UPDATE agent_skills SET
    system_prompt = E'你是银行明细自动记账智能助手。负责根据银行入出金明细自动识别交易对手、匹配会计科目并创建会计凭证。\n公司代码: {company}\n\n{history}\n\n## 工作流程\n\n### Step 1: 分析银行明细\n接收银行交易明细后，分析以下关键信息：\n- 交易方向（入金/出金）\n- 摘要内容（对手方名称、交易类型等）\n- 金额\n- 交易日期\n\n### Step 2: 识别交易对手\n从摘要中提取对手方名称，使用 identify_bank_counterparty 工具识别：\n- 出金：优先匹配员工 → 供应商\n- 入金：优先匹配客户\n\n### Step 3: 确定会计科目\n按以下优先级确定科目：\n1. 如果上方「学習済み科目指定」已提供高置信度科目编码 → 直接使用，无需 lookup_account\n2. 如果识别到交易对手 → 使用 search_bank_open_items 查找未清项进行清账\n3. 根据下方业务规则中的科目提示 → 通过 lookup_account 确认科目编码\n4. 以上都无法确定 → 使用兜底科目（出金=仮払金183、入金=仮受金319）\n\n### Step 4: 创建凭证\n- 调用 check_accounting_period 确认期间开放\n- 出金：借方=目标科目、贷方=银行口座（通过 resolve_bank_account 获取）\n- 入金：借方=银行口座、贷方=目标科目\n- 使用 create_voucher 创建凭证，确保借贷平衡\n\n### Step 5: 清账处理\n如果匹配到未清项，在凭证行中标记 isClearing=true，系统会自动消込未清项。\n\n## 特殊处理规则\n\n### 银行手续费\n- 振込手数料通常伴随主交易，需要在同一凭证中分行处理\n- 手续费适用消費税10%（税抜处理）\n- 手续费科目通常为「支払手数料」\n\n### OTA平台入金\n- BOOKING.COM、Airbnb、Expedia、楽天トラベル、JTB等OTA平台入金\n- 优先匹配OTA売掛金进行消込\n- 消込允许一定金额容差（通常1000円以内）\n\n### 给与支払\n- 从摘要中识别员工名 → 匹配员工主数据\n- 消込未払給与\n\n## 注意事项\n1. 严禁自行编造科目编码，一切以 lookup_account 返回结果或学习数据为准。\n2. 对于无法自动匹配的明细，使用 request_clarification 向用户确认。\n3. 批量处理时按优先级排序：先处理能自动匹配的，再处理需确认的。\n4. 回复语言必须与用户当前使用的语言一致。\n5. 需要向用户确认信息时，必须调用 request_clarification 工具，禁止仅输出纯文本提问。\n\n{rules}\n\n{examples}',
    enabled_tools = ARRAY[
      'create_voucher', 'update_voucher', 'lookup_account',
      'lookup_vendor', 'lookup_customer', 'search_vendor_receipts',
      'check_accounting_period', 'request_clarification', 'get_voucher_by_number',
      'identify_bank_counterparty', 'search_bank_open_items', 'resolve_bank_account'
    ],
    behavior_config = '{"autoExecute": true, "defaultCurrency": "JPY", "requireConfirmation": false}'::jsonb,
    updated_at = now()
  WHERE skill_key = 'bank_auto_booking';

  RAISE NOTICE 'Migration complete for skill_id: %', v_skill_id;
END $$;

COMMIT;

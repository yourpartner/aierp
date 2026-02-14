-- =========================================================================
-- 迁移后验证脚本：确认迁移成功且数据完整
-- =========================================================================

-- 1. 验证 bank_auto_booking 的规则数量
SELECT 'Step 1: Verify bank_auto_booking rules migrated' AS step;
SELECT COUNT(*) AS new_rules_count,
       MIN(priority) AS min_priority,
       MAX(priority) AS max_priority
FROM agent_skill_rules
WHERE skill_id = (SELECT id FROM agent_skills WHERE skill_key = 'bank_auto_booking' LIMIT 1);

-- 2. 列出所有迁移后的规则
SELECT 'Step 2: List all migrated rules' AS step;
SELECT rule_key, name, priority, is_active
FROM agent_skill_rules
WHERE skill_id = (SELECT id FROM agent_skills WHERE skill_key = 'bank_auto_booking' LIMIT 1)
ORDER BY priority ASC;

-- 3. 验证 bank_auto_booking 的 system_prompt 已更新
SELECT 'Step 3: Verify system_prompt updated' AS step;
SELECT skill_key,
       LEFT(system_prompt, 100) || '...' AS prompt_preview,
       CHAR_LENGTH(system_prompt) AS prompt_length
FROM agent_skills
WHERE skill_key = 'bank_auto_booking';

-- 4. 验证 enabled_tools 已更新
SELECT 'Step 4: Verify enabled_tools updated' AS step;
SELECT skill_key,
       ARRAY_LENGTH(enabled_tools, 1) AS tools_count,
       enabled_tools
FROM agent_skills
WHERE skill_key = 'bank_auto_booking';

-- 5. 确认新增的银行工具已包含
SELECT 'Step 5: Check for new bank-specific tools' AS step;
SELECT skill_key,
       'identify_bank_counterparty' = ANY(enabled_tools) AS has_identify_counterparty,
       'search_bank_open_items' = ANY(enabled_tools) AS has_search_open_items,
       'resolve_bank_account' = ANY(enabled_tools) AS has_resolve_account
FROM agent_skills
WHERE skill_key = 'bank_auto_booking';

-- 6. 验证 employee_channel_bindings 表已创建
SELECT 'Step 6: Verify employee_channel_bindings table created' AS step;
SELECT 
    table_name,
    (SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'employee_channel_bindings') AS column_count,
    (SELECT COUNT(*) FROM employee_channel_bindings) AS row_count
FROM information_schema.tables
WHERE table_name = 'employee_channel_bindings';

-- 7. 验证 AI capabilities 已添加
SELECT 'Step 7: Verify AI capabilities added' AS step;
SELECT COUNT(*) AS total_ai_caps,
       COUNT(DISTINCT role_id) AS roles_with_ai_caps
FROM role_caps
WHERE cap LIKE 'ai.%';

-- 8. 确认原始 moneytree_posting_rules 数据未受影响
SELECT 'Step 8: Verify original moneytree_posting_rules intact' AS step;
SELECT COUNT(*) AS original_rules_count,
       SUM(CASE WHEN is_active THEN 1 ELSE 0 END) AS active_count
FROM moneytree_posting_rules
WHERE company_code IN ('JP01', 'yanxia');

-- 9. 验证其他 skills 未受影响
SELECT 'Step 9: Verify other skills unchanged' AS step;
SELECT skill_key, name,
       (SELECT COUNT(*) FROM agent_skill_rules WHERE skill_id = agent_skills.id) AS rules_count
FROM agent_skills
WHERE skill_key != 'bank_auto_booking'
  AND is_active = true
ORDER BY skill_key;

-- =========================================================================
-- 预期结果：
-- - Step 1: new_rules_count 应为 17 条左右
-- - Step 4: tools_count 应增加到包含新的银行工具
-- - Step 5: 3 个新工具都应为 true
-- - Step 6: 表已创建且有列定义
-- - Step 7: AI capabilities 数量应增加
-- - Step 8: 原始规则数应保持不变
-- - Step 9: 其他 skills 的规则数应保持不变
-- =========================================================================

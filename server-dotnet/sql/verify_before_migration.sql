-- =========================================================================
-- 迁移前验证脚本：检查当前数据状态，确保安全迁移
-- =========================================================================

-- 1. 检查 bank_auto_booking skill 是否存在
SELECT 'Step 1: Check bank_auto_booking skill exists' AS step;
SELECT id, skill_key, name, company_code, is_active
FROM agent_skills
WHERE skill_key = 'bank_auto_booking';

-- 2. 检查现有的 bank_auto_booking 规则数量
SELECT 'Step 2: Current bank_auto_booking rules count' AS step;
SELECT COUNT(*) AS current_rules_count
FROM agent_skill_rules
WHERE skill_id = (SELECT id FROM agent_skills WHERE skill_key = 'bank_auto_booking' LIMIT 1);

-- 3. 检查 moneytree_posting_rules 中待迁移的规则
SELECT 'Step 3: Rules to be migrated from moneytree_posting_rules' AS step;
SELECT COUNT(*) AS rules_to_migrate, 
       STRING_AGG(DISTINCT company_code, ', ') AS companies
FROM moneytree_posting_rules
WHERE company_code IN ('JP01', 'yanxia')
  AND is_active = true;

-- 4. 列出所有待迁移的规则标题
SELECT 'Step 4: List of rules to be migrated' AS step;
SELECT company_code, title, priority, is_active
FROM moneytree_posting_rules
WHERE company_code IN ('JP01', 'yanxia')
  AND is_active = true
ORDER BY priority ASC;

-- 5. 检查 employee_channel_bindings 表是否存在
SELECT 'Step 5: Check if employee_channel_bindings exists' AS step;
SELECT EXISTS (
    SELECT FROM information_schema.tables 
    WHERE table_name = 'employee_channel_bindings'
) AS table_exists;

-- 6. 检查当前的 AI capabilities 数量
SELECT 'Step 6: Current AI capabilities count' AS step;
SELECT COUNT(*) AS ai_caps_count
FROM role_caps
WHERE cap LIKE 'ai.%';

-- 7. 检查所有 skills 的完整性
SELECT 'Step 7: All active skills overview' AS step;
SELECT skill_key, name, category, company_code, is_active,
       (SELECT COUNT(*) FROM agent_skill_rules WHERE skill_id = agent_skills.id) AS rules_count
FROM agent_skills
WHERE is_active = true
ORDER BY company_code NULLS FIRST, priority ASC;

-- =========================================================================
-- 执行结果解读：
-- - Step 1: 应返回至少 1 条 bank_auto_booking 记录
-- - Step 2: 显示当前规则数（迁移后会被替换）
-- - Step 3: 显示将迁移的规则数（应该是 17 条左右）
-- - Step 4: 列出所有待迁移规则的详细信息
-- - Step 5: 检查新表是否已存在（false 表示需要创建）
-- - Step 6: 显示现有 AI 能力数量
-- - Step 7: 显示所有 skill 的概览，确保其他 skill 不受影响
-- =========================================================================

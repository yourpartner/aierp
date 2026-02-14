# Azure 数据库迁移指南

## 📌 重要提示
**代码已部署到 Azure，但数据库迁移脚本需要手动执行！**

本次改造涉及以下数据库变更，**不会删除或覆盖任何现有业务数据**。

---

## 📊 需要执行的迁移脚本

### 1. `create_channel_bindings.sql`
**作用**: 创建员工渠道绑定表 + 为角色添加 AI 能力

**影响范围**:
- 创建 `employee_channel_bindings` 表（如果不存在）
- 为 `role_caps` 表添加 AI 相关能力（使用 `ON CONFLICT DO NOTHING`）

**安全性**: ✅ 幂等操作，可重复执行

---

### 2. `migrate_bank_rules_to_skills.sql` ⭐ **核心迁移**
**作用**: 将银行记账规则从独立表迁移到统一 Agent Skills 架构

**影响范围**:
- **删除** `agent_skill_rules` 中 **仅 bank_auto_booking** 的规则
- **插入** 从 `moneytree_posting_rules` 读取的 17 条规则
- **更新** `agent_skills` 表中 **仅 bank_auto_booking** 的配置（system_prompt, enabled_tools, behavior_config）
- **不影响** `moneytree_posting_rules` 原始数据（保持不变）
- **不影响** 其他 skill 的规则

**安全性**: ✅ 只修改银行记账相关数据，其他业务不受影响

---

### 3. `seed_agent_skills.sql` (可选)
**作用**: 刷新所有 agent_skills 的全局模板

**影响范围**:
- **删除** 全局 skill 模板（`company_code IS NULL`）
- **保留** 公司定制数据（`company_code IS NOT NULL`）
- **重建** 全局模板数据

**建议**: 如果之前没有手动修改过全局 skill 配置，可以执行此脚本获取最新模板

---

## 🔍 执行步骤

### Step 0: 迁移前验证（强烈建议）

```bash
psql <your_azure_connection_string> -f server-dotnet/sql/verify_before_migration.sql
```

**预期输出**:
- bank_auto_booking skill 存在
- 当前规则数（将被替换）
- moneytree_posting_rules 中待迁移的 17 条规则
- 其他 skills 状态正常

---

### Step 1: 创建渠道绑定表

```bash
psql <your_azure_connection_string> -f server-dotnet/sql/create_channel_bindings.sql
```

**预期结果**:
```
NOTICE:  AI capabilities assigned to all roles
NOTICE:  employee_channel_bindings table ready (0 rows)
NOTICE:  AI capabilities in role_caps: XX entries
```

---

### Step 2: 迁移银行规则 ⭐

```bash
psql <your_azure_connection_string> -f server-dotnet/sql/migrate_bank_rules_to_skills.sql
```

**预期结果**:
```
NOTICE:  Migration complete for skill_id: <uuid>
COMMIT
```

---

### Step 3: (可选) 刷新全局 Skills 模板

```bash
psql <your_azure_connection_string> -f server-dotnet/sql/seed_agent_skills.sql
```

---

### Step 4: 迁移后验证（强烈建议）

```bash
psql <your_azure_connection_string> -f server-dotnet/sql/verify_after_migration.sql
```

**预期输出**:
- ✅ bank_auto_booking 有 17 条规则
- ✅ system_prompt 已更新（长度 > 1000 字符）
- ✅ enabled_tools 包含 3 个新工具：
  - `identify_bank_counterparty`
  - `search_bank_open_items`
  - `resolve_bank_account`
- ✅ employee_channel_bindings 表已创建
- ✅ AI capabilities 已添加到 role_caps
- ✅ 原始 moneytree_posting_rules 数据完整
- ✅ 其他 skills 规则数未变

---

## 🛡️ 回滚方案（如果需要）

如果迁移后发现问题，可以执行以下 SQL 回滚：

```sql
BEGIN;

-- 1. 删除迁移的规则
DELETE FROM agent_skill_rules 
WHERE skill_id = (SELECT id FROM agent_skills WHERE skill_key = 'bank_auto_booking');

-- 2. 恢复旧的 system_prompt (需要提前备份)
-- UPDATE agent_skills SET system_prompt = '<old_prompt>' WHERE skill_key = 'bank_auto_booking';

-- 3. 原始 moneytree_posting_rules 数据未被修改，系统会自动回退使用

ROLLBACK;  -- 如果确认要回滚，改为 COMMIT
```

---

## ⚠️ 注意事项

1. **备份建议**: 虽然迁移脚本是安全的，但建议在执行前先备份数据库
2. **执行顺序**: 必须按照 Step 1 → Step 2 的顺序执行
3. **执行环境**: 确保在正确的 Azure PostgreSQL 数据库上执行
4. **权限要求**: 需要数据库的 DDL 和 DML 权限
5. **业务影响**: 迁移期间不影响现有业务，但建议在低峰期执行

---

## 📞 遇到问题？

如果迁移过程中遇到任何错误：

1. **查看错误信息**: PostgreSQL 会给出明确的错误提示
2. **检查前置条件**: 运行 `verify_before_migration.sql` 确认数据状态
3. **查看日志**: 检查 Azure PostgreSQL 的查询日志
4. **不要慌**: 所有操作都在事务中执行（BEGIN...COMMIT），出错会自动回滚

---

## ✅ 迁移完成后

1. 重启 Azure App Service（yanxia-api）确保新代码生效
2. 测试银行明细自动记账功能
3. 验证手续费配对逻辑是否正常
4. 检查 AI Agent 是否能正确使用新工具

---

**最后更新**: 2026-02-14
**相关 Commit**: 8c20411, 964e04c

# 株式会社アミストロング (旧 CompanyID=256) 迁移分析报告

## 1. 数据范围

- **旧系统**: Dump20260223 (MySQL yourpartnerdb2)
- **公司**: 株式会社アミストロング (ID=256, 前缀 AMIS)
- **排除**: 会计凭证及相关 (acdocheaders, acdocitems, acaccounts, acaccountgroups, acperiods, acbalances, acdoctypes)
- **其他数据**: 按表迁移，需映射到 Azure PostgreSQL 新结构

---

## 2. 旧系统 CompanyID=256 数据量统计

| 旧表名 | 行数 | 迁移目标(Azure) / 说明 |
|--------|------|------------------------|
| worktimes | 2210 | timesheets (日勤怠明细) |
| assettransactions | 1316 | 固定资产交易 (若新系统有对应表) |
| employeesalaries | 165 | employees.payload.salaries / 工资历史 |
| menus | 132 | 菜单权限 (若新系统按公司) |
| timesheets | 76 | timesheet_submissions (月度汇总) |
| businesspartners | 56 | 业务伙伴/客户供应商 (若新系统有) |
| salaries | 47 | 工资计算相关 (salaries 表或 payroll 相关) |
| employeecontracts | 46 | employees.payload.contracts |
| employees | 46 | employees (company_code=AMIS, payload) |
| teammembers | 46 | 团队成员 (若新系统有) |
| employeeemercontacters | 45 | employees.payload.emergencies |
| employeehireinsurances | 43 | employees.payload.insurance 等 |
| employeeendows | 38 | 社保厚生 (employees.payload 或独立表) |
| dependants | 28 | 抚养 (employees 或独立) |
| roleteams | 24 | 角色-团队 (roles 相关) |
| assets | 18 | 固定资产 (若新系统有) |
| assetclasses | 9 | 资产分类 |
| paymentterms | 9 | 支付条件 (若新系统有) |
| employeetypes | 8 | employment_types |
| roles | 7 | roles, role_capabilities |
| documenttemplates | 6 | 文档模板 (若新系统有) |
| housebanks | 6 | 银行/户头 (若新系统有) |
| positions | 6 | 职位 (若新系统有) |
| assetdepreciations | 5 | 资产折旧 |
| teams | 5 | 团队主数据 |
| users | 3 | users (company_code=AMIS) |
| companies | 1 | companies (company_code=AMIS) |
| expensedocuploads | 1 | 费用上传 (若新系统有) |
| prompts | 1 | 提示/流程 (若新系统有) |
| userstorages | 1 | 用户存储 (若新系统有) |

**合计**: 约 4404 行（不含会计凭证）。

---

## 3. 迁移顺序与依赖建议

1. **公司主数据**: companies → companies (1 条, company_code 如 AMIS)
2. **角色与用户**: roles, roleteams → roles / role_capabilities; users → users (3)
3. **员工主数据**: employees (46) → employees; 关联 employeecontracts, employeesalaries, employeeendows, employeehireinsurances, employeeemercontacters, dependants → 合并到 employees.payload 或对应新表
4. **雇用/工资基础**: employeetypes → employment_types; positions → 若存在; paymentterms → 若存在
5. **工资与考勤**: salaries → 若新系统有 salary 表; timesheets (76) → timesheet_submissions; worktimes (2210) → timesheets
6. **业务伙伴/银行**: businesspartners, housebanks → 按新系统 schema
7. **固定资产**: assets, assetclasses, assettransactions, assetdepreciations → 若新系统有对应模块
8. **菜单/模板/其他**: menus, documenttemplates, teams, teammembers, prompts, expensedocuploads, userstorages → 按新系统是否有对应表

---

## 4. 新系统 (Azure) 需确认的点

- **company_code**: 建议使用 `AMIS`（与旧系统 IMG_Prefix 一致）
- **accounts / 会计科目**: 不迁移凭证，但需确认是否迁移科目主数据；若迁移可参考 JP01 科目表复制或从旧 acaccounts 过滤 256 后转换
- **vouchers**: 明确不迁移
- **payroll_policies / payroll_deadlines**: 若新系统工资模块需要，可从 JP01 复制或按旧 salaries/employeetypes 配置
- **timesheets 与 users**: 新系统用 creatorUserId 关联 users，需为每位有考勤的员工建立 user（参考 YP01 迁移方式）

---

## 5. 实施前建议

1. 在 Azure 先创建 company_code=AMIS 的公司与一名 admin 用户，确认可登录。
2. 确认新系统各模块（员工、工资、考勤、固定资产、业务伙伴等）的表结构，再逐表做字段映射与转换脚本。
3. 员工号/编码：旧表 EmployeeNo 与 IMG_EmployeeNo 规则（如 AMIS001）需映射到新系统 employee_code。
4. 迁移脚本建议：按上述顺序、分表生成 SQL 或 Python 脚本，使用 `WHERE NOT EXISTS` 或唯一键避免重复插入，且仅操作 company_code='AMIS'，不影响其他公司。

---

## 6. 如何执行迁移（直接连 Azure，不通过项目触发）

- **脚本**: `scripts/run_amis_migration.py`
- **方式**: 本机运行 Python，直接连接 Azure PostgreSQL 执行 INSERT（无 SQL 脚本放入项目、无重启/初始化触发）。
- **连接**: 迁移到 **Azure** 时请务必设置环境变量 `AZURE_PG_CONNECTION`；未设置时脚本会回退到 `server-dotnet/appsettings.json` 的 `ConnectionStrings.Default`（多为本地库，慎防误写）。
  - 示例（PowerShell）: `$env:AZURE_PG_CONNECTION="Host=xxx.postgres.database.azure.com;Port=5432;Database=xxx;Username=xxx;Password=xxx;Ssl Mode=Require"`
  - 示例（CMD）: `set AZURE_PG_CONNECTION=Host=xxx.postgres.database.azure.com;Port=5432;Database=xxx;Username=xxx;Password=xxx;Ssl Mode=Require`
- **安全**: 仅插入 `company_code='AMIS'`，全部使用 `ON CONFLICT DO NOTHING`，不 UPDATE 已有数据，不触碰其他公司。
- **排除**: 会计凭证、timesheets、worktimes 不迁移。
- **执行**: `pip install psycopg2-binary` 后运行 `python scripts/run_amis_migration.py`。

---

*本报告为迁移分析与执行说明。*

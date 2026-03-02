# 日本中小企业多语言 Agent 工资系统（200人以内）详细设计书

> 目标：作为新项目立项与开发蓝图，不直接实施代码  
> 语言：中文（业务术语保留日文）  
> 版本：v1.0（基于 `yanxia` 现有工资模块经验整理）

---

## 1. 项目定位与边界

### 1.1 目标用户
- 员工规模：1-200人
- 典型行业：人才派遣（SES/一般派遣）、酒店、零售、餐饮、服务业
- 特征：外籍员工比例高、薪资结构混合（月给 + 时给）、人事/财务兼岗

### 1.2 核心价值
- 管理者：从“收集勤怠→核算→发明细→导报表”的重体力工作，降为“发起 + 复核”
- 员工：可通过邮件/URL入口，用母语提交 timesheet（上传或手工入力）
- 企业：多语言覆盖（日/英/中简/中繁/韩），减少沟通和工资争议

### 1.3 MVP边界（建议）
- 必做：月次工资、奖金（賞与）、法定基础扣缴、明细发放、银行振込数据导出
- 必做：年末调整（年末調整）**手工录入调整金额**并自动入账到当月工资
- 暂不做：年末调整全自动判定、复杂工会规则、跨法人合并计税、住民税异动自动申告

---

## 2. 日本工资计算规则（系统实现视角）

> 说明：以下为系统设计层面的“可计算规则”，非法律意见。费率和税表必须数据化管理，禁止硬编码。

### 2.1 支给（Earnings）
- 基本给（BASE）
- 通勤手当（COMMUTE）
- 时间外手当（OVERTIME_STD，通常 25%）
- 60時間超時間外（OVERTIME_60，通常 50%）
- 休日労働手当（HOLIDAY_PAY，通常 35%）
- 深夜手当（LATE_NIGHT_PAY，22:00-5:00，通常 25%）
- 賞与（BONUS）

### 2.2 控除（Deductions）
- 健康保険（HEALTH_INS）
- 介護保険（CARE_INS，40-64岁适用）
- 厚生年金（PENSION）
- 雇用保険（EMP_INS）
- 源泉所得税（WHT，按源泉徴収税額表）
- 住民税（RESIDENT_TAX，特別徴収）
- 欠勤控除（ABSENCE_DEDUCT）
- 年末调整差额（YEAR_END_ADJ，正值=还付，负值=追徴）

### 2.3 关键规则要点
1. **残業割増**
   - 法定时间外：>=25%
   - 月60h超：>=50%（中小企业已适用）
   - 深夜：+25%
   - 法定休日：+35%
   - 重叠时采用加算（如 60h超 + 深夜）

2. **介護保険适用年龄**
   - 原则：40岁当月起、65岁当月止（系统按月龄判断）

3. **源泉所得税**
   - 甲欄/乙欄/丙欄差异必须体现在员工税区分配置中
   - 月额表与日额表按支付形态切换（MVP先做月额表甲乙）

4. **住民税特別徴収**
   - 年度周期：6月-次年5月
   - 月份扣缴金额来自年度通知数据（不是实时算税）

5. **雇用保険基数**
   - 一般按工资基数计提（系统中可配置“基数=賃金+通勤”策略）

6. **社保/年金标准报酬月额**
   - 应由费率与等级表驱动（`law_rates` / `health_standard` / `pension_standard`）

7. **賞与計算**
   - 奖金应与月次工资分批处理（独立 run_type）
   - 奖金相关社会保険与源泉需按“賞与”规则处理（由法规表驱动，不写死）

8. **年末調整（本项目范围）**
   - 本项目采用“管理员手工录入调整金额”模式
   - 录入后系统在指定月份自动生成 `YEAR_END_ADJ` 项并影响当月 net
   - 必须保留录入依据、录入人、审批日志（可审计）

---

## 3. 最常见工资计算场景（产品化优先级）

### S1 月給员工（常规）
- 输入：基本给 + 固定手当 + 住民税 + 基本社保配置
- 输出：标准工资单、源泉、社保、住民税
- 风险：缺失税区分/社保基数

### S2 時給员工（工时驱动）
- 输入：timesheet + 时给单价 + 各类加班小时
- 输出：按工时计算总支给和扣除
- 风险：无 timesheet 时不能直接计算（需人工工时补录）

### S3 月給但缺勤怠
- 输入不全：无 timesheet
- 策略：可按标准工时估算（标记 warning）或禁止出账（可配置）

### S4 深夜/休日混合
- 输入：日别出退勤 + 深夜区间
- 输出：拆分 normal/overtime/holiday/lateNight 小计

### S5 月60h超加班
- 输入：月累计 overtime
- 输出：前60h 与超60h 分段倍率计算

### S6 外籍员工多语言明细
- 输出：同一计算结果，多语模板渲染（JP/EN/ZH-S/ZH-T/KO）

### S7 住民税新年度切换（6月）
- 输入：新 fiscal_year 的 12个月住民税计划
- 输出：按当前月份自动取对应字段（june...may）

### S8 工资前置检查失败
- 问题：基本给缺失、工时不足、员工状态不活跃
- 输出：preflight 报告 + 阻断/放行策略

### S9 奖金月（賞与支給）
- 输入：奖金金额（手工或批量导入）+ 奖金税区分配置
- 输出：奖金明细、奖金扣缴、奖金振込数据
- 风险：把奖金误算进月给批次导致重复扣缴

### S10 年末调整（手工差额）
- 输入：管理员录入还付/追徴金额（按员工）
- 输出：当月工资单新增 `YEAR_END_ADJ` 项、并自动影响实发
- 风险：录入口径不统一（需模板与复核）

---

## 4. 基于 yanxia 现有实现的“可复用能力图”

以下文件体现了可直接借鉴的实现思路：

- 计算入口与规则执行：`server-dotnet/Modules/HrPayrollModule.cs`
- 运行保存与会计联动：`server-dotnet/Modules/PayrollService.cs`
- 前置校验：`server-dotnet/Modules/PayrollPreflightService.cs`
- 自动化决策调度：`server-dotnet/Modules/PayrollAgentService.cs`
- 法规费率数据层：`server-dotnet/Infrastructure/LawDatasetService.cs`
- AI工时解析：`server-dotnet/Infrastructure/Skills/TimesheetAiParser.cs`
- 数据库结构：`server-dotnet/sql/payroll_full_migration.sql`

### 4.1 推荐复用
1. `PayrollService` 的 run/entry/trace 三层持久化模型  
2. 住民税按 6月-5月 fiscal year 的查询逻辑  
3. Preflight（先验检查）机制  
4. 自动生成 accounting draft 并关联 voucher 的思路  
5. 异常检测（diff summary / anomaly）机制

### 4.2 推荐重构
`HrPayrollModule.cs` 目前承担了过多职责（API + 规则编译 + 计算 + timesheet聚合 + 住民税管理）。新项目建议拆分为：
- `PayrollRuleEngine`
- `AttendanceAggregator`
- `TaxAndInsuranceProvider`
- `PayslipRenderer`
- `PayrollApplicationService`

---

## 5. 新项目目标架构（建议）

## 5.1 分层
- **Presentation**：Admin Web + Employee Portal（多语言）
- **Application**：PayrollApplicationService（run/save/confirm/send）
- **Domain**：PayrollRuleEngine（纯计算，不依赖HTTP/DB）
- **Infrastructure**：DB、邮件、文件存储、AI解析、银行/会计集成

## 5.2 技术栈建议
- Backend：.NET 8 + PostgreSQL
- Frontend：Next.js（i18n）
- Job：Hangfire / Quartz
- PDF：QuestPDF
- Excel：ClosedXML
- 邮件：SendGrid / SES
- AI：LLM + OCR（仅用于“识别与解释”，不直接决定金额）

---

## 6. 关键数据模型（核心表）

基于 `payroll_full_migration.sql` 以及新需求补充：

1. `employees`
   - 基本信息、雇用形态、税区分、社保加入、银行信息、语言偏好

2. `timesheet_submissions`（新增）
   - 员工提交记录（source=file/manual, token_id, status, parsed_confidence）

3. `timesheet_entries`
   - 日别工时、休憩、状态（submitted/approved/locked）

4. `payroll_policies`
   - 可执行规则配置（倍率、费率策略、项目启停）

5. `law_rates` / `withholding_table` / `resident_tax_schedules`
   - 法规数据主表（版本化 + effective period）

6. `payroll_runs`
   - 每月执行批次（manual/auto/agent）

7. `payroll_run_entries`
   - 员工级结果（payroll_sheet/accounting_draft/diff_summary）

8. `payslips`（新增）
   - PDF地址、发送状态、语言版本、发送时间

9. `ai_payroll_tasks`
   - 异常复核与审批任务

10. `bonus_runs`（新增）
   - 奖金批次主表（period, payment_date, status, metadata）

11. `bonus_run_entries`（新增）
   - 员工奖金条目（gross_bonus, deductions, net_bonus, payslip_id）

12. `year_end_adjustments`（新增）
   - 年末调整录入表（employee_id, target_month, adjustment_amount, reason, entered_by, approved_by）

---

## 7. Agent 业务流设计（你提出的核心）

### 7.1 勤怠收集 Agent（邮件/URL）
1. 管理者点击“开始本月收集”
2. 系统生成每位员工唯一 token URL（带过期时间）
3. 发送多语言邮件（员工语言自动匹配）
4. 员工可：
   - 上传文件（CSV/Excel/PDF/图片）
   - 手工输入（日别）
5. Agent 解析后入库，给出置信度和疑点
6. 未提交员工自动 reminder（T+2、T+5、截止前1天）

### 7.2 计算 Agent
1. Preflight 检查（工资信息/工时覆盖率/确认状态）
2. 批量执行计算（逐员工容错）
3. 产出：
   - payroll_sheet（明细）
   - accounting_draft（会计草稿）
   - anomaly flags（偏差与异常）

### 7.3 奖金 Agent（賞与）
1. 管理者创建奖金批次（支给日、对象员工、金额来源）
2. Agent 批量计算奖金扣缴（按奖金规则）
3. 产出：
   - bonus_payslip（奖金明细）
   - 振込数据（奖金批次）
   - 会计草稿（奖金费用/预提）

### 7.4 年末调整 Agent（手工录入型）
1. 管理者下载模板录入差额（employee_code, adjustment_amount, note）
2. Agent 校验格式与重复录入
3. 审批通过后写入 `year_end_adjustments`
4. 当月工资计算自动注入 `YEAR_END_ADJ`

### 7.5 发放 Agent
1. 生成多语言 payslip PDF
2. 批量邮件发送
3. 失败重试 + 投递状态回执

---

## 8. 计算引擎实现细节（必须可审计）

## 8.1 设计原则
- 金额计算必须“确定性”与“可追溯”
- LLM 不可直接输出最终金额，只可用于：
  - 非结构化输入解析
  - 异常解释与提示文案

## 8.2 计算流水（伪流程）
1. 读取员工主数据 + 当月 timesheet + policy + law dataset
2. 计算支给项（BASE/COMMUTE/OT/HOLIDAY/LATE_NIGHT）
3. 计算控除项（社保、雇保、源泉、住民税、缺勤）
4. 注入年末调整项（若有）：`YEAR_END_ADJ`
5. 计算 net
6. 生成计算 trace（每个 item 的 base/rate/formula/version）

## 8.3 关键公式（系统实现形态）
- `OvertimePay = OvertimeHours * HourlyRate * OvertimeMultiplier`
- `HealthIns = HealthBase * HealthRate`
- `CareIns = (Age in 40-64) ? HealthBase * CareRate : 0`
- `Pension = PensionBase * PensionRate`
- `EmpIns = EmploymentBase * EmploymentRate`
- `WHT = Lookup(withholding_table, taxableAmount, dependents, category)`
- `ResidentTax = resident_tax_schedules[fiscal_year][month_column]`
- `Net = Gross - Deductions + YearEndAdjustment`

## 8.4 奖金与年末调整实现约束
- 奖金与月给必须独立批次（`run_type = bonus` / `run_type = monthly`）
- 年末调整金额来源仅接受管理员录入（不做自动推断）
- 年末调整入账时必须记录：
  - adjustment source（manual import / manual input）
  - operator / approver
  - timestamp / change history
- 工资单展示：
  - `YEAR_END_ADJ > 0` 显示“年末調整還付”
  - `YEAR_END_ADJ < 0` 显示“年末調整追徴”

---

## 9. API 设计建议（最小集合）

### 9.1 员工入口
- `POST /timesheet-collection/start`
- `GET /timesheet/submit/{token}`
- `POST /timesheet/submit/{token}/upload`
- `POST /timesheet/submit/{token}/manual`
- `POST /timesheet-collection/remind`

### 9.2 管理端
- `POST /payroll/preflight`
- `POST /payroll/run`
- `POST /payroll/save`
- `POST /payroll/payslips/generate`
- `POST /payroll/payslips/send`
- `GET /payroll/runs`
- `GET /payroll/runs/{id}/entries`
- `GET /payroll/reports/export`
- `POST /bonus/run`
- `POST /bonus/save`
- `POST /bonus/payslips/send`
- `POST /year-end-adjustments/import`
- `POST /year-end-adjustments/{id}/approve`
- `GET /year-end-adjustments`

### 9.3 法规与税率
- `GET /law-rates`
- `POST /law-rates/import`
- `GET /resident-tax/employee/{id}/current`
- `POST /resident-tax/import`

---

## 10. 权限与合规

### 10.1 权限
- `payroll:read`, `payroll:write`, `payroll:approve`, `timesheet:submit`, `timesheet:proxy`

### 10.2 个人信息保护（必须）
- 加密：静态加密（DB/文件）+ 传输 TLS
- 审计：工资查看/导出/发送日志
- 数据最小化：只存必要字段
- 多租户隔离：`company_code` 强制过滤 + 行级策略

### 10.3 法规更新策略
- 每年固定窗口更新：
  - 社保料率（健康/介护/厚生/雇保）
  - 源泉税额表版本
  - 住民税年度
- 发布前跑回归集（历史月份重算差异）

---

## 11. 测试策略（必须覆盖）

### 11.1 单元测试
- 计算引擎每个 item 的边界值
- 年龄临界点（39→40、64→65）
- 6月住民税年度切换
- overtime 60h 分段
- 奖金批次扣缴与月给批次隔离
- 年末调整正负金额对 net 的影响

### 11.2 集成测试
- 上传 timesheet（CSV/Excel/图片）→ 解析 → 计算 → 明细发送闭环
- 批量200人性能测试（目标：计算<5分钟）

### 11.3 回归测试数据集
- 至少 12 个标准员工样本场景：
  - 月给、时给、夜勤、休日、外籍、无住民税、介护适用、欠勤、超60h、奖金月、年末调整还付/追徴等

---

## 12. 里程碑计划（仅项目创建期）

### M1（2周）：计算核心可跑通
- 规则引擎
- law dataset 读取
- 单员工计算 + trace

### M2（2周）：批量执行与落库
- run/entry/trace 表
- preflight
- 批量计算 + 错误容错
- 奖金 run/entry 表骨架

### M3（2周）：员工收集入口
- token URL + 上传/手填
- AI解析（CSV优先，OCR次之）
- reminder

### M4（2周）：明细发放与报表
- payslip PDF
- 邮件发送
- 银行振込CSV/会计导出
- 奖金明细/振込导出
- 年末调整导入模板与审批流

---

## 13. 新项目创建建议（基于 yanxia 经验）

1. 不要复制 `HrPayrollModule.cs` 大一体结构；拆层再实现  
2. 优先复刻 `PayrollService` 的 run/entry 持久化模型  
3. `LawDatasetService` 必须改成“纯数据驱动 + 版本化”，去掉样例硬编码  
4. Timesheet AI 解析保留，但加入“置信度阈值 + 人工确认”
5. 多语言先覆盖员工端与邮件模板，管理端可后补

---

## 14. 最终结论（可行性）

- **技术可行**：高（已有 `yanxia` 参考实现）
- **商业可行**：高（200人以内企业有明确痛点）
- **关键成功点**：
  1) 规则计算可审计  
  2) 法规数据可更新  
  3) 员工入口足够简单（邮件/URL）  
  4) 多语言体验领先现有产品  

这份设计书可直接作为你新项目的立项与技术蓝图。


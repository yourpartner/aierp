# 人才派遣版 功能模块详解

## 模块清单

| 模块ID | 模块名称 | Phase | 依赖 |
|--------|---------|-------|------|
| staffing_resource_pool | 资源池管理 | 1 | hr_core, finance_core |
| staffing_project | 案件管理 | 1 | staffing_resource_pool, crm |
| staffing_contract | 契约管理 | 1 | staffing_resource_pool, staffing_project |
| staffing_timesheet | 勤怠連携 | 2 | hr_core, staffing_contract |
| staffing_billing | 请求管理 | 2 | staffing_contract, finance_core |
| staffing_analytics | 分析报表 | 3 | staffing_contract, staffing_billing |
| staffing_email | 邮件自动化 | 4 | ai_core |
| staffing_portal | 员工门户 | 4 | hr_core, staffing_contract |
| staffing_ai | AI智能助手 | 5 | ai_core, staffing_resource_pool, staffing_project |

---

## Phase 1: 基础功能

### 1.1 资源池管理 (staffing_resource_pool)

#### 功能概述

统一管理所有类型的人力资源，包括自社社员、个人事业主、BP要员和候选人。

#### 资源类型

| 类型 | 代码 | 说明 | 成本来源 |
|------|------|------|----------|
| 自社社员 | employee | 公司正式员工 | 工资计算模块 |
| 个人事业主 | freelancer | 个人承包者 | 注文书/请求书 |
| BP要员 | bp | 供应商派遣的人员 | 供应商请求 |
| 候选人 | candidate | 商谈中的潜在资源 | 无 |

#### 可用状态

| 状态 | 说明 |
|------|------|
| available | 可用，可以推荐 |
| assigned | 稼働中，已有契约 |
| ending_soon | 当前契约即将结束 |
| unavailable | 不可用 |

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/resources | 资源列表（支持筛选） |
| GET | /staffing/resources/{id} | 资源详情 |
| POST | /staffing/resources | 创建资源 |
| PUT | /staffing/resources/{id} | 更新资源 |
| PUT | /staffing/resources/{id}/availability | 更新可用状态 |
| GET | /staffing/resources/available | 获取可用资源 |

#### 前端页面

| 页面 | 路径 | 说明 |
|------|------|------|
| 资源列表 | /staffing/resources | 资源池一览 |
| 新建资源 | /staffing/resource/new | 创建新资源 |

---

### 1.2 案件管理 (staffing_project)

#### 功能概述

管理客户的人员需求，从需求登记到入场的完整流程。

#### 案件状态

```
draft (草稿)
  │
  ▼
open (募集中)
  │
  ▼
matching (选考中) ←→ 添加/移除候选人
  │
  ▼
filled (已入场) ─→ closed (终了)
  │
  ▼
cancelled (取消)
```

#### 候选人状态

```
recommended (推荐) → client_review (客户审核)
                            │
                            ▼
                     interviewing (面试中)
                            │
                   ┌────────┴────────┐
                   ▼                  ▼
             offered (发Offer)   rejected (被拒)
                   │
                   ▼
             accepted (接受) → 创建契约
                   │
                   ▼
             withdrawn (辞退)
```

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/projects | 案件列表 |
| GET | /staffing/projects/{id} | 案件详情 |
| POST | /staffing/projects | 创建案件 |
| PUT | /staffing/projects/{id} | 更新案件 |
| PUT | /staffing/projects/{id}/status | 更新状态 |
| GET | /staffing/projects/{id}/candidates | 候选人列表 |
| POST | /staffing/projects/{id}/candidates | 添加候选人 |
| PUT | /staffing/projects/{id}/candidates/{cid} | 更新候选人状态 |

---

### 1.3 契约管理 (staffing_contract)

#### 功能概述

管理派遣/SES/请负契约，包括精算条件设置。

#### 契约类型

| 类型 | 代码 | 说明 | 特殊要求 |
|------|------|------|----------|
| 派遣 | dispatch | 劳动者派遣 | 遵守派遣法 |
| SES | ses | 业务委托/准委任 | 常见于IT行业 |
| 请负 | contract | 成果物交付 | 验收后结算 |

#### 精算类型

| 类型 | 说明 | 示例 |
|------|------|------|
| fixed | 固定月额 | 60万/月，不论工时 |
| hourly | 时间精算 | 4,000円/h × 实际工时 |
| range | 上下限精算 | 140-180h 内 60万，超出/不足按比例 |

#### 精算计算示例

**Range 精算（140-180h，基本60万）：**

```
实际工时 150h → 在范围内 → 60万

实际工时 190h → 超出 10h → 60万 + 10h × (60万÷160h) = 60万 + 3.75万 = 63.75万

实际工时 130h → 不足 10h → 60万 - 10h × (60万÷160h) = 60万 - 3.75万 = 56.25万
```

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/contracts | 契约列表 |
| GET | /staffing/contracts/{id} | 契约详情 |
| POST | /staffing/contracts | 创建契约 |
| PUT | /staffing/contracts/{id} | 更新契约 |
| PUT | /staffing/contracts/{id}/status | 更新状态 |
| GET | /staffing/contracts/expiring | 即将到期契约 |

---

## Phase 2: 工时与请求

### 2.1 勤怠連携 (staffing_timesheet)

#### 功能概述

将现有勤怠功能与派遣契约关联，进行月次工时汇总和精算计算。

#### 数据流

```
员工每周工时录入
        │
        ▼
月末自动汇总 → staffing_timesheet_summary
        │
        ▼
按契约计算精算
        │
        ├── 基本料金
        ├── 残业料金
        └── 精算调整
        │
        ▼
请求书生成
```

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/timesheets | 月次汇总列表 |
| GET | /staffing/timesheets/{contractId}/{yearMonth} | 特定契约月次详情 |
| POST | /staffing/timesheets/calculate | 计算精算 |
| PUT | /staffing/timesheets/{id}/confirm | 确认 |

---

### 2.2 请求管理 (staffing_billing)

#### 功能概述

向客户发送请求书，管理回款。

#### 请求书状态

```
draft (草稿)
  │
  ▼
confirmed (确定) → issued (发行)
                        │
                        ▼
                   sent (已送付)
                        │
            ┌───────────┼───────────┐
            ▼           ▼           ▼
       paid (入金)  partial_paid  overdue (逾期)
```

#### 请求书生成流程

```
选择期间 + 客户
        │
        ▼
获取该客户下所有活跃契约
        │
        ▼
获取每个契约的勤怠汇总
        │
        ▼
生成请求明细行
        │
        ▼
计算合计（税抜 + 消费税）
        │
        ▼
创建请求书
```

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/invoices | 请求书列表 |
| GET | /staffing/invoices/{id} | 请求书详情 |
| POST | /staffing/invoices/generate | 自动生成 |
| PUT | /staffing/invoices/{id}/confirm | 确定 |
| PUT | /staffing/invoices/{id}/send | 发送 |
| PUT | /staffing/invoices/{id}/payment | 记录入金 |

---

## Phase 3: 分析报表

### 3.1 分析报表 (staffing_analytics)

#### 功能概述

提供稼働率、売上、利益等分析报表。

#### 报表类型

| 报表 | 说明 |
|------|------|
| 仪表盘概览 | KPI 指标总览 |
| 月次売上推移 | 12个月売上/利益趋势 |
| 顾客别売上 | 按客户分析贡献度 |
| 资源别売上 | 按资源分析稼働和收益 |
| 稼働率推移 | 资源利用率趋势 |
| 契约到期预警 | 30天内到期契约 |
| 契约类型分布 | 派遣/SES/请负 分布 |

#### KPI 指标

| 指标 | 计算方式 |
|------|----------|
| 稼働率 | 稼働中资源 / 总可用资源 × 100% |
| 月売上 | 当月请求总额 |
| 月利益 | 当月売上 - 当月原价 |
| 利益率 | 月利益 / 月売上 × 100% |

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/analytics/dashboard | 仪表盘 |
| GET | /staffing/analytics/monthly-revenue | 月次売上 |
| GET | /staffing/analytics/revenue-by-client | 顾客别売上 |
| GET | /staffing/analytics/revenue-by-resource | 资源别売上 |
| GET | /staffing/analytics/utilization-trend | 稼働率推移 |
| GET | /staffing/analytics/expiring-contracts | 到期预警 |

---

## Phase 4: 自动化与门户

### 4.1 邮件自动化 (staffing_email)

#### 功能概述

自动收取和发送邮件，AI 内容识别，邮件模板管理。

#### 邮件模板类型

| 类型 | 说明 | 触发时机 |
|------|------|----------|
| project_intro | 案件推荐 | 向候选人推荐案件 |
| interview_schedule | 面试安排 | 协调面试时间 |
| contract_confirm | 契约确认 | 发送契约书 |
| invoice_send | 请求书送付 | 发送请求书 |
| timesheet_reminder | 工时提醒 | 周末提醒填写 |
| contract_expiry | 契约到期提醒 | 到期前30天/7天 |

#### 自动化规则

| 触发类型 | 说明 |
|----------|------|
| email_received | 收到邮件时 |
| entity_created | 业务对象创建时 |
| entity_updated | 业务对象更新时 |
| schedule | 定时触发 |

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/email/accounts | 邮件账户 |
| GET | /staffing/email/templates | 邮件模板 |
| POST | /staffing/email/templates | 创建模板 |
| GET | /staffing/email/inbox | 收件箱 |
| POST | /staffing/email/inbox/{id}/parse | AI解析邮件 |
| POST | /staffing/email/send | 发送邮件 |
| POST | /staffing/email/send-template | 模板发送 |
| GET | /staffing/email/rules | 自动化规则 |

---

### 4.2 员工门户 (staffing_portal)

#### 功能概述

员工/个人事业主的自助服务门户。

#### 功能矩阵

| 功能 | 自社社员 | 个人事业主 | BP要员 |
|------|----------|------------|--------|
| 工时填写 | ✓ | ✓ | ○(只读) |
| 工资明细 | ✓ | - | - |
| 证明书申请 | ✓ | - | - |
| 注文书查收 | - | ✓ | - |
| 请求书提交 | - | ✓ | - |
| 入金确认 | - | ✓ | - |

#### API 端点

| 方法 | 路径 | 说明 | 用户类型 |
|------|------|------|----------|
| GET | /portal/dashboard | 门户首页 | 全员 |
| GET | /portal/timesheets | 工时列表 | 全员 |
| POST | /portal/timesheets | 填写工时 | 全员 |
| POST | /portal/timesheets/{id}/submit | 提交审批 | 全员 |
| GET | /portal/payslips | 工资明细 | 社员 |
| GET | /portal/certificates | 证明书申请 | 社员 |
| POST | /portal/certificates | 提交申请 | 社员 |
| GET | /portal/orders | 注文书列表 | 个人事业主 |
| POST | /portal/orders/{id}/accept | 签收注文书 | 个人事业主 |
| GET | /portal/invoices | 请求书列表 | 个人事业主 |
| POST | /portal/invoices | 提交请求书 | 个人事业主 |
| GET | /portal/payments | 入金确认 | 个人事业主 |

---

## Phase 5: AI 智能助手

### 5.1 AI 智能助手 (staffing_ai)

#### 功能概述

AI 驱动的案件解析、人才匹配、沟通自动化、市场分析、流失预测。

#### 功能清单

| 功能 | 说明 |
|------|------|
| 案件解析 | 从邮件自动提取案件需求 |
| 人才匹配 | 语义级匹配候选人 |
| 沟通生成 | 自动生成个性化邮件 |
| 市场分析 | 基于历史数据的定价建议 |
| 流失预测 | 风险预警和建议行动 |
| AI 工作台 | 综合待办和优先建议 |

#### API 端点

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | /staffing/ai/parse-project-request | 解析案件需求 |
| POST | /staffing/ai/match-candidates | 匹配候选人 |
| GET | /staffing/ai/project/{id}/recommendations | 案件推荐 |
| POST | /staffing/ai/generate-outreach | 生成沟通邮件 |
| POST | /staffing/ai/market-analysis | 市场分析 |
| GET | /staffing/ai/churn-alerts | 流失预警 |
| GET | /staffing/ai/dashboard | AI 工作台 |

---

## 前端路由汇总

```typescript
// 人才派遣模块路由
const staffingRoutes = [
  // 资源池
  { path: '/staffing/resources', component: ResourcePoolList },
  { path: '/staffing/resource/new', component: ResourcePoolForm },
  
  // 案件
  { path: '/staffing/projects', component: ProjectsList },
  { path: '/staffing/project/new', component: ProjectForm },
  
  // 契约
  { path: '/staffing/contracts', component: ContractsList },
  { path: '/staffing/contract/new', component: ContractForm },
  
  // 勤怠
  { path: '/staffing/timesheets', component: TimesheetSummaryList },
  
  // 请求
  { path: '/staffing/invoices', component: InvoicesList },
  
  // 分析
  { path: '/staffing/analytics', component: AnalyticsDashboard },
  
  // 邮件
  { path: '/staffing/email/inbox', component: EmailInbox },
  { path: '/staffing/email/templates', component: EmailTemplates },
  { path: '/staffing/email/rules', component: EmailRules },
  
  // AI
  { path: '/staffing/ai/matching', component: AiMatching },
  { path: '/staffing/ai/market', component: AiMarketAnalysis },
  { path: '/staffing/ai/alerts', component: AiAlerts },
]

// 员工门户路由
const portalRoutes = [
  { path: '/portal/dashboard', component: PortalDashboard },
  { path: '/portal/timesheet', component: PortalTimesheet },
  { path: '/portal/payslip', component: PortalPayslips },
  { path: '/portal/certificates', component: PortalCertificates },
  { path: '/portal/orders', component: PortalOrders },
  { path: '/portal/invoices', component: PortalInvoices },
  { path: '/portal/payments', component: PortalPayments },
]
```


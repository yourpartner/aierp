# 人才派遣行业版 ERP 系统

> 基于模块化架构的人才派遣行业解决方案

## 目录

- [系统概述](#系统概述)
- [系统架构](#系统架构)
- [功能模块](#功能模块)
- [AI 智能场景](#ai-智能场景)
- [数据库设计](#数据库设计)
- [API 接口](#api-接口)
- [部署说明](#部署说明)

---

## 系统概述

### 背景

人才派遣（Staffing）行业的核心业务是将人力资源与客户需求进行匹配。传统的 ERP 系统无法满足该行业的特殊需求：

- 资源的多样性（自社社员、个人事业主、BP要员）
- 契约形态的复杂性（派遣契约、业务委托契约、SES）
- 精算逻辑的多样性（月额固定、时间精算、上下限精算）
- 大量的沟通协调工作

### 解决方案

本系统在标准 ERP 基础上，通过模块化架构扩展人才派遣行业专属功能，实现：

1. **统一资源池管理** - 管理所有类型的人力资源
2. **全流程案件管理** - 从需求到入场的完整流程
3. **智能契约管理** - 支持多种契约类型和精算模式
4. **AI 驱动的匹配与沟通** - 大幅提升营业效率
5. **员工自助门户** - 减少行政事务负担

### 版本信息

- 版本：1.0.0
- 发布日期：2024年
- 技术栈：.NET 8 + Vue 3 + PostgreSQL

---

## 系统架构

### 整体架构

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              前端应用层                                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐        │
│  │  管理后台   │  │  员工门户   │  │  移动端     │  │  API 对接   │        │
│  │  (Vue 3)    │  │  (Portal)   │  │  (H5/App)   │  │  (外部系统) │        │
│  └─────────────┘  └─────────────┘  └─────────────┘  └─────────────┘        │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              API 网关层                                      │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │  认证 (JWT)  │  限流  │  日志  │  路由  │  版本控制                 │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              模块服务层                                      │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                         核心模块 (Core)                              │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐                │   │
│  │  │ 认证    │  │ 财务    │  │ HR      │  │ AI      │                │   │
│  │  │ Auth    │  │ Finance │  │ Core    │  │ Core    │                │   │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘                │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                       人才派遣模块 (Staffing)                        │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐  │   │
│  │  │ 资源池  │  │ 案件    │  │ 契约    │  │ 勤怠    │  │ 请求    │  │   │
│  │  │ Pool    │  │ Project │  │ Contract│  │Timesheet│  │ Billing │  │   │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘  └─────────┘  │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐                            │   │
│  │  │ 分析    │  │ 邮件    │  │ AI助手  │                            │   │
│  │  │Analytics│  │ Email   │  │Staffing │                            │   │
│  │  └─────────┘  └─────────┘  │   AI    │                            │   │
│  │                            └─────────┘                            │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                        员工门户 (Portal)                             │   │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐                │   │
│  │  │ 工时    │  │ 工资    │  │ 证明书  │  │ 个人事业│                │   │
│  │  │Timesheet│  │ Payslip │  │ Cert    │  │ 主专属  │                │   │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘                │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              数据存储层                                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐        │
│  │ PostgreSQL  │  │   Redis     │  │   MinIO     │  │ Elasticsearch│       │
│  │  (主数据)   │  │  (缓存)     │  │  (文件)     │  │  (搜索)      │       │
│  └─────────────┘  └─────────────┘  └─────────────┘  └─────────────┘        │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 模块化架构

系统采用模块化架构，通过配置决定启用哪些模块：

```json
// appsettings.json
{
  "Edition": {
    "Type": "Staffing",           // Standard / Staffing / Retail
    "DisplayName": "人才派遣版",
    "EnabledModules": [],          // 空表示启用该版本的所有模块
    "DisabledModules": []          // 可选择性禁用某些模块
  }
}
```

模块分类：

| 分类 | 说明 | 示例 |
|------|------|------|
| Core | 核心模块，所有版本必须 | auth_core, finance_core, hr_core, ai_core |
| Standard | 标准版模块 | inventory, crm, sales, purchase |
| Staffing | 人才派遣版模块 | resource_pool, project, contract, billing |
| Extension | 扩展模块 | notifications, moneytree |

### Schema 驱动架构

系统采用 **Schema 驱动** 的设计模式：

1. **schemas 表**: 存储每个实体的 JSON Schema、UI 配置、查询配置等
2. **通用 CRUD 端点**: `/api/{entity}` 提供标准的增删改查
3. **业务逻辑端点**: 各模块只实现特殊的业务逻辑端点
4. **TableFor 映射**: 实体名到表名的映射

```
schemas 表
├── schema (JSON Schema 验证)
├── ui (列表/表单配置)
├── query (查询允许字段)
├── numbering (自动编号规则)
└── ai_hints (AI 提示)
          │
          ▼
通用 CRUD 端点                 业务逻辑端点
/api/{entity}                  /api/{entity}/特殊操作
├── GET    (列表)              ├── /from-employee
├── POST   (创建)              ├── /terminate
├── GET/:id (详情)             ├── /renew
├── PUT/:id (更新)             └── /candidates
└── DELETE/:id (删除)

---

## 功能模块

### Phase 1: 基础功能

#### 1.1 资源池管理 (ResourcePoolModule)

统一管理所有类型的人力资源。

**资源类型：**

| 类型 | 说明 | 成本计算 |
|------|------|----------|
| employee | 自社社员 | 关联工资计算 |
| freelancer | 个人事业主 | 月额/时给契约 |
| bp | BP要员 | 供应商成本 |
| candidate | 商谈中候选 | 无成本 |

**核心字段：**

```
resource_pool
├── resource_code        -- 资源编号 (RS-XXXX)
├── display_name         -- 显示名称
├── resource_type        -- 类型
├── skills               -- 技能标签 (JSONB)
├── experience_summary   -- 经验摘要
├── availability_status  -- 可用状态 (available/assigned/ending_soon/unavailable)
├── available_from       -- 可用日期
├── hourly_rate          -- 时薪
├── monthly_rate         -- 月薪
├── employee_id          -- 关联社员（如适用）
├── partner_id           -- 关联供应商（BP要员）
└── payload              -- 扩展字段
```

**功能：**
- 资源信息维护
- 技能标签管理
- 可用状态跟踪
- 与社员主数据关联

#### 1.2 案件管理 (StaffingProjectModule)

管理客户的人员需求。

**案件状态流程：**

```
draft → open → matching → filled → closed
          ↓
        cancelled
```

**核心字段：**

```
staffing_projects
├── project_code         -- 案件编号
├── project_name         -- 案件名称
├── client_partner_id    -- 客户（取引先）
├── required_skills      -- 需求技能
├── experience_years     -- 经验年数要求
├── headcount            -- 募集人数
├── budget_min/max       -- 预算范围
├── work_location        -- 工作地点
├── remote_policy        -- 远程政策
├── start_date           -- 开始日期
├── status               -- 状态
└── filled_count         -- 已入场人数
```

**候选人管理：**

```
staffing_project_candidates
├── project_id           -- 案件
├── resource_id          -- 资源
├── recommended_at       -- 推荐日期
├── status               -- 状态 (recommended/client_review/interviewing/offered/accepted/rejected)
├── interview_date       -- 面试日期
├── proposed_rate        -- 提案单价
└── rejection_reason     -- 拒绝原因
```

#### 1.3 契约管理 (StaffingContractModule)

管理派遣/SES/请负契约。

**契约类型：**

| 类型 | 说明 | 精算方式 |
|------|------|----------|
| dispatch | 派遣契约 | 适用派遣法 |
| ses | SES/业务委托 | 准委任 |
| contract | 请负 | 成果物交付 |

**精算类型：**

| 类型 | 说明 |
|------|------|
| fixed | 固定月额 |
| hourly | 时间精算 |
| range | 上下限精算（140-180h 等） |

**核心字段：**

```
staffing_contracts
├── contract_no          -- 契约编号
├── contract_type        -- 契约类型
├── resource_id          -- 派遣资源
├── client_partner_id    -- 派遣先
├── project_id           -- 关联案件
├── start_date           -- 开始日
├── end_date             -- 终了日
├── billing_rate         -- 请求单价
├── cost_rate            -- 原价（对个人/BP的支付）
├── settlement_type      -- 精算类型
├── settlement_min_hours -- 精算下限
├── settlement_max_hours -- 精算上限
├── overtime_rate        -- 残业单价率
└── status               -- 状态
```

### Phase 2: 工时与请求

#### 2.1 勤怠連携 (StaffingTimesheetModule)

将现有勤怠功能与派遣契约关联。

**月次集计：**

```
staffing_timesheet_summary
├── contract_id          -- 契约
├── resource_id          -- 资源
├── year_month           -- 年月 (YYYY-MM)
├── scheduled_hours      -- 所定时间
├── actual_hours         -- 实�的时间
├── overtime_hours       -- 残业时间
├── billable_hours       -- 请求对象时间
├── settlement_hours     -- 精算时间
├── settlement_adjustment-- 精算调整额
├── base_amount          -- 基本料金
├── overtime_amount      -- 残业料金
├── total_billing_amount -- 请求总额
├── total_cost_amount    -- 原价总额
└── status               -- 状态
```

#### 2.2 请求管理 (StaffingBillingModule)

向客户发送请求书。

**请求状态：**

```
draft → confirmed → issued → sent → paid
                              ↓
                          overdue
```

**请求书结构：**

```
staffing_invoices (请求书头)
├── invoice_no           -- 请求书番号
├── client_partner_id    -- 请求先
├── billing_period       -- 请求期间
├── subtotal             -- 税抜金额
├── tax_amount           -- 消费税
├── total_amount         -- 税込金额
├── due_date             -- 支付期限
├── status               -- 状态
└── paid_amount          -- 入金済金额

staffing_invoice_lines (请求明细)
├── invoice_id           -- 请求书
├── contract_id          -- 契约
├── resource_id          -- 资源
├── timesheet_summary_id -- 勤怠集计
├── description          -- 摘要
├── quantity             -- 数量
├── unit_price           -- 单价
├── line_amount          -- 明细金额
└── overtime_amount      -- 残业料金
```

### Phase 3: 分析报表

#### 3.1 分析仪表盘 (StaffingAnalyticsModule)

**KPI 指标：**
- 稼働率（在职资源/总资源）
- 月次売上・利益
- 客户别売上
- 资源别売上
- 契约类型分布

**报表功能：**
- 月次売上推移
- 顾客别売上分析
- 资源别稼働・収益
- 契约满期预警
- 利益率分析

### Phase 4: 自动化与门户

#### 4.1 邮件自动化 (EmailEngineModule)

**收发引擎：**
- IMAP/SMTP 配置
- 定时拉取收件箱
- 发送队列管理

**邮件模板：**
- 案件推荐模板
- 面试邀请模板
- 契约确认模板
- 请求书送付模板

**自动化规则：**
- 收到邮件 → AI 解析 → 自动创建案件
- 契约即将到期 → 自动发送提醒
- 请求书确定 → 自动发送给客户

#### 4.2 员工门户 (StaffPortalModule)

**自社社员功能：**
- 工时填写/提交
- 工资明细查看
- 证明书申请

**个人事业主功能：**
- 注文书确认/签收
- 请求书提交
- 入金确认

---

## AI 智能场景

### 设计原则

```
┌─────────────────────────────────────────────────────────────────────────┐
│                                                                         │
│   AI 不是替代营业，而是让营业从 "执行者" 变成 "决策者"                   │
│                                                                         │
│   传统营业：阅读邮件 → 整理需求 → 搜索简历 → 写推荐邮件 → 协调时间       │
│              ↑ 60% 时间在执行 ↑                                        │
│                                                                         │
│   AI赋能后：[AI处理] → 营业确认 → [AI执行] → 营业决策                   │
│              ↑ 80% 时间在决策和关系维护 ↑                               │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 场景1: 智能案件解析

**痛点：** 客户邮件是非结构化的，人工解析耗时

**解决：**

```
客户邮件
    │
    ▼
┌─────────────────────────────────────────────┐
│  AI 解析                                     │
│                                             │
│  输入：邮件原文                              │
│                                             │
│  输出：                                      │
│  {                                          │
│    "project_name": "EC系统开发",            │
│    "required_skills": ["Java", "Spring"],   │
│    "experience_years": 5,                   │
│    "budget_max": 70,                        │
│    "start_date": "2024-04-01",              │
│    "urgency": "medium"                      │
│  }                                          │
└─────────────────────────────────────────────┘
    │
    ▼
自动创建案件 + 触发匹配
```

**API：** `POST /staffing/ai/parse-project-request`

### 场景2: 语义级人才匹配

**痛点：** 关键词匹配太浅，好的匹配依赖营业的记忆力

**解决：**

```
传统匹配：
案件要求 "Java开发5年以上" → 关键词搜索 → 漏掉用 Kotlin 的优秀候选人

AI 语义匹配：
案件要求 "需要有大规模系统开发经验"
                    ↓ 语义理解
简历描述 "负责过日均PV 1亿的电商平台开发" → 匹配！
```

**匹配算法：**

```python
def calculate_match_score(required_skills, candidate_skills, experience):
    # 技能匹配（语义相似度）
    skill_score = semantic_similarity(required_skills, candidate_skills)
    
    # 经验匹配
    experience_score = evaluate_experience(experience)
    
    # 综合分数
    overall = skill_score * 0.6 + experience_score * 0.4
    
    return {
        "overall": overall,
        "skill_match": skill_score,
        "experience_match": experience_score
    }
```

**API：** `POST /staffing/ai/match-candidates`

### 场景3: 自动沟通 Agent

**痛点：** 40%+ 的时间花在重复性沟通上

**解决：**

```
┌─────────────────────────────────────────┐
│           AI 沟通 Agent                  │
└─────────────────────────────────────────┘
                    │
    ┌───────────────┼───────────────┐
    ↓               ↓               ↓
┌─────────┐   ┌─────────┐   ┌─────────┐
│ 候选人   │   │ 客户    │   │ 内部    │
│         │   │         │   │         │
│ 案件推荐 │   │ 进度更新 │   │ 状态同步 │
│ 意向确认 │   │ 候选推荐 │   │ 异常提醒 │
│ 时间协调 │   │ 面试安排 │   │ 自动催办 │
└─────────┘   └─────────┘   └─────────┘
```

**邮件生成：**

```
输入：
- 模板类型：project_intro
- 资源：山田太郎
- 案件：EC系统开发

输出：
───────────────────────────────
件名：【案件のご紹介】EC系統開発

山田様

お世話になっております。

現在、山田様のご経験・スキルにマッチする
案件がございます。

【案件概要】
・案件名：EC系統開発
・クライアント：大手EC企業
・必要スキル：Java, Spring Boot
・勤務形態：ハイブリッド（週2リモート可）

ご興味がございましたら...
───────────────────────────────
```

**API：** `POST /staffing/ai/generate-outreach`

### 场景4: 市场行情分析

**痛点：** 报价靠感觉和经验，缺乏数据支撑

**解决：**

```
输入：Java, Spring Boot, 5年经验

输出：
┌────────────────────────────────────────────────────┐
│ 市场分析                                            │
├────────────────────────────────────────────────────┤
│ 同类案件最近12个月成交单价：                         │
│   - 25分位：55万/月                                 │
│   - 中位数：63万/月                                 │
│   - 75分位：72万/月                                 │
│                                                    │
│ 建议报价：                                          │
│   - 70万/月（成交概率 60%）← 強気                   │
│   - 65万/月（成交概率 80%）← 適正                   │
│   - 58万/月（成交概率 95%）← 確実                   │
│                                                    │
│ 参考因素：                                          │
│   - 当前该技能供需比：0.85（供不应求）               │
│   - 季节性：4月开始案件多，可适当提价               │
└────────────────────────────────────────────────────┘
```

**API：** `POST /staffing/ai/market-analysis`

### 场景5: 流失预测与预警

**痛点：** 员工突然离职，重新招人成本高

**解决：**

```
监测信号：
├── 合同即将到期（30天/7天）
├── 残业过多（>40h/月）
├── 长期未调薪（>12个月）
└── 其他异常

预警示例：
┌─────────────────────────────────────────────────────┐
│ ⚠️ 高风险预警                                       │
│                                                     │
│ 山田太郎（派遣中@ABC株式会社）                       │
│                                                     │
│ 风险信号：                                          │
│   - 最近2个月残業時間增加 40%                        │
│   - 同案件同期入场人员中，薪资排名最低               │
│   - 合同3个月后到期，尚未沟通续签意向                │
│                                                     │
│ 建议行动：本周内安排面谈，了解状态                   │
└─────────────────────────────────────────────────────┘
```

**API：** `GET /staffing/ai/churn-alerts`

### AI 价值总结

| 指标 | 传统方式 | AI 辅助 | 提升 |
|------|---------|---------|------|
| 案件响应时间 | 1-2天 | 10分钟 | 90%+ |
| 单个营业可管理案件数 | 10个 | 30个 | 200% |
| 匹配成功率 | - | - | +20-30% |
| 员工流失率 | - | - | -15-20% |

---

## 数据库设计

### ER 图

```
┌─────────────────┐       ┌─────────────────┐
│ businesspartners│       │   employees     │
│   (取引先)      │       │   (社员)        │
└────────┬────────┘       └────────┬────────┘
         │                         │
         │                         │
         ▼                         ▼
┌─────────────────┐       ┌─────────────────┐
│ staffing_       │◄──────│  resource_pool  │
│ projects        │       │   (资源池)      │
│   (案件)        │       └────────┬────────┘
└────────┬────────┘                │
         │                         │
         ▼                         ▼
┌─────────────────┐       ┌─────────────────┐
│ staffing_project│       │ staffing_       │
│ _candidates     │       │ contracts       │
│ (候选人)        │       │   (契约)        │
└─────────────────┘       └────────┬────────┘
                                   │
                    ┌──────────────┼──────────────┐
                    ▼              ▼              ▼
           ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
           │ timesheet_  │ │ staffing_   │ │ staffing_   │
           │ summary     │ │ invoices    │ │ purchase_   │
           │ (勤怠集计)  │ │ (请求书)    │ │ orders      │
           └─────────────┘ └─────────────┘ │ (注文书)    │
                                           └─────────────┘
```

### 核心表清单

| 表名 | 说明 | Phase |
|------|------|-------|
| resource_pool | 资源池 | 1 |
| staffing_projects | 案件 | 1 |
| staffing_project_candidates | 案件候选人 | 1 |
| staffing_contracts | 契约 | 1 |
| staffing_timesheet_summary | 勤怠月次集计 | 2 |
| staffing_invoices | 请求书 | 2 |
| staffing_invoice_lines | 请求明细 | 2 |
| staffing_email_accounts | 邮件账户 | 4 |
| staffing_email_templates | 邮件模板 | 4 |
| staffing_email_messages | 收件箱 | 4 |
| staffing_email_queue | 发件队列 | 4 |
| staffing_email_rules | 自动化规则 | 4 |
| staffing_purchase_orders | 注文书 | 4 |
| staffing_freelancer_invoices | 个人事业主请求书 | 4 |

---

## API 接口

### 资源池管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/resources | 资源列表 |
| GET | /staffing/resources/{id} | 资源详情 |
| POST | /staffing/resources | 创建资源 |
| PUT | /staffing/resources/{id} | 更新资源 |
| PUT | /staffing/resources/{id}/availability | 更新可用状态 |

### 案件管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/projects | 案件列表 |
| GET | /staffing/projects/{id} | 案件详情 |
| POST | /staffing/projects | 创建案件 |
| PUT | /staffing/projects/{id} | 更新案件 |
| GET | /staffing/projects/{id}/candidates | 候选人列表 |
| POST | /staffing/projects/{id}/candidates | 添加候选人 |
| PUT | /staffing/projects/{id}/candidates/{cid} | 更新候选人状态 |

### 契约管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/contracts | 契约列表 |
| GET | /staffing/contracts/{id} | 契约详情 |
| POST | /staffing/contracts | 创建契约 |
| PUT | /staffing/contracts/{id} | 更新契约 |
| PUT | /staffing/contracts/{id}/status | 更新状态 |

### 请求管理

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /staffing/invoices | 请求书列表 |
| POST | /staffing/invoices/generate | 自动生成请求书 |
| PUT | /staffing/invoices/{id}/confirm | 确定请求书 |
| PUT | /staffing/invoices/{id}/send | 发送请求书 |

### AI 接口

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | /staffing/ai/parse-project-request | 解析案件需求 |
| POST | /staffing/ai/match-candidates | 匹配候选人 |
| GET | /staffing/ai/project/{id}/recommendations | 案件推荐 |
| POST | /staffing/ai/generate-outreach | 生成沟通邮件 |
| POST | /staffing/ai/market-analysis | 市场分析 |
| GET | /staffing/ai/churn-alerts | 流失预警 |
| GET | /staffing/ai/dashboard | AI 工作台 |

### 员工门户

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | /portal/dashboard | 门户首页 |
| GET | /portal/timesheets | 工时列表 |
| POST | /portal/timesheets | 填写工时 |
| POST | /portal/timesheets/{id}/submit | 提交工时 |
| GET | /portal/payslips | 工资明细 |
| GET | /portal/certificates | 证明书申请 |
| POST | /portal/certificates | 提交申请 |
| GET | /portal/orders | 注文书列表（个人事业主）|
| POST | /portal/orders/{id}/accept | 签收注文书 |
| GET | /portal/invoices | 请求书列表（个人事业主）|
| POST | /portal/invoices | 提交请求书 |
| GET | /portal/payments | 入金确认 |

---

## 部署说明

### 环境要求

- .NET 8 SDK
- Node.js 18+
- PostgreSQL 15+
- Redis（可选，缓存）

### 数据库初始化

```bash
# 执行 SQL 脚本创建表
psql -h localhost -U postgres -d your_database -f server-dotnet/sql/create_staffing_tables.sql
```

### 配置

```json
// appsettings.json
{
  "Edition": {
    "Type": "Staffing",
    "DisplayName": "人才派遣版"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=erp;Username=postgres;Password=xxx"
  },
  "OpenAI": {
    "ApiKey": "sk-xxx",
    "Model": "gpt-4"
  }
}
```

### 启动

```bash
# 后端
cd server-dotnet
dotnet run

# 前端
cd web
npm install
npm run dev
```

---

## 路线图

### 已完成

- [x] Phase 1: 资源池、案件、契约管理
- [x] Phase 2: 勤怠連携、请求管理
- [x] Phase 3: 分析报表
- [x] Phase 4: 邮件自动化、员工门户
- [x] Phase 5: AI 智能助手

### 未来规划

- [ ] 派遣法合规检查（3年规则自动提醒）
- [ ] 移动端 App
- [ ] 与外部系统集成（勤怠系统、会计系统）
- [ ] AI 增强：对话式操作、更精准的匹配算法

---

## 联系方式

如有问题，请联系开发团队。


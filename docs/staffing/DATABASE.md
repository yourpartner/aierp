# 人才派遣版 数据库设计文档

## 概述

本文档详细说明人才派遣版 ERP 的数据库表结构设计。

---

## 表结构

### 1. resource_pool - 资源池

统一管理所有类型的人力资源。

```sql
CREATE TABLE resource_pool (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    -- 基本信息
    resource_code TEXT NOT NULL,           -- 资源编号 RS-XXXX
    display_name TEXT NOT NULL,            -- 显示名称
    resource_type TEXT NOT NULL,           -- employee/freelancer/bp/candidate
    
    -- 联系方式
    email TEXT,
    phone TEXT,
    
    -- 技能与经验
    skills JSONB DEFAULT '[]',             -- ["Java", "Spring Boot", ...]
    experience_summary TEXT,               -- 经验摘要
    resume_url TEXT,                       -- 简历URL
    
    -- 可用状态
    status TEXT DEFAULT 'active',          -- active/inactive/blacklist
    availability_status TEXT DEFAULT 'available',  -- available/assigned/ending_soon/unavailable
    available_from DATE,                   -- 可用日期
    current_assignment_end DATE,           -- 当前契约终了日
    
    -- 费率
    hourly_rate DECIMAL(10,0),             -- 時給
    monthly_rate DECIMAL(12,0),            -- 月額
    
    -- 关联
    employee_id UUID,                      -- 关联社员（自社社员）
    partner_id UUID,                       -- 关联供应商（BP要员）
    
    -- 扩展
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, resource_code)
);
```

**字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| resource_type | TEXT | employee=自社社员, freelancer=个人事业主, bp=BP要员, candidate=候选人 |
| availability_status | TEXT | available=可用, assigned=稼働中, ending_soon=即将结束, unavailable=不可用 |
| skills | JSONB | 技能标签数组，用于匹配 |

---

### 2. staffing_projects - 案件

管理客户的人员需求。

```sql
CREATE TABLE staffing_projects (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    -- 基本信息
    project_code TEXT NOT NULL,            -- 案件编号 PJ-XXXX
    project_name TEXT NOT NULL,            -- 案件名称
    
    -- 客户
    client_partner_id UUID NOT NULL,       -- 关联取引先
    client_contact_name TEXT,              -- 客户联系人
    client_contact_email TEXT,
    
    -- 需求
    required_skills JSONB DEFAULT '[]',    -- 需求技能
    experience_years INT,                  -- 经验年数
    job_description TEXT,                  -- 职位描述
    headcount INT DEFAULT 1,               -- 募集人数
    
    -- 预算
    budget_min DECIMAL(10,0),              -- 预算下限
    budget_max DECIMAL(10,0),              -- 预算上限
    
    -- 工作条件
    work_location TEXT,                    -- 工作地点
    remote_policy TEXT DEFAULT 'onsite',   -- full_remote/hybrid/onsite
    
    -- 期间
    start_date DATE,                       -- 开始日期
    end_date DATE,                         -- 结束日期
    duration_months INT,                   -- 期间（月）
    
    -- 状态
    status TEXT DEFAULT 'draft',           -- draft/open/matching/filled/closed/cancelled
    priority TEXT DEFAULT 'medium',        -- high/medium/low
    filled_count INT DEFAULT 0,            -- 已入场人数
    
    -- 来源
    source_type TEXT,                      -- email/phone/referral/website
    source_email_id UUID,                  -- 关联邮件
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, project_code)
);
```

**状态流转：**

```
draft (下书き)
  ↓
open (募集中)
  ↓
matching (選考中)
  ↓
filled (充足) → closed (終了)
  ↓
cancelled (キャンセル)
```

---

### 3. staffing_project_candidates - 案件候选人

管理案件的候选人推荐和选考过程。

```sql
CREATE TABLE staffing_project_candidates (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    project_id UUID REFERENCES staffing_projects(id),
    resource_id UUID REFERENCES resource_pool(id),
    
    -- 推荐
    recommended_at TIMESTAMPTZ DEFAULT now(),
    recommended_by UUID,                   -- 推荐者（用户ID）
    
    -- 单价
    proposed_rate DECIMAL(10,0),           -- 提案单价
    final_rate DECIMAL(10,0),              -- 最终单价
    
    -- 状态
    status TEXT DEFAULT 'recommended',
    -- recommended: 推荐中
    -- client_review: 客户审核中
    -- interviewing: 面试中
    -- offered: 已发offer
    -- accepted: 已接受
    -- rejected: 被拒绝
    -- withdrawn: 辞退
    
    -- 面试
    interview_date TIMESTAMPTZ,
    interview_feedback TEXT,
    
    -- 结果
    rejection_reason TEXT,
    result_note TEXT,
    
    -- 关联契约
    contract_id UUID,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(project_id, resource_id)
);
```

---

### 4. staffing_contracts - 契约

管理派遣/SES契约。

```sql
CREATE TABLE staffing_contracts (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    -- 基本信息
    contract_no TEXT NOT NULL,             -- 契约编号
    contract_type TEXT NOT NULL,           -- dispatch/ses/contract
    
    -- 关联
    resource_id UUID REFERENCES resource_pool(id),
    client_partner_id UUID NOT NULL,       -- 派遣先
    project_id UUID,                       -- 关联案件
    
    -- 期间
    start_date DATE NOT NULL,
    end_date DATE,
    auto_renew BOOLEAN DEFAULT false,
    
    -- 请求单价
    billing_rate DECIMAL(10,0) NOT NULL,   -- 请求单价（月额/時給）
    billing_unit TEXT DEFAULT 'monthly',   -- monthly/hourly
    
    -- 原价（对个人/BP的支付）
    cost_rate DECIMAL(10,0),
    cost_unit TEXT DEFAULT 'monthly',
    
    -- 精算条件
    settlement_type TEXT DEFAULT 'fixed',  -- fixed/hourly/range
    settlement_min_hours DECIMAL(6,2),     -- 精算下限
    settlement_max_hours DECIMAL(6,2),     -- 精算上限
    
    -- 残业
    overtime_rate DECIMAL(4,2),            -- 残业单价率（1.25等）
    
    -- 状态
    status TEXT DEFAULT 'draft',
    -- draft/pending_approval/active/suspended/completed/terminated
    
    -- 派遣法相关（仅dispatch类型）
    dispatch_license_no TEXT,              -- 派遣许可番号
    dispatch_start_date DATE,              -- 派遣开始日（用于3年规则计算）
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, contract_no)
);
```

**契约类型说明：**

| 类型 | 日文 | 说明 |
|------|------|------|
| dispatch | 派遣契约 | 适用劳动者派遣法 |
| ses | 業務委託/SES | 准委任契约 |
| contract | 請負 | 成果物交付 |

**精算类型说明：**

| 类型 | 说明 | 示例 |
|------|------|------|
| fixed | 固定月额 | 60万/月，不论工时 |
| hourly | 时间精算 | 4000円/h × 实际工时 |
| range | 上下限精算 | 140-180h内60万，超/不足按比例 |

---

### 5. staffing_timesheet_summary - 勤怠月次集计

按月汇总工时和金额。

```sql
CREATE TABLE staffing_timesheet_summary (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    contract_id UUID REFERENCES staffing_contracts(id),
    resource_id UUID REFERENCES resource_pool(id),
    year_month TEXT NOT NULL,              -- YYYY-MM
    
    -- 時間集計
    scheduled_hours DECIMAL(6,2) DEFAULT 0,  -- 所定時間
    actual_hours DECIMAL(6,2) DEFAULT 0,     -- 実労働時間
    overtime_hours DECIMAL(6,2) DEFAULT 0,   -- 残業時間
    billable_hours DECIMAL(6,2) DEFAULT 0,   -- 請求対象時間
    
    -- 精算計算
    settlement_hours DECIMAL(6,2) DEFAULT 0, -- 精算時間
    settlement_adjustment DECIMAL(10,0) DEFAULT 0, -- 精算調整額
    
    -- 金額
    base_amount DECIMAL(12,0) DEFAULT 0,     -- 基本料金
    overtime_amount DECIMAL(12,0) DEFAULT 0, -- 残業料金
    adjustment_amount DECIMAL(12,0) DEFAULT 0, -- 調整料金
    total_billing_amount DECIMAL(12,0) DEFAULT 0, -- 請求総額
    total_cost_amount DECIMAL(12,0) DEFAULT 0,    -- 原価総額
    
    -- 状態
    status TEXT DEFAULT 'open',            -- open/confirmed/invoiced/closed
    submitted_at TIMESTAMPTZ,
    confirmed_at TIMESTAMPTZ,
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, contract_id, year_month)
);
```

---

### 6. staffing_invoices - 请求书

向客户发送的请求书。

```sql
CREATE TABLE staffing_invoices (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    invoice_no TEXT NOT NULL,              -- 請求書番号
    client_partner_id UUID NOT NULL,       -- 請求先
    
    -- 请求期间
    billing_year_month TEXT NOT NULL,      -- YYYY-MM
    billing_period_start DATE NOT NULL,
    billing_period_end DATE NOT NULL,
    
    -- 金額
    subtotal DECIMAL(12,0) NOT NULL,       -- 税抜金額
    tax_rate DECIMAL(4,2) DEFAULT 0.10,
    tax_amount DECIMAL(12,0),
    total_amount DECIMAL(12,0) NOT NULL,   -- 税込金額
    
    -- 日期
    invoice_date DATE NOT NULL,            -- 請求日
    due_date DATE NOT NULL,                -- 支払期限
    
    -- 状態
    status TEXT DEFAULT 'draft',
    -- draft/confirmed/issued/sent/paid/partial_paid/overdue/cancelled
    
    -- 发行
    confirmed_at TIMESTAMPTZ,
    issued_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    sent_method TEXT,                      -- email/mail/portal
    
    -- 入金
    paid_amount DECIMAL(12,0) DEFAULT 0,
    last_payment_date DATE,
    
    -- PDF
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, invoice_no)
);
```

---

### 7. staffing_invoice_lines - 请求明细

请求书的明细行。

```sql
CREATE TABLE staffing_invoice_lines (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    invoice_id UUID REFERENCES staffing_invoices(id) ON DELETE CASCADE,
    line_no INT NOT NULL,
    
    -- 关联
    contract_id UUID REFERENCES staffing_contracts(id),
    resource_id UUID REFERENCES resource_pool(id),
    timesheet_summary_id UUID REFERENCES staffing_timesheet_summary(id),
    
    -- 明细内容
    description TEXT,                      -- 摘要
    
    -- 数量・単価
    quantity DECIMAL(8,2),                 -- 数量
    unit TEXT,                             -- 単位
    unit_price DECIMAL(10,0),              -- 単価
    
    -- 残业
    overtime_hours DECIMAL(6,2) DEFAULT 0,
    overtime_amount DECIMAL(10,0) DEFAULT 0,
    
    -- 调整
    adjustment_amount DECIMAL(10,0) DEFAULT 0,
    adjustment_description TEXT,
    
    -- 金额
    line_amount DECIMAL(12,0),             -- 明細金額
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 8. staffing_email_accounts - 邮件账户

```sql
CREATE TABLE staffing_email_accounts (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    account_name TEXT NOT NULL,
    email_address TEXT NOT NULL,
    account_type TEXT DEFAULT 'imap',      -- imap/exchange/gmail
    
    -- IMAP
    imap_host TEXT,
    imap_port INT DEFAULT 993,
    
    -- SMTP
    smtp_host TEXT,
    smtp_port INT DEFAULT 587,
    
    -- 认证
    username TEXT,
    password_encrypted TEXT,
    
    is_default BOOLEAN DEFAULT false,
    is_active BOOLEAN DEFAULT true,
    last_sync_at TIMESTAMPTZ,
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 9. staffing_email_templates - 邮件模板

```sql
CREATE TABLE staffing_email_templates (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    template_code TEXT NOT NULL,           -- 唯一标识
    template_name TEXT NOT NULL,
    category TEXT DEFAULT 'general',       -- project/contract/invoice/timesheet/general
    
    subject_template TEXT NOT NULL,        -- 件名模板
    body_template TEXT NOT NULL,           -- 正文模板（HTML）
    
    variables JSONB DEFAULT '[]',          -- 可用变量定义
    
    is_active BOOLEAN DEFAULT true,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, template_code)
);
```

**模板变量示例：**

```json
[
  {"name": "clientName", "label": "顧客名", "type": "string"},
  {"name": "resourceName", "label": "リソース名", "type": "string"},
  {"name": "projectName", "label": "案件名", "type": "string"},
  {"name": "startDate", "label": "開始日", "type": "date"}
]
```

---

### 10. staffing_email_messages - 收件箱

```sql
CREATE TABLE staffing_email_messages (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    account_id UUID REFERENCES staffing_email_accounts(id),
    
    message_id TEXT,                       -- 邮件服务器的Message-ID
    folder TEXT DEFAULT 'inbox',
    
    -- 发件人
    from_address TEXT NOT NULL,
    from_name TEXT,
    
    -- 收件人
    to_addresses TEXT,
    cc_addresses TEXT,
    
    -- 内容
    subject TEXT,
    body_text TEXT,
    body_html TEXT,
    attachments JSONB DEFAULT '[]',
    
    received_at TIMESTAMPTZ NOT NULL,
    
    -- 状态
    status TEXT DEFAULT 'new',             -- new/parsed/processed/archived
    is_read BOOLEAN DEFAULT false,
    
    -- AI 解析
    parsed_intent TEXT,                    -- 意图
    parsed_data JSONB,                     -- 提取的数据
    
    -- 业务关联
    linked_entity_type TEXT,
    linked_entity_id UUID,
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 11. staffing_email_queue - 发件队列

```sql
CREATE TABLE staffing_email_queue (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    to_addresses TEXT NOT NULL,
    cc_addresses TEXT,
    
    subject TEXT NOT NULL,
    body_html TEXT,
    
    template_id UUID,
    template_data JSONB,
    
    status TEXT DEFAULT 'pending',         -- pending/sending/sent/failed
    scheduled_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    error_message TEXT,
    
    linked_entity_type TEXT,
    linked_entity_id UUID,
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 12. staffing_email_rules - 自动化规则

```sql
CREATE TABLE staffing_email_rules (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    rule_name TEXT NOT NULL,
    
    -- 触发
    trigger_type TEXT NOT NULL,            -- email_received/entity_created/schedule
    trigger_conditions JSONB DEFAULT '{}',
    
    -- 动作
    action_type TEXT NOT NULL,             -- send_email/create_entity/notify
    action_config JSONB DEFAULT '{}',
    template_id UUID,
    
    is_active BOOLEAN DEFAULT true,
    execution_count INT DEFAULT 0,
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 13. staffing_purchase_orders - 注文书

公司发给个人事业主的注文书。

```sql
CREATE TABLE staffing_purchase_orders (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    order_no TEXT NOT NULL,
    resource_id UUID REFERENCES resource_pool(id),
    contract_id UUID REFERENCES staffing_contracts(id),
    
    -- 期间
    order_date DATE NOT NULL,
    period_start DATE NOT NULL,
    period_end DATE NOT NULL,
    
    -- 金额
    unit_price DECIMAL(10,0) NOT NULL,
    settlement_type TEXT DEFAULT 'monthly',
    min_hours DECIMAL(6,2),
    max_hours DECIMAL(6,2),
    
    -- 状态
    status TEXT DEFAULT 'draft',           -- draft/sent/accepted/rejected
    sent_at TIMESTAMPTZ,
    accepted_at TIMESTAMPTZ,
    
    document_url TEXT,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, order_no)
);
```

---

### 14. staffing_freelancer_invoices - 个人事业主请求书

个人事业主提交的请求书。

```sql
CREATE TABLE staffing_freelancer_invoices (
    id UUID PRIMARY KEY,
    company_code TEXT NOT NULL,
    
    invoice_no TEXT NOT NULL,
    resource_id UUID REFERENCES resource_pool(id),
    
    -- 期间
    period_start DATE NOT NULL,
    period_end DATE NOT NULL,
    
    -- 金额
    subtotal DECIMAL(12,0) NOT NULL,
    tax_rate DECIMAL(4,2) DEFAULT 0.10,
    tax_amount DECIMAL(12,0),
    total_amount DECIMAL(12,0) NOT NULL,
    
    -- 状态
    status TEXT DEFAULT 'draft',           -- draft/submitted/approved/rejected/paid
    submitted_at TIMESTAMPTZ,
    approved_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ,
    paid_amount DECIMAL(12,0),
    
    created_at TIMESTAMPTZ DEFAULT now(),
    
    UNIQUE(company_code, invoice_no)
);
```

---

## 索引

```sql
-- 资源池
CREATE INDEX idx_resource_pool_company ON resource_pool(company_code);
CREATE INDEX idx_resource_pool_type ON resource_pool(company_code, resource_type);
CREATE INDEX idx_resource_pool_status ON resource_pool(company_code, availability_status);
CREATE INDEX idx_resource_pool_skills ON resource_pool USING GIN(skills);

-- 案件
CREATE INDEX idx_projects_company ON staffing_projects(company_code);
CREATE INDEX idx_projects_client ON staffing_projects(client_partner_id);
CREATE INDEX idx_projects_status ON staffing_projects(company_code, status);

-- 契约
CREATE INDEX idx_contracts_company ON staffing_contracts(company_code);
CREATE INDEX idx_contracts_resource ON staffing_contracts(resource_id);
CREATE INDEX idx_contracts_client ON staffing_contracts(client_partner_id);
CREATE INDEX idx_contracts_status ON staffing_contracts(company_code, status);
CREATE INDEX idx_contracts_dates ON staffing_contracts(start_date, end_date);

-- 勤怠集计
CREATE INDEX idx_timesheet_contract ON staffing_timesheet_summary(contract_id);
CREATE INDEX idx_timesheet_resource ON staffing_timesheet_summary(resource_id);
CREATE INDEX idx_timesheet_month ON staffing_timesheet_summary(company_code, year_month);

-- 请求书
CREATE INDEX idx_invoices_client ON staffing_invoices(client_partner_id);
CREATE INDEX idx_invoices_status ON staffing_invoices(company_code, status);

-- 邮件
CREATE INDEX idx_email_messages_status ON staffing_email_messages(company_code, status);
CREATE INDEX idx_email_messages_from ON staffing_email_messages(from_address);
```

---

## 序列

```sql
CREATE SEQUENCE seq_resource_code START 1;
CREATE SEQUENCE seq_project_code START 1;
CREATE SEQUENCE seq_contract_code START 1;
CREATE SEQUENCE seq_staffing_invoice START 1;
CREATE SEQUENCE seq_purchase_order START 1;
CREATE SEQUENCE seq_freelancer_invoice START 1;
```

使用示例：

```sql
-- 生成资源编号
SELECT 'RS-' || LPAD(nextval('seq_resource_code')::text, 4, '0');
-- 结果: RS-0001

-- 生成案件编号
SELECT 'PJ-' || to_char(now(), 'YYYYMM') || '-' || LPAD(nextval('seq_project_code')::text, 4, '0');
-- 结果: PJ-202401-0001
```


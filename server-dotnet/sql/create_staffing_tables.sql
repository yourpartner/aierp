-- =============================================
-- 人材派遣版テーブル作成
-- =============================================

-- リソースプール（要員統合管理）
CREATE TABLE IF NOT EXISTS resource_pool (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_code TEXT NOT NULL,

    -- 基本情報
    display_name TEXT NOT NULL,
    display_name_kana TEXT,

    -- リソース種別: employee/freelancer/bp/candidate
    resource_type TEXT NOT NULL,

    -- 所属関連
    employee_id UUID,                     -- 自社社員の場合: employeesへの参照
    supplier_partner_id UUID,             -- BP要員の場合: 協力会社のbusinesspartners.id

    -- 連絡先（自社社員以外の場合）
    email TEXT,
    phone TEXT,

    -- スキル・経験
    primary_skill_category TEXT,
    experience_years INT,
    skills JSONB DEFAULT '[]',
    certifications JSONB DEFAULT '[]',

    -- 単価情報
    default_billing_rate DECIMAL(10,0),
    default_cost_rate DECIMAL(10,0),
    rate_type TEXT DEFAULT 'monthly',     -- hourly/daily/monthly

    -- 稼働状態
    availability_status TEXT DEFAULT 'available',
    -- available/assigned/partially_available/unavailable/candidate

    current_assignment_id UUID,
    available_from DATE,

    -- 希望条件
    desired_locations TEXT[],
    desired_job_types TEXT[],
    min_rate DECIMAL(10,0),

    -- メモ
    internal_notes TEXT,
    expires_at TIMESTAMPTZ,

    status TEXT DEFAULT 'active',
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, resource_code)
);

CREATE INDEX IF NOT EXISTS idx_resource_pool_type ON resource_pool(company_code, resource_type);
CREATE INDEX IF NOT EXISTS idx_resource_pool_status ON resource_pool(company_code, availability_status);
CREATE INDEX IF NOT EXISTS idx_resource_pool_employee ON resource_pool(employee_id) WHERE employee_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_resource_pool_supplier ON resource_pool(supplier_partner_id) WHERE supplier_partner_id IS NOT NULL;

-- 案件（プロジェクト/要員依頼）
CREATE TABLE IF NOT EXISTS staffing_projects (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    project_code TEXT NOT NULL,

    -- 顧客情報（取引先参照）
    client_partner_id UUID NOT NULL,

    -- 案件基本情報
    project_name TEXT NOT NULL,
    job_category TEXT,
    job_description TEXT,

    -- 契約形態: dispatch/ses/contract
    contract_type TEXT NOT NULL,

    -- 募集条件
    headcount INT DEFAULT 1,
    filled_count INT DEFAULT 0,

    -- 期間
    expected_start_date DATE,
    expected_end_date DATE,

    -- 就業条件
    work_location TEXT,
    work_days TEXT,
    work_hours TEXT,
    remote_work_ratio INT,

    -- 料金条件
    billing_rate_min DECIMAL(10,0),
    billing_rate_max DECIMAL(10,0),
    rate_type TEXT DEFAULT 'monthly',

    -- 要求スキル
    required_skills JSONB DEFAULT '[]',
    preferred_skills JSONB DEFAULT '[]',
    experience_years_min INT,

    -- 営業情報
    sales_rep_id UUID,
    source TEXT,

    -- 状態
    status TEXT DEFAULT 'open',
    -- open/matching/filled/on_hold/closed/cancelled

    priority TEXT DEFAULT 'normal',

    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, project_code)
);

CREATE INDEX IF NOT EXISTS idx_staffing_projects_client ON staffing_projects(client_partner_id);
CREATE INDEX IF NOT EXISTS idx_staffing_projects_status ON staffing_projects(company_code, status);

-- 案件候補者（マッチング記録）
CREATE TABLE IF NOT EXISTS staffing_project_candidates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    project_id UUID REFERENCES staffing_projects(id) ON DELETE CASCADE,
    resource_id UUID REFERENCES resource_pool(id) ON DELETE CASCADE,

    -- マッチング状態
    status TEXT DEFAULT 'proposed',
    -- proposed/client_review/interview_scheduled/interview_done/offered/accepted/rejected/withdrawn

    proposed_rate DECIMAL(10,0),
    proposed_at TIMESTAMPTZ DEFAULT now(),

    -- 面談情報
    interview_date TIMESTAMPTZ,
    interview_notes TEXT,

    -- 決定情報
    decided_at TIMESTAMPTZ,
    final_rate DECIMAL(10,0),
    rejection_reason TEXT,

    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_project_candidates_project ON staffing_project_candidates(project_id);
CREATE INDEX IF NOT EXISTS idx_project_candidates_resource ON staffing_project_candidates(resource_id);

-- 契約（派遣/業務委託統合）
CREATE TABLE IF NOT EXISTS staffing_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,

    -- 関連
    project_id UUID REFERENCES staffing_projects(id),
    resource_id UUID REFERENCES resource_pool(id),
    client_partner_id UUID NOT NULL,

    -- 契約形態: dispatch/ses/contract
    contract_type TEXT NOT NULL,

    -- 契約期間
    start_date DATE NOT NULL,
    end_date DATE,

    -- 派遣契約の場合の追加情報
    dispatch_info JSONB,

    -- 就業条件
    work_location TEXT,
    work_days TEXT,
    work_start_time TIME,
    work_end_time TIME,
    monthly_work_hours DECIMAL(5,1),

    -- 料金条件（対顧客請求）
    billing_rate DECIMAL(10,0) NOT NULL,
    billing_rate_type TEXT DEFAULT 'monthly',
    overtime_rate_multiplier DECIMAL(3,2) DEFAULT 1.25,

    -- 精算条件
    settlement_type TEXT DEFAULT 'range',
    settlement_lower_hours DECIMAL(5,1),
    settlement_upper_hours DECIMAL(5,1),

    -- 原価
    cost_rate DECIMAL(10,0),
    cost_rate_type TEXT DEFAULT 'monthly',

    -- 支払先
    payee_type TEXT,                      -- resource/supplier
    payee_partner_id UUID,

    -- 状態
    status TEXT DEFAULT 'active',
    -- draft/active/extended/ended/terminated

    termination_date DATE,
    termination_reason TEXT,

    -- 更新履歴
    renewal_count INT DEFAULT 0,
    original_contract_id UUID,

    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_no)
);

CREATE INDEX IF NOT EXISTS idx_staffing_contracts_resource ON staffing_contracts(resource_id);
CREATE INDEX IF NOT EXISTS idx_staffing_contracts_client ON staffing_contracts(client_partner_id);
CREATE INDEX IF NOT EXISTS idx_staffing_contracts_status ON staffing_contracts(company_code, status);
CREATE INDEX IF NOT EXISTS idx_staffing_contracts_dates ON staffing_contracts(company_code, start_date, end_date);

-- =============================================
-- Phase 2: 勤怠連携・請求管理
-- =============================================

-- 勤怠サマリー（契約別月次集計）
CREATE TABLE IF NOT EXISTS staffing_timesheet_summary (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_contracts(id),
    resource_id UUID REFERENCES resource_pool(id),
    
    -- 期間
    year_month TEXT NOT NULL,              -- YYYY-MM形式
    
    -- 時間集計
    scheduled_hours DECIMAL(6,2),          -- 所定時間
    actual_hours DECIMAL(6,2),             -- 実労働時間
    overtime_hours DECIMAL(6,2) DEFAULT 0, -- 残業時間
    holiday_hours DECIMAL(6,2) DEFAULT 0,  -- 休日労働時間
    night_hours DECIMAL(6,2) DEFAULT 0,    -- 深夜労働時間
    
    -- 精算計算
    billable_hours DECIMAL(6,2),           -- 請求対象時間
    settlement_hours DECIMAL(6,2),         -- 精算時間（幅精算後）
    deduction_hours DECIMAL(6,2) DEFAULT 0, -- 控除時間（下限未達）
    excess_hours DECIMAL(6,2) DEFAULT 0,    -- 超過時間（上限超過）
    
    -- 金額計算
    base_amount DECIMAL(12,0),             -- 基本金額
    overtime_amount DECIMAL(12,0) DEFAULT 0, -- 残業金額
    adjustment_amount DECIMAL(12,0) DEFAULT 0, -- 調整金額
    total_billing_amount DECIMAL(12,0),    -- 請求総額
    total_cost_amount DECIMAL(12,0),       -- 原価総額
    
    -- 状態
    status TEXT DEFAULT 'open',            -- open/confirmed/invoiced
    confirmed_at TIMESTAMPTZ,
    confirmed_by UUID,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_id, year_month)
);

CREATE INDEX IF NOT EXISTS idx_timesheet_summary_contract ON staffing_timesheet_summary(contract_id);
CREATE INDEX IF NOT EXISTS idx_timesheet_summary_period ON staffing_timesheet_summary(company_code, year_month);
CREATE INDEX IF NOT EXISTS idx_timesheet_summary_status ON staffing_timesheet_summary(company_code, status);

-- 請求書
CREATE TABLE IF NOT EXISTS staffing_invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_no TEXT NOT NULL,
    
    -- 請求先（取引先）
    client_partner_id UUID NOT NULL,
    
    -- 請求期間
    billing_period_start DATE NOT NULL,
    billing_period_end DATE NOT NULL,
    billing_year_month TEXT NOT NULL,      -- YYYY-MM
    
    -- 金額
    subtotal DECIMAL(12,0) NOT NULL,       -- 小計
    tax_rate DECIMAL(4,2) DEFAULT 0.10,    -- 税率
    tax_amount DECIMAL(12,0),              -- 消費税額
    total_amount DECIMAL(12,0) NOT NULL,   -- 請求総額
    
    -- 日付
    invoice_date DATE NOT NULL,
    due_date DATE NOT NULL,
    
    -- 状態
    status TEXT DEFAULT 'draft',
    -- draft: 下書き
    -- confirmed: 確定
    -- issued: 発行済
    -- sent: 送付済
    -- paid: 入金済
    -- partial_paid: 一部入金
    -- overdue: 延滞
    -- cancelled: キャンセル
    
    -- 発行・送付
    confirmed_at TIMESTAMPTZ,
    issued_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    sent_method TEXT,                      -- email/mail/portal
    
    -- 入金
    paid_amount DECIMAL(12,0) DEFAULT 0,
    last_payment_date DATE,
    
    -- PDF保存先
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, invoice_no)
);

CREATE INDEX IF NOT EXISTS idx_staffing_invoices_client ON staffing_invoices(client_partner_id);
CREATE INDEX IF NOT EXISTS idx_staffing_invoices_period ON staffing_invoices(company_code, billing_year_month);
CREATE INDEX IF NOT EXISTS idx_staffing_invoices_status ON staffing_invoices(company_code, status);

-- 請求明細
CREATE TABLE IF NOT EXISTS staffing_invoice_lines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_id UUID REFERENCES staffing_invoices(id) ON DELETE CASCADE,
    line_no INT NOT NULL,
    
    -- 契約・リソース
    contract_id UUID REFERENCES staffing_contracts(id),
    resource_id UUID REFERENCES resource_pool(id),
    timesheet_summary_id UUID REFERENCES staffing_timesheet_summary(id),
    
    -- 明細内容
    description TEXT,                      -- 摘要（例：○○様 2024年1月稼働分）
    
    -- 数量・単価
    quantity DECIMAL(8,2),                 -- 数量（時間/日数/人月）
    unit TEXT,                             -- 単位（時間/日/人月）
    unit_price DECIMAL(10,0),              -- 単価
    
    -- 精算調整
    overtime_hours DECIMAL(6,2) DEFAULT 0,
    overtime_amount DECIMAL(10,0) DEFAULT 0,
    adjustment_amount DECIMAL(10,0) DEFAULT 0,
    adjustment_description TEXT,
    
    -- 金額
    line_amount DECIMAL(12,0),             -- 明細金額
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_invoice_lines_invoice ON staffing_invoice_lines(invoice_id);
CREATE INDEX IF NOT EXISTS idx_invoice_lines_contract ON staffing_invoice_lines(contract_id);

-- ============================================================
-- Phase 4: 邮件自动化 & 员工门户
-- ============================================================

-- 邮件账户配置
CREATE TABLE IF NOT EXISTS staffing_email_accounts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    account_name TEXT NOT NULL,              -- 账户名称
    email_address TEXT NOT NULL,             -- 邮箱地址
    account_type TEXT DEFAULT 'imap',        -- imap/exchange/gmail
    
    -- IMAP设置
    imap_host TEXT,
    imap_port INT DEFAULT 993,
    imap_use_ssl BOOLEAN DEFAULT true,
    
    -- SMTP设置
    smtp_host TEXT,
    smtp_port INT DEFAULT 587,
    smtp_use_tls BOOLEAN DEFAULT true,
    
    -- 认证
    username TEXT,
    password_encrypted TEXT,                 -- 加密存储
    oauth_token JSONB,                       -- OAuth认证
    
    is_default BOOLEAN DEFAULT false,
    is_active BOOLEAN DEFAULT true,
    last_sync_at TIMESTAMPTZ,
    sync_error TEXT,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_email_accounts_company ON staffing_email_accounts(company_code);

-- 邮件模板
CREATE TABLE IF NOT EXISTS staffing_email_templates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    template_code TEXT NOT NULL,             -- 模板代码（唯一标识）
    template_name TEXT NOT NULL,             -- 模板名称
    category TEXT DEFAULT 'general',         -- 分类：contract/invoice/timesheet/project/general
    
    subject_template TEXT NOT NULL,          -- 主题模板（支持变量替换）
    body_template TEXT NOT NULL,             -- 正文模板（HTML）
    
    variables JSONB DEFAULT '[]',            -- 可用变量定义
    -- 例: [{"name": "clientName", "label": "顧客名", "type": "string"}]
    
    is_active BOOLEAN DEFAULT true,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, template_code)
);

CREATE INDEX IF NOT EXISTS idx_email_templates_company ON staffing_email_templates(company_code);
CREATE INDEX IF NOT EXISTS idx_email_templates_category ON staffing_email_templates(company_code, category);

-- 收件箱（已接收的邮件）
CREATE TABLE IF NOT EXISTS staffing_email_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    account_id UUID REFERENCES staffing_email_accounts(id),
    
    message_id TEXT,                         -- 邮件服务器的Message-ID
    folder TEXT DEFAULT 'inbox',             -- inbox/sent/archive
    
    -- 发件人信息
    from_address TEXT NOT NULL,
    from_name TEXT,
    
    -- 收件人
    to_addresses TEXT,                       -- JSON数组或逗号分隔
    cc_addresses TEXT,
    
    -- 邮件内容
    subject TEXT,
    body_text TEXT,
    body_html TEXT,
    attachments JSONB DEFAULT '[]',          -- 附件信息
    
    received_at TIMESTAMPTZ NOT NULL,
    
    -- 处理状态
    status TEXT DEFAULT 'new',               -- new/parsed/processed/archived/spam
    is_read BOOLEAN DEFAULT false,
    
    -- AI解析结果
    parsed_intent TEXT,                      -- project_request/contract_confirm/invoice_related/payment_confirm/unknown
    parsed_data JSONB,                       -- 提取的结构化数据
    parsed_at TIMESTAMPTZ,
    
    -- 业务关联
    linked_entity_type TEXT,                 -- partner/project/contract/invoice
    linked_entity_id UUID,
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_email_messages_company ON staffing_email_messages(company_code);
CREATE INDEX IF NOT EXISTS idx_email_messages_folder ON staffing_email_messages(company_code, folder);
CREATE INDEX IF NOT EXISTS idx_email_messages_status ON staffing_email_messages(company_code, status);
CREATE INDEX IF NOT EXISTS idx_email_messages_from ON staffing_email_messages(from_address);
CREATE INDEX IF NOT EXISTS idx_email_messages_received ON staffing_email_messages(company_code, received_at);

-- 发件队列
CREATE TABLE IF NOT EXISTS staffing_email_queue (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    account_id UUID REFERENCES staffing_email_accounts(id),
    
    -- 收件人
    to_addresses TEXT NOT NULL,
    cc_addresses TEXT,
    bcc_addresses TEXT,
    
    -- 邮件内容
    subject TEXT NOT NULL,
    body_html TEXT,
    body_text TEXT,
    attachments JSONB DEFAULT '[]',
    
    -- 模板
    template_id UUID REFERENCES staffing_email_templates(id),
    template_data JSONB,                     -- 模板变量值
    
    -- 状态
    status TEXT DEFAULT 'pending',           -- pending/sending/sent/failed/cancelled
    scheduled_at TIMESTAMPTZ,                -- 定时发送
    sent_at TIMESTAMPTZ,
    error_message TEXT,
    retry_count INT DEFAULT 0,
    
    -- 业务关联
    linked_entity_type TEXT,
    linked_entity_id UUID,
    
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_email_queue_company ON staffing_email_queue(company_code);
CREATE INDEX IF NOT EXISTS idx_email_queue_status ON staffing_email_queue(status);
CREATE INDEX IF NOT EXISTS idx_email_queue_scheduled ON staffing_email_queue(status, scheduled_at);

-- 邮件自动化规则
CREATE TABLE IF NOT EXISTS staffing_email_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    rule_name TEXT NOT NULL,
    
    -- 触发条件
    trigger_type TEXT NOT NULL,              
    -- email_received: 收到邮件时
    -- entity_created: 业务对象创建时（contract/invoice等）
    -- entity_updated: 业务对象更新时
    -- schedule: 定时触发
    -- manual: 手动触发
    
    trigger_conditions JSONB DEFAULT '{}',   -- 触发条件详情
    -- 例: {"intent": "project_request", "fromDomain": "@client.co.jp"}
    
    -- 动作
    action_type TEXT NOT NULL,               
    -- send_email: 发送邮件
    -- create_entity: 创建业务对象
    -- update_entity: 更新业务对象
    -- notify: 发送通知
    
    action_config JSONB DEFAULT '{}',        -- 动作配置
    template_id UUID REFERENCES staffing_email_templates(id),
    
    is_active BOOLEAN DEFAULT true,
    execution_count INT DEFAULT 0,
    last_executed_at TIMESTAMPTZ,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_email_rules_company ON staffing_email_rules(company_code);
CREATE INDEX IF NOT EXISTS idx_email_rules_trigger ON staffing_email_rules(company_code, trigger_type, is_active);

-- 个人事业主：注文书（从公司发给个人事业主）
CREATE TABLE IF NOT EXISTS staffing_purchase_orders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    order_no TEXT NOT NULL,                  -- 注文書番号
    
    -- 关联
    resource_id UUID REFERENCES resource_pool(id),  -- 个人事业主
    contract_id UUID REFERENCES staffing_contracts(id),
    
    -- 期间
    order_date DATE NOT NULL,                -- 発注日
    period_start DATE NOT NULL,              -- 稼働期間開始
    period_end DATE NOT NULL,                -- 稼働期間終了
    
    -- 金额
    unit_price DECIMAL(10,0) NOT NULL,       -- 単価
    settlement_type TEXT DEFAULT 'monthly',  -- monthly/hourly
    min_hours DECIMAL(6,2),                  -- 精算下限
    max_hours DECIMAL(6,2),                  -- 精算上限
    overtime_rate DECIMAL(4,2),              -- 残業単価率
    
    -- 状态
    status TEXT DEFAULT 'draft',             -- draft/sent/accepted/rejected
    sent_at TIMESTAMPTZ,
    accepted_at TIMESTAMPTZ,
    rejected_at TIMESTAMPTZ,
    rejection_reason TEXT,
    
    -- 文档
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, order_no)
);

CREATE INDEX IF NOT EXISTS idx_purchase_orders_resource ON staffing_purchase_orders(resource_id);
CREATE INDEX IF NOT EXISTS idx_purchase_orders_contract ON staffing_purchase_orders(contract_id);
CREATE INDEX IF NOT EXISTS idx_purchase_orders_status ON staffing_purchase_orders(company_code, status);

-- 个人事业主：请求书（从个人事业主发给公司）
CREATE TABLE IF NOT EXISTS staffing_freelancer_invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_no TEXT NOT NULL,                -- 請求書番号
    
    -- 关联
    resource_id UUID REFERENCES resource_pool(id),  -- 个人事业主
    purchase_order_id UUID REFERENCES staffing_purchase_orders(id),
    timesheet_summary_id UUID REFERENCES staffing_timesheet_summary(id),
    
    -- 期间
    period_start DATE NOT NULL,
    period_end DATE NOT NULL,
    
    -- 金额
    subtotal DECIMAL(12,0) NOT NULL,         -- 税抜金額
    tax_rate DECIMAL(4,2) DEFAULT 0.10,
    tax_amount DECIMAL(12,0),
    total_amount DECIMAL(12,0) NOT NULL,     -- 税込金額
    
    -- 状态
    status TEXT DEFAULT 'draft',             -- draft/submitted/approved/rejected/paid
    submitted_at TIMESTAMPTZ,
    approved_at TIMESTAMPTZ,
    rejected_at TIMESTAMPTZ,
    rejection_reason TEXT,
    
    -- 支付
    paid_at TIMESTAMPTZ,
    paid_amount DECIMAL(12,0),
    payment_reference TEXT,                  -- 振込明細参照
    
    -- 文档
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, invoice_no)
);

CREATE INDEX IF NOT EXISTS idx_freelancer_invoices_resource ON staffing_freelancer_invoices(resource_id);
CREATE INDEX IF NOT EXISTS idx_freelancer_invoices_status ON staffing_freelancer_invoices(company_code, status);
CREATE INDEX IF NOT EXISTS idx_freelancer_invoices_period ON staffing_freelancer_invoices(company_code, period_start, period_end);

-- ============================================================
-- 採番用シーケンス
-- ============================================================
CREATE SEQUENCE IF NOT EXISTS seq_resource_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_project_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_contract_code START 1;
CREATE SEQUENCE IF NOT EXISTS seq_staffing_invoice START 1;
CREATE SEQUENCE IF NOT EXISTS seq_purchase_order START 1;
CREATE SEQUENCE IF NOT EXISTS seq_freelancer_invoice START 1;


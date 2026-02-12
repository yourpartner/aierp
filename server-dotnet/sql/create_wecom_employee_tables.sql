-- ============================================================
-- 企业微信 员工 AI Gateway 数据表
-- Phase 1: Timesheet + WeChat Work 融合
-- ============================================================

-- 1. 员工企业微信会话表：跟踪每个员工的对话上下文
CREATE TABLE IF NOT EXISTS wecom_employee_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    wecom_user_id TEXT NOT NULL,           -- 企业微信 userId
    employee_id UUID,                       -- 关联的员工 ID
    resource_id UUID,                       -- 关联的 resource_pool ID
    
    -- 会话状态
    current_intent TEXT,                    -- 当前意图 (timesheet.entry, payroll.query 等)
    session_state JSONB DEFAULT '{}',       -- 多轮对话的中间状态 (如已填写的工时数据)
    
    -- 活跃窗口
    last_active_at TIMESTAMPTZ DEFAULT now(),
    expires_at TIMESTAMPTZ DEFAULT (now() + interval '30 minutes'),
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
-- 注意：不能在 partial index 中使用 now()（非 IMMUTABLE），改用普通索引
CREATE INDEX IF NOT EXISTS idx_wecom_emp_session_active 
    ON wecom_employee_sessions(company_code, wecom_user_id, expires_at DESC);
CREATE INDEX IF NOT EXISTS idx_wecom_emp_session_employee 
    ON wecom_employee_sessions(company_code, employee_id);

-- 2. 员工企业微信消息记录
CREATE TABLE IF NOT EXISTS wecom_employee_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID REFERENCES wecom_employee_sessions(id),
    company_code TEXT NOT NULL,
    wecom_user_id TEXT NOT NULL,
    direction TEXT NOT NULL DEFAULT 'in',    -- in=员工发送, out=系统回复
    message_type TEXT DEFAULT 'text',        -- text, image, file, voice
    content TEXT,
    intent TEXT,                             -- 分类后的意图
    metadata JSONB DEFAULT '{}',             -- 附加数据 (文件URL, 解析结果等)
    created_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_wecom_emp_msg_session 
    ON wecom_employee_messages(session_id, created_at);
CREATE INDEX IF NOT EXISTS idx_wecom_emp_msg_user 
    ON wecom_employee_messages(company_code, wecom_user_id, created_at DESC);

-- 3. Timesheet 每日工时明细表（支持周录入视图）
--    与 staffing_timesheet_summary 是明细/汇总关系
CREATE TABLE IF NOT EXISTS timesheet_daily_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_id UUID NOT NULL,
    contract_id UUID,
    
    -- 日期与时间
    entry_date DATE NOT NULL,
    start_time TIME,                         -- 上班时间
    end_time TIME,                           -- 下班时间
    break_minutes INT DEFAULT 60,            -- 休息时间(分钟)
    
    -- 工时计算
    regular_hours DECIMAL(4,2) DEFAULT 0,    -- 正常工时
    overtime_hours DECIMAL(4,2) DEFAULT 0,   -- 加班工时
    holiday_flag BOOLEAN DEFAULT FALSE,      -- 是否休日
    
    -- 来源标记
    source TEXT DEFAULT 'manual',            -- manual, wecom, excel_upload, ai_parsed
    source_message_id UUID,                  -- 来源消息 ID（企业微信录入时）
    source_file_url TEXT,                    -- 来源文件 URL（文件上传时）
    
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_timesheet_daily_entry 
    ON timesheet_daily_entries(company_code, resource_id, entry_date, contract_id);
CREATE INDEX IF NOT EXISTS idx_timesheet_daily_resource_month 
    ON timesheet_daily_entries(company_code, resource_id, entry_date);

-- 4. Timesheet 文件上传记录
CREATE TABLE IF NOT EXISTS timesheet_uploads (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_id UUID NOT NULL,
    
    -- 文件信息
    file_name TEXT NOT NULL,
    file_url TEXT NOT NULL,
    file_type TEXT,                           -- xlsx, csv, pdf, image
    file_size INT,
    
    -- AI 解析状态
    parse_status TEXT DEFAULT 'pending',      -- pending, parsing, parsed, failed
    parsed_data JSONB,                        -- AI 解析出的工时数据
    parse_errors JSONB,                       -- 解析错误/警告
    confidence DECIMAL(3,2),                  -- 解析置信度
    
    -- 审核
    reviewed_by UUID,
    reviewed_at TIMESTAMPTZ,
    applied BOOLEAN DEFAULT FALSE,            -- 是否已导入到 daily_entries
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_timesheet_upload_resource 
    ON timesheet_uploads(company_code, resource_id, created_at DESC);

-- 5. 为 staffing_timesheet_summary 添加审批字段 (如不存在)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'staffing_timesheet_summary' AND column_name = 'approval_status'
    ) THEN
        ALTER TABLE staffing_timesheet_summary 
            ADD COLUMN approval_status TEXT DEFAULT 'draft',          -- draft/submitted/approved/rejected
            ADD COLUMN submitted_at TIMESTAMPTZ,
            ADD COLUMN submitted_by UUID,
            ADD COLUMN approved_at TIMESTAMPTZ,
            ADD COLUMN approved_by UUID,
            ADD COLUMN rejection_reason TEXT,
            ADD COLUMN approval_history JSONB DEFAULT '[]';
    END IF;
END $$;

-- 6. 休假申请表
CREATE TABLE IF NOT EXISTS leave_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    employee_id UUID NOT NULL,
    resource_id UUID,
    
    -- 休假详情
    leave_type TEXT NOT NULL DEFAULT 'paid',    -- paid(有休), sick(病假), special(特別), unpaid(無給)
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    days DECIMAL(4,1) NOT NULL DEFAULT 1,       -- 天数（支持0.5半天）
    reason TEXT,
    
    -- 审批
    status TEXT DEFAULT 'pending',              -- pending, approved, rejected, cancelled
    approved_by UUID,
    approved_at TIMESTAMPTZ,
    rejection_reason TEXT,
    
    -- 来源
    source TEXT DEFAULT 'web',                  -- web, wecom
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_leave_requests_employee 
    ON leave_requests(company_code, employee_id, start_date DESC);
CREATE INDEX IF NOT EXISTS idx_leave_requests_status 
    ON leave_requests(company_code, status);

-- 7. 为 certificate_requests 改造 (添加缺失列，如需)
DO $$ BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'certificate_requests' AND column_name = 'employee_id'
    ) THEN
        ALTER TABLE certificate_requests 
            ADD COLUMN employee_id UUID,
            ADD COLUMN request_type TEXT DEFAULT 'employment',
            ADD COLUMN status TEXT DEFAULT 'pending',
            ADD COLUMN requested_at TIMESTAMPTZ DEFAULT now(),
            ADD COLUMN completed_at TIMESTAMPTZ,
            ADD COLUMN document_url TEXT,
            ADD COLUMN notes TEXT,
            ADD COLUMN purpose TEXT,
            ADD COLUMN wecom_source BOOLEAN DEFAULT FALSE;
    END IF;
END $$;

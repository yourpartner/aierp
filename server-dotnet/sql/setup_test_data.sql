-- ============================================================
-- Setup test data for Timesheet + WeChat AI features
-- Run: psql -h localhost -U postgres -d postgres -f sql/setup_test_data.sql
-- ============================================================

-- 1. Create resource pool entry for admin user (JP01)
INSERT INTO stf_resources (company_code, payload)
VALUES (
    'JP01',
    jsonb_build_object(
        'resource_code', 'RES-ADMIN-001',
        'display_name', 'Admin User',
        'resource_type', 'internal',
        'availability_status', 'available',
        'status', 'active',
        'employee_id', '04c98bba-ad35-4c88-ad75-894f9a3b4916',
        'email', 'admin@example.com',
        'phone', '090-0000-0001',
        'skills', '["management","testing"]',
        'hourly_rate', 5000,
        'monthly_rate', 800000,
        'join_date', '2024-01-01',
        'notes', 'Test resource for admin user'
    )
)
ON CONFLICT (company_code, resource_code) DO NOTHING;

-- 2. Create the new tables if not exists
-- (wecom_employee_sessions, wecom_employee_messages, timesheet_daily_entries, etc.)
CREATE TABLE IF NOT EXISTS wecom_employee_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    wecom_user_id TEXT NOT NULL,
    employee_id UUID,
    resource_id UUID,
    session_state JSONB DEFAULT '{}',
    last_intent TEXT,
    expires_at TIMESTAMPTZ DEFAULT now() + interval '30 minutes',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_wecom_sessions_user
    ON wecom_employee_sessions(company_code, wecom_user_id, expires_at DESC);

CREATE TABLE IF NOT EXISTS wecom_employee_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    session_id UUID REFERENCES wecom_employee_sessions(id),
    direction TEXT NOT NULL DEFAULT 'inbound',
    msg_type TEXT DEFAULT 'text',
    content TEXT,
    intent TEXT,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_wecom_messages_session
    ON wecom_employee_messages(session_id, created_at);

CREATE TABLE IF NOT EXISTS timesheet_daily_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_id UUID NOT NULL,
    contract_id UUID,
    entry_date DATE NOT NULL,
    start_time TEXT,
    end_time TEXT,
    break_minutes INTEGER DEFAULT 60,
    regular_hours DECIMAL(5,2) DEFAULT 0,
    overtime_hours DECIMAL(5,2) DEFAULT 0,
    holiday_flag BOOLEAN DEFAULT false,
    source TEXT DEFAULT 'web',
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_timesheet_daily_resource
    ON timesheet_daily_entries(company_code, resource_id, entry_date);

CREATE TABLE IF NOT EXISTS timesheet_uploads (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_id UUID NOT NULL,
    file_name TEXT,
    file_url TEXT,
    file_type TEXT,
    file_size BIGINT,
    parse_status TEXT DEFAULT 'pending',
    parsed_data JSONB,
    parsed_at TIMESTAMPTZ,
    applied BOOLEAN DEFAULT false,
    applied_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE TABLE IF NOT EXISTS leave_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    employee_id UUID NOT NULL,
    resource_id UUID,
    leave_type TEXT NOT NULL DEFAULT 'paid',
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    days DECIMAL(4,1) NOT NULL DEFAULT 1,
    reason TEXT,
    status TEXT DEFAULT 'pending',
    approved_by UUID,
    approved_at TIMESTAMPTZ,
    rejection_reason TEXT,
    source TEXT DEFAULT 'web',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_leave_requests_employee
    ON leave_requests(company_code, employee_id, start_date DESC);
CREATE INDEX IF NOT EXISTS idx_leave_requests_status
    ON leave_requests(company_code, status);

-- 3. Add approval columns to staffing_timesheet_summary if they don't exist
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                   WHERE table_name = 'staffing_timesheet_summary' AND column_name = 'approval_status') THEN
        ALTER TABLE staffing_timesheet_summary 
            ADD COLUMN approval_status TEXT DEFAULT 'open',
            ADD COLUMN submitted_at TIMESTAMPTZ,
            ADD COLUMN approved_by UUID,
            ADD COLUMN approved_at TIMESTAMPTZ,
            ADD COLUMN rejection_reason TEXT;
    END IF;
EXCEPTION WHEN undefined_table THEN
    NULL;
END $$;

-- 4. Add columns to certificate_requests if needed
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'certificate_requests') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns 
                       WHERE table_name = 'certificate_requests' AND column_name = 'wecom_source') THEN
            ALTER TABLE certificate_requests 
                ADD COLUMN IF NOT EXISTS employee_id UUID,
                ADD COLUMN IF NOT EXISTS request_type TEXT DEFAULT 'employment',
                ADD COLUMN IF NOT EXISTS status TEXT DEFAULT 'pending',
                ADD COLUMN IF NOT EXISTS requested_at TIMESTAMPTZ DEFAULT now(),
                ADD COLUMN IF NOT EXISTS completed_at TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS document_url TEXT,
                ADD COLUMN IF NOT EXISTS notes TEXT,
                ADD COLUMN IF NOT EXISTS purpose TEXT,
                ADD COLUMN IF NOT EXISTS wecom_source BOOLEAN DEFAULT false;
        END IF;
    END IF;
END $$;

-- 5. Verify setup
DO $$
DECLARE
    res_id UUID;
    res_count INT;
BEGIN
    SELECT id INTO res_id FROM stf_resources 
    WHERE company_code = 'JP01' AND payload->>'employee_id' = '04c98bba-ad35-4c88-ad75-894f9a3b4916';
    
    IF res_id IS NOT NULL THEN
        RAISE NOTICE 'SUCCESS: Resource created with ID: %', res_id;
    ELSE
        RAISE NOTICE 'WARNING: Resource not found!';
    END IF;
    
    SELECT COUNT(*) INTO res_count FROM information_schema.tables 
    WHERE table_name IN ('wecom_employee_sessions', 'wecom_employee_messages', 
                         'timesheet_daily_entries', 'timesheet_uploads', 'leave_requests');
    RAISE NOTICE 'Tables created: %/5', res_count;
END $$;

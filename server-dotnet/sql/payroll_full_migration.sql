-- ============================================
-- 工资计算完整数据库迁移脚本
-- 整合所有工资相关的表结构和索引
-- ============================================

-- 1. 工资计算运行记录表
CREATE TABLE IF NOT EXISTS payroll_runs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    policy_id UUID,
    period_month TEXT NOT NULL,
    run_type TEXT NOT NULL DEFAULT 'manual',
    status TEXT NOT NULL DEFAULT 'pending',
    total_amount NUMERIC(18,2),
    diff_summary JSONB,
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

-- 工资计算运行记录索引
CREATE INDEX IF NOT EXISTS idx_payroll_runs_company_month 
ON payroll_runs(company_code, period_month);

CREATE INDEX IF NOT EXISTS idx_payroll_runs_status 
ON payroll_runs(company_code, status);

-- 唯一约束：同一公司、月份、类型、政策只能有一条记录
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_payroll_runs_company_month_type_policy') THEN
        ALTER TABLE payroll_runs 
        ADD CONSTRAINT uq_payroll_runs_company_month_type_policy 
        UNIQUE (company_code, period_month, run_type, policy_id);
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Constraint uq_payroll_runs_company_month_type_policy may already exist or duplicates exist';
END $$;

-- 2. 工资计算条目表
CREATE TABLE IF NOT EXISTS payroll_run_entries (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL REFERENCES payroll_runs(id) ON DELETE CASCADE,
    company_code TEXT NOT NULL,
    employee_id UUID NOT NULL,
    employee_code TEXT,
    employee_name TEXT,
    department_code TEXT,
    total_amount NUMERIC(18,2) NOT NULL,
    payroll_sheet JSONB NOT NULL,
    accounting_draft JSONB NOT NULL,
    diff_summary JSONB,
    metadata JSONB,
    voucher_id UUID,
    voucher_no TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);

-- 添加可能缺失的列
ALTER TABLE payroll_run_entries ADD COLUMN IF NOT EXISTS voucher_id UUID;
ALTER TABLE payroll_run_entries ADD COLUMN IF NOT EXISTS voucher_no TEXT;

-- 工资计算条目索引
CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_run 
ON payroll_run_entries(run_id);

CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_employee 
ON payroll_run_entries(employee_id);

CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_company 
ON payroll_run_entries(company_code);

CREATE INDEX IF NOT EXISTS ix_payroll_run_entries_voucher_id 
ON payroll_run_entries (voucher_id);

-- 唯一约束：同一运行记录内每个员工只能有一条记录
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_payroll_run_entries_run_employee') THEN
        ALTER TABLE payroll_run_entries 
        ADD CONSTRAINT uq_payroll_run_entries_run_employee 
        UNIQUE (run_id, employee_id);
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Constraint uq_payroll_run_entries_run_employee may already exist or duplicates exist';
END $$;

-- 3. 工资计算追踪表（存储计算过程）
CREATE TABLE IF NOT EXISTS payroll_run_traces (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id UUID NOT NULL REFERENCES payroll_runs(id) ON DELETE CASCADE,
    entry_id UUID NOT NULL REFERENCES payroll_run_entries(id) ON DELETE CASCADE,
    employee_id UUID NOT NULL,
    trace JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_payroll_run_traces_entry 
ON payroll_run_traces(entry_id);

-- 4. AI工资审核任务表
CREATE TABLE IF NOT EXISTS ai_payroll_tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL,
    company_code TEXT NOT NULL,
    run_id UUID,
    entry_id UUID,
    employee_id UUID,
    employee_code TEXT,
    employee_name TEXT,
    period_month TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    task_type TEXT DEFAULT 'confirmation',
    summary TEXT,
    metadata JSONB,
    diff_summary JSONB,
    payload JSONB,
    target_user_id TEXT,
    assigned_user_id TEXT,
    completed_by_user_id TEXT,
    comments TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    completed_at TIMESTAMPTZ
);

-- 添加可能缺失的列
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS task_type TEXT DEFAULT 'confirmation';
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS diff_summary JSONB;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS target_user_id TEXT;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS assigned_user_id TEXT;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS completed_by_user_id TEXT;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS comments TEXT;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS completed_at TIMESTAMPTZ;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS payload JSONB;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS period_month TEXT;
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS summary TEXT;

-- AI任务索引
CREATE INDEX IF NOT EXISTS idx_ai_payroll_tasks_session 
ON ai_payroll_tasks(session_id);

CREATE INDEX IF NOT EXISTS idx_ai_payroll_tasks_company 
ON ai_payroll_tasks(company_code, status);

CREATE INDEX IF NOT EXISTS idx_ai_payroll_tasks_target_user 
ON ai_payroll_tasks(company_code, target_user_id) 
WHERE target_user_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_ai_payroll_tasks_type_status 
ON ai_payroll_tasks(company_code, task_type, status);

-- 唯一约束：同一公司、运行记录、条目只能有一个任务
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_ai_payroll_tasks_company_run_entry') THEN
        ALTER TABLE ai_payroll_tasks 
        ADD CONSTRAINT uq_ai_payroll_tasks_company_run_entry 
        UNIQUE (company_code, run_id, entry_id);
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Constraint may already exist or there are duplicates';
END $$;

-- 5. 工资计算截止日期表
CREATE TABLE IF NOT EXISTS payroll_deadlines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    period_month TEXT NOT NULL,
    deadline_at TIMESTAMPTZ NOT NULL,
    warning_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'pending',
    notified_at TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    metadata JSONB,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE (company_code, period_month)
);

CREATE INDEX IF NOT EXISTS idx_payroll_deadlines_status 
ON payroll_deadlines(company_code, status, deadline_at);

-- 6. 工资政策表
CREATE TABLE IF NOT EXISTS payroll_policies (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    policy_code TEXT,
    version TEXT,
    payload JSONB NOT NULL,
    is_active BOOLEAN DEFAULT false,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

-- 添加可能缺失的列
ALTER TABLE payroll_policies ADD COLUMN IF NOT EXISTS policy_code TEXT;
ALTER TABLE payroll_policies ADD COLUMN IF NOT EXISTS version TEXT;
ALTER TABLE payroll_policies ADD COLUMN IF NOT EXISTS is_active BOOLEAN DEFAULT false;
ALTER TABLE payroll_policies ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ DEFAULT now();

CREATE INDEX IF NOT EXISTS idx_payroll_policies_company 
ON payroll_policies(company_code);

-- 只有 is_active 列存在时才创建索引
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'payroll_policies' AND column_name = 'is_active'
    ) THEN
        CREATE INDEX IF NOT EXISTS idx_payroll_policies_active 
        ON payroll_policies(company_code, is_active) 
        WHERE is_active = true;
    END IF;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Could not create index idx_payroll_policies_active';
END $$;

-- ============================================
-- 清理重复数据（如果有）
-- ============================================

-- 清理重复的 payroll_run_entries（保留最新的）
DELETE FROM payroll_run_entries
WHERE id IN (
    SELECT id
    FROM (
        SELECT id,
               ROW_NUMBER() OVER (PARTITION BY run_id, employee_id ORDER BY created_at DESC) as rn
        FROM payroll_run_entries
    ) t
    WHERE t.rn > 1
);

-- 清理重复的 payroll_runs（保留最新的）
DELETE FROM payroll_runs
WHERE id IN (
    SELECT id
    FROM (
        SELECT id,
               ROW_NUMBER() OVER (PARTITION BY company_code, period_month, run_type, policy_id ORDER BY created_at DESC) as rn
        FROM payroll_runs
    ) t
    WHERE t.rn > 1
);

-- ============================================
-- 验证表结构
-- ============================================
SELECT 'payroll_runs' as table_name, COUNT(*) as row_count FROM payroll_runs
UNION ALL
SELECT 'payroll_run_entries', COUNT(*) FROM payroll_run_entries
UNION ALL
SELECT 'payroll_run_traces', COUNT(*) FROM payroll_run_traces
UNION ALL
SELECT 'ai_payroll_tasks', COUNT(*) FROM ai_payroll_tasks
UNION ALL
SELECT 'payroll_deadlines', COUNT(*) FROM payroll_deadlines
UNION ALL
SELECT 'payroll_policies', COUNT(*) FROM payroll_policies;


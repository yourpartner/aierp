-- ============================================================================
-- 统一任务表迁移脚本
-- 将 ai_invoice_tasks, ai_sales_order_tasks, ai_payroll_tasks, alert_tasks 
-- 统一到 ai_tasks 表
-- ============================================================================

-- 1. 创建统一的 ai_tasks 表
CREATE TABLE IF NOT EXISTS ai_tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID,                         -- AI 会话 ID（可选，有些任务不属于特定会话）
    company_code TEXT NOT NULL,
    task_type TEXT NOT NULL,                 -- 'invoice', 'sales_order', 'payroll', 'alert', 等
    status TEXT NOT NULL DEFAULT 'pending',  -- 'pending', 'in_progress', 'completed', 'cancelled', 'failed'
    title TEXT,
    summary TEXT,
    
    -- 通用用户字段
    user_id TEXT,                            -- 创建者/所属用户
    target_user_id TEXT,                     -- 目标用户（用于通知）
    assigned_user_id TEXT,                   -- 已分配用户
    
    -- 所有类型特定的数据都放在 payload 中
    payload JSONB DEFAULT '{}',
    metadata JSONB DEFAULT '{}',
    
    -- 时间戳
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    completed_by TEXT
);

-- 2. 创建索引
CREATE INDEX IF NOT EXISTS idx_ai_tasks_session ON ai_tasks(session_id);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_company_type ON ai_tasks(company_code, task_type);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_company_status ON ai_tasks(company_code, status);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_target_user ON ai_tasks(company_code, target_user_id, status);
CREATE INDEX IF NOT EXISTS idx_ai_tasks_created ON ai_tasks(created_at DESC);

-- 3. 迁移 ai_invoice_tasks 数据
INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, payload, metadata, created_at, updated_at)
SELECT 
    id,
    session_id,
    company_code,
    'invoice',
    COALESCE(status, 'pending'),
    COALESCE(file_name, 'Invoice Task'),
    summary,
    user_id,
    jsonb_build_object(
        'fileId', file_id,
        'documentSessionId', document_session_id,
        'fileName', file_name,
        'contentType', content_type,
        'fileSize', file_size,
        'blobName', blob_name,
        'documentLabel', document_label,
        'storedPath', stored_path,
        'analysis', analysis
    ),
    COALESCE(metadata, '{}'::jsonb),
    created_at,
    updated_at
FROM ai_invoice_tasks
ON CONFLICT (id) DO NOTHING;

-- 4. 迁移 ai_sales_order_tasks 数据
INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, payload, metadata, created_at, updated_at, completed_at)
SELECT 
    id,
    session_id,
    company_code,
    'sales_order',
    COALESCE(status, 'pending'),
    COALESCE(sales_order_no, 'Sales Order Task'),
    summary,
    user_id,
    jsonb_build_object(
        'salesOrderId', sales_order_id,
        'salesOrderNo', sales_order_no,
        'customerCode', customer_code,
        'customerName', customer_name
    ) || COALESCE(payload, '{}'::jsonb),
    COALESCE(metadata, '{}'::jsonb),
    created_at,
    updated_at,
    completed_at
FROM ai_sales_order_tasks
ON CONFLICT (id) DO NOTHING;

-- 5. 迁移 ai_payroll_tasks 数据
INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, user_id, target_user_id, assigned_user_id, payload, metadata, created_at, updated_at, completed_at, completed_by)
SELECT 
    id,
    session_id,
    company_code,
    'payroll',
    COALESCE(status, 'pending'),
    COALESCE(employee_name, 'Payroll Task'),
    summary,
    NULL,
    target_user_id,
    assigned_user_id,
    jsonb_build_object(
        'runId', run_id,
        'entryId', entry_id,
        'employeeId', employee_id,
        'employeeCode', employee_code,
        'employeeName', employee_name,
        'periodMonth', period_month,
        'taskType', task_type,
        'diffSummary', diff_summary,
        'comments', comments
    ) || COALESCE(payload, '{}'::jsonb),
    COALESCE(metadata, '{}'::jsonb),
    created_at,
    updated_at,
    completed_at,
    completed_by_user_id
FROM ai_payroll_tasks
ON CONFLICT (id) DO NOTHING;

-- 6. 迁移 alert_tasks 数据
INSERT INTO ai_tasks (id, session_id, company_code, task_type, status, title, summary, assigned_user_id, payload, metadata, created_at, updated_at, completed_at, completed_by)
SELECT 
    id,
    NULL,
    company_code,
    'alert',
    COALESCE(status, 'pending'),
    title,
    description,
    assigned_to,
    jsonb_build_object(
        'alertId', alert_id,
        'alertTaskType', task_type,
        'priority', priority,
        'dueDate', due_date,
        'completionNote', completion_note
    ) || COALESCE(payload, '{}'::jsonb),
    '{}'::jsonb,
    created_at,
    updated_at,
    completed_at,
    completed_by
FROM alert_tasks
ON CONFLICT (id) DO NOTHING;

-- 7. 创建视图以保持向后兼容（可选，过渡期使用）
-- CREATE OR REPLACE VIEW ai_invoice_tasks_view AS
-- SELECT 
--     id, session_id, company_code,
--     payload->>'fileId' as file_id,
--     payload->>'documentSessionId' as document_session_id,
--     payload->>'fileName' as file_name,
--     payload->>'contentType' as content_type,
--     (payload->>'fileSize')::bigint as file_size,
--     payload->>'blobName' as blob_name,
--     payload->>'documentLabel' as document_label,
--     payload->>'storedPath' as stored_path,
--     user_id,
--     status,
--     summary,
--     payload->'analysis' as analysis,
--     metadata,
--     created_at,
--     updated_at
-- FROM ai_tasks
-- WHERE task_type = 'invoice';

-- 注意：在确认数据迁移完成后，可以删除旧表
-- DROP TABLE IF EXISTS ai_invoice_tasks;
-- DROP TABLE IF EXISTS ai_sales_order_tasks;
-- DROP TABLE IF EXISTS ai_payroll_tasks;
-- DROP TABLE IF EXISTS alert_tasks;


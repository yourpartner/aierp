-- Migration: Add new columns to ai_payroll_tasks for enhanced task management
-- Date: 2024

-- Add task_type column to distinguish between confirmation and anomaly handling tasks
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS task_type TEXT DEFAULT 'confirmation';

-- Add assigned_user_id for explicit task assignment (separate from target_user_id)
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS assigned_user_id TEXT;

-- Add completed_by_user_id to track who completed the task
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS completed_by_user_id TEXT;

-- Add comments field for reviewer notes
ALTER TABLE ai_payroll_tasks ADD COLUMN IF NOT EXISTS comments TEXT;

-- Create unique constraint to prevent duplicate tasks for the same entry
CREATE UNIQUE INDEX IF NOT EXISTS uq_payroll_tasks_entry 
ON ai_payroll_tasks(company_code, run_id, entry_id);

-- Create index for user-based queries
CREATE INDEX IF NOT EXISTS idx_payroll_tasks_user 
ON ai_payroll_tasks(company_code, target_user_id, status);

CREATE INDEX IF NOT EXISTS idx_payroll_tasks_assigned 
ON ai_payroll_tasks(company_code, assigned_user_id, status);

-- Create index for task type queries
CREATE INDEX IF NOT EXISTS idx_payroll_tasks_type 
ON ai_payroll_tasks(company_code, task_type, status);

-- Create index for month-based queries
CREATE INDEX IF NOT EXISTS idx_payroll_tasks_month 
ON ai_payroll_tasks(company_code, period_month, status);

-- Add preflight_checks table to store preflight check results
CREATE TABLE IF NOT EXISTS payroll_preflight_checks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    period_month TEXT NOT NULL,
    run_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    can_proceed BOOLEAN NOT NULL DEFAULT false,
    total_employees INT NOT NULL DEFAULT 0,
    passed_employees INT NOT NULL DEFAULT 0,
    failed_employees INT NOT NULL DEFAULT 0,
    results JSONB NOT NULL DEFAULT '[]'::jsonb,
    global_warnings JSONB NOT NULL DEFAULT '[]'::jsonb,
    config JSONB,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_preflight_checks_company_month 
ON payroll_preflight_checks(company_code, period_month, run_at DESC);

-- Add payroll_deadlines table for tracking calculation deadlines
CREATE TABLE IF NOT EXISTS payroll_deadlines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    period_month TEXT NOT NULL,
    deadline_at TIMESTAMPTZ NOT NULL,
    warning_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'pending',
    completed_at TIMESTAMPTZ,
    notified_at TIMESTAMPTZ,
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(company_code, period_month)
);

CREATE INDEX IF NOT EXISTS idx_payroll_deadlines_status 
ON payroll_deadlines(company_code, status, deadline_at);


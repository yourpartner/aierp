-- Fix duplicate payroll entries and add unique constraint
-- This ensures only one entry per employee per payroll run

-- Step 1: Find and remove duplicate entries, keeping only the latest one
WITH duplicates AS (
    SELECT id, run_id, employee_id, created_at,
           ROW_NUMBER() OVER (PARTITION BY run_id, employee_id ORDER BY created_at DESC) as rn
    FROM payroll_run_entries
)
DELETE FROM payroll_run_entries
WHERE id IN (SELECT id FROM duplicates WHERE rn > 1);

-- Step 2: Add unique constraint on run_id + employee_id
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint 
        WHERE conname = 'uq_payroll_run_entries_run_employee'
    ) THEN
        ALTER TABLE payroll_run_entries 
        ADD CONSTRAINT uq_payroll_run_entries_run_employee 
        UNIQUE (run_id, employee_id);
    END IF;
END $$;

-- Step 3: Also ensure only one run per company/month/runType/policy combination
-- First clean up duplicate runs (keep the latest)
WITH run_duplicates AS (
    SELECT id, company_code, period_month, run_type, policy_id, created_at,
           ROW_NUMBER() OVER (
               PARTITION BY company_code, period_month, run_type, COALESCE(policy_id::text, '')
               ORDER BY created_at DESC
           ) as rn
    FROM payroll_runs
)
DELETE FROM payroll_runs
WHERE id IN (SELECT id FROM run_duplicates WHERE rn > 1);

-- Step 4: Add unique constraint on payroll_runs
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint 
        WHERE conname = 'uq_payroll_runs_company_month_type_policy'
    ) THEN
        -- Create a unique index that handles NULL policy_id properly
        CREATE UNIQUE INDEX IF NOT EXISTS uq_payroll_runs_company_month_type_policy
        ON payroll_runs (company_code, period_month, run_type, COALESCE(policy_id::text, ''));
    END IF;
END $$;

-- Verify remaining data
SELECT 'payroll_runs' as table_name, COUNT(*) as count FROM payroll_runs
UNION ALL
SELECT 'payroll_run_entries' as table_name, COUNT(*) as count FROM payroll_run_entries;


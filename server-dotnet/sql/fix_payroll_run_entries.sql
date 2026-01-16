-- Fix payroll_run_entries table: add missing voucher columns
-- Run this script to update the table structure

-- Add voucher_id column if not exists
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'payroll_run_entries' AND column_name = 'voucher_id'
    ) THEN
        ALTER TABLE payroll_run_entries ADD COLUMN voucher_id UUID;
    END IF;
END $$;

-- Add voucher_no column if not exists
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'payroll_run_entries' AND column_name = 'voucher_no'
    ) THEN
        ALTER TABLE payroll_run_entries ADD COLUMN voucher_no TEXT;
    END IF;
END $$;

-- Create index on voucher_id for faster lookups
CREATE INDEX IF NOT EXISTS idx_payroll_run_entries_voucher_id 
ON payroll_run_entries(voucher_id) WHERE voucher_id IS NOT NULL;

-- Verify the changes
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_name = 'payroll_run_entries'
ORDER BY ordinal_position;


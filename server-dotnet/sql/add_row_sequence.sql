-- Add row_sequence field to preserve original CSV order
ALTER TABLE moneytree_transactions 
ADD COLUMN IF NOT EXISTS row_sequence INTEGER;

-- Add comment
COMMENT ON COLUMN moneytree_transactions.row_sequence IS 'Original row number from CSV file to preserve bank transaction order';

-- Create index for efficient sorting
CREATE INDEX IF NOT EXISTS idx_moneytree_transactions_sequence 
ON moneytree_transactions (company_code, transaction_date, row_sequence);


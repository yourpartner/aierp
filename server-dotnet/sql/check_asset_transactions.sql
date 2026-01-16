-- 检查 asset_transactions 表结构
SELECT column_name, data_type, is_generated
FROM information_schema.columns 
WHERE table_name = 'asset_transactions' 
ORDER BY ordinal_position;

-- 检查现有交易数据
SELECT id, transaction_type, posting_date, voucher_id, voucher_no, 
       payload->>'voucherId' as payload_voucher_id,
       payload->>'voucherNo' as payload_voucher_no
FROM asset_transactions
LIMIT 10;

-- 如果生成列不存在，添加它们（注意：生成列需要先删除再添加）
-- 首先检查是否需要添加
DO $$
BEGIN
  -- 检查 voucher_id 列是否存在
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.columns 
    WHERE table_name = 'asset_transactions' AND column_name = 'voucher_id'
  ) THEN
    -- 添加生成列
    ALTER TABLE asset_transactions 
    ADD COLUMN voucher_id TEXT GENERATED ALWAYS AS (payload->>'voucherId') STORED;
    
    ALTER TABLE asset_transactions 
    ADD COLUMN voucher_no TEXT GENERATED ALWAYS AS (payload->>'voucherNo') STORED;
    
    RAISE NOTICE 'Added voucher_id and voucher_no generated columns';
  ELSE
    RAISE NOTICE 'voucher_id column already exists';
  END IF;
END $$;


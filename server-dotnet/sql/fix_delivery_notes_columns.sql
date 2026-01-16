-- 修复 delivery_notes 表 - 只添加缺失的列
-- fn_jsonb_to_date 函数已存在，不需要重新创建

-- 添加 delivery_no 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='delivery_no') THEN
        ALTER TABLE delivery_notes ADD COLUMN delivery_no TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'deliveryNo', payload->>'deliveryNo')
        ) STORED;
    END IF;
END $$;

-- 删除旧的 sales_order_no 列（如果存在）
ALTER TABLE delivery_notes DROP COLUMN IF EXISTS sales_order_no;

-- 添加 so_no 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='so_no') THEN
        ALTER TABLE delivery_notes ADD COLUMN so_no TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'salesOrderNo', payload->>'salesOrderNo')
        ) STORED;
    END IF;
END $$;

-- 添加 customer_code 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='customer_code') THEN
        ALTER TABLE delivery_notes ADD COLUMN customer_code TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'customerCode', payload->>'customerCode')
        ) STORED;
    END IF;
END $$;

-- 添加 customer_name 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='customer_name') THEN
        ALTER TABLE delivery_notes ADD COLUMN customer_name TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'customerName', payload->>'customerName')
        ) STORED;
    END IF;
END $$;

-- 添加 delivery_date 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='delivery_date') THEN
        ALTER TABLE delivery_notes ADD COLUMN delivery_date DATE GENERATED ALWAYS AS (
            fn_jsonb_to_date(COALESCE(payload->'header', payload), 'deliveryDate')
        ) STORED;
    END IF;
END $$;

-- 添加 status 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='status') THEN
        ALTER TABLE delivery_notes ADD COLUMN status TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'status', payload->>'status', 'draft')
        ) STORED;
    END IF;
END $$;

-- 添加 print_status 列（如果不存在）
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='delivery_notes' AND column_name='print_status') THEN
        ALTER TABLE delivery_notes ADD COLUMN print_status TEXT GENERATED ALWAYS AS (
            COALESCE(payload->'header'->>'printStatus', payload->>'printStatus')
        ) STORED;
    END IF;
END $$;

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_dn_company_no ON delivery_notes(company_code, delivery_no);
CREATE INDEX IF NOT EXISTS idx_dn_company_so ON delivery_notes(company_code, so_no);
CREATE INDEX IF NOT EXISTS idx_dn_company_status ON delivery_notes(company_code, status);
CREATE INDEX IF NOT EXISTS idx_dn_company_customer ON delivery_notes(company_code, customer_code);

-- 验证
SELECT 'delivery_notes 表修复完成' AS result;
SELECT column_name, data_type 
FROM information_schema.columns 
WHERE table_name = 'delivery_notes'
ORDER BY ordinal_position;

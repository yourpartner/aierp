-- 盘点功能数据库表结构

-- 盘点主表（盘点单头）
CREATE TABLE IF NOT EXISTS inventory_counts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    count_no VARCHAR(50) NOT NULL,                    -- 盘点单号
    warehouse_code VARCHAR(20) NOT NULL,              -- 盘点仓库
    count_date DATE NOT NULL,                         -- 盘点日期
    status VARCHAR(20) NOT NULL DEFAULT 'draft',      -- draft/in_progress/completed/cancelled
    description TEXT,                                  -- 盘点说明
    created_by VARCHAR(50),                           -- 创建人
    completed_at TIMESTAMPTZ,                         -- 完成时间
    completed_by VARCHAR(50),                         -- 完成人
    posted_at TIMESTAMPTZ,                            -- 过账时间（生成库存调整的时间）
    posted_by VARCHAR(50),                            -- 过账人
    adjustment_voucher_no VARCHAR(50),                -- 关联的调整凭证号
    payload JSONB DEFAULT '{}',                       -- 扩展字段
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(company_code, count_no)
);

-- 盘点明细表
CREATE TABLE IF NOT EXISTS inventory_count_lines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    count_id UUID NOT NULL REFERENCES inventory_counts(id) ON DELETE CASCADE,
    line_no INTEGER NOT NULL,                         -- 行号
    material_code VARCHAR(50) NOT NULL,               -- 品目编码
    material_name VARCHAR(200),                       -- 品目名称（冗余）
    bin_code VARCHAR(50),                             -- 棚番
    batch_no VARCHAR(50),                             -- 批次号
    uom VARCHAR(20),                                  -- 单位
    system_qty DECIMAL(18,4) NOT NULL DEFAULT 0,      -- 系统库存数量
    actual_qty DECIMAL(18,4),                         -- 实际盘点数量（null表示未盘点）
    variance_qty DECIMAL(18,4) GENERATED ALWAYS AS (COALESCE(actual_qty, 0) - system_qty) STORED, -- 差异数量
    variance_reason TEXT,                             -- 差异原因
    status VARCHAR(20) NOT NULL DEFAULT 'pending',    -- pending/counted/confirmed
    counted_at TIMESTAMPTZ,                           -- 盘点时间
    counted_by VARCHAR(50),                           -- 盘点人
    payload JSONB DEFAULT '{}',                       -- 扩展字段
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(count_id, line_no)
);

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_inventory_counts_company ON inventory_counts(company_code);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_warehouse ON inventory_counts(company_code, warehouse_code);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_status ON inventory_counts(company_code, status);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_date ON inventory_counts(company_code, count_date);
CREATE INDEX IF NOT EXISTS idx_inventory_count_lines_count ON inventory_count_lines(count_id);
CREATE INDEX IF NOT EXISTS idx_inventory_count_lines_material ON inventory_count_lines(company_code, material_code);

-- 盘点单号序列表
CREATE TABLE IF NOT EXISTS inventory_count_sequences (
    company_code VARCHAR(20) NOT NULL,
    prefix VARCHAR(10) NOT NULL DEFAULT 'IC',
    year INTEGER NOT NULL,
    month INTEGER NOT NULL,
    last_number INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(company_code, prefix, year, month)
);

-- 显示创建结果
SELECT 'inventory_counts' as table_name, COUNT(*) as row_count FROM inventory_counts
UNION ALL
SELECT 'inventory_count_lines', COUNT(*) FROM inventory_count_lines
UNION ALL
SELECT 'inventory_count_sequences', COUNT(*) FROM inventory_count_sequences;


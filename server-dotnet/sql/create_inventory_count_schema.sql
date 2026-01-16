-- 盘点功能数据库表结构（Schema 方式，header + lines 存储在 payload 中）

-- 盘点主表（使用通用实体表结构）
CREATE TABLE IF NOT EXISTS inventory_counts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code VARCHAR(20) NOT NULL,
    count_no VARCHAR(50),                              -- 盘点单号（从 payload.header.countNo 提取）
    warehouse_code VARCHAR(20),                        -- 仓库代码（从 payload.header.warehouseCode 提取）
    count_date DATE,                                   -- 盘点日期（从 payload.header.countDate 提取）
    status VARCHAR(20) DEFAULT 'draft',                -- 状态（从 payload.header.status 提取）
    payload JSONB NOT NULL DEFAULT '{"header":{},"lines":[]}',  -- 完整数据：header + lines
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 添加约束
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_inventory_counts_count_no') THEN
        ALTER TABLE inventory_counts ADD CONSTRAINT uq_inventory_counts_count_no UNIQUE(company_code, count_no);
    END IF;
END $$;

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_inventory_counts_company ON inventory_counts(company_code);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_warehouse ON inventory_counts(company_code, warehouse_code);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_status ON inventory_counts(company_code, status);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_date ON inventory_counts(company_code, count_date);
CREATE INDEX IF NOT EXISTS idx_inventory_counts_payload ON inventory_counts USING GIN(payload);

-- 盘点单号序列表
CREATE TABLE IF NOT EXISTS inventory_count_sequences (
    company_code VARCHAR(20) NOT NULL,
    prefix VARCHAR(10) NOT NULL DEFAULT 'IC',
    year INTEGER NOT NULL,
    month INTEGER NOT NULL,
    last_number INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(company_code, prefix, year, month)
);

-- 创建触发器：自动从 payload 同步核心字段
CREATE OR REPLACE FUNCTION sync_inventory_count_fields()
RETURNS TRIGGER AS $$
BEGIN
    NEW.count_no := NEW.payload->'header'->>'countNo';
    NEW.warehouse_code := NEW.payload->'header'->>'warehouseCode';
    NEW.count_date := (NEW.payload->'header'->>'countDate')::DATE;
    NEW.status := COALESCE(NEW.payload->'header'->>'status', 'draft');
    NEW.updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sync_inventory_count_fields ON inventory_counts;
CREATE TRIGGER trg_sync_inventory_count_fields
    BEFORE INSERT OR UPDATE ON inventory_counts
    FOR EACH ROW
    EXECUTE FUNCTION sync_inventory_count_fields();

-- 显示创建结果
SELECT 'inventory_counts' as table_name, COUNT(*) as row_count FROM inventory_counts
UNION ALL
SELECT 'inventory_count_sequences', COUNT(*) FROM inventory_count_sequences;

-- 示例数据结构说明：
/*
payload 结构示例:
{
  "header": {
    "countNo": "IC202411300001",
    "countDate": "2024-11-30",
    "warehouseCode": "WH01",
    "warehouseName": "主倉庫",
    "status": "in_progress",
    "description": "11月定期棚卸",
    "createdBy": "admin",
    "completedAt": null,
    "completedBy": null,
    "postedAt": null,
    "postedBy": null
  },
  "lines": [
    {
      "lineNo": 1,
      "materialCode": "MAT001",
      "materialName": "商品A",
      "binCode": "A-01",
      "batchNo": "LOT001",
      "uom": "個",
      "systemQty": 100,
      "actualQty": 98,
      "varianceQty": -2,
      "varianceReason": "破損",
      "status": "counted",
      "countedAt": "2024-11-30T10:30:00Z",
      "countedBy": "tanaka"
    },
    {
      "lineNo": 2,
      "materialCode": "MAT002",
      "materialName": "商品B",
      "binCode": "A-02",
      "batchNo": null,
      "uom": "個",
      "systemQty": 50,
      "actualQty": null,
      "varianceQty": null,
      "varianceReason": null,
      "status": "pending",
      "countedAt": null,
      "countedBy": null
    }
  ]
}
*/


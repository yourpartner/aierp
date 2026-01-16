-- ==============================================
-- 给固定资产添加部门字段
-- 运行方式: psql -h localhost -U postgres -d postgres -f add_department_to_fixed_assets.sql
-- ==============================================

-- 1. 添加生成列
ALTER TABLE fixed_assets 
ADD COLUMN IF NOT EXISTS department_id TEXT GENERATED ALWAYS AS (payload->>'departmentId') STORED;

-- 2. 添加索引（用于按部门查询和报表）
CREATE INDEX IF NOT EXISTS idx_fixed_assets_department ON fixed_assets(company_code, department_id);

-- 3. 添加注释
COMMENT ON COLUMN fixed_assets.department_id IS '所属部門ID（payload.departmentIdから生成）- 部門別財務報表に使用';

-- 4. 显示已添加的列
SELECT column_name, data_type, is_nullable 
FROM information_schema.columns 
WHERE table_name = 'fixed_assets' AND column_name = 'department_id';



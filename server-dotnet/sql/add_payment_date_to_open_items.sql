-- 为 open_items 表添加 payment_date 字段
ALTER TABLE open_items ADD COLUMN IF NOT EXISTS payment_date DATE;

COMMENT ON COLUMN open_items.payment_date IS '支付日期（用于FIFO清账排序）';

-- 创建索引以优化FIFO查询
CREATE INDEX IF NOT EXISTS idx_oi_payment_date 
ON open_items(company_code, partner_id, payment_date) 
WHERE residual_amount > 0;

-- 从关联凭证行回填 payment_date
UPDATE open_items oi
SET payment_date = (
    SELECT (line->>'paymentDate')::date
    FROM vouchers v, jsonb_array_elements(v.payload->'lines') AS line
    WHERE v.id = oi.voucher_id
      AND (line->>'lineNo')::int = oi.voucher_line_no
    LIMIT 1
)
WHERE oi.payment_date IS NULL;

-- 对于没有支付日期的，使用凭证日期作为默认值
UPDATE open_items
SET payment_date = doc_date
WHERE payment_date IS NULL AND doc_date IS NOT NULL;

-- 确认结果
SELECT 
    COUNT(*) as total,
    COUNT(payment_date) as with_payment_date,
    COUNT(*) - COUNT(payment_date) as without_payment_date
FROM open_items;


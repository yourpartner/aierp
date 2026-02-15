-- 添加 posting_method 列：记录记账方式（SmartPosting / Skill / ChatKitAI / Manual）
ALTER TABLE moneytree_transactions ADD COLUMN IF NOT EXISTS posting_method TEXT;

-- 添加 posting_confidence 列：记录记账置信度（0.0 ~ 1.0）
ALTER TABLE moneytree_transactions ADD COLUMN IF NOT EXISTS posting_confidence NUMERIC(3,2);

-- 创建索引：方便查询低置信度的已记账交易
CREATE INDEX IF NOT EXISTS idx_moneytree_tx_low_confidence 
ON moneytree_transactions (company_code, posting_status, posting_confidence)
WHERE posting_status = 'posted' AND posting_confidence IS NOT NULL AND posting_confidence < 0.8;

COMMENT ON COLUMN moneytree_transactions.posting_method IS '记账方式: SmartPosting | Skill | ChatKitAI | Manual';
COMMENT ON COLUMN moneytree_transactions.posting_confidence IS '记账置信度 0.0~1.0，低于阈值需要用户确认';

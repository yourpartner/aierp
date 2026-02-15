-- 添加 posting_confidence 列到 moneytree_transactions 表
-- 用于存储自动记账的置信度 (0.0 - 1.0)
-- 低置信度的记账结果需要用户确认

ALTER TABLE moneytree_transactions
ADD COLUMN IF NOT EXISTS posting_confidence NUMERIC(3,2) DEFAULT NULL;

-- 添加 posting_method 列（如果不存在）
-- 记录记账方式：SmartPosting / Skill / ChatKitAI / Manual
ALTER TABLE moneytree_transactions
ADD COLUMN IF NOT EXISTS posting_method TEXT DEFAULT NULL;

-- 创建索引：方便查询低置信度的已记账明细
CREATE INDEX IF NOT EXISTS idx_moneytree_tx_low_confidence
ON moneytree_transactions (company_code, posting_status, posting_confidence)
WHERE posting_status = 'posted' AND posting_confidence IS NOT NULL AND posting_confidence < 0.8;

COMMENT ON COLUMN moneytree_transactions.posting_confidence IS '自動記帳の置信度 (0.0-1.0)。0.8未満は要確認。';
COMMENT ON COLUMN moneytree_transactions.posting_method IS '記帳方法: SmartPosting, Skill, ChatKitAI, Manual';

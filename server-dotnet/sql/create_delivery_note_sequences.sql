-- 创建纳品书编号序列表
CREATE TABLE IF NOT EXISTS delivery_note_sequences (
    company_code TEXT NOT NULL,
    prefix TEXT NOT NULL DEFAULT 'DN',
    year INTEGER NOT NULL,
    month INTEGER NOT NULL,
    last_number INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (company_code, prefix, year, month)
);

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_dns_company ON delivery_note_sequences(company_code);

SELECT 'delivery_note_sequences 表创建成功' AS result;


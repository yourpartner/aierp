-- 为现有数据设置 row_sequence
-- 策略：按账户号、交易日期分组，根据余额降序来推断原始顺序
-- 余额高的交易在前（银行余额从高到低变化）

WITH sequenced AS (
    SELECT 
        id,
        ROW_NUMBER() OVER (
            PARTITION BY company_code, account_number, transaction_date 
            ORDER BY COALESCE(balance, 0) DESC
        ) as seq
    FROM moneytree_transactions
    WHERE row_sequence IS NULL OR row_sequence = 0
)
UPDATE moneytree_transactions t
SET row_sequence = s.seq
FROM sequenced s
WHERE t.id = s.id;

-- 验证
SELECT 
    transaction_date,
    row_sequence,
    description,
    withdrawal_amount,
    deposit_amount,
    balance
FROM moneytree_transactions 
WHERE company_code = 'zenken'
ORDER BY transaction_date, row_sequence
LIMIT 20;


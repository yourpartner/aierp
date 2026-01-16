-- 恢复银行明细状态并删除错误生成的凭证
-- 执行前请确认公司代码和凭证号

-- 1. 查看需要处理的数据
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2024-12-10'
  AND (description LIKE '%振込手数料%' OR description LIKE '%カク ナン%')
ORDER BY row_sequence;

-- 2. 恢复手续费交易状态（振込手数料 -145, 凭证号 2512000027）
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_message = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE company_code = 'JP01'
  AND voucher_no = '2512000027';

-- 3. 恢复主交易状态（振込 カク ナン -212,204, 凭证号 2512000003）
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_message = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE company_code = 'JP01'
  AND voucher_no = '2512000003'
  AND description LIKE '%カク ナン%';

-- 4. 删除手续费凭证（2512000027）
-- 首先检查凭证内容
SELECT id, voucher_no, payload->'header'->>'summary' as summary
FROM vouchers
WHERE company_code = 'JP01'
  AND voucher_no = '2512000027';

-- 删除凭证（会级联删除相关的 open_items 等）
DELETE FROM vouchers
WHERE company_code = 'JP01'
  AND voucher_no = '2512000027';

-- 5. 刷新总账视图
REFRESH MATERIALIZED VIEW CONCURRENTLY gl_entries_mv;

-- 6. 确认结果
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2024-12-10'
  AND (description LIKE '%振込手数料%' OR description LIKE '%カク ナン%')
ORDER BY row_sequence;


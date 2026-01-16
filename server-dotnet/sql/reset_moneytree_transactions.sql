-- 恢复银行明细到初始状态，并删除错误创建的凭证
-- 公司代码：JP01

BEGIN;

-- 1. 查看将要删除的凭证（2025-12-25的OT类型凭证，摘要包含"振込"）
SELECT id, voucher_no, posting_date, payload->'header'->>'summary' as summary, voucher_type
FROM vouchers
WHERE company_code = 'JP01'
  AND posting_date = '2025-12-25'
  AND voucher_type = 'OT'
  AND payload->'header'->>'summary' LIKE '%振込%'
ORDER BY voucher_no;

-- 2. 查看将要恢复的银行明细
SELECT id, transaction_date, description, withdrawal_amount, deposit_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND posting_status IN ('posted', 'linked')
  AND transaction_date = '2025-12-25'
ORDER BY id;

-- 3. 恢复银行明细状态
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE company_code = 'JP01'
  AND posting_status IN ('posted', 'linked')
  AND transaction_date = '2025-12-25';

-- 4. 删除错误创建的凭证
DELETE FROM vouchers
WHERE company_code = 'JP01'
  AND posting_date = '2025-12-25'
  AND voucher_type = 'OT'
  AND payload->'header'->>'summary' LIKE '%振込%';

-- 5. 确认结果
SELECT 'Transactions reset:' as action, count(*) as count
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2025-12-25'
  AND posting_status = 'pending';

COMMIT;

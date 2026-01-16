-- 1. 解除错误关联的手续费记录 (row_sequence=1)
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE id = 'bd452abd-849b-4b39-89f6-ae0eadde1a77';

-- 2. 恢复刚才被处理的两条记录 (row_sequence=15, 16)
UPDATE moneytree_transactions
SET posting_status = 'pending',
    posting_error = NULL,
    voucher_id = NULL,
    voucher_no = NULL,
    rule_id = NULL,
    rule_title = NULL,
    posting_run_id = NULL,
    updated_at = now()
WHERE id IN ('c6c09936-1a90-4134-802d-99f49a57bf4d', '892d080b-e3de-4135-be52-4742c773ac1a');

-- 3. 删除刚才新生成的凭证
DELETE FROM vouchers WHERE company_code = 'JP01' AND voucher_no IN ('2512000028', '2512000029');

-- 4. 确认结果
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no, row_sequence
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND transaction_date = '2025-12-10'
  AND (description LIKE '%手数料%' OR description LIKE '%カク ナン%')
ORDER BY row_sequence;


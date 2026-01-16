-- 查看最近的银行明细（包含手续费和凭证号2512000003或2512000027的记录）
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND (voucher_no IN ('2512000003', '2512000027') OR posting_status IN ('posted', 'linked'))
ORDER BY transaction_date DESC, row_sequence
LIMIT 20;


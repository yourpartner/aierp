-- 查看关联到凭证 2512000003 的银行明细
SELECT id, transaction_date, description, withdrawal_amount, posting_status, voucher_no
FROM moneytree_transactions
WHERE company_code = 'JP01'
  AND voucher_no = '2512000003';


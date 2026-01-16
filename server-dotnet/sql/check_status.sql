-- 检查12月25日交易状态
SELECT id, description, withdrawal_amount, posting_status, voucher_id 
FROM moneytree_transactions 
WHERE transaction_date = '2025-12-25' 
  AND (description LIKE '%フジカフエウイル%' OR description = '振込手数料')
ORDER BY ABS(withdrawal_amount) DESC
LIMIT 5;

-- 检查凭证2512000037是否存在
SELECT id, voucher_no FROM vouchers WHERE voucher_no = '2512000037';

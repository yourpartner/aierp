BEGIN;

-- 删除新建凭证的 open_items
DELETE FROM open_items WHERE voucher_id='f2c566c9-3c15-496c-91c1-328e9d13c9bc';

-- 删除新建凭证
DELETE FROM vouchers WHERE id='f2c566c9-3c15-496c-91c1-328e9d13c9bc';

-- 清除4条银行明细的匹配关系
UPDATE moneytree_transactions 
SET posting_status='pending', 
    posting_error=NULL, 
    voucher_id=NULL, 
    voucher_no=NULL, 
    rule_id=NULL, 
    rule_title=NULL 
WHERE id IN (
    '3ba6fd71-aa48-4a59-85e4-91ae36470757',
    'd1326442-f62a-40e2-b982-2bdd41bc54fd',
    'ea34dcd7-31fb-4ca7-9241-31bcd4ea1f2b',
    '3e2c8c40-2eab-4790-a93a-f3d18df09be8'
);

-- 恢复凭证序号
UPDATE voucher_sequences SET last_number=59 WHERE company_code='JP01' AND yymm='2511';

COMMIT;


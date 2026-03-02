-- 仅清理 FOS01 的会计相关数据，便于重新导入。不影响他社。
-- 顺序：open_items → vouchers → accounts → voucher_sequences（仅 FOS01）
BEGIN;
DELETE FROM open_items WHERE company_code = 'FOS01';
DELETE FROM vouchers WHERE company_code = 'FOS01';
DELETE FROM accounts WHERE company_code = 'FOS01';
DELETE FROM voucher_sequences WHERE company_code = 'FOS01';
COMMIT;

-- 删除采购和销售相关单据

-- 采购相关
DELETE FROM purchase_order_lines;
DELETE FROM purchase_orders;
DELETE FROM vendor_invoices;

-- 销售相关
DELETE FROM ai_sales_order_tasks;
DELETE FROM sales_alerts;
DELETE FROM delivery_notes;
DELETE FROM sales_invoices;
DELETE FROM sales_orders;

-- 重置序列号
DELETE FROM delivery_note_sequences;
DELETE FROM sales_invoice_sequences;


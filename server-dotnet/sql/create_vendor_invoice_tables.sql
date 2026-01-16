-- 供应商请求书表
CREATE TABLE IF NOT EXISTS vendor_invoices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  invoice_no TEXT GENERATED ALWAYS AS (payload->>'invoiceNo') STORED,
  invoice_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'invoiceDate')) STORED,
  vendor_code TEXT GENERATED ALWAYS AS (payload->>'vendorCode') STORED,
  grand_total NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'grandTotal')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_vendor_invoices_company_no ON vendor_invoices(company_code, invoice_no);
CREATE INDEX IF NOT EXISTS idx_vendor_invoices_company_date ON vendor_invoices(company_code, invoice_date DESC);
CREATE INDEX IF NOT EXISTS idx_vendor_invoices_company_vendor ON vendor_invoices(company_code, vendor_code);

-- 在 schemas 表中添加 vendor_invoice schema
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'vendor_invoice',
  1,
  TRUE,
  '{"type":"object","properties":{"invoiceNo":{"type":"string"},"invoiceDate":{"type":"string","format":"date"},"dueDate":{"type":"string","format":"date"},"vendorCode":{"type":"string"},"vendorName":{"type":"string"},"currency":{"type":"string","default":"JPY"},"subtotal":{"type":"number"},"taxTotal":{"type":"number"},"grandTotal":{"type":"number"},"memo":{"type":"string"},"status":{"type":"string","enum":["draft","posted","paid"],"default":"draft"},"voucherId":{"type":"string"},"lines":{"type":"array","items":{"type":"object","properties":{"lineNo":{"type":"integer"},"materialCode":{"type":"string"},"materialName":{"type":"string"},"quantity":{"type":"number"},"uom":{"type":"string"},"unitPrice":{"type":"number"},"amount":{"type":"number"},"taxRate":{"type":"number"},"taxAmount":{"type":"number"},"matchedReceiptId":{"type":"string"},"matchedPoNo":{"type":"string"}}}}},"required":["invoiceNo","invoiceDate","vendorCode"]}',
  '{"list":{"columns":["invoice_no","vendor_code","invoice_date","grand_total","status"]},"form":{"layout":[]}}',
  '{"filters":["invoice_no","vendor_code","invoice_date","status"],"sorts":["invoice_date"]}',
  NULL,
  NULL,
  NULL,
  '{"displayNames":{"ja":"請求書","zh":"供应商请求书","en":"Vendor Invoice"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- 添加供应商请求书权限
INSERT INTO permissions (code, label, module, perm_type, is_sensitive, description, sort_order)
VALUES 
('vendor_invoice:read', '{"ja":"請求書参照","zh":"查看供应商请求书","en":"View Vendor Invoices"}', 'inventory', 'action', false,
 '{"ja":"請求書情報を閲覧する権限","zh":"允许查看供应商请求书","en":"Permission to view vendor invoices"}', 10),
('vendor_invoice:manage', '{"ja":"請求書管理","zh":"供应商请求书管理","en":"Vendor Invoice Management"}', 'inventory', 'action', false,
 '{"ja":"請求書を管理する権限","zh":"允许管理供应商请求书","en":"Permission to manage vendor invoices"}', 11)
ON CONFLICT (code) DO NOTHING;

-- 添加供应商请求书菜单
INSERT INTO permission_menus (module, menu_key, label, route, required_caps, sort_order)
VALUES 
('inventory', 'inv.vi.list', '{"ja":"請求書一覧","zh":"请求书列表","en":"Vendor Invoices"}', NULL, ARRAY['vendor_invoice:read'], 16),
('inventory', 'inv.vi.new', '{"ja":"請求書登録","zh":"新建请求书","en":"New Vendor Invoice"}', NULL, ARRAY['vendor_invoice:manage'], 17)
ON CONFLICT (module, menu_key) DO NOTHING;


-- 添加供应商请求书权限（能力）
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES 
('vendor_invoice:read', '{"ja":"請求書参照","zh":"查看供应商请求书","en":"View Vendor Invoices"}', 'inventory', 'action', false,
 '{"ja":"請求書情報を閲覧する権限","zh":"允许查看供应商请求书","en":"Permission to view vendor invoices"}', 10),
('vendor_invoice:manage', '{"ja":"請求書管理","zh":"供应商请求书管理","en":"Vendor Invoice Management"}', 'inventory', 'action', false,
 '{"ja":"請求書を管理する権限","zh":"允许管理供应商请求书","en":"Permission to manage vendor invoices"}', 11)
ON CONFLICT (cap_code) DO NOTHING;

-- 添加供应商请求书菜单
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES 
('inventory', 'inv.vi.list', '{"ja":"請求書一覧","zh":"请求书列表","en":"Vendor Invoices"}', NULL, ARRAY['vendor_invoice:read'], 16),
('inventory', 'inv.vi.new', '{"ja":"請求書登録","zh":"新建请求书","en":"New Vendor Invoice"}', NULL, ARRAY['vendor_invoice:manage'], 17)
ON CONFLICT (menu_key) DO NOTHING;


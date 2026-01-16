-- 添加請求書作成菜单
-- caps_required设为空数组，表示不需要特殊权限即可访问（与请求书一览相同）
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES (
  'orders', 
  'crm.salesInvoiceCreate', 
  '{"ja":"請求書作成","zh":"请求书创建","en":"Create Invoice"}',
  NULL,
  ARRAY[]::TEXT[],
  45
)
ON CONFLICT (menu_key) DO NOTHING;


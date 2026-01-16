-- 添加合計残高試算表菜单项
-- 执行此脚本为已有系统添加试算表菜单

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) 
VALUES (
  'finance', 
  'trial.balance', 
  '{"ja":"合計残高試算表","zh":"合计残高试算表","en":"Trial Balance"}', 
  '/trial-balance', 
  ARRAY['voucher:read'], 
  6
)
ON CONFLICT (menu_key) DO UPDATE SET
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

SELECT 'trial.balance menu added successfully' as result;


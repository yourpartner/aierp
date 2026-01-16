-- ============================================
-- 添加新功能菜单项
-- 包括：合計残高試算表、月次締め
-- ============================================

-- 1. 添加合計残高試算表菜单
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

-- 2. 添加月次締め相关权限（如果不存在）
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES 
('monthly_closing:view', '{"ja":"月次締め参照","zh":"查看月结","en":"View Monthly Closing"}', 'finance', 'action', false,
 '{"ja":"月次締め状況を閲覧する権限","zh":"允许查看月结状态","en":"Permission to view monthly closing status"}', 50),
('monthly_closing:start', '{"ja":"月次締め開始","zh":"启动月结","en":"Start Monthly Closing"}', 'finance', 'action', false,
 '{"ja":"月次締めプロセスを開始する権限","zh":"允许启动月结流程","en":"Permission to start monthly closing process"}', 51),
('monthly_closing:check', '{"ja":"月次締めチェック","zh":"月结检查","en":"Monthly Closing Check"}', 'finance', 'action', false,
 '{"ja":"チェック項目を実行・確認する権限","zh":"允许执行和确认检查项目","en":"Permission to execute and confirm check items"}', 52),
('monthly_closing:adjustment', '{"ja":"月末調整仕訳","zh":"月末调整分录","en":"Month-end Adjustment"}', 'finance', 'action', false,
 '{"ja":"月末調整仕訳を作成する権限","zh":"允许创建月末调整分录","en":"Permission to create month-end adjustments"}', 53),
('monthly_closing:approve', '{"ja":"月次締め承認","zh":"月结审批","en":"Approve Monthly Closing"}', 'finance', 'action', true,
 '{"ja":"月次締めを承認する権限","zh":"允许审批月结","en":"Permission to approve monthly closing"}', 54),
('monthly_closing:close', '{"ja":"月次締め確定","zh":"月结确认","en":"Close Monthly Period"}', 'finance', 'action', true,
 '{"ja":"月次締めを確定する権限","zh":"允许确认月结","en":"Permission to close monthly period"}', 55),
('monthly_closing:reopen', '{"ja":"月次締め再開","zh":"月结重开","en":"Reopen Monthly Period"}', 'finance', 'action', true,
 '{"ja":"締め済み期間を再開する権限（特権）","zh":"允许重开已结月份（特权）","en":"Permission to reopen closed period (privileged)"}', 56)
ON CONFLICT (cap_code) DO NOTHING;

-- 3. 添加月次締め菜单
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) 
VALUES (
  'finance', 
  'fin.monthlyClosing', 
  '{"ja":"月次締め","zh":"月结","en":"Monthly Closing"}', 
  '/financial/monthly-closing', 
  ARRAY['monthly_closing:view'], 
  20
)
ON CONFLICT (menu_key) DO UPDATE SET
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 4. 添加帳簿出力菜单
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) 
VALUES (
  'finance', 
  'ledger.export', 
  '{"ja":"帳簿出力","zh":"帐簿导出","en":"Ledger Export"}', 
  '/ledger-export', 
  ARRAY['voucher:read'], 
  7
)
ON CONFLICT (menu_key) DO UPDATE SET
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 5. 验证结果
SELECT 'Added menus:' as info;
SELECT menu_key, menu_name->>'ja' as name_ja, menu_path 
FROM permission_menus 
WHERE menu_key IN ('trial.balance', 'fin.monthlyClosing', 'ledger.export')
ORDER BY display_order;

SELECT 'New feature menus added successfully' as result;


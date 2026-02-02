-- =====================================================
-- 添加人才派遣模块菜单和权限
-- =====================================================

-- 添加人才派遣模块定义
INSERT INTO permission_modules (module_code, module_name, icon, display_order) VALUES
('staffing', '{"ja":"人材派遣","zh":"人才派遣","en":"Staffing"}', 'Users', 8)
ON CONFLICT (module_code) DO UPDATE SET
  module_name = EXCLUDED.module_name,
  icon = EXCLUDED.icon,
  display_order = EXCLUDED.display_order;

-- 添加人才派遣权限
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order) VALUES
('staffing:resource:read', '{"ja":"リソース参照","zh":"查看资源","en":"View Resources"}', 'staffing', 'action', false,
 '{"ja":"リソースプールを閲覧する権限","zh":"允许查看资源池","en":"Permission to view resource pool"}', 1),
('staffing:resource:manage', '{"ja":"リソース管理","zh":"管理资源","en":"Manage Resources"}', 'staffing', 'action', false,
 '{"ja":"リソースを管理する権限","zh":"允许管理资源","en":"Permission to manage resources"}', 2),
('staffing:project:read', '{"ja":"案件参照","zh":"查看案件","en":"View Projects"}', 'staffing', 'action', false,
 '{"ja":"案件を閲覧する権限","zh":"允许查看案件","en":"Permission to view projects"}', 3),
('staffing:project:manage', '{"ja":"案件管理","zh":"管理案件","en":"Manage Projects"}', 'staffing', 'action', false,
 '{"ja":"案件を管理する権限","zh":"允许管理案件","en":"Permission to manage projects"}', 4),
('staffing:contract:read', '{"ja":"契約参照","zh":"查看合同","en":"View Contracts"}', 'staffing', 'action', false,
 '{"ja":"契約を閲覧する権限","zh":"允许查看合同","en":"Permission to view contracts"}', 5),
('staffing:contract:manage', '{"ja":"契約管理","zh":"管理合同","en":"Manage Contracts"}', 'staffing', 'action', false,
 '{"ja":"契約を管理する権限","zh":"允许管理合同","en":"Permission to manage contracts"}', 6),
('staffing:timesheet:read', '{"ja":"勤怠参照","zh":"查看勤怠","en":"View Timesheets"}', 'staffing', 'action', false,
 '{"ja":"勤怠を閲覧する権限","zh":"允许查看勤怠","en":"Permission to view timesheets"}', 7),
('staffing:timesheet:manage', '{"ja":"勤怠管理","zh":"管理勤怠","en":"Manage Timesheets"}', 'staffing', 'action', false,
 '{"ja":"勤怠を管理する権限","zh":"允许管理勤怠","en":"Permission to manage timesheets"}', 8),
('staffing:invoice:read', '{"ja":"請求参照","zh":"查看账单","en":"View Invoices"}', 'staffing', 'action', false,
 '{"ja":"請求を閲覧する権限","zh":"允许查看账单","en":"Permission to view invoices"}', 9),
('staffing:invoice:manage', '{"ja":"請求管理","zh":"管理账单","en":"Manage Invoices"}', 'staffing', 'action', false,
 '{"ja":"請求を管理する権限","zh":"允许管理账单","en":"Permission to manage invoices"}', 10),
('staffing:analytics', '{"ja":"分析レポート","zh":"分析报表","en":"Analytics"}', 'staffing', 'action', false,
 '{"ja":"分析レポートを閲覧する権限","zh":"允许查看分析报表","en":"Permission to view analytics"}', 11),
('staffing:email', '{"ja":"メール管理","zh":"邮件管理","en":"Email Management"}', 'staffing', 'action', false,
 '{"ja":"メールを管理する権限","zh":"允许管理邮件","en":"Permission to manage emails"}', 12),
('staffing:ai', '{"ja":"AI機能","zh":"AI功能","en":"AI Features"}', 'staffing', 'action', false,
 '{"ja":"AI機能を使用する権限","zh":"允许使用AI功能","en":"Permission to use AI features"}', 13)
ON CONFLICT (cap_code) DO UPDATE SET
  cap_name = EXCLUDED.cap_name,
  module_code = EXCLUDED.module_code,
  cap_type = EXCLUDED.cap_type,
  is_sensitive = EXCLUDED.is_sensitive,
  description = EXCLUDED.description,
  display_order = EXCLUDED.display_order;

-- 添加人才派遣菜单
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) VALUES
-- 资源管理
('staffing', 'staffing.resources', '{"ja":"リソースプール","zh":"资源池","en":"Resource Pool"}', '/staffing/resources', ARRAY['staffing:resource:read'], 1),
-- 案件管理
('staffing', 'staffing.projects', '{"ja":"案件一覧","zh":"案件列表","en":"Projects"}', '/staffing/projects', ARRAY['staffing:project:read'], 2),
-- 契約管理
('staffing', 'staffing.contracts', '{"ja":"契約一覧","zh":"合同列表","en":"Contracts"}', '/staffing/contracts', ARRAY['staffing:contract:read'], 3),
-- 勤怠連携
('staffing', 'staffing.timesheets', '{"ja":"勤怠サマリ","zh":"勤怠汇总","en":"Timesheet Summary"}', '/staffing/timesheets', ARRAY['staffing:timesheet:read'], 4),
-- 請求管理
('staffing', 'staffing.invoices', '{"ja":"請求一覧","zh":"账单列表","en":"Invoices"}', '/staffing/invoices', ARRAY['staffing:invoice:read'], 5),
-- 分析レポート
('staffing', 'staffing.analytics', '{"ja":"分析ダッシュボード","zh":"分析仪表盘","en":"Analytics Dashboard"}', '/staffing/analytics', ARRAY['staffing:analytics'], 6),
-- メール自動化
('staffing', 'staffing.email.inbox', '{"ja":"受信トレイ","zh":"收件箱","en":"Inbox"}', '/staffing/email/inbox', ARRAY['staffing:email'], 7),
('staffing', 'staffing.email.templates', '{"ja":"テンプレート","zh":"邮件模板","en":"Templates"}', '/staffing/email/templates', ARRAY['staffing:email'], 8),
('staffing', 'staffing.email.rules', '{"ja":"自動化ルール","zh":"自动化规则","en":"Rules"}', '/staffing/email/rules', ARRAY['staffing:email'], 9),
-- AI機能
('staffing', 'staffing.ai.matching', '{"ja":"AIマッチング","zh":"AI匹配","en":"AI Matching"}', '/staffing/ai/matching', ARRAY['staffing:ai'], 10),
('staffing', 'staffing.ai.market', '{"ja":"市場分析","zh":"市场分析","en":"Market Analysis"}', '/staffing/ai/market', ARRAY['staffing:ai'], 11),
('staffing', 'staffing.ai.alerts', '{"ja":"AIアラート","zh":"AI预警","en":"AI Alerts"}', '/staffing/ai/alerts', ARRAY['staffing:ai'], 12)
ON CONFLICT (menu_key) DO UPDATE SET
  module_code = EXCLUDED.module_code,
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 为现有管理员角色添加人才派遣权限
INSERT INTO role_caps (role_id, cap)
SELECT r.id, c.cap_code
FROM roles r
CROSS JOIN permission_caps c
WHERE r.role_code IN ('admin', 'SYS_ADMIN', 'ADMIN')
  AND c.module_code = 'staffing'
ON CONFLICT DO NOTHING;

-- 为JP01公司的admin角色添加权限
INSERT INTO role_caps (role_id, cap)
SELECT r.id, c.cap_code
FROM roles r
CROSS JOIN permission_caps c
WHERE r.company_code = 'JP01'
  AND r.role_code IN ('admin', 'ADMIN')
  AND c.module_code = 'staffing'
ON CONFLICT DO NOTHING;

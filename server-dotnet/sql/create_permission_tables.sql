-- =====================================================
-- 权限管理系统表结构
-- Permission Management System Tables
-- =====================================================

-- 1. 扩展 users 表，支持非员工用户（如税理士）
DO $$ 
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='user_type') THEN
    ALTER TABLE users ADD COLUMN user_type TEXT DEFAULT 'internal';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='employee_id') THEN
    ALTER TABLE users ADD COLUMN employee_id UUID;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='email') THEN
    ALTER TABLE users ADD COLUMN email TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='phone') THEN
    ALTER TABLE users ADD COLUMN phone TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='is_active') THEN
    ALTER TABLE users ADD COLUMN is_active BOOLEAN DEFAULT true;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='last_login_at') THEN
    ALTER TABLE users ADD COLUMN last_login_at TIMESTAMPTZ;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='external_type') THEN
    ALTER TABLE users ADD COLUMN external_type TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='updated_at') THEN
    ALTER TABLE users ADD COLUMN updated_at TIMESTAMPTZ DEFAULT now();
  END IF;
END $$;

-- 为 employee_id 添加索引
CREATE INDEX IF NOT EXISTS idx_users_employee_id ON users(employee_id) WHERE employee_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_users_user_type ON users(company_code, user_type);
CREATE INDEX IF NOT EXISTS idx_users_is_active ON users(company_code, is_active);

-- 2. 角色表扩展，增加角色描述和角色类型
DO $$ 
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='roles' AND column_name='description') THEN
    ALTER TABLE roles ADD COLUMN description TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='roles' AND column_name='role_type') THEN
    ALTER TABLE roles ADD COLUMN role_type TEXT DEFAULT 'custom';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='roles' AND column_name='is_active') THEN
    ALTER TABLE roles ADD COLUMN is_active BOOLEAN DEFAULT true;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='roles' AND column_name='source_prompt') THEN
    ALTER TABLE roles ADD COLUMN source_prompt TEXT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='roles' AND column_name='updated_at') THEN
    ALTER TABLE roles ADD COLUMN updated_at TIMESTAMPTZ DEFAULT now();
  END IF;
END $$;

-- 修改 company_code 允许 NULL（用于系统角色模板）
ALTER TABLE roles ALTER COLUMN company_code DROP NOT NULL;

CREATE INDEX IF NOT EXISTS idx_roles_is_active ON roles(company_code, is_active);
CREATE INDEX IF NOT EXISTS idx_roles_role_type ON roles(company_code, role_type);

-- 为支持NULL company_code的角色（系统角色），创建唯一索引
DROP INDEX IF EXISTS uq_roles_system;
CREATE UNIQUE INDEX uq_roles_system ON roles(role_code) WHERE company_code IS NULL;

-- 3. 功能模块定义表（系统级）
CREATE TABLE IF NOT EXISTS permission_modules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  module_code TEXT NOT NULL UNIQUE,
  module_name JSONB NOT NULL,
  icon TEXT,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 4. 功能菜单/页面定义表
CREATE TABLE IF NOT EXISTS permission_menus (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  module_code TEXT NOT NULL,
  menu_key TEXT NOT NULL UNIQUE,
  menu_name JSONB NOT NULL,
  menu_path TEXT,
  caps_required TEXT[],
  caps_all_required TEXT[],
  parent_menu_key TEXT,
  description JSONB,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_permission_menus_module ON permission_menus(module_code);
CREATE INDEX IF NOT EXISTS idx_permission_menus_parent ON permission_menus(parent_menu_key);

-- 5. 能力（Capability）定义表
CREATE TABLE IF NOT EXISTS permission_caps (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  cap_code TEXT NOT NULL UNIQUE,
  cap_name JSONB NOT NULL,
  module_code TEXT NOT NULL,
  cap_type TEXT DEFAULT 'action',
  is_sensitive BOOLEAN DEFAULT false,
  description JSONB,
  display_order INT DEFAULT 0,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_permission_caps_module ON permission_caps(module_code);
CREATE INDEX IF NOT EXISTS idx_permission_caps_type ON permission_caps(cap_type);

-- 6. 数据范围权限表（控制用户能看到哪些数据）
CREATE TABLE IF NOT EXISTS role_data_scopes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  role_id UUID NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  entity_type TEXT NOT NULL,
  scope_type TEXT NOT NULL,
  scope_filter JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(role_id, entity_type)
);

CREATE INDEX IF NOT EXISTS idx_role_data_scopes_role ON role_data_scopes(role_id);
CREATE INDEX IF NOT EXISTS idx_role_data_scopes_entity ON role_data_scopes(entity_type);

-- 7. AI角色生成日志（追踪AI参与的角色设置）
CREATE TABLE IF NOT EXISTS ai_role_generations (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  role_id UUID REFERENCES roles(id) ON DELETE SET NULL,
  user_prompt TEXT NOT NULL,
  ai_response JSONB,
  status TEXT DEFAULT 'pending',
  applied_at TIMESTAMPTZ,
  applied_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_ai_role_generations_company ON ai_role_generations(company_code);
CREATE INDEX IF NOT EXISTS idx_ai_role_generations_role ON ai_role_generations(role_id);
CREATE INDEX IF NOT EXISTS idx_ai_role_generations_status ON ai_role_generations(status);

-- =====================================================
-- 初始化系统数据
-- =====================================================

-- 模块定义
INSERT INTO permission_modules (module_code, module_name, icon, display_order) VALUES
('finance', '{"ja":"財務会計","zh":"财务会计","en":"Finance & Accounting"}', 'Wallet', 1),
('hr', '{"ja":"人事管理","zh":"人事管理","en":"HR Management"}', 'User', 2),
('inventory', '{"ja":"在庫購買","zh":"库存采购","en":"Inventory & Purchasing"}', 'Box', 3),
('fixed_asset', '{"ja":"固定資産","zh":"固定资产","en":"Fixed Assets"}', 'Building', 4),
('sales', '{"ja":"販売管理","zh":"销售管理","en":"Sales Management"}', 'ShoppingCart', 5),
('crm', '{"ja":"CRM","zh":"CRM","en":"CRM"}', 'Users', 6),
('system', '{"ja":"システム設定","zh":"系统设置","en":"System Settings"}', 'Settings', 7)
ON CONFLICT (module_code) DO UPDATE SET
  module_name = EXCLUDED.module_name,
  icon = EXCLUDED.icon,
  display_order = EXCLUDED.display_order;

-- 能力定义（按模块）
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order) VALUES
-- 财务模块
('voucher:create', '{"ja":"仕訳作成","zh":"创建凭证","en":"Create Voucher"}', 'finance', 'action', false, 
 '{"ja":"会計仕訳を作成する権限","zh":"允许创建会计凭证/分录","en":"Permission to create accounting vouchers"}', 1),
('voucher:read', '{"ja":"仕訳参照","zh":"查看凭证","en":"View Voucher"}', 'finance', 'action', false,
 '{"ja":"仕訳を閲覧する権限","zh":"允许查看会计凭证","en":"Permission to view vouchers"}', 2),
('voucher:edit', '{"ja":"仕訳編集","zh":"编辑凭证","en":"Edit Voucher"}', 'finance', 'action', false,
 '{"ja":"仕訳を編集する権限","zh":"允许编辑会计凭证","en":"Permission to edit vouchers"}', 3),
('voucher:delete', '{"ja":"仕訳削除","zh":"删除凭证","en":"Delete Voucher"}', 'finance', 'action', true,
 '{"ja":"仕訳を削除する権限（敏感）","zh":"允许删除会计凭证（敏感操作）","en":"Permission to delete vouchers (sensitive)"}', 4),
('voucher:post', '{"ja":"仕訳転記","zh":"过账凭证","en":"Post Voucher"}', 'finance', 'action', true,
 '{"ja":"仕訳を転記する権限","zh":"允许过账凭证","en":"Permission to post vouchers"}', 5),
('account:read', '{"ja":"勘定科目参照","zh":"查看科目","en":"View Accounts"}', 'finance', 'action', false,
 '{"ja":"勘定科目を閲覧する権限","zh":"允许查看会计科目","en":"Permission to view chart of accounts"}', 6),
('account:manage', '{"ja":"勘定科目管理","zh":"科目管理","en":"Manage Accounts"}', 'finance', 'action', false,
 '{"ja":"勘定科目を管理する権限","zh":"允许管理会计科目","en":"Permission to manage chart of accounts"}', 7),
('report:financial', '{"ja":"財務レポート","zh":"财务报表","en":"Financial Reports"}', 'finance', 'action', false,
 '{"ja":"財務レポートを閲覧する権限","zh":"允许查看财务报表","en":"Permission to view financial reports"}', 8),
('op:bank-payment', '{"ja":"銀行支払","zh":"银行付款","en":"Bank Payment"}', 'finance', 'action', true,
 '{"ja":"銀行支払操作の権限（敏感）","zh":"允许执行银行付款操作（敏感）","en":"Permission for bank payment operations (sensitive)"}', 9),
('op:bank-collect', '{"ja":"銀行入金","zh":"银行收款","en":"Bank Collection"}', 'finance', 'action', true,
 '{"ja":"銀行入金操作の権限（敏感）","zh":"允许执行银行收款操作（敏感）","en":"Permission for bank collection operations (sensitive)"}', 10),
('period:manage', '{"ja":"会計期間管理","zh":"会计期间管理","en":"Accounting Period Management"}', 'finance', 'action', true,
 '{"ja":"会計期間を管理する権限","zh":"允许管理会计期间（开关账）","en":"Permission to manage accounting periods"}', 11),
('bp:read', '{"ja":"取引先参照","zh":"查看业务伙伴","en":"View Business Partners"}', 'finance', 'action', false,
 '{"ja":"取引先情報を閲覧する権限","zh":"允许查看业务伙伴信息","en":"Permission to view business partners"}', 12),
('bp:manage', '{"ja":"取引先管理","zh":"业务伙伴管理","en":"Manage Business Partners"}', 'finance', 'action', false,
 '{"ja":"取引先を管理する権限","zh":"允许管理业务伙伴","en":"Permission to manage business partners"}', 13),

-- HR模块
('employee:create', '{"ja":"従業員登録","zh":"创建员工","en":"Create Employee"}', 'hr', 'action', false,
 '{"ja":"従業員を登録する権限","zh":"允许创建员工记录","en":"Permission to create employee records"}', 1),
('employee:read', '{"ja":"従業員参照","zh":"查看员工","en":"View Employee"}', 'hr', 'action', false,
 '{"ja":"従業員情報を閲覧する権限","zh":"允许查看员工基本信息","en":"Permission to view employee basic info"}', 2),
('employee:edit', '{"ja":"従業員編集","zh":"编辑员工","en":"Edit Employee"}', 'hr', 'action', false,
 '{"ja":"従業員情報を編集する権限","zh":"允许编辑员工信息","en":"Permission to edit employee info"}', 3),
('employee:delete', '{"ja":"従業員削除","zh":"删除员工","en":"Delete Employee"}', 'hr', 'action', true,
 '{"ja":"従業員を削除する権限（敏感）","zh":"允许删除员工记录（敏感）","en":"Permission to delete employees (sensitive)"}', 4),
('employee:salary', '{"ja":"給与情報参照","zh":"查看薪资信息","en":"View Salary Info"}', 'hr', 'action', true,
 '{"ja":"給与・社保等の機密情報を閲覧（敏感）","zh":"查看员工的薪资、社保等敏感信息","en":"View salary and insurance info (sensitive)"}', 5),
('department:read', '{"ja":"部門参照","zh":"查看部门","en":"View Departments"}', 'hr', 'action', false,
 '{"ja":"部門情報を閲覧する権限","zh":"允许查看部门信息","en":"Permission to view department info"}', 6),
('department:manage', '{"ja":"部門管理","zh":"部门管理","en":"Department Management"}', 'hr', 'action', false,
 '{"ja":"部門を管理する権限","zh":"允许管理部门","en":"Permission to manage departments"}', 7),
('payroll:execute', '{"ja":"給与計算実行","zh":"执行薪资计算","en":"Execute Payroll"}', 'hr', 'action', true,
 '{"ja":"給与計算を実行する権限（敏感）","zh":"允许执行薪资计算（敏感）","en":"Permission to execute payroll (sensitive)"}', 8),
('payroll:view', '{"ja":"給与計算参照","zh":"查看薪资计算","en":"View Payroll"}', 'hr', 'action', true,
 '{"ja":"給与計算結果を閲覧する権限","zh":"允许查看薪资计算结果","en":"Permission to view payroll results"}', 9),
('timesheet:read', '{"ja":"勤怠参照","zh":"查看考勤","en":"View Timesheet"}', 'hr', 'action', false,
 '{"ja":"勤怠情報を閲覧する権限","zh":"允许查看考勤记录","en":"Permission to view timesheets"}', 10),
('timesheet:manage', '{"ja":"勤怠管理","zh":"考勤管理","en":"Timesheet Management"}', 'hr', 'action', false,
 '{"ja":"勤怠を管理する権限","zh":"允许管理考勤记录","en":"Permission to manage timesheets"}', 11),
('approval:submit', '{"ja":"申請提出","zh":"提交申请","en":"Submit Requests"}', 'hr', 'action', false,
 '{"ja":"各種申請を提出する権限","zh":"允许提交各类申请","en":"Permission to submit requests"}', 12),
('approval:approve', '{"ja":"承認","zh":"审批","en":"Approve Requests"}', 'hr', 'action', false,
 '{"ja":"申請を承認する権限","zh":"允许审批申请","en":"Permission to approve requests"}', 13),
('approval:manage', '{"ja":"承認フロー管理","zh":"审批流程管理","en":"Approval Flow Management"}', 'hr', 'action', false,
 '{"ja":"承認フローを管理する権限","zh":"允许管理审批流程","en":"Permission to manage approval flows"}', 14),
('cert:request', '{"ja":"証明書申請","zh":"证明申请","en":"Certificate Request"}', 'hr', 'action', false,
 '{"ja":"各種証明書を申請する権限","zh":"允许申请各类证明","en":"Permission to request certificates"}', 15),
('cert:issue', '{"ja":"証明書発行","zh":"证明发放","en":"Issue Certificates"}', 'hr', 'action', false,
 '{"ja":"証明書を発行する権限","zh":"允许发放证明","en":"Permission to issue certificates"}', 16),

-- 库存模块
('material:read', '{"ja":"品目参照","zh":"查看物料","en":"View Materials"}', 'inventory', 'action', false,
 '{"ja":"品目情報を閲覧する権限","zh":"允许查看物料信息","en":"Permission to view materials"}', 1),
('material:manage', '{"ja":"品目管理","zh":"物料管理","en":"Material Management"}', 'inventory', 'action', false,
 '{"ja":"品目を管理する権限","zh":"允许管理物料","en":"Permission to manage materials"}', 2),
('warehouse:read', '{"ja":"倉庫参照","zh":"查看仓库","en":"View Warehouses"}', 'inventory', 'action', false,
 '{"ja":"倉庫情報を閲覧する権限","zh":"允许查看仓库信息","en":"Permission to view warehouses"}', 3),
('warehouse:manage', '{"ja":"倉庫管理","zh":"仓库管理","en":"Warehouse Management"}', 'inventory', 'action', false,
 '{"ja":"倉庫を管理する権限","zh":"允许管理仓库","en":"Permission to manage warehouses"}', 4),
('stock:read', '{"ja":"在庫参照","zh":"查看库存","en":"View Stock"}', 'inventory', 'action', false,
 '{"ja":"在庫情報を閲覧する権限","zh":"允许查看库存信息","en":"Permission to view stock"}', 5),
('stock:movement', '{"ja":"在庫移動","zh":"库存移动","en":"Stock Movement"}', 'inventory', 'action', false,
 '{"ja":"在庫移動を実行する権限","zh":"允许执行库存移动","en":"Permission for stock movements"}', 6),
('stock:adjust', '{"ja":"在庫調整","zh":"库存调整","en":"Stock Adjustment"}', 'inventory', 'action', true,
 '{"ja":"在庫数量を調整する権限（敏感）","zh":"允许调整库存数量（敏感）","en":"Permission for stock adjustments (sensitive)"}', 7),
('purchase:read', '{"ja":"発注参照","zh":"查看采购订单","en":"View Purchase Orders"}', 'inventory', 'action', false,
 '{"ja":"発注情報を閲覧する権限","zh":"允许查看采购订单","en":"Permission to view purchase orders"}', 8),
('purchase:manage', '{"ja":"発注管理","zh":"采购订单管理","en":"Purchase Order Management"}', 'inventory', 'action', false,
 '{"ja":"発注を管理する権限","zh":"允许管理采购订单","en":"Permission to manage purchase orders"}', 9),

-- 固定资产模块
('fa:read', '{"ja":"固定資産参照","zh":"查看固定资产","en":"View Fixed Assets"}', 'fixed_asset', 'action', false,
 '{"ja":"固定資産を閲覧する権限","zh":"允许查看固定资产","en":"Permission to view fixed assets"}', 1),
('fa:manage', '{"ja":"固定資産管理","zh":"固定资产管理","en":"Manage Fixed Assets"}', 'fixed_asset', 'action', false,
 '{"ja":"固定資産を管理する権限","zh":"允许管理固定资产","en":"Permission to manage fixed assets"}', 2),
('fa:depreciate', '{"ja":"減価償却実行","zh":"执行折旧","en":"Execute Depreciation"}', 'fixed_asset', 'action', true,
 '{"ja":"減価償却を実行する権限","zh":"允许执行折旧计算","en":"Permission to execute depreciation"}', 3),

-- 销售模块
('order:read', '{"ja":"受注参照","zh":"查看订单","en":"View Orders"}', 'sales', 'action', false,
 '{"ja":"受注を閲覧する権限","zh":"允许查看销售订单","en":"Permission to view sales orders"}', 1),
('order:create', '{"ja":"受注作成","zh":"创建订单","en":"Create Orders"}', 'sales', 'action', false,
 '{"ja":"受注を作成する権限","zh":"允许创建销售订单","en":"Permission to create orders"}', 2),
('order:edit', '{"ja":"受注編集","zh":"编辑订单","en":"Edit Orders"}', 'sales', 'action', false,
 '{"ja":"受注を編集する権限","zh":"允许编辑销售订单","en":"Permission to edit orders"}', 3),
('order:delete', '{"ja":"受注削除","zh":"删除订单","en":"Delete Orders"}', 'sales', 'action', true,
 '{"ja":"受注を削除する権限（敏感）","zh":"允许删除销售订单（敏感）","en":"Permission to delete orders (sensitive)"}', 4),
('delivery:manage', '{"ja":"出荷管理","zh":"发货管理","en":"Delivery Management"}', 'sales', 'action', false,
 '{"ja":"出荷を管理する権限","zh":"允许管理发货","en":"Permission to manage deliveries"}', 5),
('invoice:manage', '{"ja":"請求書管理","zh":"发票管理","en":"Invoice Management"}', 'sales', 'action', false,
 '{"ja":"請求書を管理する権限","zh":"允许管理销售发票","en":"Permission to manage invoices"}', 6),
('sales:analytics', '{"ja":"販売分析","zh":"销售分析","en":"Sales Analytics"}', 'sales', 'action', false,
 '{"ja":"販売分析を閲覧する権限","zh":"允许查看销售分析","en":"Permission to view sales analytics"}', 7),

-- CRM模块
('contact:read', '{"ja":"連絡先参照","zh":"查看联系人","en":"View Contacts"}', 'crm', 'action', false,
 '{"ja":"連絡先を閲覧する権限","zh":"允许查看联系人","en":"Permission to view contacts"}', 1),
('contact:manage', '{"ja":"連絡先管理","zh":"联系人管理","en":"Contact Management"}', 'crm', 'action', false,
 '{"ja":"連絡先を管理する権限","zh":"允许管理联系人","en":"Permission to manage contacts"}', 2),
('deal:read', '{"ja":"商談参照","zh":"查看商机","en":"View Deals"}', 'crm', 'action', false,
 '{"ja":"商談を閲覧する権限","zh":"允许查看商机","en":"Permission to view deals"}', 3),
('deal:manage', '{"ja":"商談管理","zh":"商机管理","en":"Deal Management"}', 'crm', 'action', false,
 '{"ja":"商談を管理する権限","zh":"允许管理商机","en":"Permission to manage deals"}', 4),
('quote:read', '{"ja":"見積参照","zh":"查看报价","en":"View Quotes"}', 'crm', 'action', false,
 '{"ja":"見積を閲覧する権限","zh":"允许查看报价","en":"Permission to view quotes"}', 5),
('quote:manage', '{"ja":"見積管理","zh":"报价管理","en":"Quote Management"}', 'crm', 'action', false,
 '{"ja":"見積を管理する権限","zh":"允许管理报价","en":"Permission to manage quotes"}', 6),
('activity:read', '{"ja":"活動参照","zh":"查看活动","en":"View Activities"}', 'crm', 'action', false,
 '{"ja":"活動を閲覧する権限","zh":"允许查看活动记录","en":"Permission to view activities"}', 7),
('activity:manage', '{"ja":"活動管理","zh":"活动管理","en":"Activity Management"}', 'crm', 'action', false,
 '{"ja":"活動を管理する権限","zh":"允许管理活动记录","en":"Permission to manage activities"}', 8),

-- 系统模块
('roles:manage', '{"ja":"ロール管理","zh":"角色管理","en":"Role Management"}', 'system', 'action', true,
 '{"ja":"ロールを管理する権限（敏感）","zh":"允许管理角色权限（敏感）","en":"Permission to manage roles (sensitive)"}', 1),
('user:read', '{"ja":"ユーザー参照","zh":"查看用户","en":"View Users"}', 'system', 'action', false,
 '{"ja":"ユーザーを閲覧する権限","zh":"允许查看用户列表","en":"Permission to view users"}', 2),
('user:manage', '{"ja":"ユーザー管理","zh":"用户管理","en":"User Management"}', 'system', 'action', true,
 '{"ja":"ユーザーを管理する権限（敏感）","zh":"允许管理用户（敏感）","en":"Permission to manage users (sensitive)"}', 3),
('schema:read', '{"ja":"スキーマ参照","zh":"查看Schema","en":"View Schemas"}', 'system', 'action', false,
 '{"ja":"スキーマを閲覧する権限","zh":"允许查看Schema定义","en":"Permission to view schemas"}', 4),
('schema:edit', '{"ja":"スキーマ編集","zh":"Schema编辑","en":"Schema Editor"}', 'system', 'action', true,
 '{"ja":"スキーマを編集する権限（敏感）","zh":"允许编辑Schema定义（敏感）","en":"Permission to edit schemas (sensitive)"}', 5),
('workflow:read', '{"ja":"ワークフロー参照","zh":"查看工作流","en":"View Workflows"}', 'system', 'action', false,
 '{"ja":"ワークフローを閲覧する権限","zh":"允许查看工作流规则","en":"Permission to view workflows"}', 6),
('workflow:manage', '{"ja":"ワークフロー管理","zh":"工作流管理","en":"Workflow Management"}', 'system', 'action', true,
 '{"ja":"ワークフローを管理する権限","zh":"允许管理工作流规则","en":"Permission to manage workflows"}', 7),
('company:settings', '{"ja":"会社設定","zh":"公司设置","en":"Company Settings"}', 'system', 'action', true,
 '{"ja":"会社設定を変更する権限","zh":"允许修改公司设置","en":"Permission to modify company settings"}', 8),
('ai:scenarios', '{"ja":"AIシナリオ管理","zh":"AI场景管理","en":"AI Scenario Management"}', 'system', 'action', false,
 '{"ja":"AIシナリオを管理する権限","zh":"允许管理AI场景配置","en":"Permission to manage AI scenarios"}', 9),
('scheduler:manage', '{"ja":"スケジューラー管理","zh":"计划任务管理","en":"Scheduler Management"}', 'system', 'action', true,
 '{"ja":"スケジュールタスクを管理する権限","zh":"允许管理计划任务","en":"Permission to manage scheduled tasks"}', 10)
ON CONFLICT (cap_code) DO UPDATE SET
  cap_name = EXCLUDED.cap_name,
  module_code = EXCLUDED.module_code,
  cap_type = EXCLUDED.cap_type,
  is_sensitive = EXCLUDED.is_sensitive,
  description = EXCLUDED.description,
  display_order = EXCLUDED.display_order;

-- 菜单定义
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order) VALUES
-- 财务菜单
('finance', 'voucher.new', '{"ja":"仕訳作成","zh":"新建凭证","en":"New Voucher"}', '/voucher/new', ARRAY['voucher:create'], 1),
('finance', 'vouchers.list', '{"ja":"仕訳一覧","zh":"凭证列表","en":"Vouchers"}', '/vouchers', ARRAY['voucher:read'], 2),
('finance', 'accounts.list', '{"ja":"勘定科目","zh":"科目列表","en":"Accounts"}', '/accounts', ARRAY['account:read'], 3),
('finance', 'account.ledger', '{"ja":"元帳","zh":"科目明细账","en":"Account Ledger"}', '/account-ledger', ARRAY['voucher:read'], 4),
('finance', 'account.balance', '{"ja":"残高試算表","zh":"科目余额表","en":"Account Balance"}', '/account-balance', ARRAY['voucher:read'], 5),
('finance', 'trial.balance', '{"ja":"合計残高試算表","zh":"合计残高试算表","en":"Trial Balance"}', '/trial-balance', ARRAY['voucher:read'], 6),
('finance', 'ledger.export', '{"ja":"帳簿出力","zh":"帐簿导出","en":"Ledger Export"}', '/ledger-export', ARRAY['voucher:read'], 7),
('finance', 'op.bankPayment', '{"ja":"銀行支払","zh":"银行付款","en":"Bank Payment"}', '/operations/bank-payment', ARRAY['op:bank-payment'], 8),
('finance', 'op.fbPayment', '{"ja":"FB支払","zh":"FB付款","en":"FB Payment"}', '/fb-payment', ARRAY['op:bank-payment'], 9),
('finance', 'fin.reports', '{"ja":"財務レポート","zh":"财务报表","en":"Financial Reports"}', '/financial/statements', ARRAY['report:financial'], 10),
('finance', 'fin.designer', '{"ja":"レポート設計","zh":"报表设计器","en":"Report Designer"}', '/financial/nodes', ARRAY['report:financial'], 11),
('finance', 'rcpt.planner', '{"ja":"入金計画","zh":"收款计划","en":"Receipt Planner"}', NULL, ARRAY['op:bank-collect'], 12),
('finance', 'moneytree.transactions', '{"ja":"Moneytree取引","zh":"Moneytree交易","en":"Moneytree Transactions"}', '/moneytree/transactions', ARRAY['voucher:read'], 13),
('finance', 'acct.periods', '{"ja":"会計期間","zh":"会计期间","en":"Accounting Periods"}', NULL, ARRAY['period:manage'], 14),
('finance', 'bp.list', '{"ja":"取引先一覧","zh":"业务伙伴列表","en":"Business Partners"}', '/businesspartners', ARRAY['bp:read'], 15),
('finance', 'bp.new', '{"ja":"取引先登録","zh":"新建业务伙伴","en":"New Partner"}', '/businesspartner/new', ARRAY['bp:manage'], 16),

-- HR菜单
('hr', 'hr.dept', '{"ja":"部門管理","zh":"部门管理","en":"Departments"}', '/hr/departments', ARRAY['department:read'], 1),
('hr', 'hr.emps', '{"ja":"従業員一覧","zh":"员工列表","en":"Employees"}', '/hr/employees', ARRAY['employee:read'], 2),
('hr', 'hr.emp.new', '{"ja":"従業員登録","zh":"新建员工","en":"New Employee"}', '/hr/employee/new', ARRAY['employee:create'], 3),
('hr', 'hr.policy.editor', '{"ja":"給与ポリシー","zh":"薪资策略","en":"Payroll Policy"}', '/hr/policy/editor', ARRAY['payroll:view'], 4),
('hr', 'payroll.execute', '{"ja":"給与計算","zh":"执行薪资计算","en":"Execute Payroll"}', NULL, ARRAY['payroll:execute'], 5),
('hr', 'payroll.history', '{"ja":"給与履歴","zh":"薪资历史","en":"Payroll History"}', NULL, ARRAY['payroll:view'], 6),
('hr', 'timesheets.list', '{"ja":"勤怠一覧","zh":"考勤列表","en":"Timesheets"}', '/timesheets', ARRAY['timesheet:read'], 7),
('hr', 'timesheet.new', '{"ja":"勤怠入力","zh":"新建考勤","en":"New Timesheet"}', '/timesheet/new', ARRAY['timesheet:manage'], 8),
('hr', 'cert.request', '{"ja":"証明書申請","zh":"证明申请","en":"Certificate Request"}', '/cert/request', ARRAY['cert:request'], 9),
('hr', 'cert.list', '{"ja":"証明書一覧","zh":"证明列表","en":"Certificates"}', NULL, ARRAY['cert:request'], 10),
('hr', 'approvals.inbox', '{"ja":"承認受信箱","zh":"审批收件箱","en":"Approvals Inbox"}', '/approvals/inbox', ARRAY['approval:approve','approval:submit'], 11),

-- 库存菜单
('inventory', 'inv.materials', '{"ja":"品目一覧","zh":"物料列表","en":"Materials"}', NULL, ARRAY['material:read'], 1),
('inventory', 'inv.material.new', '{"ja":"品目登録","zh":"新建物料","en":"New Material"}', NULL, ARRAY['material:manage'], 2),
('inventory', 'inv.warehouses', '{"ja":"倉庫一覧","zh":"仓库列表","en":"Warehouses"}', NULL, ARRAY['warehouse:read'], 3),
('inventory', 'inv.warehouse.new', '{"ja":"倉庫登録","zh":"新建仓库","en":"New Warehouse"}', NULL, ARRAY['warehouse:manage'], 4),
('inventory', 'inv.bins', '{"ja":"ロケーション","zh":"库位列表","en":"Bins"}', NULL, ARRAY['warehouse:read'], 5),
('inventory', 'inv.bin.new', '{"ja":"ロケーション登録","zh":"新建库位","en":"New Bin"}', NULL, ARRAY['warehouse:manage'], 6),
('inventory', 'inv.stockstatus', '{"ja":"在庫ステータス","zh":"库存状态","en":"Stock Status"}', NULL, ARRAY['stock:read'], 7),
('inventory', 'inv.batches', '{"ja":"ロット一覧","zh":"批次列表","en":"Batches"}', NULL, ARRAY['stock:read'], 8),
('inventory', 'inv.batch.new', '{"ja":"ロット登録","zh":"新建批次","en":"New Batch"}', NULL, ARRAY['stock:movement'], 9),
('inventory', 'inv.movement', '{"ja":"在庫移動","zh":"库存移动","en":"Stock Movement"}', NULL, ARRAY['stock:movement'], 10),
('inventory', 'inv.balances', '{"ja":"在庫残高","zh":"库存余额","en":"Stock Balances"}', NULL, ARRAY['stock:read'], 11),
('inventory', 'inv.ledger', '{"ja":"在庫台帳","zh":"库存台账","en":"Stock Ledger"}', NULL, ARRAY['stock:read'], 12),
('inventory', 'inv.counts', '{"ja":"棚卸","zh":"盘点","en":"Stock Counts"}', NULL, ARRAY['stock:adjust'], 13),
('inventory', 'inv.count.report', '{"ja":"棚卸レポート","zh":"盘点报告","en":"Count Report"}', NULL, ARRAY['stock:read'], 14),
('inventory', 'inv.po.list', '{"ja":"発注一覧","zh":"采购订单列表","en":"Purchase Orders"}', NULL, ARRAY['purchase:read'], 15),
('inventory', 'inv.po.new', '{"ja":"発注登録","zh":"新建采购订单","en":"New Purchase Order"}', NULL, ARRAY['purchase:manage'], 16),

-- 固定资产菜单
('fixed_asset', 'fa.classes', '{"ja":"資産クラス","zh":"资产类别","en":"Asset Classes"}', '/fixed-assets/classes', ARRAY['fa:read'], 1),
('fixed_asset', 'fa.list', '{"ja":"固定資産一覧","zh":"固定资产列表","en":"Fixed Assets"}', '/fixed-assets/list', ARRAY['fa:read'], 2),
('fixed_asset', 'fa.depreciation', '{"ja":"減価償却","zh":"折旧计算","en":"Depreciation"}', '/fixed-assets/depreciation', ARRAY['fa:depreciate'], 3),

-- 销售菜单
('sales', 'crm.salesOrders', '{"ja":"受注一覧","zh":"销售订单","en":"Sales Orders"}', NULL, ARRAY['order:read'], 1),
('sales', 'crm.orderEntry', '{"ja":"受注入力","zh":"订单录入","en":"Order Entry"}', NULL, ARRAY['order:create'], 2),
('sales', 'crm.deliveryNotes', '{"ja":"出荷伝票","zh":"发货单","en":"Delivery Notes"}', NULL, ARRAY['delivery:manage'], 3),
('sales', 'crm.salesInvoices', '{"ja":"請求書","zh":"销售发票","en":"Sales Invoices"}', NULL, ARRAY['invoice:manage'], 4),
('sales', 'crm.salesAnalytics', '{"ja":"販売分析","zh":"销售分析","en":"Sales Analytics"}', NULL, ARRAY['sales:analytics'], 5),
('sales', 'crm.salesAlerts', '{"ja":"販売アラート","zh":"销售预警","en":"Sales Alerts"}', '/sales-alerts', ARRAY['sales:analytics'], 6),

-- CRM菜单
('crm', 'crm.contacts', '{"ja":"連絡先","zh":"联系人","en":"Contacts"}', NULL, ARRAY['contact:read'], 1),
('crm', 'crm.deals', '{"ja":"商談","zh":"商机","en":"Deals"}', NULL, ARRAY['deal:read'], 2),
('crm', 'crm.quotes', '{"ja":"見積","zh":"报价","en":"Quotes"}', NULL, ARRAY['quote:read'], 3),
('crm', 'crm.activities', '{"ja":"活動","zh":"活动","en":"Activities"}', NULL, ARRAY['activity:read'], 4),

-- 系统菜单
('system', 'company.settings', '{"ja":"会社設定","zh":"公司设置","en":"Company Settings"}', '/company/settings', ARRAY['company:settings'], 1),
('system', 'system.users', '{"ja":"ユーザー管理","zh":"用户管理","en":"User Management"}', '/system/users', ARRAY['user:manage'], 2),
('system', 'system.roles', '{"ja":"ロール管理","zh":"角色管理","en":"Role Management"}', '/system/roles', ARRAY['roles:manage'], 3),
('system', 'schema.editor', '{"ja":"スキーマ編集","zh":"Schema编辑器","en":"Schema Editor"}', '/schema', ARRAY['schema:edit'], 4),
('system', 'approvals.designer', '{"ja":"承認フロー設計","zh":"审批流程设计","en":"Approval Designer"}', NULL, ARRAY['approval:manage'], 5),
('system', 'scheduler.tasks', '{"ja":"スケジューラー","zh":"计划任务","en":"Scheduler"}', NULL, ARRAY['scheduler:manage'], 4),
('system', 'notif.ruleRuns', '{"ja":"通知ルール実行","zh":"通知规则运行","en":"Notification Runs"}', '/notifications/runs', ARRAY['workflow:read'], 5),
('system', 'notif.logs', '{"ja":"通知ログ","zh":"通知日志","en":"Notification Logs"}', '/notifications/logs', ARRAY['workflow:read'], 6),
('system', 'ai.workflowRules', '{"ja":"ワークフロールール","zh":"工作流规则","en":"Workflow Rules"}', '/workflow/rules', ARRAY['workflow:manage'], 7),
('system', 'ai.agentScenarios', '{"ja":"AIシナリオ","zh":"AI场景","en":"AI Scenarios"}', '/ai/agent-scenarios', ARRAY['ai:scenarios'], 8),
('system', 'user.management', '{"ja":"ユーザー管理","zh":"用户管理","en":"User Management"}', '/system/users', ARRAY['user:manage'], 9),
('system', 'role.management', '{"ja":"ロール管理","zh":"角色管理","en":"Role Management"}', '/system/roles', ARRAY['roles:manage'], 10)
ON CONFLICT (menu_key) DO UPDATE SET
  module_code = EXCLUDED.module_code,
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 预置角色（系统级，company_code为NULL表示模板）
-- 使用单独的INSERT语句来处理NULL company_code
DO $$
BEGIN
  -- 删除旧的系统角色（如果存在）然后重新插入
  DELETE FROM roles WHERE company_code IS NULL AND role_code IN (
    'SYS_ADMIN', 'ACCOUNTANT_SENIOR', 'ACCOUNTANT_JUNIOR', 'HR_MANAGER', 'HR_STAFF',
    'SALES_MANAGER', 'SALES_STAFF', 'WAREHOUSE_MANAGER', 'WAREHOUSE_STAFF',
    'TAX_ACCOUNTANT', 'AUDITOR', 'VIEWER'
  );
  
  INSERT INTO roles (company_code, role_code, role_name, role_type, description) VALUES
  (NULL, 'SYS_ADMIN', '系统管理员', 'builtin', '拥有所有权限，包括系统设置和用户管理'),
  (NULL, 'ACCOUNTANT_SENIOR', '高级会计', 'builtin', '负责日常会计工作，可创建编辑过账凭证，查看报表，执行银行操作'),
  (NULL, 'ACCOUNTANT_JUNIOR', '初级会计', 'builtin', '可创建编辑凭证但不能过账，不能执行银行操作'),
  (NULL, 'HR_MANAGER', '人事经理', 'builtin', '人事全部权限，可查看薪资信息，执行薪资计算'),
  (NULL, 'HR_STAFF', '人事专员', 'builtin', '人事基本权限，可管理员工信息，不能查看薪资详情'),
  (NULL, 'SALES_MANAGER', '销售经理', 'builtin', '销售和CRM全部权限，可查看销售分析'),
  (NULL, 'SALES_STAFF', '销售人员', 'builtin', '销售基本权限，可管理自己的客户和订单'),
  (NULL, 'WAREHOUSE_MANAGER', '仓库主管', 'builtin', '库存全部权限，可执行库存调整'),
  (NULL, 'WAREHOUSE_STAFF', '仓库人员', 'builtin', '库存基本权限，可执行入出库操作'),
  (NULL, 'TAX_ACCOUNTANT', '税理士', 'builtin', '外部税理士，只读访问财务数据和报表'),
  (NULL, 'AUDITOR', '审计师', 'builtin', '外部审计师，只读访问所有财务数据'),
  (NULL, 'VIEWER', '只读用户', 'builtin', '只能查看基本信息，不能执行任何操作');
END $$;

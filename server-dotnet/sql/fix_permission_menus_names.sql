-- 修复 permission_menus 表中菜单名称，使其与各页面实际标题保持一致

-- ===== 财务会计菜单名称修复 =====

UPDATE permission_menus SET menu_name = '{"ja":"仕訳作成","zh":"新建仕訳","en":"New Journal Entry"}'
WHERE menu_key = 'voucher.new';

UPDATE permission_menus SET menu_name = '{"ja":"会計伝票一覧","zh":"会计凭证列表","en":"Vouchers"}'
WHERE menu_key = 'vouchers.list';

UPDATE permission_menus SET menu_name = '{"ja":"勘定科目一覧","zh":"科目列表","en":"Chart of Accounts"}'
WHERE menu_key = 'accounts.list';

UPDATE permission_menus SET menu_name = '{"ja":"勘定明細一覧","zh":"科目明细账","en":"Account Ledger"}'
WHERE menu_key = 'account.ledger';

UPDATE permission_menus SET menu_name = '{"ja":"勘定残高","zh":"科目余额表","en":"Account Balance"}'
WHERE menu_key = 'account.balance';

UPDATE permission_menus SET menu_name = '{"ja":"銀行出金配分","zh":"银行出金配分","en":"Bank Payment Allocation"}'
WHERE menu_key = 'op.bankPayment';

UPDATE permission_menus SET menu_name = '{"ja":"自動支払","zh":"自动支付","en":"Auto Payment"}'
WHERE menu_key = 'op.fbPayment';

UPDATE permission_menus SET menu_name = '{"ja":"財務諸表","zh":"财务报表","en":"Financial Statements"}'
WHERE menu_key = 'fin.reports';

UPDATE permission_menus SET menu_name = '{"ja":"財務諸表構成","zh":"报表设计器","en":"Statement Designer"}'
WHERE menu_key = 'fin.designer';

UPDATE permission_menus SET menu_name = '{"ja":"銀行明細","zh":"银行明细","en":"Bank Transactions"}'
WHERE menu_key = 'moneytree.transactions';

-- ===== 追加缺失的菜单项 =====

-- 消費税申告書
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('finance', 'fin.consumptionTax', '{"ja":"消費税申告書","zh":"消费税申报表","en":"Consumption Tax Return"}', '/financial/consumption-tax', ARRAY['report:financial'], 17)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name, menu_path = EXCLUDED.menu_path;

-- 現金出納帳
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('finance', 'fin.cashLedger', '{"ja":"現金出納帳","zh":"现金出纳账","en":"Cash Ledger"}', '/cash/ledger', ARRAY['voucher:read'], 18)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name, menu_path = EXCLUDED.menu_path;

-- 月次締め
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('finance', 'fin.monthlyClosing', '{"ja":"月次締め","zh":"月结","en":"Monthly Closing"}', '/financial/monthly-closing', ARRAY['period:manage'], 19)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name, menu_path = EXCLUDED.menu_path;

-- 資金繰り
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('finance', 'fin.cashFlow', '{"ja":"資金繰り","zh":"资金周转","en":"Cash Flow"}', '/finance/cash-flow', ARRAY['voucher:read'], 20)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name, menu_path = EXCLUDED.menu_path;

-- 経費精算一覧
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('finance', 'fin.expenseClaims', '{"ja":"経費精算一覧","zh":"经费报销一览","en":"Expense Claims"}', '/finance/expense-claims', ARRAY['voucher:read'], 21)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name, menu_path = EXCLUDED.menu_path;

-- ===== HR 菜单名称修复 =====

UPDATE permission_menus SET menu_name = '{"ja":"部門階層","zh":"部门层级","en":"Departments"}'
WHERE menu_key = 'hr.dept';

-- ===== 销售菜单（追加缺失项） =====

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('sales', 'sales.orders', '{"ja":"受注一覧","zh":"销售订单列表","en":"Sales Orders"}', '/crm/sales-orders', ARRAY['so:read'], 1)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name;

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('sales', 'sales.deliveryNotes', '{"ja":"納品書一覧","zh":"纳品书列表","en":"Delivery Notes"}', '/delivery-notes', ARRAY['so:read'], 2)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name;

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('sales', 'sales.invoices', '{"ja":"請求書一覧","zh":"请求书列表","en":"Sales Invoices"}', '/sales-invoices', ARRAY['so:read'], 3)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name;

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('sales', 'sales.analytics', '{"ja":"販売分析","zh":"销售分析","en":"Sales Analytics"}', '/sales-analytics', ARRAY['so:read'], 4)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name;

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES ('sales', 'sales.alerts', '{"ja":"販売アラート","zh":"销售提醒","en":"Sales Alerts"}', '/sales-alerts', ARRAY['so:read'], 5)
ON CONFLICT (menu_key) DO UPDATE SET menu_name = EXCLUDED.menu_name;

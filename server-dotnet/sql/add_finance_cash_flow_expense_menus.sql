-- 会計メニューに「資金繰り」「経費精算一覧」を追加し、ロール設定で選択可能にする
-- 固定資産関連の menu_name を日本語に統一

INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES
('finance', 'menu_cash_flow', '{"ja":"資金繰り","zh":"资金周转","en":"Cash Flow"}', '/finance/cash-flow', ARRAY['voucher:read'], 17),
('finance', 'menu_expense_claims', '{"ja":"経費精算一覧","zh":"费用报销一览","en":"Expense Claims"}', '/finance/expense-claims', ARRAY['voucher:read'], 18)
ON CONFLICT (menu_key) DO UPDATE SET
  module_code = EXCLUDED.module_code,
  menu_name = EXCLUDED.menu_name,
  menu_path = EXCLUDED.menu_path,
  caps_required = EXCLUDED.caps_required,
  display_order = EXCLUDED.display_order;

-- 固定資産メニューを日本語表示に統一（menu_name が英語の場合は日本語に更新）
UPDATE permission_menus SET menu_name = '{"ja":"資産クラス管理","zh":"资产类别","en":"資産クラス管理"}' WHERE menu_key = 'fa.classes';
UPDATE permission_menus SET menu_name = '{"ja":"固定資産一覧","zh":"固定资产列表","en":"固定資産一覧"}' WHERE menu_key = 'fa.list';
UPDATE permission_menus SET menu_name = '{"ja":"定期償却記帳","zh":"折旧计算","en":"定期償却記帳"}' WHERE menu_key = 'fa.depreciation';

SELECT 'Finance cash flow and expense claims menus added; fixed asset labels set to Japanese.' AS result;

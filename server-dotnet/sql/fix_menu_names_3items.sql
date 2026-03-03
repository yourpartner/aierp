UPDATE permission_menus
SET menu_name = '{"ja":"入金配分","zh":"入金配分","en":"Receipt Allocation"}'
WHERE menu_key = 'rcpt.planner';

UPDATE permission_menus
SET menu_name = '{"ja":"出金配分","zh":"出金配分","en":"Payment Allocation"}'
WHERE menu_key = 'op.bankPayment';

UPDATE permission_menus
SET module_code = 'fixed_assets'
WHERE module_code = 'fixed_asset';

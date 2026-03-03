-- ============================================================
-- LP01 Ｌ＆Ｐ合同会社 セットアップ
-- 既存データへの影響は一切なし
-- ============================================================
BEGIN;

-- 1. 会社登録
INSERT INTO companies (company_code, name)
SELECT 'LP01', 'Ｌ＆Ｐ合同会社'
WHERE NOT EXISTS (SELECT 1 FROM companies WHERE company_code='LP01');

-- 2. 会計科目（JP01コピー）
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', a.payload FROM accounts a WHERE a.company_code='JP01'
AND NOT EXISTS (SELECT 1 FROM accounts x WHERE x.company_code='LP01' AND x.payload->>'code' = a.payload->>'code');

-- 3. LP01追加科目
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"134","name":"三井住友銀行","category":"BS","openItem":false,"isBank":true,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='134');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"135","name":"東京スター銀行","category":"BS","openItem":false,"isBank":true,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='135');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"171","name":"商品","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='171');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"217","name":"少額資産","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='217');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"292","name":"設立費","category":"BS","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='292');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"712","name":"期首棚卸高","category":"PL","openItem":false,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='712');
INSERT INTO accounts (company_code, payload)
SELECT 'LP01', '{"code":"314","name":"未払金","category":"BS","openItem":true,"isBank":false,"isCash":false}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code='LP01' AND payload->>'code'='314');

-- 4. 科目名修正
UPDATE accounts SET payload=jsonb_set(payload,'{name}','"減価償却費"'::jsonb) WHERE company_code='LP01' AND payload->>'code'='845';
UPDATE accounts SET payload=jsonb_set(payload,'{name}','"旅費交通費"'::jsonb) WHERE company_code='LP01' AND payload->>'code'='842';
UPDATE accounts SET payload=jsonb_set(payload,'{name}','"未払法人税等"'::jsonb) WHERE company_code='LP01' AND payload->>'code'='316';

-- 5. ADMINロール
INSERT INTO roles (company_code, role_code, role_name, is_active, role_type)
SELECT 'LP01','ADMIN','ADMIN',true,'custom'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE company_code='LP01' AND role_code='ADMIN');

-- 6. ロール権限付与
DO $$
DECLARE v_role_id uuid;
BEGIN
  SELECT id INTO v_role_id FROM roles WHERE company_code='LP01' AND role_code='ADMIN' LIMIT 1;
  IF v_role_id IS NOT NULL THEN
    INSERT INTO role_caps (role_id, cap) VALUES
  (v_role_id, 'account:manage'),
  (v_role_id, 'account:read'),
  (v_role_id, 'ai.admin.bind'),
  (v_role_id, 'ai.certificate.apply'),
  (v_role_id, 'ai.certificate.approve'),
  (v_role_id, 'ai.delivery.manage'),
  (v_role_id, 'ai.general'),
  (v_role_id, 'ai.invoice.recognize'),
  (v_role_id, 'ai.leave.apply'),
  (v_role_id, 'ai.leave.approve'),
  (v_role_id, 'ai.order.manage'),
  (v_role_id, 'ai.payroll.query'),
  (v_role_id, 'ai.payroll.report'),
  (v_role_id, 'ai.report.financial'),
  (v_role_id, 'ai:scenarios'),
  (v_role_id, 'ai.timesheet.approve'),
  (v_role_id, 'ai.timesheet.entry'),
  (v_role_id, 'ai.timesheet.query'),
  (v_role_id, 'ai.voucher.create'),
  (v_role_id, 'approval:approve'),
  (v_role_id, 'approval:manage'),
  (v_role_id, 'approval:submit'),
  (v_role_id, 'bp:manage'),
  (v_role_id, 'bp:read'),
  (v_role_id, 'cash:manage'),
  (v_role_id, 'cash:read'),
  (v_role_id, 'cash:reconcile'),
  (v_role_id, 'cert:issue'),
  (v_role_id, 'cert:request'),
  (v_role_id, 'company:settings'),
  (v_role_id, 'consumption_tax:manage'),
  (v_role_id, 'consumption_tax:read'),
  (v_role_id, 'delivery:manage'),
  (v_role_id, 'department:manage'),
  (v_role_id, 'department:read'),
  (v_role_id, 'employee:create'),
  (v_role_id, 'employee:delete'),
  (v_role_id, 'employee:edit'),
  (v_role_id, 'employee:read'),
  (v_role_id, 'employee:salary'),
  (v_role_id, 'fa:depreciate'),
  (v_role_id, 'fa:manage'),
  (v_role_id, 'fa:read'),
  (v_role_id, 'invoice:manage'),
  (v_role_id, 'material:manage'),
  (v_role_id, 'material:read'),
  (v_role_id, 'monthly_closing:adjustment'),
  (v_role_id, 'monthly_closing:approve'),
  (v_role_id, 'monthly_closing:check'),
  (v_role_id, 'monthly_closing:close'),
  (v_role_id, 'monthly_closing:reopen'),
  (v_role_id, 'monthly_closing:start'),
  (v_role_id, 'monthly_closing:view'),
  (v_role_id, 'op:bank-collect'),
  (v_role_id, 'op:bank-payment'),
  (v_role_id, 'order:create'),
  (v_role_id, 'order:delete'),
  (v_role_id, 'order:edit'),
  (v_role_id, 'order:read'),
  (v_role_id, 'payroll:execute'),
  (v_role_id, 'payroll:resident_tax'),
  (v_role_id, 'payroll:view'),
  (v_role_id, 'period:manage'),
  (v_role_id, 'purchase:manage'),
  (v_role_id, 'purchase:read'),
  (v_role_id, 'report:financial'),
  (v_role_id, 'roles:manage'),
  (v_role_id, 'sales:analytics'),
  (v_role_id, 'scheduler:manage'),
  (v_role_id, 'schema:edit'),
  (v_role_id, 'schema:read'),
  (v_role_id, 'staffing:ai'),
  (v_role_id, 'staffing:analytics'),
  (v_role_id, 'staffing:contract:manage'),
  (v_role_id, 'staffing:contract:read'),
  (v_role_id, 'staffing:email'),
  (v_role_id, 'staffing:hatchuu:read'),
  (v_role_id, 'staffing:hatchuu:write'),
  (v_role_id, 'staffing:invoice:manage'),
  (v_role_id, 'staffing:invoice:read'),
  (v_role_id, 'staffing:juchuu:read'),
  (v_role_id, 'staffing:juchuu:write'),
  (v_role_id, 'staffing:project:manage'),
  (v_role_id, 'staffing:project:read'),
  (v_role_id, 'staffing:resource:manage'),
  (v_role_id, 'staffing:resource:read'),
  (v_role_id, 'staffing:timesheet:manage'),
  (v_role_id, 'staffing:timesheet:read'),
  (v_role_id, 'stock:adjust'),
  (v_role_id, 'stock:movement'),
  (v_role_id, 'stock:read'),
  (v_role_id, 'timesheet:manage'),
  (v_role_id, 'timesheet:proxy'),
  (v_role_id, 'timesheet:read'),
  (v_role_id, 'user:manage'),
  (v_role_id, 'user:read'),
  (v_role_id, 'vendor_invoice:manage'),
  (v_role_id, 'vendor_invoice:read'),
  (v_role_id, 'voucher:create'),
  (v_role_id, 'voucher:delete'),
  (v_role_id, 'voucher:edit'),
  (v_role_id, 'voucher:post'),
  (v_role_id, 'voucher:read'),
  (v_role_id, 'voucher:reverse'),
  (v_role_id, 'warehouse:manage'),
  (v_role_id, 'warehouse:read'),
  (v_role_id, 'workflow:manage'),
  (v_role_id, 'workflow:read')
    ON CONFLICT DO NOTHING;
  END IF;
END $$;

-- 7. 管理者ユーザー登録
INSERT INTO users (company_code, employee_code, password_hash, name, user_type, is_active)
SELECT 'LP01','admin','$2a$11$exnM6jr437kN0R9aDTzjsuJYoL5Dh0gtZM/h4qjDGiYnh3dBQG.Ey','LP01 Admin','internal',true
WHERE NOT EXISTS (SELECT 1 FROM users WHERE company_code='LP01' AND employee_code='admin');

-- 8. ユーザーにADMINロールを付与
DO $$
DECLARE v_user_id uuid; v_role_id uuid;
BEGIN
  SELECT id INTO v_user_id FROM users WHERE company_code='LP01' AND employee_code='admin' LIMIT 1;
  SELECT id INTO v_role_id FROM roles WHERE company_code='LP01' AND role_code='ADMIN' LIMIT 1;
  IF v_user_id IS NOT NULL AND v_role_id IS NOT NULL THEN
    INSERT INTO user_roles (user_id, role_id) VALUES (v_user_id, v_role_id) ON CONFLICT DO NOTHING;
  END IF;
END $$;

-- 9. スキーマコピー
INSERT INTO schemas (id, company_code, name, schema, ui, query, core_fields, validators, numbering, ai_hints, created_at, updated_at)
SELECT gen_random_uuid(), 'LP01', s.name, s.schema, s.ui, s.query, s.core_fields, s.validators, s.numbering, s.ai_hints, now(), now()
FROM schemas s WHERE s.company_code='JP01'
AND s.name NOT IN (SELECT name FROM schemas WHERE company_code='LP01')
ON CONFLICT DO NOTHING;

COMMIT;

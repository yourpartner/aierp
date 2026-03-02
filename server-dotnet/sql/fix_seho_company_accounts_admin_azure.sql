-- SEHO（株式会社雪峰）: 公司 + 会计科目(参考JP01) + ADMIN角色 + admin账户，仅影响SEHO
BEGIN;

-- 1. 公司
INSERT INTO companies (company_code, name)
SELECT 'SEHO', '株式会社雪峰'
WHERE NOT EXISTS (SELECT 1 FROM companies WHERE company_code = 'SEHO');

-- 2. 会计科目（从 JP01 复制）
INSERT INTO accounts (company_code, payload)
SELECT 'SEHO', payload
FROM accounts
WHERE company_code = 'JP01'
ON CONFLICT (company_code, account_code) DO NOTHING;

-- 3. admin 用户（密码=admin）
INSERT INTO users (company_code, employee_code, password_hash, name, user_type, is_active)
VALUES ('SEHO', 'admin', '$2a$11$exnM6jr437kN0R9aDTzjsuJYoL5Dh0gtZM/h4qjDGiYnh3dBQG.Ey', 'SEHO Admin', 'internal', true)
ON CONFLICT (company_code, employee_code) DO UPDATE
SET password_hash = EXCLUDED.password_hash, is_active = true;

-- 4. 若 SEHO 无员工则先建一条供 admin 关联（无则跳过）
INSERT INTO employees (company_code, payload)
SELECT 'SEHO', '{"code":"SEHO001","nameKanji":"王 雪峰","nameKana":"オウ セツホウ","gender":"M","birthDate":"1990-01-01","nationality":"JP","contact":{},"insurance":{},"contracts":[],"departments":[],"bankAccounts":[],"emergencies":[],"attachments":[]}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM employees WHERE company_code = 'SEHO' AND payload->>'code' = 'SEHO001');

UPDATE users
SET employee_id = (SELECT id FROM employees WHERE company_code = 'SEHO' AND payload->>'code' = 'SEHO001' LIMIT 1)
WHERE company_code = 'SEHO' AND employee_code = 'admin';

-- 5. ADMIN 角色
INSERT INTO roles (company_code, role_code, role_name, is_active, role_type)
SELECT 'SEHO', 'ADMIN', 'ADMIN', true, 'custom'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE company_code = 'SEHO' AND role_code = 'ADMIN');

-- 6. admin 用户赋予 ADMIN 角色
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u, roles r
WHERE u.company_code = 'SEHO' AND u.employee_code = 'admin'
  AND r.company_code = 'SEHO' AND r.role_code = 'ADMIN'
  AND NOT EXISTS (SELECT 1 FROM user_roles ur2 WHERE ur2.user_id = u.id AND ur2.role_id = r.id);

-- 7. 菜单权限（从 JP01 admin 复制 role_caps）
INSERT INTO role_caps (role_id, cap)
SELECT (SELECT id FROM roles WHERE company_code = 'SEHO' AND role_code = 'ADMIN' LIMIT 1), caps.cap
FROM (
  SELECT DISTINCT rc.cap
  FROM role_caps rc
  JOIN roles r ON r.id = rc.role_id
  WHERE r.company_code = 'JP01' AND r.role_code IN ('admin', 'ADMIN')
) caps
ON CONFLICT (role_id, cap) DO NOTHING;

COMMIT;

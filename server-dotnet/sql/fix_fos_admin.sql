-- Fix admin user for FOS01
BEGIN;

-- 1. Update password hash (correctly escaping $)
UPDATE users 
SET password_hash = '$2a$11$exnM6jr437kN0R9aDTzjsuJYoL5Dh0gtZM/h4qjDGiYnh3dBQG.Ey'
WHERE company_code = 'FOS01' AND employee_code = 'admin';

-- 2. Link to employee FOS001
UPDATE users
SET employee_id = (SELECT id FROM employees WHERE company_code = 'FOS01' AND employee_code = 'FOS001' LIMIT 1)
WHERE company_code = 'FOS01' AND employee_code = 'admin';

-- 3. Create ADMIN role for FOS01 if not exists
INSERT INTO roles (company_code, role_code, role_name, is_active, role_type)
VALUES ('FOS01', 'ADMIN', 'ADMIN', true, 'custom')
ON CONFLICT (company_code, role_code) DO NOTHING;

-- 4. Assign ADMIN role to admin user
INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u, roles r
WHERE u.company_code = 'FOS01' AND u.employee_code = 'admin'
  AND r.company_code = 'FOS01' AND r.role_code = 'ADMIN'
ON CONFLICT DO NOTHING;

COMMIT;

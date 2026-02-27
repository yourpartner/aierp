-- Reset admin password to "admin"
UPDATE users 
SET password_hash = '$2a$11$exnM6jr437kN0R9aDTzjsuJYoL5Dh0gtZM/h4qjDGiYnh3dBQG.Ey'
WHERE employee_code = 'admin' AND company_code = 'JP01';

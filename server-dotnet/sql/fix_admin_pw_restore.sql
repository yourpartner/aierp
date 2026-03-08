UPDATE users
SET password_hash = '$2a$11$rBBeZQB65eJqaac5QvCzxOq1T2r9CMBWlpS0mEUrwUOQ4PsDjOBwm'
WHERE employee_code = 'admin' AND company_code = 'JP01'
RETURNING employee_code, substring(password_hash,1,30) AS hash_prefix;

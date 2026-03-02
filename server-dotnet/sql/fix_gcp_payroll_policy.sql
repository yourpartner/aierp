-- GCP 公司没有 payroll_policies 会导致工资计算结果为空。
-- 从 JP01 复制一条有效 policy 到 GCP，仅插入 company_code='GCP'，不影响 JP01 及其他公司。
-- 仅当 GCP 尚无任何 policy 时插入。
INSERT INTO payroll_policies (company_code, payload, created_at, updated_at, version, is_active)
SELECT 'GCP', payload, now(), now(), version, is_active
FROM payroll_policies
WHERE company_code = 'JP01' AND is_active = true
  AND NOT EXISTS (SELECT 1 FROM payroll_policies WHERE company_code = 'GCP' LIMIT 1)
ORDER BY created_at DESC
LIMIT 1;

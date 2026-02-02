-- 导入 CompanyID=172 (JP01) 的住民税数据
-- 记录1: EmployeeID=927 不存在于新系统，跳过

-- 记录2: Year=2025, EmployeeID=813, TotalAmount=8000
-- 月份: 202506=1400, 202507=600, 202508=600, 202509=600, 202510=600, 202511=600, 202512=600, 202601=600, 202602=600, 202603=600, 202604=600, 202605=600
INSERT INTO resident_tax_schedules (
  company_code, employee_id, fiscal_year, annual_amount,
  june_amount, july_amount, august_amount, september_amount,
  october_amount, november_amount, december_amount,
  january_amount, february_amount, march_amount, april_amount, may_amount,
  status, metadata
) VALUES (
  'JP01',
  '1ad2d4c4-2ced-4838-a90f-01cffa050155',
  2025,
  8000,
  1400, 600, 600, 600, 600, 600, 600, 600, 600, 600, 600, 600,
  'active',
  '{"legacy_employee_id": 813, "imported_from": "old_system_dump", "original_create_time": "2025-06-17T16:31:47"}'::jsonb
) ON CONFLICT (company_code, employee_id, fiscal_year) DO UPDATE SET
  annual_amount = EXCLUDED.annual_amount,
  june_amount = EXCLUDED.june_amount,
  july_amount = EXCLUDED.july_amount,
  august_amount = EXCLUDED.august_amount,
  september_amount = EXCLUDED.september_amount,
  october_amount = EXCLUDED.october_amount,
  november_amount = EXCLUDED.november_amount,
  december_amount = EXCLUDED.december_amount,
  january_amount = EXCLUDED.january_amount,
  february_amount = EXCLUDED.february_amount,
  march_amount = EXCLUDED.march_amount,
  april_amount = EXCLUDED.april_amount,
  may_amount = EXCLUDED.may_amount,
  metadata = EXCLUDED.metadata,
  updated_at = now();

-- 记录3: Year=2025, EmployeeID=848, TotalAmount=21100
-- 月份: 202506=2400, 202507=1700, 202508=1700, 202509=1700, 202510=1700, 202511=1700, 202512=1700, 202601=1700, 202602=1700, 202603=1700, 202604=1700, 202605=1700
INSERT INTO resident_tax_schedules (
  company_code, employee_id, fiscal_year, annual_amount,
  june_amount, july_amount, august_amount, september_amount,
  october_amount, november_amount, december_amount,
  january_amount, february_amount, march_amount, april_amount, may_amount,
  status, metadata
) VALUES (
  'JP01',
  '31885ec3-8d21-4b81-8325-232a24aba98b',
  2025,
  21100,
  2400, 1700, 1700, 1700, 1700, 1700, 1700, 1700, 1700, 1700, 1700, 1700,
  'active',
  '{"legacy_employee_id": 848, "imported_from": "old_system_dump", "original_create_time": "2025-06-17T16:22:10"}'::jsonb
) ON CONFLICT (company_code, employee_id, fiscal_year) DO UPDATE SET
  annual_amount = EXCLUDED.annual_amount,
  june_amount = EXCLUDED.june_amount,
  july_amount = EXCLUDED.july_amount,
  august_amount = EXCLUDED.august_amount,
  september_amount = EXCLUDED.september_amount,
  october_amount = EXCLUDED.october_amount,
  november_amount = EXCLUDED.november_amount,
  december_amount = EXCLUDED.december_amount,
  january_amount = EXCLUDED.january_amount,
  february_amount = EXCLUDED.february_amount,
  march_amount = EXCLUDED.march_amount,
  april_amount = EXCLUDED.april_amount,
  may_amount = EXCLUDED.may_amount,
  metadata = EXCLUDED.metadata,
  updated_at = now();

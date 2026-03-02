-- WEL01 2025年12月 給与計算結果 インポート
-- 対象: 6名, 総支給額: 900,975円
BEGIN;

-- payroll_runs: ヘッダー
INSERT INTO payroll_runs (id, company_code, period_month, run_type, status, total_amount, metadata)
VALUES ('b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', '2025-12', 'manual', 'approved', 900975,
  '{"source": "yourpartnerdb2_dump_20260223", "imported_at": "2026-03-02T09:02:50"}'::jsonb)
ON CONFLICT (company_code, COALESCE(policy_id, '00000000-0000-0000-0000-000000000000'::uuid), period_month, run_type) DO NOTHING;

-- payroll_run_entries: 社員別明細
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('c5395531-16e1-4d81-a5ce-025b3d569957', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', 'da2a3637-aaec-455c-962a-a0bd36c1b153', 'WEL002', 'サツキ ヨシハラ', 52325,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000009", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 10, "actualMinutes": 2415, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 52325}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 0}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 0}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 0}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 52325, "totalDeductions": 0, "netPay": 52325, "comment": "", "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 52325, "ResidentTax": 0}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('642c1027-3228-4325-8713-5787da694afe', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', '6bf5938d-5b12-47c2-8b56-bb007766f2f3', 'WEL003', 'ユカリ カワタ', 189475,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000010", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 18, "actualMinutes": 8745, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 189475}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 4390}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 0}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 0}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 189475, "totalDeductions": 4390, "netPay": 185085, "comment": "", "rawSalaryItems": {"IncomeTax": 4390, "BaseSalary": 189475, "ResidentTax": 0}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('4bfcec52-bcb6-47bf-80c0-3be3cc49b91a', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', '63d30ed2-9f55-4d8b-8673-2b2ddc835683', 'WEL007', 'キョウコ ハヅキ', 300000,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000011", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 23, "actualMinutes": 11040, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 300000}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 8380}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 0}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 0}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 300000, "totalDeductions": 8380, "netPay": 291620, "comment": "", "rawSalaryItems": {"IncomeTax": 8380, "BaseSalary": 300000}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('e3606d31-9f40-4311-9fb8-06626d23a56d', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', 'cf9b9975-6bd1-48cd-9a2a-9ffe17300b0b', 'WEL008', 'ディクシャ バラヒ', 36075,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000012", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 6, "actualMinutes": 1665, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 36075}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 0}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 0}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 0}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 36075, "totalDeductions": 0, "netPay": 36075, "comment": "", "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 36075, "ResidentTax": 0}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('618e680d-cdb2-4a1b-8203-62b24bb438d6', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', '9cd8e45e-8b7c-408b-97e1-7681d6248069', 'WEL014', 'アユシュ ポカレル', 23100,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000013", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 4, "actualMinutes": 1260, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 23100}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 0}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 0}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 0}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 23100, "totalDeductions": 0, "netPay": 23100, "comment": "", "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 23100, "TravelFare": 6400, "ResidentTax": 0}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;
INSERT INTO payroll_run_entries (id, run_id, company_code, employee_id, employee_code, employee_name, total_amount, payroll_sheet, accounting_draft)
VALUES ('d4609b02-1447-4574-bc86-5dc3f875a776', 'b5db737c-416b-4614-a6f8-013d62ff53e2', 'WEL01', '6bf5938d-5b12-47c2-8b56-bb007766f2f3', 'WEL003', 'マイケルエンジェル カバイェロ', 300000,
  '{"period": "2025-12", "fromDate": "2025-12-01", "toDate": "2025-12-31", "dueDate": "2026-01-23", "docNo": "2512000014", "workRate": 100, "calendarWorkDays": 23, "actualWorkDays": 25, "actualMinutes": 11010, "otMinutes": 0, "hdMinutes": 0, "earnings": [{"itemCode": "BaseSalary", "itemName": "基本給", "amount": 300000}], "deductions": [{"itemCode": "IncomeTax", "itemName": "所得税", "amount": 6830}, {"itemCode": "ResidentTax", "itemName": "住民税", "amount": 0}, {"itemCode": "HealthInsurance", "itemName": "健康保険", "amount": 14865}, {"itemCode": "EndowInsurance", "itemName": "厚生年金", "amount": 27450}, {"itemCode": "HireInsurance", "itemName": "雇用保険", "amount": 0}, {"itemCode": "LongCareInsurance", "itemName": "介護保険", "amount": 0}], "totalEarnings": 300000, "totalDeductions": 49145, "netPay": 250855, "comment": "", "rawSalaryItems": {"IncomeTax": 6830, "BaseSalary": 300000, "EndowInsurance": 27450, "HealthInsurance": 14865}}'::jsonb,
  '{}'::jsonb)
ON CONFLICT DO NOTHING;

COMMIT;
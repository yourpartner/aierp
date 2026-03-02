-- WEL01 payroll data fix: TravelFare補完 + 未導入フィールドをmetadataに追加
-- 他社データへの影響なし (WHERE company_code='WEL01' AND id='...' で限定)
BEGIN;

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 52325, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 52325}]'::jsonb,
    metadata = '{"docNo": "2512000009", "netPay": 52325, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 2415, "totalEarnings": 52325, "actualWorkDays": 10, "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 52325, "ResidentTax": 0}, "totalDeductions": 0, "calendarWorkDays": 23, "salaryDeductAmt": 45834, "baseDeductAmt": 40000}'::jsonb,
    total_amount = 52325
WHERE id = 'c5395531-16e1-4d81-a5ce-025b3d569957' AND company_code = 'WEL01';

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 189475, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 189475}, {"kind": "deduction", "amount": 4390, "itemCode": "IncomeTax", "itemName": "所得税", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 4390}]'::jsonb,
    metadata = '{"docNo": "2512000010", "netPay": 185085, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 8745, "totalEarnings": 189475, "actualWorkDays": 18, "rawSalaryItems": {"IncomeTax": 4390, "BaseSalary": 189475, "ResidentTax": 0}, "totalDeductions": 4390, "calendarWorkDays": 23, "salaryDeductAmt": 45834, "baseDeductAmt": 40000}'::jsonb,
    total_amount = 189475
WHERE id = '642c1027-3228-4325-8713-5787da694afe' AND company_code = 'WEL01';

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 300000, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 300000}, {"kind": "deduction", "amount": 8380, "itemCode": "IncomeTax", "itemName": "所得税", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 8380}]'::jsonb,
    metadata = '{"docNo": "2512000011", "netPay": 291620, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 11040, "totalEarnings": 300000, "actualWorkDays": 23, "rawSalaryItems": {"IncomeTax": 8380, "BaseSalary": 300000}, "totalDeductions": 8380, "calendarWorkDays": 23, "salaryDeductAmt": 96667, "baseDeductAmt": 40000, "endowBase": 88000, "healthInsuranceBase": 58000}'::jsonb,
    total_amount = 300000
WHERE id = '4bfcec52-bcb6-47bf-80c0-3be3cc49b91a' AND company_code = 'WEL01';

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 36075, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 36075}]'::jsonb,
    metadata = '{"docNo": "2512000012", "netPay": 36075, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 1665, "totalEarnings": 36075, "actualWorkDays": 6, "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 36075, "ResidentTax": 0}, "totalDeductions": 0, "calendarWorkDays": 23, "salaryDeductAmt": 45834, "baseDeductAmt": 40000}'::jsonb,
    total_amount = 36075
WHERE id = 'e3606d31-9f40-4311-9fb8-06626d23a56d' AND company_code = 'WEL01';

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 23100, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 23100}, {"itemCode": "TravelFare", "itemName": "交通費", "amount": 6400, "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 6400, "kind": "earning"}]'::jsonb,
    metadata = '{"docNo": "2512000013", "netPay": 29500, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 1260, "totalEarnings": 29500, "actualWorkDays": 4, "rawSalaryItems": {"IncomeTax": 0, "BaseSalary": 23100, "TravelFare": 6400, "ResidentTax": 0}, "totalDeductions": 0, "calendarWorkDays": 23, "salaryDeductAmt": 45834, "baseDeductAmt": 40000}'::jsonb,
    total_amount = 29500
WHERE id = '618e680d-cdb2-4a1b-8203-62b24bb438d6' AND company_code = 'WEL01';

UPDATE payroll_run_entries
SET payroll_sheet = '[{"kind": "earning", "amount": 300000, "itemCode": "BaseSalary", "itemName": "基本給", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 300000}, {"kind": "deduction", "amount": 6830, "itemCode": "IncomeTax", "itemName": "所得税", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 6830}, {"kind": "deduction", "amount": 14865, "itemCode": "HealthInsurance", "itemName": "健康保険", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 14865}, {"kind": "deduction", "amount": 27450, "itemCode": "EndowInsurance", "itemName": "厚生年金", "adjustment": 0, "isManuallyAdded": false, "adjustmentReason": "", "calculatedAmount": 27450}]'::jsonb,
    metadata = '{"docNo": "2512000014", "netPay": 250855, "period": "2025-12", "toDate": "2025-12-31", "dueDate": "2026-01-23", "fromDate": "2025-12-01", "workRate": 100, "hdMinutes": 0, "otMinutes": 0, "actualMinutes": 11010, "totalEarnings": 300000, "actualWorkDays": 25, "rawSalaryItems": {"IncomeTax": 6830, "BaseSalary": 300000, "EndowInsurance": 27450, "HealthInsurance": 14865}, "totalDeductions": 49145, "calendarWorkDays": 23, "salaryDeductAmt": 83973, "baseDeductAmt": 40000, "endowBase": 300000, "healthInsuranceBase": 300000}'::jsonb,
    total_amount = 300000
WHERE id = 'd4609b02-1447-4574-bc86-5dc3f875a776' AND company_code = 'WEL01';

COMMIT;

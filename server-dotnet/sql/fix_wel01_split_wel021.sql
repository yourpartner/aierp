-- WEL003 重複社員修正: カバイェロ を WEL021 として新規登録し payroll entry を更新
-- 他社データへの影響なし (全て company_code='WEL01')
BEGIN;

-- 1. 新社員 WEL021 (MICHAELAN CABALLERO) を作成
INSERT INTO employees (id, company_code, payload)
VALUES (
  'b2f1d25e-06ca-457c-b89d-c25b122d65b2',
  'WEL01',
  '{"code": "WEL021", "nameKanji": "CABALLERO MICHAELAN", "nameKana": "カバイェロ マイケルエンジェル", "birthDate": "1997-01-31", "gender": "F", "contact": {"phone": "00000000000", "email": "", "postalCode": "", "address": "長野県北佐久郡軽井沢町大字長倉２３５０番地１６０"}, "contracts": [{"periodFrom": "2025-12-01", "periodTo": "2026-11-30", "employmentTypeCode": ""}], "salaries": [{"startDate": "2025-12-01", "description": "BaseSalary 300000円 ～2026-11-30"}], "departments": [], "attachments": [], "bankAccounts": [], "emergencies": []}'::jsonb
)
ON CONFLICT DO NOTHING;

-- 2. payroll_run_entries (docNo=2512000014) を WEL021 に更新
UPDATE payroll_run_entries
SET
  employee_id   = 'b2f1d25e-06ca-457c-b89d-c25b122d65b2',
  employee_code = 'WEL021',
  employee_name = 'MICHAELAN CABALLERO',
  payroll_sheet = payroll_sheet
                  || jsonb_build_object('employeeCode', 'WEL021')
WHERE id = 'd4609b02-1447-4574-bc86-5dc3f875a776'
  AND company_code = 'WEL01';

COMMIT;

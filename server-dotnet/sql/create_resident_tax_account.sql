-- 创建住民税会计科目 (3185)
INSERT INTO accounts (company_code, payload)
SELECT 'JP01', 
'{
  "code": "3185",
  "name": "住民税預り金",
  "isBank": false,
  "isCash": false,
  "taxType": "NON_TAX",
  "category": "BS",
  "openItem": true,
  "createdAt": "2026-01-27T00:00:00+00:00",
  "createdBy": "system",
  "updatedAt": "2026-01-27T00:00:00+00:00",
  "updatedBy": "system",
  "fieldRules": {
    "assetId": "hidden",
    "vendorId": "hidden",
    "customerId": "hidden",
    "employeeId": "required",
    "paymentDate": "hidden",
    "departmentId": "hidden"
  },
  "fsBalanceGroup": "BS-L-1",
  "openItemBaseline": "EMPLOYEE"
}'::jsonb
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE company_code = 'JP01' AND account_code = '3185');

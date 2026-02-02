-- 检查 nlText 是否包含住民税
SELECT 
  CASE WHEN payload->>'nlText' LIKE '%住民税%' 
       THEN 'OK: nlText contains 住民税' 
       ELSE 'MISSING: nlText does not contain 住民税' 
  END as nltext_status,
  CASE WHEN EXISTS (
         SELECT 1 FROM jsonb_array_elements(payload->'rules') r 
         WHERE r->>'item' = 'RESIDENT_TAX'
       ) 
       THEN 'OK: rules contains RESIDENT_TAX' 
       ELSE 'MISSING: rules does not contain RESIDENT_TAX' 
  END as rules_status,
  CASE WHEN EXISTS (
         SELECT 1 FROM jsonb_array_elements(payload->'dsl'->'journalRules') j 
         WHERE j->>'name' = 'residentTax'
       ) 
       THEN 'OK: journalRules contains residentTax' 
       ELSE 'MISSING: journalRules does not contain residentTax' 
  END as journal_status,
  CASE WHEN EXISTS (
         SELECT 1 FROM jsonb_array_elements(payload->'dsl'->'payrollItems') p 
         WHERE p->>'code' = 'RESIDENT_TAX'
       ) 
       THEN 'OK: payrollItems contains RESIDENT_TAX' 
       ELSE 'MISSING: payrollItems does not contain RESIDENT_TAX' 
  END as items_status
FROM payroll_policies 
WHERE company_code = 'JP01' 
  AND (payload->>'isActive')::boolean = true;

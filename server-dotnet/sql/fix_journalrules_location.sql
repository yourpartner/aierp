-- 将 journalRules 从 dsl 节点复制到根级别
UPDATE payroll_policies
SET payload = payload || jsonb_build_object('journalRules', payload->'dsl'->'journalRules')
WHERE company_code = 'JP01' 
  AND (payload->>'isActive')::boolean = true
  AND payload->'dsl'->'journalRules' IS NOT NULL
  AND payload->'journalRules' IS NULL;

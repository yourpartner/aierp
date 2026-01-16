-- Cleanup duplicate schema records
-- Keep only the latest record for each (company_code, name, version, is_active) combination

-- First, create a temp table with the IDs to keep
CREATE TEMP TABLE schemas_to_keep AS
SELECT DISTINCT ON (COALESCE(company_code, ''), name, version, is_active) id
FROM schemas
ORDER BY COALESCE(company_code, ''), name, version, is_active, created_at DESC;

-- Delete all records not in the keep list
DELETE FROM schemas 
WHERE id NOT IN (SELECT id FROM schemas_to_keep);

-- Show remaining count
SELECT name, COUNT(*) as cnt FROM schemas GROUP BY name ORDER BY name;

-- Drop temp table
DROP TABLE schemas_to_keep;


-- Fix historical voucher data: set baseLineNo for tax lines to link them to their base lines
-- Version 2: Simplified matching logic

-- Step 1: Check how many tax lines need fixing
SELECT COUNT(*) as tax_lines_without_baselineno 
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line->>'accountCode'
WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
  AND (line->>'baseLineNo' IS NULL OR line->>'baseLineNo' = '');

-- Step 2: Create temp table with tax lines info
DROP TABLE IF EXISTS temp_tax_lines;
CREATE TEMP TABLE temp_tax_lines AS
SELECT 
    v.id as voucher_id,
    v.company_code,
    (line_ord.ordinality - 1) as array_idx,
    (line.value->>'lineNo')::int as tax_line_no,
    line.value->>'drcr' as tax_drcr,
    COALESCE((line.value->>'amount')::numeric, 0) as tax_amount,
    COALESCE((line.value->>'taxRate')::numeric, 0) as tax_rate
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(value, ordinality)
CROSS JOIN LATERAL (SELECT line_ord.value) as line(value)
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line.value->>'accountCode'
WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
  AND (line.value->>'baseLineNo' IS NULL OR line.value->>'baseLineNo' = '');

-- Step 3: Create temp table with all non-tax lines
DROP TABLE IF EXISTS temp_base_lines;
CREATE TEMP TABLE temp_base_lines AS
SELECT 
    v.id as voucher_id,
    (line.value->>'lineNo')::int as base_line_no,
    line.value->>'drcr' as base_drcr,
    COALESCE((line.value->>'amount')::numeric, 0) as base_amount
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line(value)
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line.value->>'accountCode'
WHERE COALESCE(a.payload->>'taxType', '') != 'TAX_ACCOUNT';

-- Step 4: Find best matches based on same drcr direction and amount relationship
DROP TABLE IF EXISTS temp_matches;
CREATE TEMP TABLE temp_matches AS
SELECT DISTINCT ON (t.voucher_id, t.array_idx)
    t.voucher_id,
    t.array_idx,
    t.tax_line_no,
    b.base_line_no,
    t.tax_amount,
    b.base_amount,
    t.tax_drcr,
    b.base_drcr,
    -- Score: same direction + amount matches 10% rate
    CASE WHEN t.tax_drcr = b.base_drcr THEN 10 ELSE 0 END +
    CASE 
        WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.10, 0)) <= 1 THEN 30
        WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.08, 0)) <= 1 THEN 25
        WHEN t.tax_rate > 0 AND ABS(t.tax_amount - ROUND(b.base_amount * t.tax_rate / 100, 0)) <= 1 THEN 35
        ELSE 0 
    END as score
FROM temp_tax_lines t
JOIN temp_base_lines b ON t.voucher_id = b.voucher_id
WHERE t.tax_drcr = b.base_drcr  -- Same direction is required
ORDER BY t.voucher_id, t.array_idx, 
    CASE 
        WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.10, 0)) <= 1 THEN 1
        WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.08, 0)) <= 1 THEN 2
        ELSE 3
    END,
    b.base_line_no;  -- Prefer earlier lines if multiple matches

-- Step 5: Show what will be updated
SELECT 
    m.voucher_id,
    v.payload->'header'->>'voucherNo' as voucher_no,
    m.tax_line_no,
    m.base_line_no,
    m.tax_amount,
    m.base_amount,
    m.score
FROM temp_matches m
JOIN vouchers v ON v.id = m.voucher_id
WHERE m.score >= 10
ORDER BY voucher_no;

-- Step 6: Perform the update
DO $$
DECLARE
    rec RECORD;
    current_payload JSONB;
    updated_payload JSONB;
    update_count INT := 0;
BEGIN
    FOR rec IN 
        SELECT voucher_id, array_idx, base_line_no 
        FROM temp_matches 
        WHERE score >= 10
        ORDER BY voucher_id, array_idx DESC
    LOOP
        SELECT payload INTO current_payload FROM vouchers WHERE id = rec.voucher_id;
        
        -- Set baseLineNo
        updated_payload := jsonb_set(
            current_payload,
            ARRAY['lines', rec.array_idx::text, 'baseLineNo'],
            to_jsonb(rec.base_line_no)
        );
        
        -- Set isTaxLine flag
        updated_payload := jsonb_set(
            updated_payload,
            ARRAY['lines', rec.array_idx::text, 'isTaxLine'],
            'true'::jsonb
        );
        
        UPDATE vouchers SET payload = updated_payload, updated_at = NOW() WHERE id = rec.voucher_id;
        update_count := update_count + 1;
    END LOOP;
    
    RAISE NOTICE 'Total updated: % tax lines', update_count;
END $$;

-- Step 7: Verify results
SELECT COUNT(*) as remaining_without_baselineno 
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line->>'accountCode'
WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
  AND (line->>'baseLineNo' IS NULL OR line->>'baseLineNo' = '');

-- Cleanup
DROP TABLE IF EXISTS temp_tax_lines;
DROP TABLE IF EXISTS temp_base_lines;
DROP TABLE IF EXISTS temp_matches;


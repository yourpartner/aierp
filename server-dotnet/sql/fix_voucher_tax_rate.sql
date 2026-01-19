-- 修复历史凭证数据：为消费税明细行推断并设置 taxRate
-- 推断逻辑：
-- 1. 如果税行有 baseLineNo，找到对应的税基明细行，用 税额/税基金额 计算税率
-- 2. 如果计算结果接近 10% 或 8%（日本标准税率/轻减税率），设置对应税率
-- 3. 如果无法关联到税基明细，根据税额与同凭证其他行的金额关系推断

-- Step 1: 查看需要修复的税行数量
SELECT 
    COUNT(*) as need_fix_count,
    COUNT(CASE WHEN line->>'baseLineNo' IS NOT NULL THEN 1 END) as has_base_line_no
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
WHERE a.payload->>'taxType' IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
  AND (line_ord.line->>'taxRate' IS NULL OR line_ord.line->>'taxRate' = '');

-- Step 2: 预览推断结果（不执行更新）
WITH tax_lines AS (
    SELECT 
        v.id as voucher_id,
        v.company_code,
        v.voucher_no,
        line_ord.idx - 1 as array_idx,
        (line_ord.line->>'lineNo')::int as line_no,
        line_ord.line->>'accountCode' as account_code,
        line_ord.line->>'drcr' as drcr,
        COALESCE((line_ord.line->>'amount')::numeric, 0) as tax_amount,
        line_ord.line->>'baseLineNo' as base_line_no_str,
        a.payload->>'name' as account_name
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
    JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
    WHERE a.payload->>'taxType' IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
      AND (line_ord.line->>'taxRate' IS NULL OR line_ord.line->>'taxRate' = '')
),
base_lines AS (
    SELECT 
        v.id as voucher_id,
        (line_ord.line->>'lineNo')::int as line_no,
        line_ord.line->>'drcr' as drcr,
        COALESCE((line_ord.line->>'amount')::numeric, 0) as base_amount
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
    LEFT JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
    WHERE COALESCE(a.payload->>'taxType', '') NOT IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
      AND COALESCE((line_ord.line->>'isTaxLine')::boolean, false) = false
),
tax_with_base AS (
    SELECT 
        t.*,
        b.base_amount,
        CASE 
            WHEN b.base_amount > 0 THEN ROUND(t.tax_amount * 100.0 / b.base_amount, 1)
            ELSE NULL
        END as calculated_rate
    FROM tax_lines t
    LEFT JOIN base_lines b ON b.voucher_id = t.voucher_id 
        AND b.line_no::text = t.base_line_no_str
        AND b.drcr = t.drcr
),
inferred_rates AS (
    SELECT 
        *,
        CASE 
            -- 如果计算出的税率在 9.5-10.5% 范围内，认为是 10%
            WHEN calculated_rate BETWEEN 9.5 AND 10.5 THEN 10
            -- 如果计算出的税率在 7.5-8.5% 范围内，认为是 8%
            WHEN calculated_rate BETWEEN 7.5 AND 8.5 THEN 8
            -- 如果计算出的税率在 0-0.5% 范围内，认为是 0%
            WHEN calculated_rate BETWEEN 0 AND 0.5 THEN 0
            -- 如果没有关联到税基明细，但税额为正，默认 10%
            WHEN calculated_rate IS NULL AND tax_amount > 0 THEN 10
            -- 其他情况默认 10%
            ELSE 10
        END as inferred_rate
    FROM tax_with_base
)
SELECT 
    voucher_no,
    account_name,
    tax_amount,
    base_amount,
    calculated_rate,
    inferred_rate
FROM inferred_rates
ORDER BY voucher_id
LIMIT 50;

-- Step 3: 执行更新（将 taxRate 写入凭证）
DO $$
DECLARE
    rec RECORD;
    current_payload JSONB;
    updated_payload JSONB;
    update_count INT := 0;
BEGIN
    FOR rec IN
        WITH tax_lines AS (
            SELECT 
                v.id as voucher_id,
                v.company_code,
                line_ord.idx - 1 as array_idx,
                (line_ord.line->>'lineNo')::int as line_no,
                line_ord.line->>'drcr' as drcr,
                COALESCE((line_ord.line->>'amount')::numeric, 0) as tax_amount,
                line_ord.line->>'baseLineNo' as base_line_no_str
            FROM vouchers v
            CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
            JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
            WHERE a.payload->>'taxType' IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
              AND (line_ord.line->>'taxRate' IS NULL OR line_ord.line->>'taxRate' = '')
        ),
        base_lines AS (
            SELECT 
                v.id as voucher_id,
                (line_ord.line->>'lineNo')::int as line_no,
                line_ord.line->>'drcr' as drcr,
                COALESCE((line_ord.line->>'amount')::numeric, 0) as base_amount
            FROM vouchers v
            CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
            LEFT JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
            WHERE COALESCE(a.payload->>'taxType', '') NOT IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
              AND COALESCE((line_ord.line->>'isTaxLine')::boolean, false) = false
        ),
        tax_with_base AS (
            SELECT 
                t.*,
                b.base_amount,
                CASE 
                    WHEN b.base_amount > 0 THEN ROUND(t.tax_amount * 100.0 / b.base_amount, 1)
                    ELSE NULL
                END as calculated_rate
            FROM tax_lines t
            LEFT JOIN base_lines b ON b.voucher_id = t.voucher_id 
                AND b.line_no::text = t.base_line_no_str
                AND b.drcr = t.drcr
        )
        SELECT 
            voucher_id,
            array_idx,
            CASE 
                WHEN calculated_rate BETWEEN 9.5 AND 10.5 THEN 10
                WHEN calculated_rate BETWEEN 7.5 AND 8.5 THEN 8
                WHEN calculated_rate BETWEEN 0 AND 0.5 THEN 0
                WHEN calculated_rate IS NULL AND tax_amount > 0 THEN 10
                ELSE 10
            END as inferred_rate
        FROM tax_with_base
    LOOP
        -- 获取当前 payload
        SELECT payload INTO current_payload FROM vouchers WHERE id = rec.voucher_id;
        
        -- 设置 taxRate
        updated_payload := jsonb_set(
            current_payload,
            ARRAY['lines', rec.array_idx::text, 'taxRate'],
            to_jsonb(rec.inferred_rate)
        );
        
        -- 更新凭证
        UPDATE vouchers SET payload = updated_payload, updated_at = NOW() WHERE id = rec.voucher_id;
        update_count := update_count + 1;
    END LOOP;
    
    RAISE NOTICE 'Updated % voucher tax lines with inferred taxRate', update_count;
END $$;

-- Step 4: 验证修复结果
SELECT 
    COUNT(*) as remaining_without_tax_rate
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line_ord(line, idx)
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line_ord.line->>'accountCode'
WHERE a.payload->>'taxType' IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
  AND (line_ord.line->>'taxRate' IS NULL OR line_ord.line->>'taxRate' = '');

-- 查看修复后的税率分布
SELECT 
    (line->>'taxRate')::int as tax_rate,
    COUNT(*) as count
FROM vouchers v
CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line->>'accountCode'
WHERE a.payload->>'taxType' IN ('TAX_ACCOUNT', 'INPUT_TAX', 'OUTPUT_TAX')
GROUP BY (line->>'taxRate')::int
ORDER BY tax_rate;


-- 修复历史凭证数据：为消费税明细行设置 baseLineNo，建立与税基明细的关联
-- 这个脚本会找到所有没有 baseLineNo 的消费税明细行，并尝试推断对应的税基明细

-- 首先查看需要修复的数据量
WITH tax_lines_without_base AS (
    SELECT 
        v.id as voucher_id,
        v.payload->'header'->>'voucherNo' as voucher_no,
        line_idx,
        line.value as tax_line
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line(value, line_idx)
    JOIN accounts a ON a.company_code = v.company_code 
                   AND a.account_code = line.value->>'accountCode'
    WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
      AND (line.value->>'baseLineNo' IS NULL OR line.value->>'baseLineNo' = '')
)
SELECT COUNT(*) as need_fix_count FROM tax_lines_without_base;

-- 修复逻辑：
-- 1. 对于每个没有 baseLineNo 的消费税明细行
-- 2. 找到同一凭证中同一借贷方向的税基明细行（非消费税科目）
-- 3. 如果只有一个税基明细，直接关联
-- 4. 如果有多个税基明细，按税率和金额关系推断

-- 创建临时表存储需要更新的凭证
CREATE TEMP TABLE vouchers_to_fix AS
WITH tax_lines AS (
    SELECT 
        v.id as voucher_id,
        v.company_code,
        line_idx - 1 as array_idx,  -- JSON 数组索引从 0 开始
        line.value->>'lineNo' as tax_line_no,
        line.value->>'drcr' as tax_drcr,
        COALESCE((line.value->>'amount')::numeric, 0) as tax_amount,
        COALESCE((line.value->>'taxRate')::numeric, 0) as tax_rate
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line(value, line_idx)
    JOIN accounts a ON a.company_code = v.company_code 
                   AND a.account_code = line.value->>'accountCode'
    WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
      AND (line.value->>'baseLineNo' IS NULL OR line.value->>'baseLineNo' = '')
),
base_lines AS (
    -- 选择同一凭证中非消费税科目的明细行作为潜在的税基明细
    SELECT 
        v.id as voucher_id,
        v.company_code,
        line.value->>'lineNo' as base_line_no,
        line.value->>'drcr' as base_drcr,
        COALESCE((line.value->>'amount')::numeric, 0) as base_amount,
        COALESCE((line.value->>'taxRate')::numeric, 0) as base_tax_rate
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line(value, line_idx)
    JOIN accounts a ON a.company_code = v.company_code 
                   AND a.account_code = line.value->>'accountCode'
    WHERE COALESCE(a.payload->>'taxType', '') != 'TAX_ACCOUNT'
      -- 排除银行/现金等资产负债类科目，只保留可能产生消费税的费用/收入/应收应付科目
      AND COALESCE(a.payload->>'isBank', 'false') != 'true'
      AND COALESCE(a.payload->>'isCash', 'false') != 'true'
),
matches AS (
    SELECT 
        t.voucher_id,
        t.array_idx,
        t.tax_line_no,
        b.base_line_no,
        -- 计算匹配度评分
        CASE 
            -- 借贷方向相同 +10 分
            WHEN t.tax_drcr = b.base_drcr THEN 10
            ELSE 0
        END +
        CASE 
            -- 税率匹配（税基明细有税率，且与消费税明细税率相同）+5 分
            WHEN t.tax_rate > 0 AND b.base_tax_rate > 0 AND t.tax_rate = b.base_tax_rate THEN 5
            ELSE 0
        END +
        CASE 
            -- 金额关系匹配（税额 = 税基 × 税率，允许 1 円误差）+20 分
            WHEN t.tax_rate > 0 AND ABS(t.tax_amount - ROUND(b.base_amount * t.tax_rate / 100, 0)) <= 1 THEN 20
            WHEN t.tax_rate = 0 AND b.base_tax_rate > 0 AND ABS(t.tax_amount - ROUND(b.base_amount * b.base_tax_rate / 100, 0)) <= 1 THEN 20
            -- 10% 税率推断
            WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.1, 0)) <= 1 THEN 15
            -- 8% 税率推断
            WHEN ABS(t.tax_amount - ROUND(b.base_amount * 0.08, 0)) <= 1 THEN 15
            ELSE 0
        END as score
    FROM tax_lines t
    JOIN base_lines b ON t.voucher_id = b.voucher_id
),
best_matches AS (
    SELECT DISTINCT ON (voucher_id, array_idx)
        voucher_id,
        array_idx,
        tax_line_no,
        base_line_no,
        score
    FROM matches
    WHERE score >= 10  -- 至少方向匹配或金额关系匹配
    ORDER BY voucher_id, array_idx, score DESC
)
SELECT * FROM best_matches;

-- 查看将要修复的数据
SELECT 
    bm.voucher_id,
    v.payload->'header'->>'voucherNo' as voucher_no,
    bm.tax_line_no,
    bm.base_line_no,
    bm.score
FROM vouchers_to_fix bm
JOIN vouchers v ON v.id = bm.voucher_id
ORDER BY voucher_no;

-- 执行修复（如果确认数据正确，取消注释以下语句）
-- UPDATE vouchers v
-- SET payload = (
--     SELECT jsonb_set(
--         v.payload,
--         ARRAY['lines', f.array_idx::text, 'baseLineNo'],
--         to_jsonb(f.base_line_no)
--     )
--     FROM vouchers_to_fix f
--     WHERE f.voucher_id = v.id
-- )
-- WHERE id IN (SELECT voucher_id FROM vouchers_to_fix);

-- 正式修复：逐条更新（更安全的方式）
DO $$
DECLARE
    rec RECORD;
    current_payload JSONB;
    updated_payload JSONB;
    update_count INT := 0;
BEGIN
    FOR rec IN 
        SELECT voucher_id, array_idx, base_line_no 
        FROM vouchers_to_fix 
        ORDER BY voucher_id, array_idx DESC  -- 从后往前更新，避免索引变化
    LOOP
        -- 获取当前 payload
        SELECT payload INTO current_payload FROM vouchers WHERE id = rec.voucher_id;
        
        -- 设置 baseLineNo
        updated_payload := jsonb_set(
            current_payload,
            ARRAY['lines', rec.array_idx::text, 'baseLineNo'],
            to_jsonb(rec.base_line_no::int)
        );
        
        -- 同时设置 isTaxLine 标记
        updated_payload := jsonb_set(
            updated_payload,
            ARRAY['lines', rec.array_idx::text, 'isTaxLine'],
            'true'::jsonb
        );
        
        -- 更新凭证
        UPDATE vouchers SET payload = updated_payload, updated_at = NOW() WHERE id = rec.voucher_id;
        update_count := update_count + 1;
        
        RAISE NOTICE 'Updated voucher % line index % with baseLineNo %', rec.voucher_id, rec.array_idx, rec.base_line_no;
    END LOOP;
    
    RAISE NOTICE 'Total updated: % tax lines', update_count;
END $$;

-- 验证修复结果
WITH tax_lines_without_base AS (
    SELECT 
        v.id as voucher_id,
        v.payload->'header'->>'voucherNo' as voucher_no
    FROM vouchers v
    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') WITH ORDINALITY as line(value, line_idx)
    JOIN accounts a ON a.company_code = v.company_code 
                   AND a.account_code = line.value->>'accountCode'
    WHERE a.payload->>'taxType' = 'TAX_ACCOUNT'
      AND (line.value->>'baseLineNo' IS NULL OR line.value->>'baseLineNo' = '')
)
SELECT COUNT(*) as remaining_without_baselineno FROM tax_lines_without_base;

-- 清理临时表
DROP TABLE IF EXISTS vouchers_to_fix;



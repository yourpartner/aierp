-- Test: find existing voucher by bank account sum amount
SELECT id, payload->'header'->>'voucherNo' AS voucher_no,
       (payload->'header'->>'postingDate')::date = '2025-11-05'::date AS is_exact_match,
       (
         SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
         FROM jsonb_array_elements(payload->'lines') AS line
         WHERE line->>'accountCode' = '131'
           AND line->>'drcr' = 'CR'
       ) as bank_sum
FROM vouchers
WHERE company_code = 'JP01'
  AND (payload->'header'->>'postingDate')::date BETWEEN ('2025-11-05'::date - 5) AND ('2025-11-05'::date + 5)
  AND (
    SELECT COALESCE(SUM((line->>'amount')::numeric), 0)
    FROM jsonb_array_elements(payload->'lines') AS line
    WHERE line->>'accountCode' = '131'
      AND line->>'drcr' = 'CR'
  ) = 559168
  AND NOT EXISTS (
    SELECT 1 FROM moneytree_transactions mt
    WHERE mt.voucher_id = vouchers.id
      AND mt.company_code = vouchers.company_code
  )
ORDER BY ABS((payload->'header'->>'postingDate')::date - '2025-11-05'::date), created_at DESC
LIMIT 5;


-- 查看凭证 2512000003 的内容
SELECT id, voucher_no, voucher_date, 
       payload->'header'->>'summary' as summary,
       payload->'header'->>'voucherType' as voucher_type,
       jsonb_array_length(payload->'lines') as line_count
FROM vouchers
WHERE company_code = 'JP01'
  AND voucher_no = '2512000003';

-- 查看凭证行明细
SELECT v.voucher_no, 
       line->>'accountCode' as account_code,
       line->>'drcr' as drcr,
       line->>'amount' as amount,
       line->>'note' as note,
       line->>'vendorId' as vendor_id
FROM vouchers v,
     jsonb_array_elements(v.payload->'lines') as line
WHERE v.company_code = 'JP01'
  AND v.voucher_no = '2512000003';


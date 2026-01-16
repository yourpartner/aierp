-- ============================================
-- 从旧系统 CompanyID=172 导入取引先到 JP01
-- 跳过已存在的取引先（按名称匹配）
-- ============================================

-- 支付条件映射表 (旧系统 PaymentTermID -> 新系统 paymentTerms)
-- 986 -> 月末締翌々月5日:   cutOffDay=31, paymentMonth=2, paymentDay=5
-- 987 -> 月末締翌々月20日:  cutOffDay=31, paymentMonth=2, paymentDay=20
-- 988 -> 月末締翌月25日:    cutOffDay=31, paymentMonth=1, paymentDay=25
-- 989 -> 月末締翌月末日:    cutOffDay=31, paymentMonth=1, paymentDay=31
-- 990 -> 月末締翌々月末日:  cutOffDay=31, paymentMonth=2, paymentDay=31
-- 991 -> 月末締翌々月25日:  cutOffDay=31, paymentMonth=2, paymentDay=25
-- 992 -> 月末締翌々月15日:  cutOffDay=31, paymentMonth=2, paymentDay=15
-- 993 -> 月末締翌々月10日:  cutOffDay=31, paymentMonth=2, paymentDay=10

-- 1. コムチュア株式会社 (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'コムチュア株式会社',
  'nameKana', 'コムチュア',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%コムチュア%');

-- 2. グロースポイント株式会社 (PaymentTermID=990 -> 月末締翌々月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'グロースポイント株式会社',
  'nameKana', 'グロースポイント',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 31, 'description', '月末締翌々月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%グロースポイント%');

-- 3. リアルテックジャパン株式会社 (PaymentTermID=987 -> 月末締翌々月20日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'リアルテックジャパン株式会社',
  'nameKana', 'リアルテック',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'address', jsonb_build_object('postalCode', '101-0065', 'address', '東京都千代田区西神田3-8-1 千代田ファーストビル東館7階'),
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 20, 'description', '月末締翌々月20日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%リアルテック%');

-- 4. 株式会社ユアパートナー (PaymentTermID=993 -> 月末締翌々月10日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社ユアパートナー',
  'nameKana', 'ユアパートナー',
  'flags', jsonb_build_object('customer', true, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 10, 'description', '月末締翌々月10日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%ユアパートナー%');

-- 5. 東弘貿易株式会社 (PaymentTermID=991 -> 月末締翌々月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '東弘貿易株式会社',
  'nameKana', '東弘貿易',
  'flags', jsonb_build_object('customer', true, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 25, 'description', '月末締翌々月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%東弘貿易%');

-- 6. 株式会社はばたーく (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社はばたーく',
  'nameKana', 'はばたーく',
  'flags', jsonb_build_object('customer', true, 'vendor', true),
  'status', 'active',
  'address', jsonb_build_object('postalCode', '103-0023', 'address', '東京都中央区日本橋本町4-8-15 ネオカワイビル5F'),
  'contact', jsonb_build_object('contactPerson', '和田　裕夢'),
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%はばたーく%');

-- 7. 星辰ソフト株式会社 (PaymentTermID=993 -> 月末締翌々月10日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '星辰ソフト株式会社',
  'nameKana', '星辰',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 10, 'description', '月末締翌々月10日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%星辰%');

-- 8. 叡聿科技（大連）有限会社 (PaymentTermID=990 -> 月末締翌々月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '叡聿科技（大連）有限会社',
  'nameKana', '叡聿科技',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 31, 'description', '月末締翌々月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%叡聿%');

-- 9. 株式会社RTS - 已存在，跳过

-- 10. 日本タタ・コンサルタンシー・サービシズ（株） (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '日本タタ・コンサルタンシー・サービシズ株式会社',
  'nameKana', '日本TCS',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%タタ%');

-- 11. 輝聿株式会社 (PaymentTermID=992 -> 月末締翌々月15日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '輝聿株式会社',
  'nameKana', '輝聿',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 15, 'description', '月末締翌々月15日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%輝聿%');

-- 12. 株式会社晴海コンサルティング (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社晴海コンサルティング',
  'nameKana', 'ハルミ',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'address', jsonb_build_object('postalCode', '104-0053', 'address', '東京都中央区晴海2-3-2-4516'),
  'bankInfo', jsonb_build_object('bankCode', '0310', 'branchCode', '101', 'accountNo', '1738233', 'accountHolder', 'ﾊﾙﾐｺﾝｻﾙﾃｨｸﾞ(ｶ'),
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%晴海%');

-- 13. Dalian YiPin Business Consulting Co.Ltd. (PaymentTermID=990 -> 月末締翌々月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'Dalian YiPin Business Consulting Co.Ltd.',
  'nameKana', 'DLYiPin',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 31, 'description', '月末締翌々月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%YiPin%');

-- 14. 株式会社ピー・エフ・シー (PaymentTermID=987 -> 月末締翌々月20日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社ピー・エフ・シー',
  'nameKana', 'PFC',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 20, 'description', '月末締翌々月20日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%ピー・エフ・シー%');

-- 15. タカイジャパン株式会社 - 已存在，跳过

-- 16. ベース株式会社 - 已存在，跳过

-- 17. DX Leverager株式会社 - 已存在，跳过

-- 18. 株式会社SuccessConsulting (PaymentTermID=990 -> 月末締翌々月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社SuccessConsulting',
  'nameKana', 'ｻｸｾｽｺﾝｻﾙﾃｨﾝｸﾞ',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 31, 'description', '月末締翌々月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%SuccessConsulting%');

-- 19. UTO株式会社 (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'UTO株式会社',
  'nameKana', 'UTO',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%UTO%');

-- 20. 株式会社東来 (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社東来',
  'nameKana', '東来',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%東来%');

-- 21. 株式会社川島商事 (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社川島商事',
  'nameKana', '川島商事',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%川島商事%');

-- 22. 株式会社言毅 (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社言毅',
  'nameKana', '言毅',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%言毅%');

-- 23. 株式会社新誠 (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社新誠',
  'nameKana', '新誠',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%新誠%');

-- 24. 株式会社アトムテック (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社アトムテック',
  'nameKana', 'アトムテック',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%アトムテック%');

-- 25. UNIFIRE RESOURCES株式会社 - 已存在，跳过

-- 26. BDT株式会社 (PaymentTermID=992 -> 月末締翌々月15日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'BDT株式会社',
  'nameKana', 'BDT',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 2, 'paymentDay', 15, 'description', '月末締翌々月15日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%BDT%');

-- 27. 華栄株式会社 (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '華栄株式会社',
  'nameKana', '華栄',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%華栄%');

-- 28. 株式会社日藝 (PaymentTermID=988 -> 月末締翌月25日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', '株式会社日藝',
  'nameKana', '日藝',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 25, 'description', '月末締翌月25日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%日藝%');

-- 29. イメガ株式会社 (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'イメガ株式会社',
  'nameKana', 'イメガ',
  'flags', jsonb_build_object('customer', true, 'vendor', false),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%イメガ%');

-- 30. Total Solution 株式会社 (PaymentTermID=989 -> 月末締翌月末日)
INSERT INTO businesspartners (company_code, payload)
SELECT 'JP01', jsonb_build_object(
  'code', (SELECT 'BP' || LPAD((COALESCE(MAX(SUBSTRING(partner_code FROM 3)::int), 0) + 1)::text, 6, '0') FROM businesspartners WHERE company_code = 'JP01'),
  'name', 'Total Solution株式会社',
  'nameKana', 'Total Solution',
  'flags', jsonb_build_object('customer', false, 'vendor', true),
  'status', 'active',
  'paymentTerms', jsonb_build_object('cutOffDay', 31, 'paymentMonth', 1, 'paymentDay', 31, 'description', '月末締翌月末日')
)
WHERE NOT EXISTS (SELECT 1 FROM businesspartners WHERE company_code = 'JP01' AND name ILIKE '%Total Solution%');


-- 修复员工姓名顺序（从 "名 姓" 改为 "姓 名"）
-- 漢字姓名
UPDATE employees
SET payload = jsonb_set(
    payload,
    '{nameKanji}',
    to_jsonb(
        CASE 
            WHEN payload->>'nameKanji' LIKE '% %' THEN
                split_part(payload->>'nameKanji', ' ', 2) || ' ' || split_part(payload->>'nameKanji', ' ', 1)
            ELSE payload->>'nameKanji'
        END
    )
)
WHERE company_code = 'JP01' 
  AND payload->>'nameKanji' LIKE '% %'
  AND payload->>'nameKanji' NOT LIKE '%BOWALGAHA%'; -- 跳过英文名

-- カナ姓名
UPDATE employees
SET payload = jsonb_set(
    payload,
    '{nameKana}',
    to_jsonb(
        CASE 
            WHEN payload->>'nameKana' LIKE '% %' THEN
                split_part(payload->>'nameKana', ' ', 2) || ' ' || split_part(payload->>'nameKana', ' ', 1)
            ELSE payload->>'nameKana'
        END
    )
)
WHERE company_code = 'JP01' 
  AND payload->>'nameKana' LIKE '% %'
  AND payload->>'nameKana' NOT LIKE '%ボウワル%'; -- 跳过英文名

-- 确认结果
SELECT payload->>'nameKanji' as name_kanji, payload->>'nameKana' as name_kana 
FROM employees 
WHERE company_code = 'JP01' 
LIMIT 10;


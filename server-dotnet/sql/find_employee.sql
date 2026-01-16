-- 查找何 博碩员工
SELECT id, employee_code, payload->>'nameKanji' as name_kanji, payload->>'nameKana' as name_kana
FROM employees 
WHERE company_code = 'JP01'
ORDER BY employee_code;


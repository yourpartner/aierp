-- 为常用科目添加别名，便于Agent按名称查找

-- 111 現金
UPDATE accounts SET payload = payload || '{"aliases": ["現金", "キャッシュ", "cash", "手元現金"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '111';

-- 152 売掛金
UPDATE accounts SET payload = payload || '{"aliases": ["売掛金", "売掛", "AR", "accounts receivable", "得意先勘定"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '152';

-- 189 仮払消費税
UPDATE accounts SET payload = payload || '{"aliases": ["仮払消費税", "仮払税", "input tax", "進項税", "仕入消費税"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '189';

-- 312 買掛金
UPDATE accounts SET payload = payload || '{"aliases": ["買掛金", "買掛", "AP", "accounts payable", "仕入先勘定"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '312';

-- 314 未払金
UPDATE accounts SET payload = payload || '{"aliases": ["未払金", "未払", "accrued payable", "应付款"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '314';

-- 315 未払費用
UPDATE accounts SET payload = payload || '{"aliases": ["未払費用", "未払経費", "accrued expense", "应计费用"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '315';

-- 324 仮受消費税
UPDATE accounts SET payload = payload || '{"aliases": ["仮受消費税", "仮受税", "output tax", "銷項税", "売上消費税"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '324';

-- 832 給与手当
UPDATE accounts SET payload = payload || '{"aliases": ["給与手当", "給与", "給料", "人件費", "salary", "工资"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '832';

-- 842 旅費交通費
UPDATE accounts SET payload = payload || '{"aliases": ["旅費交通費", "交通費", "電車代", "タクシー代", "出張旅費", "travel", "transportation"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '842';

-- 843 通信費
UPDATE accounts SET payload = payload || '{"aliases": ["通信費", "電話代", "インターネット代", "携帯代", "通信料", "communication"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '843';

-- 844 交際費
UPDATE accounts SET payload = payload || '{"aliases": ["交際費", "接待費", "飲食費", "entertainment", "交际费"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '844';

-- 852 消耗品費
UPDATE accounts SET payload = payload || '{"aliases": ["消耗品費", "消耗品", "事務用品費", "supplies", "办公用品"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '852';

-- 858 支払手数料
UPDATE accounts SET payload = payload || '{"aliases": ["支払手数料", "手数料", "振込手数料", "bank fee", "银行手续费"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '858';

-- 861 新聞図書費
UPDATE accounts SET payload = payload || '{"aliases": ["新聞図書費", "図書費", "書籍代", "新聞代", "books", "图书费"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '861';

-- 869 雑費
UPDATE accounts SET payload = payload || '{"aliases": ["雑費", "その他経費", "miscellaneous", "杂费"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '869';

-- 877 会議費
UPDATE accounts SET payload = payload || '{"aliases": ["会議費", "ミーティング費", "打ち合わせ", "meeting", "会议费"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '877';

-- 914 雑収入
UPDATE accounts SET payload = payload || '{"aliases": ["雑収入", "その他収入", "miscellaneous income", "杂收入"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '914';

-- 924 雑損失
UPDATE accounts SET payload = payload || '{"aliases": ["雑損失", "その他損失", "miscellaneous loss", "杂损失"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '924';

-- 3181 社会保険預り金
UPDATE accounts SET payload = payload || '{"aliases": ["社会保険預り金", "社保預り", "健康保険預り金", "社会保険"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '3181';

-- 3182 厚生年金預り金
UPDATE accounts SET payload = payload || '{"aliases": ["厚生年金預り金", "年金預り", "厚生年金"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '3182';

-- 3183 雇用保険預り金
UPDATE accounts SET payload = payload || '{"aliases": ["雇用保険預り金", "雇保預り", "雇用保険"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '3183';

-- 3184 源泉所得税預り金
UPDATE accounts SET payload = payload || '{"aliases": ["源泉所得税預り金", "源泉預り", "源泉税", "所得税預り"]}'::jsonb
WHERE company_code = 'JP01' AND account_code = '3184';


-- 更新会计科目的财务报表分组 (fsBalanceGroup / fsProfitGroup)
-- 运行方式: psql -h localhost -U postgres -d postgres -f update_account_fs_groups.sql

-- ============================================
-- BS 科目 - 资产类
-- ============================================

-- 現金及び預金 (BS-A-1-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-1-1"')
WHERE company_code = 'JP01' AND account_code IN ('111', '131', '132', '133', '141');

-- 受取手形及び売掛金 (BS-A-1-2)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-1-2"')
WHERE company_code = 'JP01' AND account_code IN ('152', '162', '185');

-- 商品・貯蔵品 (BS-A-1-3)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-1-3"')
WHERE company_code = 'JP01' AND account_code IN ('177');

-- その他流動資産 (BS-A-1 流動資産)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-1"')
WHERE company_code = 'JP01' AND account_code IN ('181', '182', '183', '184', '189', '191', '001');

-- 有形固定資産 - 建物及び構築物 (BS-A-2-1-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-2-1-1"')
WHERE company_code = 'JP01' AND account_code IN ('211', '212', '221', '222');

-- 有形固定資産 - 機械装置・車両・備品 (BS-A-2-1-2)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-2-1-2"')
WHERE company_code = 'JP01' AND account_code IN ('215', '216');

-- 無形固定資産・投資等 (BS-A-2 固定資産)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-2"')
WHERE company_code = 'JP01' AND account_code IN ('265', '267', '272', '274', '275', '276', '279', '291');

-- ============================================
-- BS 科目 - 負債類
-- ============================================

-- 支払手形及び買掛金 (BS-L-1-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-L-1-1"')
WHERE company_code = 'JP01' AND account_code IN ('312');

-- 短期借入金 (BS-L-1-2)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-L-1-2"')
WHERE company_code = 'JP01' AND account_code IN ('313', '323');

-- その他流動負債 (BS-L-1 流動負債)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-L-1"')
WHERE company_code = 'JP01' AND account_code IN ('314', '315', '316', '317', '318', '3181', '3182', '3183', '3184', '319', '324', '333');

-- 長期借入金 (BS-L-2-2)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-L-2-2"')
WHERE company_code = 'JP01' AND account_code IN ('361', '362');

-- その他固定負債 (BS-L-2 固定負債)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-L-2"')
WHERE company_code = 'JP01' AND account_code IN ('364');

-- ============================================
-- BS 科目 - 純資産類
-- ============================================

-- 資本金 (BS-N-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-N-1"')
WHERE company_code = 'JP01' AND account_code IN ('411');

-- 利益剰余金 (BS-N-3-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-N-3-1"')
WHERE company_code = 'JP01' AND account_code IN ('451');

-- データ移行用 (BS-A-1 流動資産)
UPDATE accounts SET payload = jsonb_set(payload, '{fsBalanceGroup}', '"BS-A-1"')
WHERE company_code = 'JP01' AND account_code IN ('999');

-- ============================================
-- PL 科目
-- ============================================

-- 売上高 (PL-1)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-1"')
WHERE company_code = 'JP01' AND account_code IN ('612', '614');

-- 売上原価 (PL-2)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-2"')
WHERE company_code = 'JP01' AND account_code IN ('711', '713', '714');

-- 販売費及び一般管理費 (PL-3)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-3"')
WHERE company_code = 'JP01' AND account_code IN (
    '831', '832', '834', '836', '837', 
    '841', '842', '843', '844', '845', '846', '847', '849',
    '852', '853', '854', '856', '857', '858', '859',
    '861', '862', '863', '864', '865', '868', '869',
    '873', '877', '878', '879'
);

-- 営業外収益 (PL-4)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-4"')
WHERE company_code = 'JP01' AND account_code IN ('911', '912', '914', '933');

-- 営業外費用 (PL-5)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-5"')
WHERE company_code = 'JP01' AND account_code IN ('921', '924');

-- 特別損失 (PL-7)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-7"')
WHERE company_code = 'JP01' AND account_code IN ('942');

-- 法人税等 (PL-8)
UPDATE accounts SET payload = jsonb_set(payload, '{fsProfitGroup}', '"PL-8"')
WHERE company_code = 'JP01' AND account_code IN ('951');

-- 完了確認
SELECT account_code, payload->>'name' as name, payload->>'category' as category, 
       payload->>'fsBalanceGroup' as bs_group, payload->>'fsProfitGroup' as pl_group
FROM accounts 
WHERE company_code = 'JP01' 
ORDER BY account_code;


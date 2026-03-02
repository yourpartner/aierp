-- 将 roles 表中的中文角色名・描述改为日语（系统预置角色 company_code IS NULL）
-- Run against your DB; safe to re-run (idempotent).

UPDATE roles SET role_name = 'システム管理者', description = '全ての権限（システム設定・ユーザー管理を含む）'
WHERE company_code IS NULL AND role_code = 'SYS_ADMIN';

UPDATE roles SET role_name = '上級会計', description = '日常の会計業務、仕訳の作成・編集・転記、レポート参照、銀行操作が可能'
WHERE company_code IS NULL AND role_code = 'ACCOUNTANT_SENIOR';

UPDATE roles SET role_name = '初級会計', description = '仕訳の作成・編集は可能。転記・銀行操作は不可'
WHERE company_code IS NULL AND role_code = 'ACCOUNTANT_JUNIOR';

UPDATE roles SET role_name = '人事マネージャー', description = '人事の全権限、給与情報の参照・給与計算の実行が可能'
WHERE company_code IS NULL AND role_code = 'HR_MANAGER';

UPDATE roles SET role_name = '人事担当', description = '人事の基本権限、従業員情報の管理が可能。給与詳細は参照不可'
WHERE company_code IS NULL AND role_code = 'HR_STAFF';

UPDATE roles SET role_name = '販売マネージャー', description = '販売・CRMの全権限、販売分析の参照が可能'
WHERE company_code IS NULL AND role_code = 'SALES_MANAGER';

UPDATE roles SET role_name = '販売担当', description = '販売の基本権限、自担当の顧客・受注の管理が可能'
WHERE company_code IS NULL AND role_code = 'SALES_STAFF';

UPDATE roles SET role_name = '倉庫責任者', description = '在庫の全権限、在庫調整が可能'
WHERE company_code IS NULL AND role_code = 'WAREHOUSE_MANAGER';

UPDATE roles SET role_name = '倉庫担当', description = '在庫の基本権限、入出庫操作が可能'
WHERE company_code IS NULL AND role_code = 'WAREHOUSE_STAFF';

UPDATE roles SET role_name = '税理士', description = '外部税理士。財務データ・レポートの参照のみ'
WHERE company_code IS NULL AND role_code = 'TAX_ACCOUNTANT';

UPDATE roles SET role_name = '監査担当', description = '外部監査。全財務データの参照のみ'
WHERE company_code IS NULL AND role_code = 'AUDITOR';

UPDATE roles SET role_name = '閲覧専用ユーザー', description = '基本情報の参照のみ。操作は不可'
WHERE company_code IS NULL AND role_code = 'VIEWER';

-- 公司别角色：若 role_name 为常见中文则改为日语（不影响英文 ADMIN 等）
UPDATE roles SET role_name = '管理者' WHERE company_code IS NOT NULL AND role_code = 'ADMIN' AND role_name = '系统管理员';
UPDATE roles SET role_name = '管理者' WHERE company_code IS NOT NULL AND role_code = 'ADMIN' AND role_name = '管理员';
UPDATE roles SET role_name = '会計担当' WHERE company_code IS NOT NULL AND role_name = '会计';
UPDATE roles SET role_name = '人事担当' WHERE company_code IS NOT NULL AND role_name = '人事专员';
UPDATE roles SET role_name = '販売担当' WHERE company_code IS NOT NULL AND role_name = '销售人员';
UPDATE roles SET role_name = '閲覧専用' WHERE company_code IS NOT NULL AND role_name = '只读用户';

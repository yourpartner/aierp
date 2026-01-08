# 人才派遣版需求设计 V2

## 一、业务模式分析

### 1.1 两种主要契约形态

```
┌─────────────────────────────────────────────────────────────────────┐
│                        人材ビジネス契約形態                           │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌─────────────────────┐         ┌─────────────────────┐            │
│  │    業務委託契約      │         │     派遣契約        │            │
│  │   (請負/準委任)      │         │  (労働者派遣)       │            │
│  ├─────────────────────┤         ├─────────────────────┤            │
│  │ • 成果物/業務完成   │         │ • 労働力提供        │            │
│  │ • 指揮命令:受託側   │         │ • 指揮命令:派遣先   │            │
│  │ • 占比更大         │         │ • 法規制更严格      │            │
│  │ • SES/業務請負等   │         │ • 抵触日管理必須    │            │
│  └─────────────────────┘         └─────────────────────┘            │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 1.2 契約类型明细

| 契約類型 | 説明 | 指揮命令 | 法規制 |
|---------|------|---------|-------|
| **派遣契約** | 派遣先に労働力を提供 | 派遣先 | 派遣法適用（抵触日等） |
| **準委任契約** | SES等、業務遂行を委託 | 受託側 | 派遣法不適用 |
| **請負契約** | 成果物の完成を約束 | 受託側 | 派遣法不適用 |

---

## 二、资源池（リソースプール）设计

### 2.1 资源来源分类

```
┌─────────────────────────────────────────────────────────────────────┐
│                        リソースプール                                 │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐               │
│  │   自社社員    │  │ 個人事業主   │  │ 協力会社社員  │               │
│  │  (正社員等)   │  │ (フリーランス)│  │  (BP社員)    │               │
│  └──────────────┘  └──────────────┘  └──────────────┘               │
│         ↓                  ↓                  ↓                      │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                    統一リソース管理                              │ │
│  │  • 共通スキル管理                                               │ │
│  │  • 稼働状況管理                                                 │ │
│  │  • 案件マッチング                                               │ │
│  │  • 契約・単価管理                                               │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
│  ┌──────────────┐  ┌──────────────┐                                 │
│  │ 協力会社の    │  │  商談中      │                                 │
│  │ 個人事業主    │  │  (未採用)    │                                 │
│  └──────────────┘  └──────────────┘                                 │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 资源类型定义

| リソース種別 | 説明 | 支払先 |
|------------|------|-------|
| **自社社員** | 自社雇用の正社員・契約社員等 | 給与として自社から支払 |
| **個人事業主** | フリーランス、直接契約 | 報酬として自社から支払 |
| **BP要員** | 協力会社経由の要員（社員・個人事業主問わず） | BPへ支払 |
| **商談中** | 採用検討中の候補者 | - |

---

## 三、与现有模块的关系

### 3.1 模块整合策略

```
┌─────────────────────────────────────────────────────────────────────┐
│                      モジュール関係                                   │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  【標準版モジュール（そのまま利用）】                                   │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ 取引先（businesspartners）                                      │ │
│  │   → 派遣先/委託先は取引先として管理                               │ │
│  │   → flag_customer=true で顧客識別                               │ │
│  │   → 支払条件等は既存フィールド利用                                │ │
│  ├────────────────────────────────────────────────────────────────┤ │
│  │ 社員（employees）                                               │ │
│  │   → 自社社員はemployeesテーブルで管理                            │ │
│  │   → リソースプールから参照                                       │ │
│  ├────────────────────────────────────────────────────────────────┤ │
│  │ 勤怠（timesheets）                                              │ │
│  │   → 週次入力・承認の既存機能を拡張                                │ │
│  │   → 案件・契約との紐付けを追加                                   │ │
│  ├────────────────────────────────────────────────────────────────┤ │
│  │ 給与（payroll）                                                 │ │
│  │   → 自社社員の給与計算は既存機能利用                              │ │
│  ├────────────────────────────────────────────────────────────────┤ │
│  │ 銀行明細（moneytree）                                           │ │
│  │   → 入金確認は既存機能利用                                       │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
│  【人材版専用モジュール（新規開発）】                                   │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ リソースプール - 全リソース統合管理                               │ │
│  │ 案件管理 - 顧客からの要員依頼                                    │ │
│  │ 契約管理 - 派遣/業務委託契約                                     │ │
│  │ 請求管理 - 案件・契約に基づく請求書生成                           │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.2 取引先（派遣先）との連携

```
既存の businesspartners テーブル
├── partner_code      → そのまま利用
├── name              → そのまま利用
├── flag_customer     → 派遣先/委託先 = true
├── flag_vendor       → 協力会社(BP) = true（リソース供給元として）
├── payment_terms     → 支払条件（既存）
├── payload           → 人材版固有情報を追加
│   ├── staffing.isClient        : boolean  -- 派遣先フラグ
│   ├── staffing.isSupplier      : boolean  -- 要員供給元フラグ
│   ├── staffing.billingContact  : object   -- 請求先担当者
│   └── staffing.workLocations   : array    -- 就業場所一覧
```

### 3.3 社員マスタとの連携

```
既存の employees テーブル
├── employee_code     → そのまま利用
├── payload           → 人材版固有情報を追加
│   ├── staffing.resourcePoolId  : UUID    -- リソースプールへのリンク
│   └── staffing.dispatchable    : boolean -- 派遣可能フラグ
```

---

## 四、新規開発モジュール

### 4.1 リソースプール（resource_pool）

**コンセプト**: 全ての要員を統一的に管理する中核テーブル

```sql
-- リソースプール（要員統合管理）
CREATE TABLE resource_pool (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    resource_code TEXT NOT NULL,          -- リソースコード（自動採番）
    
    -- 基本情報
    display_name TEXT NOT NULL,           -- 表示名
    display_name_kana TEXT,               -- フリガナ
    
    -- リソース種別
    resource_type TEXT NOT NULL,          -- employee/freelancer/bp/candidate
    
    -- 所属関連
    employee_id UUID,                     -- 自社社員の場合: employeesへのFK
    supplier_partner_id UUID,             -- BP社員の場合: 協力会社のbusinesspartners.id
    
    -- 連絡先（自社社員以外の場合）
    email TEXT,
    phone TEXT,
    
    -- スキル・経験
    primary_skill_category TEXT,          -- 主要スキルカテゴリ
    experience_years INT,                 -- 経験年数
    skills JSONB DEFAULT '[]',            -- スキル一覧
    certifications JSONB DEFAULT '[]',    -- 資格一覧
    
    -- 単価情報
    default_billing_rate DECIMAL(10,0),   -- デフォルト請求単価（対顧客）
    default_cost_rate DECIMAL(10,0),      -- デフォルト原価単価（支払/給与）
    rate_type TEXT DEFAULT 'hourly',      -- hourly/daily/monthly
    
    -- 稼働状態
    availability_status TEXT DEFAULT 'available', 
    -- available: 稼働可能
    -- assigned: アサイン中
    -- partially_available: 一部稼働可能
    -- unavailable: 稼働不可
    -- candidate: 商談中
    
    current_assignment_id UUID,           -- 現在のアサイン（契約ID）
    available_from DATE,                  -- 稼働可能日
    
    -- 希望条件
    desired_locations TEXT[],             -- 希望勤務地
    desired_job_types TEXT[],             -- 希望職種
    min_rate DECIMAL(10,0),               -- 希望最低単価
    
    -- メモ
    internal_notes TEXT,                  -- 社内メモ
    
    -- 有効期限（商談中の場合等）
    expires_at TIMESTAMPTZ,
    
    status TEXT DEFAULT 'active',         -- active/inactive/archived
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, resource_code)
);

-- インデックス
CREATE INDEX idx_resource_pool_type ON resource_pool(company_code, resource_type);
CREATE INDEX idx_resource_pool_status ON resource_pool(company_code, availability_status);
CREATE INDEX idx_resource_pool_employee ON resource_pool(employee_id) WHERE employee_id IS NOT NULL;
CREATE INDEX idx_resource_pool_supplier ON resource_pool(supplier_partner_id) WHERE supplier_partner_id IS NOT NULL;
```

### 4.2 案件管理（staffing_projects）

**コンセプト**: 顧客からの要員依頼を管理

```sql
-- 案件（プロジェクト/要員依頼）
CREATE TABLE staffing_projects (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    project_code TEXT NOT NULL,           -- 案件番号
    
    -- 顧客情報（取引先参照）
    client_partner_id UUID NOT NULL,      -- businesspartners.id
    
    -- 案件基本情報
    project_name TEXT NOT NULL,           -- 案件名
    job_category TEXT,                    -- 職種カテゴリ
    job_description TEXT,                 -- 業務内容
    
    -- 契約形態
    contract_type TEXT NOT NULL,          -- dispatch(派遣)/ses(準委任)/contract(請負)
    
    -- 募集条件
    headcount INT DEFAULT 1,              -- 募集人数
    filled_count INT DEFAULT 0,           -- 決定人数
    
    -- 期間
    expected_start_date DATE,             -- 開始予定日
    expected_end_date DATE,               -- 終了予定日
    
    -- 就業条件
    work_location TEXT,                   -- 勤務地
    work_days TEXT,                       -- 勤務曜日
    work_hours TEXT,                      -- 勤務時間
    remote_work_ratio INT,                -- リモート比率(%)
    
    -- 料金条件
    billing_rate_min DECIMAL(10,0),       -- 請求単価下限
    billing_rate_max DECIMAL(10,0),       -- 請求単価上限
    rate_type TEXT DEFAULT 'monthly',     -- hourly/daily/monthly
    
    -- 要求スキル
    required_skills JSONB DEFAULT '[]',   -- 必須スキル
    preferred_skills JSONB DEFAULT '[]',  -- 歓迎スキル
    experience_years_min INT,             -- 必要経験年数
    
    -- 営業情報
    sales_rep_id UUID,                    -- 営業担当者
    source TEXT,                          -- 案件獲得経路
    
    -- 状態
    status TEXT DEFAULT 'open',           
    -- open: 募集中
    -- matching: マッチング中
    -- filled: 充足
    -- on_hold: 保留
    -- closed: クローズ
    -- cancelled: キャンセル
    
    priority TEXT DEFAULT 'normal',       -- urgent/high/normal/low
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, project_code)
);

-- 案件候補者（マッチング記録）
CREATE TABLE staffing_project_candidates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    project_id UUID REFERENCES staffing_projects(id),
    resource_id UUID REFERENCES resource_pool(id),
    
    -- マッチング状態
    status TEXT DEFAULT 'proposed',
    -- proposed: 提案中
    -- client_review: 顧客検討中
    -- interview_scheduled: 面談予定
    -- interview_done: 面談完了
    -- offered: オファー中
    -- accepted: 決定
    -- rejected: 不採用
    -- withdrawn: 辞退
    
    proposed_rate DECIMAL(10,0),          -- 提案単価
    proposed_at TIMESTAMPTZ DEFAULT now(),
    
    -- 面談情報
    interview_date TIMESTAMPTZ,
    interview_notes TEXT,
    
    -- 決定情報
    decided_at TIMESTAMPTZ,
    final_rate DECIMAL(10,0),             -- 決定単価
    rejection_reason TEXT,
    
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);
```

### 4.3 契約管理（staffing_contracts）

**コンセプト**: 派遣契約・業務委託契約を統合管理

```sql
-- 契約（派遣/業務委託統合）
CREATE TABLE staffing_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,            -- 契約番号
    
    -- 関連
    project_id UUID REFERENCES staffing_projects(id),
    resource_id UUID REFERENCES resource_pool(id),
    client_partner_id UUID NOT NULL,      -- 顧客（取引先）
    
    -- 契約形態
    contract_type TEXT NOT NULL,          -- dispatch/ses/contract
    
    -- 契約期間
    start_date DATE NOT NULL,
    end_date DATE,                        -- NULLの場合は無期限
    
    -- 派遣契約の場合の追加情報
    dispatch_info JSONB,
    -- {
    --   "organizationUnit": "開発部",    -- 組織単位（抵触日計算用）
    --   "collisionDate": "2027-04-01",  -- 抵触日
    --   "supervisorName": "山田太郎",    -- 指揮命令者
    --   "complaintHandler": "鈴木花子"   -- 苦情処理担当
    -- }
    
    -- 就業条件
    work_location TEXT,
    work_days TEXT,                       -- 勤務曜日
    work_start_time TIME,
    work_end_time TIME,
    monthly_work_hours DECIMAL(5,1),      -- 月間所定労働時間（精算基準）
    
    -- 料金条件（対顧客請求）
    billing_rate DECIMAL(10,0) NOT NULL,  -- 請求単価
    billing_rate_type TEXT DEFAULT 'monthly', -- hourly/daily/monthly
    overtime_rate_multiplier DECIMAL(3,2) DEFAULT 1.25,
    
    -- 精算条件
    settlement_type TEXT DEFAULT 'range', -- range(幅精算)/actual(実精算)/fixed(固定)
    settlement_lower_hours DECIMAL(5,1),  -- 下限時間
    settlement_upper_hours DECIMAL(5,1),  -- 上限時間
    
    -- 原価（リソースへの支払/給与）
    cost_rate DECIMAL(10,0),              -- 原価単価
    cost_rate_type TEXT DEFAULT 'monthly',
    
    -- 支払先（自社社員以外）
    payee_type TEXT,                      -- resource(直接)/supplier(BP経由)
    payee_partner_id UUID,                -- BP経由の場合の支払先
    
    -- 状態
    status TEXT DEFAULT 'active',
    -- draft: 下書き
    -- active: 有効
    -- extended: 延長
    -- ended: 終了
    -- terminated: 途中解約
    
    termination_date DATE,
    termination_reason TEXT,
    
    -- 更新履歴
    renewal_count INT DEFAULT 0,
    original_contract_id UUID,            -- 更新元契約
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_no)
);

-- インデックス
CREATE INDEX idx_staffing_contracts_resource ON staffing_contracts(resource_id);
CREATE INDEX idx_staffing_contracts_client ON staffing_contracts(client_partner_id);
CREATE INDEX idx_staffing_contracts_status ON staffing_contracts(company_code, status);
CREATE INDEX idx_staffing_contracts_dates ON staffing_contracts(company_code, start_date, end_date);
```

### 4.4 勤怠連携（timesheets拡張）

**コンセプト**: 既存の勤怠機能に案件・契約情報を紐付け

```sql
-- 既存timesheetsテーブルのpayloadに追加するフィールド
-- payload.staffing = {
--   "contractId": "uuid",      -- 契約ID
--   "projectId": "uuid",       -- 案件ID
--   "resourceId": "uuid",      -- リソースID
--   "billingHours": 160,       -- 請求対象時間
--   "settlementHours": 165,    -- 精算時間（残業含む）
--   "overtimeHours": 5,        -- 残業時間
--   "clientApproved": true,    -- 顧客承認済（不要とのこと、削除可）
-- }

-- 週次勤怠サマリー（人材版用ビュー or 別テーブル）
CREATE TABLE staffing_timesheet_summary (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_contracts(id),
    year_month TEXT NOT NULL,             -- YYYY-MM
    week_number INT,                      -- 週番号（オプション）
    
    -- 時間集計
    scheduled_hours DECIMAL(6,2),         -- 所定時間
    actual_hours DECIMAL(6,2),            -- 実労働時間
    overtime_hours DECIMAL(6,2),          -- 残業時間
    billable_hours DECIMAL(6,2),          -- 請求対象時間
    
    -- 精算計算
    settlement_hours DECIMAL(6,2),        -- 精算時間
    settlement_adjustment DECIMAL(10,0),  -- 精算調整額（控除/追加）
    
    -- 状態
    status TEXT DEFAULT 'open',           -- open/submitted/approved/closed
    submitted_at TIMESTAMPTZ,
    approved_at TIMESTAMPTZ,
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_id, year_month)
);
```

### 4.5 請求管理（staffing_invoices）

**コンセプト**: 契約・勤怠に基づく請求書生成

```sql
-- 請求書
CREATE TABLE staffing_invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_no TEXT NOT NULL,
    
    -- 請求先（取引先）
    client_partner_id UUID NOT NULL,
    
    -- 請求期間
    billing_period_start DATE NOT NULL,
    billing_period_end DATE NOT NULL,
    
    -- 金額
    subtotal DECIMAL(12,0) NOT NULL,
    tax_rate DECIMAL(4,2) DEFAULT 0.10,
    tax_amount DECIMAL(12,0),
    total_amount DECIMAL(12,0) NOT NULL,
    
    -- 日付
    invoice_date DATE NOT NULL,
    due_date DATE NOT NULL,
    
    -- 状態
    status TEXT DEFAULT 'draft',
    -- draft: 下書き
    -- issued: 発行済
    -- sent: 送付済
    -- paid: 入金済
    -- partial_paid: 一部入金
    -- overdue: 延滞
    -- cancelled: キャンセル
    
    -- 発行・送付
    issued_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    sent_method TEXT,                     -- email/mail/portal
    
    -- 入金（銀行明細との連携用）
    paid_amount DECIMAL(12,0) DEFAULT 0,
    last_payment_date DATE,
    
    -- PDF
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, invoice_no)
);

-- 請求明細
CREATE TABLE staffing_invoice_lines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_id UUID REFERENCES staffing_invoices(id),
    line_no INT NOT NULL,
    
    -- 契約・リソース
    contract_id UUID REFERENCES staffing_contracts(id),
    resource_id UUID REFERENCES resource_pool(id),
    
    -- 明細内容
    description TEXT,                     -- 摘要
    
    -- 数量・単価
    quantity DECIMAL(8,2),                -- 数量（時間/日数/人月）
    unit TEXT,                            -- 単位
    unit_price DECIMAL(10,0),             -- 単価
    
    -- 精算調整
    settlement_adjustment DECIMAL(10,0),  -- 精算調整額
    adjustment_description TEXT,
    
    -- 金額
    line_amount DECIMAL(12,0),
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

## 五、多版本共存アーキテクチャ

### 5.1 設計原則

```
┌─────────────────────────────────────────────────────────────────────┐
│                    マルチエディション設計原則                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. 標準版は常に独立して動作可能                                       │
│     → 人材版モジュールが存在しなくても影響なし                          │
│                                                                      │
│  2. 業界版はオプショナルな拡張                                         │
│     → 新テーブル追加のみ、既存テーブル構造変更なし                       │
│     → payload JSONBへのオプショナルフィールド追加                       │
│                                                                      │
│  3. 複数業界版の同時有効化をサポート                                    │
│     → Edition.Type: ["standard", "staffing", "retail"]               │
│     → 各業界版のモジュールは独立                                       │
│                                                                      │
│  4. UIは動的に構成                                                    │
│     → /edition/menus APIで有効メニューを返す                          │
│     → フロントエンドは動的にメニュー表示                                │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 5.2 設定例

```json
// 標準版のみ
{
  "Edition": {
    "Type": "Standard",
    "EnabledModules": []  // 空 = 標準版全モジュール
  }
}

// 人材派遣版
{
  "Edition": {
    "Type": "Staffing",
    "EnabledModules": [],
    "DisabledModules": ["inventory", "fixed_assets"]
  }
}

// 複数業界版（将来）
{
  "Edition": {
    "Types": ["Standard", "Staffing"],  // 配列対応
    "EnabledModules": ["finance_core", "hr_core", "staffing_*"]
  }
}
```

### 5.3 データベース影響の最小化

| アプローチ | 説明 |
|-----------|------|
| **新テーブル追加** | resource_pool, staffing_* は完全に独立 |
| **payload拡張** | 既存テーブルはpayload.staffingで拡張 |
| **外部キーなし** | 人材版テーブルから標準版への参照は論理参照（UUID保持のみ） |
| **マイグレーション** | 人材版有効化時のみ関連テーブル作成 |

---

## 六、開発優先度

### Phase 1: コア機能（3-4週間）

| モジュール | 機能 | 依存 |
|-----------|------|-----|
| リソースプール | CRUD、スキル管理、稼働状態 | なし |
| 案件管理 | CRUD、候補者マッチング | リソースプール |
| 契約管理 | 派遣/業務委託、期間管理 | リソースプール、案件 |

### Phase 2: 請求連携（2-3週間）

| モジュール | 機能 | 依存 |
|-----------|------|-----|
| 勤怠連携 | 契約紐付け、月次集計 | 契約、既存勤怠 |
| 請求書生成 | 自動生成、PDF出力 | 契約、勤怠 |

### Phase 3: 分析・拡張（将来）

| モジュール | 機能 | 依存 |
|-----------|------|-----|
| レポート | 稼働率、売上分析 | 全モジュール |
| コンプライアンス | 抵触日アラート（必要時） | 契約 |

---

## 七、用語対照

| システム用語 | 日本語業界用語 | 説明 |
|-------------|---------------|------|
| resource_pool | 要員プール | 全リソース統合管理 |
| staffing_projects | 案件 | 顧客からの要員依頼 |
| staffing_contracts | 契約 | 派遣/業務委託契約 |
| contract_type: dispatch | 派遣契約 | 労働者派遣法適用 |
| contract_type: ses | 準委任契約 | SES等 |
| contract_type: contract | 請負契約 | 成果物契約 |
| collision_date | 抵触日 | 派遣3年制限 |


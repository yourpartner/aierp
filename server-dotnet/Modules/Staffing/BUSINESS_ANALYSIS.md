# 日本人才派遣行业业务分析与开发需求

## 一、行业概述

### 1.1 三方关系结构

```
┌──────────────┐         派遣契約         ┌──────────────┐
│   派遣元     │◄─────────────────────────►│   派遣先     │
│ (派遣会社)   │                           │ (用工企业)   │
└──────┬───────┘                           └──────┬───────┘
       │                                          │
       │ 雇用契約                                  │ 指揮命令
       │ (劳动合同)                                │ (工作指示)
       │                                          │
       └──────────────►┌──────────────┐◄───────────┘
                       │ 派遣スタッフ  │
                       │ (派遣员工)   │
                       └──────────────┘
```

### 1.2 日本派遣法关键规定

| 规定项目 | 内容 | 系统影响 |
|---------|------|---------|
| **派遣期间制限（抵触日）** | 同一派遣先的同一组织单位最长3年 | 需要抵触日管理和预警 |
| **无期雇用转换** | 有期雇用超过5年可申请无期转换 | 需要雇用期间追踪 |
| **同一労働同一賃金** | 派遣员工与正社员同工同酬 | 需要薪资比较管理 |
| **マージン率公开** | 必须公开派遣费与员工工资的差额比例 | 需要利润率计算报表 |
| **派遣先均等・均衡方式** | 参照派遣先的待遇决定派遣员工待遇 | 需要待遇比较功能 |

---

## 二、核心业务流程

### 2.1 完整业务流程图

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              派遣业务全流程                                   │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐       │
│  │ 1.営業  │──►│ 2.募集  │──►│ 3.契約  │──►│ 4.派遣  │──►│ 5.請求  │       │
│  │ 案件獲得│   │ マッチング│   │ 締結   │   │ 開始   │   │ 回収   │       │
│  └─────────┘   └─────────┘   └─────────┘   └─────────┘   └─────────┘       │
│       │             │             │             │             │             │
│       ▼             ▼             ▼             ▼             ▼             │
│  ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐       │
│  │派遣先管理│   │スタッフ │   │基本契約 │   │勤怠管理 │   │請求書  │       │
│  │案件管理 │   │人材DB  │   │個別契約 │   │就業管理 │   │売掛管理│       │
│  └─────────┘   └─────────┘   └─────────┘   └─────────┘   └─────────┘       │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 阶段详解

#### Phase 1: 営業・案件獲得（营业・案件获取）

**业务活动：**
- 开拓新派遣先（客户企业）
- 接收派遣需求（案件）
- 报价与条件协商
- 现场确认（工作环境调查）

**关键数据：**
- 派遣先企业信息
- 案件明细（职位、人数、期间、单价）
- 营业活动记录
- 报价书

#### Phase 2: 募集・マッチング（招募・匹配）

**业务活动：**
- 从人才库检索符合条件的候选人
- 确认候选人意向
- 安排与派遣先面谈（顔合わせ）
- 技能测试与评估

**关键数据：**
- 候选人技能档案
- 面谈记录
- 匹配历史

#### Phase 3: 契約締結（合同签订）

**业务活动：**
- 签订派遣基本契約（与派遣先）
- 签订派遣個別契約（每次派遣）
- 签订雇用契約（与派遣员工）
- 发送派遣通知書

**关键文档：**
- 派遣基本契約書
- 派遣個別契約書
- 雇用契約書
- 就業条件明示書
- 派遣先通知書

#### Phase 4: 派遣開始・就業管理（派遣开始・就业管理）

**业务活动：**
- 入职手续办理
- 日常勤怠管理
- 定期访问和跟进
- 派遣先确认勤怠
- 问题处理和协调

**关键数据：**
- 每日勤怠记录
- 加班时间管理
- 休假管理
- 派遣先确认签字

#### Phase 5: 請求・回収（请求・收款）

**业务活动：**
- 月末勤怠确定
- 生成请求书（账单）
- 发送请求书
- 入金确认
- 催收管理

**关键数据：**
- 请求书明细
- 入金记录
- 应收账款

---

## 三、模块开发需求

### 3.1 模块总览

```
人才派遣版 ERP 模块结构
├── 核心模块（复用标准版）
│   ├── 财务核心 - 凭证、科目、账本
│   ├── 人事核心 - 员工、部门
│   └── AI核心 - 智能助手
│
├── 派遣专用模块（新开发）
│   ├── 派遣先管理 - 客户企业管理
│   ├── 案件管理 - 派遣需求管理
│   ├── スタッフ管理 - 派遣员工管理
│   ├── 契約管理 - 合同全生命周期
│   ├── 勤怠管理 - 工时记录与确认
│   ├── 請求管理 - 账单生成与收款
│   ├── コンプライアンス - 法规合规管理
│   └── 分析レポート - 业务分析报表
│
└── 可选模块
    ├── スタッフポータル - 员工自助门户
    └── 派遣先ポータル - 客户自助门户
```

---

### 3.2 派遣先管理模块

**模块ID:** `staffing_client`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 派遣先登録 | 客户企业基本信息登记 | P0 |
| 担当者管理 | 客户企业联系人管理 | P0 |
| 事業所管理 | 客户企业各工作地点管理 | P1 |
| 与信管理 | 客户信用额度管理 | P2 |
| 取引履歴 | 交易历史记录 | P1 |

#### 数据模型

```sql
-- 派遣先（客户企业）
CREATE TABLE staffing_clients (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    client_code TEXT NOT NULL,           -- 派遣先コード
    client_name TEXT NOT NULL,           -- 派遣先名
    client_name_kana TEXT,               -- フリガナ
    industry TEXT,                       -- 業種
    employee_count INT,                  -- 従業員数
    capital DECIMAL(15,0),               -- 資本金
    address TEXT,                        -- 本社住所
    phone TEXT,
    fax TEXT,
    website TEXT,
    credit_limit DECIMAL(12,0),          -- 与信限度額
    payment_terms TEXT,                  -- 支払条件（月末締め翌月末払い等）
    payment_day INT,                     -- 締め日
    payment_month_offset INT DEFAULT 1,  -- 支払月（翌月=1）
    billing_contact_name TEXT,           -- 請求先担当者
    billing_email TEXT,
    status TEXT DEFAULT 'active',        -- active/inactive/suspended
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, client_code)
);

-- 派遣先担当者
CREATE TABLE staffing_client_contacts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    client_id UUID REFERENCES staffing_clients(id),
    contact_name TEXT NOT NULL,
    contact_name_kana TEXT,
    department TEXT,                     -- 所属部署
    position TEXT,                       -- 役職
    phone TEXT,
    mobile TEXT,
    email TEXT,
    is_primary BOOLEAN DEFAULT false,    -- 主担当フラグ
    is_billing_contact BOOLEAN DEFAULT false,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);

-- 派遣先事業所（工作地点）
CREATE TABLE staffing_client_offices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    client_id UUID REFERENCES staffing_clients(id),
    office_code TEXT NOT NULL,
    office_name TEXT NOT NULL,
    address TEXT,
    nearest_station TEXT,                -- 最寄り駅
    work_start_time TIME,                -- 就業開始時間
    work_end_time TIME,                  -- 就業終了時間
    break_minutes INT DEFAULT 60,        -- 休憩時間
    dress_code TEXT,                     -- 服装規定
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 3.3 案件管理模块

**模块ID:** `staffing_order`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 案件登録 | 派遣需求登记 | P0 |
| 案件検索 | 按条件检索案件 | P0 |
| マッチング | 与人才库匹配 | P1 |
| 進捗管理 | 案件状态追踪 | P0 |
| 報価書作成 | 生成报价书 | P1 |

#### 数据模型

```sql
-- 派遣案件（派遣需求/订单）
CREATE TABLE staffing_orders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    order_no TEXT NOT NULL,              -- 案件番号
    client_id UUID REFERENCES staffing_clients(id),
    office_id UUID REFERENCES staffing_client_offices(id),
    
    -- 案件基本信息
    job_title TEXT NOT NULL,             -- 職種名
    job_category TEXT,                   -- 職種カテゴリ
    headcount INT DEFAULT 1,             -- 募集人数
    filled_count INT DEFAULT 0,          -- 決定人数
    
    -- 就业条件
    work_start_date DATE,                -- 就業開始日
    work_end_date DATE,                  -- 就業終了日（予定）
    work_days TEXT,                      -- 勤務曜日
    work_start_time TIME,
    work_end_time TIME,
    overtime_expected DECIMAL(4,1),      -- 想定残業時間/月
    
    -- 料金条件
    billing_type TEXT DEFAULT 'hourly',  -- hourly/daily/monthly
    billing_rate DECIMAL(10,0),          -- 派遣料金単価
    overtime_rate_multiplier DECIMAL(3,2) DEFAULT 1.25,
    holiday_rate_multiplier DECIMAL(3,2) DEFAULT 1.35,
    
    -- 要求条件
    required_skills TEXT[],              -- 必須スキル
    preferred_skills TEXT[],             -- 歓迎スキル
    required_experience_years INT,       -- 必要経験年数
    age_range TEXT,                      -- 年齢層
    gender_preference TEXT,              -- 性別希望
    
    -- 状态
    status TEXT DEFAULT 'open',          -- open/matching/filled/closed/cancelled
    priority TEXT DEFAULT 'normal',      -- urgent/high/normal/low
    
    -- 营业担当
    sales_rep_id UUID,                   -- 営業担当者
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, order_no)
);

-- 案件候选人（匹配记录）
CREATE TABLE staffing_order_candidates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    order_id UUID REFERENCES staffing_orders(id),
    staff_id UUID,                       -- 候选员工ID
    
    status TEXT DEFAULT 'proposed',      -- proposed/interview/offered/accepted/rejected/withdrawn
    proposed_at TIMESTAMPTZ DEFAULT now(),
    interview_date TIMESTAMPTZ,
    interview_result TEXT,
    offer_rate DECIMAL(10,0),            -- 提示单价
    decided_at TIMESTAMPTZ,
    rejection_reason TEXT,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 3.4 スタッフ管理模块

**模块ID:** `staffing_staff`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| スタッフ登録 | 派遣员工登记 | P0 |
| スキル管理 | 技能和资格管理 | P0 |
| 経歴管理 | 工作经历管理 | P1 |
| 稼働状況 | 就业状态管理 | P0 |
| 社会保険 | 社保加入管理 | P1 |
| 有給管理 | 带薪休假管理 | P1 |

#### 数据模型

```sql
-- 派遣スタッフ（派遣员工扩展表）
CREATE TABLE staffing_staff (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    staff_code TEXT NOT NULL,            -- スタッフコード
    employee_id UUID,                    -- 关联员工主表
    
    -- 基本信息
    last_name TEXT NOT NULL,
    first_name TEXT NOT NULL,
    last_name_kana TEXT,
    first_name_kana TEXT,
    gender TEXT,
    birth_date DATE,
    nationality TEXT,
    
    -- 联系信息
    postal_code TEXT,
    address TEXT,
    phone TEXT,
    mobile TEXT,
    email TEXT,
    emergency_contact_name TEXT,
    emergency_contact_phone TEXT,
    emergency_contact_relation TEXT,
    
    -- 雇用信息
    employment_type TEXT DEFAULT 'registered', -- registered(登録型)/permanent(常用型)
    hire_date DATE,
    registration_date DATE,
    
    -- 银行账户
    bank_name TEXT,
    bank_branch TEXT,
    bank_account_type TEXT,              -- 普通/当座
    bank_account_number TEXT,
    bank_account_holder TEXT,
    
    -- 社保
    social_insurance_enrolled BOOLEAN DEFAULT false,
    health_insurance_number TEXT,
    pension_number TEXT,
    employment_insurance_number TEXT,
    
    -- 状态
    availability_status TEXT DEFAULT 'available', -- available/assigned/leave/inactive
    desired_hourly_rate DECIMAL(8,0),    -- 希望時給
    desired_work_areas TEXT[],           -- 希望勤務地
    desired_job_types TEXT[],            -- 希望職種
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, staff_code)
);

-- スタッフスキル
CREATE TABLE staffing_staff_skills (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    staff_id UUID REFERENCES staffing_staff(id),
    skill_category TEXT,                 -- 語学/IT/事務/専門資格等
    skill_name TEXT NOT NULL,
    skill_level TEXT,                    -- 初級/中級/上級/ネイティブ等
    certification_name TEXT,             -- 資格名
    certification_date DATE,
    expiry_date DATE,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);

-- スタッフ職歴
CREATE TABLE staffing_staff_work_history (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    staff_id UUID REFERENCES staffing_staff(id),
    company_name TEXT,
    department TEXT,
    position TEXT,
    job_description TEXT,
    start_date DATE,
    end_date DATE,
    is_dispatch BOOLEAN DEFAULT false,   -- 派遣就業かどうか
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);

-- 有給休暇管理
CREATE TABLE staffing_staff_paid_leave (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    staff_id UUID REFERENCES staffing_staff(id),
    fiscal_year INT NOT NULL,            -- 年度
    granted_days DECIMAL(4,1),           -- 付与日数
    used_days DECIMAL(4,1) DEFAULT 0,    -- 使用日数
    carry_over_days DECIMAL(4,1) DEFAULT 0, -- 繰越日数
    grant_date DATE,
    expiry_date DATE,
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 3.5 契約管理模块

**模块ID:** `staffing_contract`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 基本契約管理 | 派遣基本合同管理 | P0 |
| 個別契約管理 | 派遣个别合同管理 | P0 |
| 雇用契約管理 | 与员工的劳动合同 | P0 |
| 契約書生成 | 自动生成合同文档 | P1 |
| 更新・終了 | 合同续签和终止 | P0 |
| 抵触日管理 | 派遣期间限制管理 | P0 |

#### 数据模型

```sql
-- 派遣基本契約（与派遣先的框架合同）
CREATE TABLE staffing_master_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,
    client_id UUID REFERENCES staffing_clients(id),
    
    contract_start_date DATE NOT NULL,
    contract_end_date DATE,              -- NULL表示无期限
    auto_renew BOOLEAN DEFAULT true,     -- 自动更新
    
    -- 通用条款
    payment_terms TEXT,                  -- 支払条件
    billing_cycle TEXT DEFAULT 'monthly', -- 請求サイクル
    default_overtime_multiplier DECIMAL(3,2) DEFAULT 1.25,
    default_holiday_multiplier DECIMAL(3,2) DEFAULT 1.35,
    
    status TEXT DEFAULT 'active',        -- draft/active/expired/terminated
    
    document_url TEXT,                   -- 契約書PDFのURL
    signed_date DATE,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_no)
);

-- 派遣個別契約（每次派遣的具体合同）
CREATE TABLE staffing_individual_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,
    
    master_contract_id UUID REFERENCES staffing_master_contracts(id),
    order_id UUID REFERENCES staffing_orders(id),
    staff_id UUID REFERENCES staffing_staff(id),
    client_id UUID REFERENCES staffing_clients(id),
    office_id UUID REFERENCES staffing_client_offices(id),
    
    -- 派遣期间
    dispatch_start_date DATE NOT NULL,
    dispatch_end_date DATE NOT NULL,
    
    -- 抵触日管理
    organization_unit TEXT,              -- 組織単位（抵触日カウント用）
    collision_date DATE,                 -- 抵触日（3年后）
    collision_date_notified BOOLEAN DEFAULT false,
    
    -- 就业条件
    job_title TEXT,
    job_description TEXT,
    work_location TEXT,
    work_days TEXT,                      -- 勤務曜日
    work_start_time TIME,
    work_end_time TIME,
    break_minutes INT,
    
    -- 料金条件
    billing_type TEXT DEFAULT 'hourly',
    billing_rate DECIMAL(10,0) NOT NULL, -- 派遣料金
    staff_hourly_rate DECIMAL(8,0),      -- スタッフ時給
    overtime_rate DECIMAL(10,0),
    holiday_rate DECIMAL(10,0),
    
    -- 指揮命令者
    supervisor_name TEXT,                -- 指揮命令者
    supervisor_department TEXT,
    supervisor_phone TEXT,
    
    -- 苦情処理
    complaint_handler_dispatch TEXT,     -- 派遣元苦情処理担当
    complaint_handler_client TEXT,       -- 派遣先苦情処理担当
    
    status TEXT DEFAULT 'active',        -- draft/active/extended/ended/terminated
    termination_reason TEXT,
    termination_date DATE,
    
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_no)
);

-- 派遣スタッフ雇用契約
CREATE TABLE staffing_employment_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,
    staff_id UUID REFERENCES staffing_staff(id),
    
    employment_type TEXT,                -- 有期/無期
    contract_start_date DATE NOT NULL,
    contract_end_date DATE,              -- 無期の場合NULL
    
    -- 有期雇用の場合の更新管理
    renewal_count INT DEFAULT 0,         -- 更新回数
    total_employment_months INT DEFAULT 0, -- 通算雇用月数（5年ルール用）
    conversion_right_date DATE,          -- 無期転換申込権発生日
    
    base_hourly_rate DECIMAL(8,0),       -- 基本時給
    transportation_allowance DECIMAL(8,0), -- 通勤手当
    
    probation_end_date DATE,             -- 試用期間終了日
    
    status TEXT DEFAULT 'active',
    
    document_url TEXT,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_no)
);
```

---

### 3.6 勤怠管理模块

**模块ID:** `staffing_attendance`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 日次勤怠入力 | 每日工时录入 | P0 |
| タイムシート | 工时表管理 | P0 |
| 派遣先承認 | 客户确认工时 | P0 |
| 残業管理 | 加班时间管理 | P0 |
| 36協定管理 | 加班上限管理 | P1 |
| 休暇申請 | 休假申请管理 | P1 |

#### 数据模型

```sql
-- 勤怠記録（日次）
CREATE TABLE staffing_attendance (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_individual_contracts(id),
    staff_id UUID REFERENCES staffing_staff(id),
    
    work_date DATE NOT NULL,
    
    -- 实际工时
    clock_in TIME,                       -- 出勤時刻
    clock_out TIME,                      -- 退勤時刻
    break_minutes INT DEFAULT 60,        -- 休憩時間(分)
    
    -- 计算结果
    scheduled_hours DECIMAL(4,2),        -- 所定労働時間
    actual_hours DECIMAL(4,2),           -- 実労働時間
    overtime_hours DECIMAL(4,2) DEFAULT 0,    -- 残業時間
    late_night_hours DECIMAL(4,2) DEFAULT 0,  -- 深夜時間(22:00-5:00)
    holiday_hours DECIMAL(4,2) DEFAULT 0,     -- 休日出勤時間
    
    -- 状态
    attendance_type TEXT DEFAULT 'work', -- work/absent/paid_leave/sick/holiday
    is_late BOOLEAN DEFAULT false,       -- 遅刻
    is_early_leave BOOLEAN DEFAULT false, -- 早退
    
    -- 承认
    staff_confirmed BOOLEAN DEFAULT false,
    staff_confirmed_at TIMESTAMPTZ,
    client_approved BOOLEAN DEFAULT false,
    client_approved_at TIMESTAMPTZ,
    client_approver_name TEXT,
    
    -- 备注
    staff_notes TEXT,                    -- スタッフ備考
    client_notes TEXT,                   -- 派遣先備考
    
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_id, work_date)
);

-- 月次勤怠サマリー
CREATE TABLE staffing_attendance_monthly (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_individual_contracts(id),
    staff_id UUID REFERENCES staffing_staff(id),
    year_month TEXT NOT NULL,            -- YYYY-MM形式
    
    -- 日数集計
    work_days INT DEFAULT 0,             -- 出勤日数
    absent_days INT DEFAULT 0,           -- 欠勤日数
    paid_leave_days DECIMAL(4,1) DEFAULT 0, -- 有給取得日数
    
    -- 時間集計
    total_scheduled_hours DECIMAL(6,2),  -- 所定労働時間合計
    total_actual_hours DECIMAL(6,2),     -- 実労働時間合計
    total_overtime_hours DECIMAL(6,2),   -- 残業時間合計
    total_late_night_hours DECIMAL(6,2), -- 深夜時間合計
    total_holiday_hours DECIMAL(6,2),    -- 休日出勤時間合計
    
    -- 36協定チェック用（年間累計）
    ytd_overtime_hours DECIMAL(8,2),     -- 年度累計残業時間
    
    -- 締め状態
    is_closed BOOLEAN DEFAULT false,
    closed_at TIMESTAMPTZ,
    closed_by TEXT,
    
    -- 承认状态
    client_approved BOOLEAN DEFAULT false,
    client_approved_at TIMESTAMPTZ,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, contract_id, year_month)
);
```

---

### 3.7 請求管理模块

**模块ID:** `staffing_billing`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 請求書作成 | 生成请求书 | P0 |
| 請求書発行 | 发送请求书 | P0 |
| 入金管理 | 收款确认 | P0 |
| 売掛管理 | 应收账款管理 | P0 |
| 支払管理 | 给员工的支付 | P0 |
| マージン計算 | 利润率计算 | P1 |

#### 数据模型

```sql
-- 請求書ヘッダー
CREATE TABLE staffing_invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_no TEXT NOT NULL,
    
    client_id UUID REFERENCES staffing_clients(id),
    billing_period_start DATE NOT NULL,  -- 請求対象期間開始
    billing_period_end DATE NOT NULL,    -- 請求対象期間終了
    
    -- 金額
    subtotal DECIMAL(12,0) NOT NULL,     -- 小計
    tax_rate DECIMAL(4,2) DEFAULT 0.10,  -- 消費税率
    tax_amount DECIMAL(12,0),            -- 消費税額
    total_amount DECIMAL(12,0) NOT NULL, -- 請求総額
    
    -- 日期
    invoice_date DATE NOT NULL,          -- 請求日
    due_date DATE NOT NULL,              -- 支払期限
    
    -- 状态
    status TEXT DEFAULT 'draft',         -- draft/issued/sent/paid/overdue/cancelled
    issued_at TIMESTAMPTZ,
    sent_at TIMESTAMPTZ,
    sent_method TEXT,                    -- email/mail/portal
    
    -- 入金
    paid_amount DECIMAL(12,0) DEFAULT 0,
    paid_at TIMESTAMPTZ,
    payment_method TEXT,
    
    document_url TEXT,                   -- 請求書PDF
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, invoice_no)
);

-- 請求書明細
CREATE TABLE staffing_invoice_lines (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    invoice_id UUID REFERENCES staffing_invoices(id),
    line_no INT NOT NULL,
    
    contract_id UUID REFERENCES staffing_individual_contracts(id),
    staff_id UUID REFERENCES staffing_staff(id),
    
    -- 明细内容
    description TEXT,                    -- 摘要（スタッフ名+職種等）
    
    -- 工时
    regular_hours DECIMAL(6,2),          -- 所定時間
    overtime_hours DECIMAL(6,2),         -- 残業時間
    late_night_hours DECIMAL(6,2),       -- 深夜時間
    holiday_hours DECIMAL(6,2),          -- 休日時間
    
    -- 单价
    regular_rate DECIMAL(10,0),
    overtime_rate DECIMAL(10,0),
    late_night_rate DECIMAL(10,0),
    holiday_rate DECIMAL(10,0),
    
    -- 金额
    regular_amount DECIMAL(12,0),
    overtime_amount DECIMAL(12,0),
    late_night_amount DECIMAL(12,0),
    holiday_amount DECIMAL(12,0),
    line_total DECIMAL(12,0),
    
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT now()
);

-- スタッフ給与（派遣员工工资）
CREATE TABLE staffing_payroll (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    staff_id UUID REFERENCES staffing_staff(id),
    year_month TEXT NOT NULL,            -- YYYY-MM
    
    -- 基于勤怠计算
    total_regular_hours DECIMAL(6,2),
    total_overtime_hours DECIMAL(6,2),
    total_late_night_hours DECIMAL(6,2),
    total_holiday_hours DECIMAL(6,2),
    
    -- 支给项目
    base_pay DECIMAL(10,0),              -- 基本給
    overtime_pay DECIMAL(10,0),          -- 残業手当
    late_night_pay DECIMAL(10,0),        -- 深夜手当
    holiday_pay DECIMAL(10,0),           -- 休日手当
    transportation_allowance DECIMAL(8,0), -- 通勤手当
    other_allowances DECIMAL(10,0),      -- その他手当
    gross_pay DECIMAL(12,0),             -- 総支給額
    
    -- 控除项目
    health_insurance DECIMAL(8,0),       -- 健康保険
    pension DECIMAL(8,0),                -- 厚生年金
    employment_insurance DECIMAL(8,0),   -- 雇用保険
    income_tax DECIMAL(8,0),             -- 所得税
    resident_tax DECIMAL(8,0),           -- 住民税
    other_deductions DECIMAL(8,0),       -- その他控除
    total_deductions DECIMAL(10,0),      -- 控除合計
    
    net_pay DECIMAL(12,0),               -- 差引支給額
    
    -- 支付状态
    status TEXT DEFAULT 'calculated',    -- calculated/approved/paid
    payment_date DATE,
    
    notes TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    UNIQUE(company_code, staff_id, year_month)
);
```

---

### 3.8 コンプライアンス模块

**模块ID:** `staffing_compliance`

#### 功能清单

| 功能 | 说明 | 优先级 |
|-----|------|-------|
| 抵触日アラート | 3年期限预警 | P0 |
| 36協定チェック | 加班上限检查 | P0 |
| 無期転換管理 | 5年无期转换管理 | P1 |
| マージン率公開 | 利润率公示 | P1 |
| 法定書類管理 | 法定文件管理 | P1 |

#### 预警规则

```sql
-- 合规预警记录
CREATE TABLE staffing_compliance_alerts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    
    alert_type TEXT NOT NULL,            -- collision_date/overtime_limit/conversion_right/contract_expiry
    severity TEXT DEFAULT 'warning',     -- info/warning/critical
    
    -- 关联对象
    staff_id UUID,
    contract_id UUID,
    client_id UUID,
    
    -- 预警内容
    title TEXT NOT NULL,
    description TEXT,
    due_date DATE,                       -- 到期日/抵触日等
    days_remaining INT,                  -- 剩余天数
    
    -- 处理状态
    status TEXT DEFAULT 'open',          -- open/acknowledged/resolved/ignored
    acknowledged_by TEXT,
    acknowledged_at TIMESTAMPTZ,
    resolved_by TEXT,
    resolved_at TIMESTAMPTZ,
    resolution_notes TEXT,
    
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

-- 预警规则配置
CREATE TABLE staffing_compliance_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    
    rule_type TEXT NOT NULL,             -- collision_date/overtime/conversion
    alert_days_before INT[],             -- 提前几天预警 [90, 60, 30, 14, 7]
    is_active BOOLEAN DEFAULT true,
    
    created_at TIMESTAMPTZ DEFAULT now()
);
```

---

### 3.9 分析レポート模块

**模块ID:** `staffing_analytics`

#### 报表清单

| 报表 | 说明 | 优先级 |
|-----|------|-------|
| 稼働状況レポート | 员工就业状态统计 | P0 |
| 売上レポート | 销售额统计 | P0 |
| 派遣先別実績 | 按客户的业绩分析 | P1 |
| マージン分析 | 利润率分析 | P1 |
| 契約更新予測 | 合同续签预测 | P2 |
| 抵触日一覧 | 即将到期的派遣 | P0 |

---

## 四、开发优先级和阶段规划

### Phase 1: 核心业务（4-6周）

```
优先开发：
├── 派遣先管理（客户企业）
├── スタッフ管理（基本信息）
├── 個別契約管理
├── 勤怠管理（日次入力+月次集計）
└── 請求書作成（基本功能）
```

### Phase 2: 业务完善（4-6周）

```
├── 案件管理
├── マッチング機能
├── 契約書自動生成
├── 請求書PDF生成
├── 入金管理
└── スタッフ給与計算
```

### Phase 3: 合规与分析（2-4周）

```
├── 抵触日管理
├── 36協定チェック
├── 無期転換管理
├── 業績レポート
└── マージン分析
```

### Phase 4: 扩展功能（可选）

```
├── スタッフポータル（员工自助）
├── 派遣先ポータル（客户自助）
├── モバイルアプリ
└── AI匹配优化
```

---

## 五、与现有模块的集成

| 现有模块 | 集成方式 |
|---------|---------|
| 财务核心 | 请求书生成凭证，入金记录 |
| 员工主表 | スタッフ表关联员工表 |
| 商业伙伴 | 派遣先可关联BP表 |
| 工资计算 | 复用薪资计算引擎 |
| AI Agent | 智能匹配和预警 |

---

## 六、术语对照表

| 日语 | 中文 | 英语 |
|-----|------|------|
| 派遣元 | 派遣公司 | Staffing Agency |
| 派遣先 | 用工企业 | Client Company |
| 派遣スタッフ | 派遣员工 | Temporary Staff |
| 抵触日 | 派遣期限日 | Collision Date |
| 個別契約 | 个别合同 | Individual Contract |
| 基本契約 | 框架合同 | Master Contract |
| 勤怠 | 考勤/工时 | Attendance |
| 請求書 | 请求书/账单 | Invoice |
| マージン率 | 利润率 | Margin Rate |
| 36協定 | 36协定(加班协议) | Article 36 Agreement |


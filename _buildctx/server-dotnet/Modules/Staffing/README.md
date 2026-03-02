# 人才派遣版模块 (Staffing Edition)

此目录包含人才派遣行业专用的模块。

## 计划模块

### 1. StaffingContractModule - 派遣合同管理
- 派遣合同CRUD
- 合同模板管理
- 合同到期提醒
- 合同续签流程

### 2. StaffingDispatchModule - 派遣人员管理
- 派遣员工登记
- 派遣先（客户企业）管理
- 派遣状态跟踪
- 派遣历史记录

### 3. StaffingTimesheetModule - 工时管理（扩展）
- 派遣员工工时记录
- 客户确认流程
- 工时汇总报表
- 与账单生成联动

### 4. StaffingBillingModule - 账单管理
- 按工时自动生成账单
- 派遣费用计算
- 客户账单发送
- 收款跟踪

### 5. StaffingReportModule - 派遣报表
- 派遣人数统计
- 收入分析
- 客户分析
- 合同到期预警

## 数据库表设计

```sql
-- 派遣合同
CREATE TABLE staffing_contracts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_no TEXT NOT NULL,
    employee_id UUID REFERENCES employees(id),
    client_partner_id UUID REFERENCES business_partners(id),
    start_date DATE NOT NULL,
    end_date DATE,
    hourly_rate DECIMAL(10,2),
    monthly_rate DECIMAL(12,2),
    billing_cycle TEXT DEFAULT 'monthly',
    status TEXT DEFAULT 'active',
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

-- 派遣记录
CREATE TABLE staffing_dispatches (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_contracts(id),
    dispatch_date DATE NOT NULL,
    work_location TEXT,
    supervisor TEXT,
    status TEXT DEFAULT 'active',
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now()
);

-- 派遣工时
CREATE TABLE staffing_timesheets (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_contracts(id),
    work_date DATE NOT NULL,
    start_time TIME,
    end_time TIME,
    break_hours DECIMAL(4,2) DEFAULT 0,
    work_hours DECIMAL(4,2),
    overtime_hours DECIMAL(4,2) DEFAULT 0,
    client_approved BOOLEAN DEFAULT false,
    approved_at TIMESTAMPTZ,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now()
);

-- 派遣账单
CREATE TABLE staffing_billings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    billing_no TEXT NOT NULL,
    contract_id UUID REFERENCES staffing_contracts(id),
    billing_period_start DATE NOT NULL,
    billing_period_end DATE NOT NULL,
    total_hours DECIMAL(8,2),
    hourly_rate DECIMAL(10,2),
    amount DECIMAL(12,2),
    tax_amount DECIMAL(12,2),
    total_amount DECIMAL(12,2),
    status TEXT DEFAULT 'draft',
    issued_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT now()
);
```

## 开发计划

1. Phase 1: 合同和派遣基础管理
2. Phase 2: 工时记录和客户确认
3. Phase 3: 账单自动生成
4. Phase 4: 报表和分析


-- ============================================================
-- Phase 1: 渠道绑定表 + Phase 2: AI 能力分配
-- ============================================================

-- ========== Phase 1: employee_channel_bindings ==========

CREATE TABLE IF NOT EXISTS employee_channel_bindings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    user_id UUID NOT NULL,              -- → users.id
    channel TEXT NOT NULL,              -- 'wecom' | 'line' | 'slack' ...
    channel_user_id TEXT NOT NULL,      -- 渠道内的用户唯一标识
    channel_name TEXT,                  -- 渠道内显示名（冗余）
    bind_method TEXT DEFAULT 'manual',  -- 'manual' | 'self_service'
    status TEXT DEFAULT 'active',       -- 'active' | 'suspended' | 'unbound'
    bound_at TIMESTAMPTZ DEFAULT now(),
    unbound_at TIMESTAMPTZ,
    bind_fail_count INTEGER DEFAULT 0,  -- 自助绑定失败计数
    bind_locked_until TIMESTAMPTZ,      -- 失败锁定截止时间
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

-- 同一渠道+渠道用户只能绑定一个系统账号
CREATE UNIQUE INDEX IF NOT EXISTS uq_channel_bindings_channel_user
    ON employee_channel_bindings(channel, channel_user_id)
    WHERE status = 'active';

-- 按企业+渠道查询
CREATE INDEX IF NOT EXISTS idx_channel_bindings_company
    ON employee_channel_bindings(company_code, channel, status);

-- 按系统用户查询
CREATE INDEX IF NOT EXISTS idx_channel_bindings_user
    ON employee_channel_bindings(user_id, channel);

-- ========== Phase 2: AI 能力 (caps) ==========

-- 插入 AI 渠道能力到系统角色
-- 注意：role_caps 的 role_id 需要从 roles 表中查出

-- 定义一个函数方便批量插入
DO $$
DECLARE
    v_role_id UUID;
    v_caps TEXT[];
    v_cap TEXT;
BEGIN
    -- ========== 一般员工能力 → 所有角色都有 ==========
    -- 我们给所有现有角色都加上基础 AI 能力
    FOR v_role_id IN 
        SELECT id FROM roles WHERE is_active = true
    LOOP
        -- 所有角色都有基础员工能力
        v_caps := ARRAY[
            'ai.timesheet.entry',
            'ai.timesheet.query',
            'ai.payroll.query',
            'ai.certificate.apply',
            'ai.leave.apply',
            'ai.general'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    -- ========== 会计角色 (ACCOUNTANT_SENIOR, ACCOUNTANT_JUNIOR) ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code IN ('ACCOUNTANT_SENIOR', 'ACCOUNTANT_JUNIOR')
    LOOP
        v_caps := ARRAY[
            'ai.invoice.recognize',
            'ai.voucher.create',
            'ai.report.financial',
            'ai.payroll.report'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    -- ========== 管理者角色 (HR_MANAGER, SYS_ADMIN) ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code IN ('HR_MANAGER', 'SYS_ADMIN')
    LOOP
        v_caps := ARRAY[
            'ai.timesheet.approve',
            'ai.leave.approve',
            'ai.certificate.approve',
            'ai.invoice.recognize',
            'ai.voucher.create',
            'ai.report.financial',
            'ai.payroll.report',
            'ai.order.manage',
            'ai.delivery.manage',
            'ai.admin.bind'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    -- ========== 销售角色 (SALES_MANAGER, SALES_STAFF) ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code IN ('SALES_MANAGER', 'SALES_STAFF')
    LOOP
        v_caps := ARRAY[
            'ai.order.manage',
            'ai.delivery.manage'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    -- ========== SALES_MANAGER 额外能力 ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code = 'SALES_MANAGER'
    LOOP
        INSERT INTO role_caps (role_id, cap)
        VALUES (v_role_id, 'ai.report.financial')
        ON CONFLICT (role_id, cap) DO NOTHING;
    END LOOP;

    -- ========== HR_STAFF 也需要审批能力 ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code = 'HR_STAFF'
    LOOP
        v_caps := ARRAY[
            'ai.timesheet.approve',
            'ai.leave.approve',
            'ai.certificate.approve',
            'ai.payroll.report'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    -- ========== 为公司级别的 admin/ADMIN 角色添加全部 AI 能力 ==========
    FOR v_role_id IN 
        SELECT id FROM roles WHERE role_code IN ('admin', 'ADMIN')
    LOOP
        v_caps := ARRAY[
            'ai.timesheet.entry', 'ai.timesheet.query', 'ai.timesheet.approve',
            'ai.payroll.query', 'ai.payroll.report',
            'ai.certificate.apply', 'ai.certificate.approve',
            'ai.leave.apply', 'ai.leave.approve',
            'ai.invoice.recognize', 'ai.voucher.create',
            'ai.report.financial',
            'ai.order.manage', 'ai.delivery.manage',
            'ai.admin.bind', 'ai.general'
        ];
        FOREACH v_cap IN ARRAY v_caps LOOP
            INSERT INTO role_caps (role_id, cap)
            VALUES (v_role_id, v_cap)
            ON CONFLICT (role_id, cap) DO NOTHING;
        END LOOP;
    END LOOP;

    RAISE NOTICE 'AI capabilities assigned to all roles';
END $$;

-- 验证
DO $$
DECLARE
    cnt INT;
BEGIN
    SELECT COUNT(*) INTO cnt FROM employee_channel_bindings;
    RAISE NOTICE 'employee_channel_bindings table ready (% rows)', cnt;

    SELECT COUNT(*) INTO cnt FROM role_caps WHERE cap LIKE 'ai.%';
    RAISE NOTICE 'AI capabilities in role_caps: % entries', cnt;
END $$;

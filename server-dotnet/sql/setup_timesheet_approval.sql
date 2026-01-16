-- 1. 为 timesheet 创建默认审批规则（审批人为 admin 角色）
DO $$
DECLARE
    v_schema_id UUID;
    v_existing_ui JSONB;
    v_new_ui JSONB;
BEGIN
    -- 查找现有的 timesheet schema（注意：name 字段，不是 entity）
    SELECT id, ui INTO v_schema_id, v_existing_ui
    FROM schemas
    WHERE name = 'timesheet' AND is_active = true
    ORDER BY version DESC
    LIMIT 1;
    
    IF v_schema_id IS NULL THEN
        -- 创建新的 timesheet schema
        INSERT INTO schemas (name, version, is_active, schema, ui)
        VALUES (
            'timesheet',
            1,
            true,
            jsonb_build_object(
                'type', 'object',
                'properties', jsonb_build_object(
                    'date', jsonb_build_object('type', 'string'),
                    'startTime', jsonb_build_object('type', 'string'),
                    'endTime', jsonb_build_object('type', 'string'),
                    'hours', jsonb_build_object('type', 'number'),
                    'overtime', jsonb_build_object('type', 'number'),
                    'status', jsonb_build_object('type', 'string')
                )
            ),
            jsonb_build_object(
                'approval', jsonb_build_object(
                    'nl', '従業員が勤怠を提出すると、admin権限を持つユーザーが承認します。',
                    'rules', jsonb_build_object(
                        'steps', jsonb_build_array(
                            jsonb_build_object(
                                'name', '管理者承認',
                                'who', jsonb_build_array(
                                    jsonb_build_object('by', 'role', 'roleCode', 'admin')
                                )
                            )
                        )
                    )
                )
            )
        );
        RAISE NOTICE 'Created new timesheet schema with approval rules';
    ELSE
        -- 更新现有 schema，添加审批规则
        v_new_ui := COALESCE(v_existing_ui, '{}'::jsonb) || jsonb_build_object(
            'approval', jsonb_build_object(
                'nl', '従業員が勤怠を提出すると、admin権限を持つユーザーが承認します。',
                'rules', jsonb_build_object(
                    'steps', jsonb_build_array(
                        jsonb_build_object(
                            'name', '管理者承認',
                            'who', jsonb_build_array(
                                jsonb_build_object('by', 'role', 'roleCode', 'admin')
                            )
                        )
                    )
                )
            )
        );
        
        UPDATE schemas
        SET ui = v_new_ui,
            updated_at = now()
        WHERE id = v_schema_id;
        RAISE NOTICE 'Updated existing timesheet schema with approval rules';
    END IF;
END $$;

-- 2. 为已经 submitted 的 timesheet 创建审批任务
-- 按员工+月份分组，每组创建一个审批任务
DO $$
DECLARE
    v_company TEXT;
    v_user_id TEXT;
    v_month TEXT;
    v_object_id UUID;
    v_admin_user_id UUID;
    v_admin_email TEXT;
    v_count INT := 0;
BEGIN
    -- 获取所有已提交的 timesheet 按员工+月份分组
    FOR v_company, v_user_id, v_month IN
        SELECT DISTINCT 
            company_code,
            payload->>'creatorUserId' as user_id,
            to_char((payload->>'date')::date, 'YYYY-MM') as month
        FROM timesheets
        WHERE payload->>'status' IN ('submitted', 'draft')
          AND payload->>'creatorUserId' IS NOT NULL
          AND payload->>'creatorUserId' != ''
    LOOP
        -- 计算 objectId（与后端逻辑一致：MD5(userId|month)）
        v_object_id := md5(v_user_id || '|' || v_month)::uuid;
        
        -- 检查是否已存在审批任务
        IF NOT EXISTS (
            SELECT 1 FROM approval_tasks 
            WHERE company_code = v_company 
              AND entity = 'timesheet' 
              AND object_id = v_object_id
        ) THEN
            -- 查找 admin 角色的用户作为审批人
            SELECT u.id, COALESCE(e.payload->>'contact.email', '') INTO v_admin_user_id, v_admin_email
            FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id
            LEFT JOIN role_caps rc ON rc.role_id = r.id
            LEFT JOIN employees e ON e.company_code = u.company_code AND e.payload->>'code' = u.employee_code
            WHERE u.company_code = v_company
              AND u.is_active = true
              AND (r.role_code = 'admin' OR rc.cap = 'admin')
            LIMIT 1;
            
            IF v_admin_user_id IS NOT NULL THEN
                -- 创建审批任务
                INSERT INTO approval_tasks (
                    company_code, entity, object_id, step_no, step_name, 
                    approver_user_id, approver_email, status
                ) VALUES (
                    v_company, 'timesheet', v_object_id, 1, 
                    '勤怠承認 ' || v_month,
                    v_admin_user_id::text, v_admin_email, 'pending'
                );
                v_count := v_count + 1;
                RAISE NOTICE 'Created approval task for user % month % in company %', v_user_id, v_month, v_company;
            ELSE
                RAISE NOTICE 'No admin user found for company %, skipping', v_company;
            END IF;
        ELSE
            RAISE NOTICE 'Approval task already exists for user % month %', v_user_id, v_month;
        END IF;
    END LOOP;
    
    RAISE NOTICE 'Total approval tasks created: %', v_count;
END $$;

-- 3. 将所有 draft 状态的 timesheet 更新为 submitted
UPDATE timesheets
SET payload = payload || '{"status": "submitted"}'::jsonb,
    updated_at = now()
WHERE payload->>'status' = 'draft';

-- 显示结果
SELECT 'Timesheet approval setup completed' as message;

-- 验证创建的审批任务
SELECT 
    company_code,
    entity,
    step_name,
    approver_user_id,
    status,
    created_at
FROM approval_tasks
WHERE entity = 'timesheet'
ORDER BY created_at DESC
LIMIT 20;

-- ============================================================================
-- 人才派遣模块 Schema 定义
-- 运行方式: psql -h localhost -U postgres -d your_db -f seed_staffing_schemas.sql
-- ============================================================================

-- 1. 资源池 (resource)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL, -- 全局 schema
    'resource',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["resource_code", "display_name", "resource_type"],
        "properties": {
            "resource_code": { "type": "string", "maxLength": 20, "description": "资源编号" },
            "display_name": { "type": "string", "maxLength": 100, "description": "显示名称" },
            "resource_type": { 
                "type": "string", 
                "enum": ["employee", "freelancer", "bp", "candidate"],
                "description": "资源类型"
            },
            "email": { "type": "string", "format": "email" },
            "phone": { "type": "string" },
            "skills": { 
                "type": "array", 
                "items": { "type": "string" },
                "description": "技能标签"
            },
            "experience_summary": { "type": "string", "description": "经验摘要" },
            "resume_url": { "type": "string", "format": "uri" },
            "status": { 
                "type": "string", 
                "enum": ["active", "inactive", "blacklist"],
                "default": "active"
            },
            "availability_status": { 
                "type": "string", 
                "enum": ["available", "assigned", "ending_soon", "unavailable"],
                "default": "available"
            },
            "available_from": { "type": "string", "format": "date" },
            "current_assignment_end": { "type": "string", "format": "date" },
            "hourly_rate": { "type": "number", "minimum": 0 },
            "monthly_rate": { "type": "number", "minimum": 0 },
            "employee_id": { "type": "string", "format": "uuid" },
            "partner_id": { "type": "string", "format": "uuid" }
        }
    }'::jsonb,
    '{
        "title": "资源池管理",
        "titleJa": "リソースプール管理",
        "listColumns": [
            { "field": "resource_code", "label": "编号", "labelJa": "コード", "width": 100 },
            { "field": "display_name", "label": "名称", "labelJa": "名前", "width": 150 },
            { "field": "resource_type", "label": "类型", "labelJa": "タイプ", "width": 100, 
              "options": [
                  {"value": "employee", "label": "自社社员", "labelJa": "自社社員"},
                  {"value": "freelancer", "label": "个人事业主", "labelJa": "個人事業主"},
                  {"value": "bp", "label": "BP要员", "labelJa": "BP要員"},
                  {"value": "candidate", "label": "候选人", "labelJa": "候補者"}
              ]
            },
            { "field": "skills", "label": "技能", "labelJa": "スキル", "type": "tags" },
            { "field": "availability_status", "label": "可用状态", "labelJa": "稼働状況", "width": 100,
              "options": [
                  {"value": "available", "label": "可用", "labelJa": "可能", "color": "success"},
                  {"value": "assigned", "label": "稼働中", "labelJa": "稼働中", "color": "primary"},
                  {"value": "ending_soon", "label": "即将结束", "labelJa": "終了予定", "color": "warning"},
                  {"value": "unavailable", "label": "不可用", "labelJa": "不可", "color": "danger"}
              ]
            },
            { "field": "monthly_rate", "label": "月额", "labelJa": "月額", "type": "currency" }
        ],
        "formSections": [
            {
                "title": "基本信息",
                "titleJa": "基本情報",
                "fields": ["resource_code", "display_name", "resource_type", "email", "phone"]
            },
            {
                "title": "技能与经验",
                "titleJa": "スキル・経験",
                "fields": ["skills", "experience_summary", "resume_url"]
            },
            {
                "title": "可用性",
                "titleJa": "稼働状況",
                "fields": ["status", "availability_status", "available_from", "current_assignment_end"]
            },
            {
                "title": "费率",
                "titleJa": "単価",
                "fields": ["hourly_rate", "monthly_rate"]
            },
            {
                "title": "关联",
                "titleJa": "関連",
                "fields": ["employee_id", "partner_id"]
            }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["resource_type", "availability_status", "status"],
        "searchFields": ["resource_code", "display_name", "skills"],
        "defaultSort": "-created_at"
    }'::jsonb,
    '["resource_code", "display_name", "resource_type"]'::jsonb,
    NULL,
    '{
        "prefix": "RS-",
        "sequence": "seq_resource_code",
        "padding": 4,
        "field": "resource_code"
    }'::jsonb,
    '{
        "description": "人力资源池，管理自社社员、个人事业主、BP要员和候选人",
        "useCases": ["人员检索", "技能匹配", "可用性查询"]
    }'::jsonb
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 2. 案件 (staffing_project)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_project',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["project_code", "project_name", "client_partner_id"],
        "properties": {
            "project_code": { "type": "string", "maxLength": 30 },
            "project_name": { "type": "string", "maxLength": 200 },
            "client_partner_id": { "type": "string", "format": "uuid", "description": "客户(取引先)" },
            "client_contact_name": { "type": "string" },
            "client_contact_email": { "type": "string", "format": "email" },
            "required_skills": { "type": "array", "items": { "type": "string" } },
            "experience_years": { "type": "integer", "minimum": 0 },
            "job_description": { "type": "string" },
            "headcount": { "type": "integer", "minimum": 1, "default": 1 },
            "budget_min": { "type": "number" },
            "budget_max": { "type": "number" },
            "work_location": { "type": "string" },
            "remote_policy": { 
                "type": "string", 
                "enum": ["full_remote", "hybrid", "onsite"],
                "default": "onsite"
            },
            "start_date": { "type": "string", "format": "date" },
            "end_date": { "type": "string", "format": "date" },
            "duration_months": { "type": "integer" },
            "status": { 
                "type": "string", 
                "enum": ["draft", "open", "matching", "filled", "closed", "cancelled"],
                "default": "draft"
            },
            "priority": { 
                "type": "string", 
                "enum": ["high", "medium", "low"],
                "default": "medium"
            },
            "filled_count": { "type": "integer", "default": 0 },
            "source_type": { "type": "string", "enum": ["email", "phone", "referral", "website"] },
            "source_email_id": { "type": "string", "format": "uuid" }
        }
    }'::jsonb,
    '{
        "title": "案件管理",
        "titleJa": "案件管理",
        "listColumns": [
            { "field": "project_code", "label": "案件编号", "labelJa": "案件コード", "width": 120 },
            { "field": "project_name", "label": "案件名称", "labelJa": "案件名", "width": 200 },
            { "field": "client_partner_id", "label": "客户", "labelJa": "クライアント", "type": "relation", "relation": "businesspartner" },
            { "field": "required_skills", "label": "需求技能", "labelJa": "必要スキル", "type": "tags" },
            { "field": "headcount", "label": "募集人数", "labelJa": "募集人数", "width": 80 },
            { "field": "budget_max", "label": "预算上限", "labelJa": "予算上限", "type": "currency" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100,
              "options": [
                  {"value": "draft", "label": "草稿", "labelJa": "下書き", "color": "info"},
                  {"value": "open", "label": "募集中", "labelJa": "募集中", "color": "success"},
                  {"value": "matching", "label": "选考中", "labelJa": "選考中", "color": "warning"},
                  {"value": "filled", "label": "已入场", "labelJa": "充足", "color": "primary"},
                  {"value": "closed", "label": "已关闭", "labelJa": "終了", "color": "default"},
                  {"value": "cancelled", "label": "已取消", "labelJa": "キャンセル", "color": "danger"}
              ]
            },
            { "field": "start_date", "label": "开始日期", "labelJa": "開始日", "type": "date" }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "priority", "client_partner_id"],
        "searchFields": ["project_code", "project_name", "required_skills"],
        "defaultSort": "-created_at"
    }'::jsonb,
    '["project_code", "project_name", "client_partner_id"]'::jsonb,
    NULL,
    '{
        "prefix": "PJ-",
        "dateFormat": "YYYYMM",
        "sequence": "seq_project_code",
        "padding": 4,
        "field": "project_code"
    }'::jsonb,
    '{
        "description": "客户的人员需求案件",
        "useCases": ["案件登记", "候选人匹配", "进度跟踪"]
    }'::jsonb
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 3. 案件候选人 (staffing_project_candidate)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_project_candidate',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["project_id", "resource_id"],
        "properties": {
            "project_id": { "type": "string", "format": "uuid" },
            "resource_id": { "type": "string", "format": "uuid" },
            "recommended_at": { "type": "string", "format": "date-time" },
            "recommended_by": { "type": "string", "format": "uuid" },
            "proposed_rate": { "type": "number" },
            "final_rate": { "type": "number" },
            "status": { 
                "type": "string", 
                "enum": ["recommended", "client_review", "interviewing", "offered", "accepted", "rejected", "withdrawn"],
                "default": "recommended"
            },
            "interview_date": { "type": "string", "format": "date-time" },
            "interview_feedback": { "type": "string" },
            "rejection_reason": { "type": "string" },
            "result_note": { "type": "string" },
            "contract_id": { "type": "string", "format": "uuid" }
        }
    }'::jsonb,
    '{
        "title": "案件候选人",
        "titleJa": "案件候補者",
        "listColumns": [
            { "field": "resource_id", "label": "候选人", "labelJa": "候補者", "type": "relation", "relation": "resource" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100 },
            { "field": "proposed_rate", "label": "提案单价", "labelJa": "提案単価", "type": "currency" },
            { "field": "interview_date", "label": "面试日期", "labelJa": "面談日", "type": "datetime" }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "project_id", "resource_id"],
        "defaultSort": "-recommended_at"
    }'::jsonb,
    '["project_id", "resource_id"]'::jsonb,
    NULL,
    NULL,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 4. 契约 (staffing_contract)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_contract',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["contract_no", "contract_type", "resource_id", "client_partner_id", "start_date", "billing_rate"],
        "properties": {
            "contract_no": { "type": "string", "maxLength": 30 },
            "contract_type": { 
                "type": "string", 
                "enum": ["dispatch", "ses", "contract"],
                "description": "契约类型"
            },
            "resource_id": { "type": "string", "format": "uuid" },
            "client_partner_id": { "type": "string", "format": "uuid" },
            "project_id": { "type": "string", "format": "uuid" },
            "start_date": { "type": "string", "format": "date" },
            "end_date": { "type": "string", "format": "date" },
            "auto_renew": { "type": "boolean", "default": false },
            "billing_rate": { "type": "number", "minimum": 0 },
            "billing_unit": { "type": "string", "enum": ["monthly", "hourly"], "default": "monthly" },
            "cost_rate": { "type": "number", "minimum": 0 },
            "cost_unit": { "type": "string", "enum": ["monthly", "hourly"], "default": "monthly" },
            "settlement_type": { 
                "type": "string", 
                "enum": ["fixed", "hourly", "range"],
                "default": "fixed"
            },
            "settlement_min_hours": { "type": "number" },
            "settlement_max_hours": { "type": "number" },
            "overtime_rate": { "type": "number", "default": 1.25 },
            "status": { 
                "type": "string", 
                "enum": ["draft", "pending_approval", "active", "suspended", "completed", "terminated"],
                "default": "draft"
            },
            "dispatch_license_no": { "type": "string" },
            "dispatch_start_date": { "type": "string", "format": "date" }
        }
    }'::jsonb,
    '{
        "title": "契约管理",
        "titleJa": "契約管理",
        "listColumns": [
            { "field": "contract_no", "label": "契约编号", "labelJa": "契約番号", "width": 120 },
            { "field": "contract_type", "label": "类型", "labelJa": "タイプ", "width": 80,
              "options": [
                  {"value": "dispatch", "label": "派遣", "labelJa": "派遣"},
                  {"value": "ses", "label": "SES", "labelJa": "SES"},
                  {"value": "contract", "label": "请负", "labelJa": "請負"}
              ]
            },
            { "field": "resource_id", "label": "资源", "labelJa": "リソース", "type": "relation", "relation": "resource" },
            { "field": "client_partner_id", "label": "客户", "labelJa": "クライアント", "type": "relation", "relation": "businesspartner" },
            { "field": "billing_rate", "label": "请求单价", "labelJa": "請求単価", "type": "currency" },
            { "field": "start_date", "label": "开始日", "labelJa": "開始日", "type": "date" },
            { "field": "end_date", "label": "终了日", "labelJa": "終了日", "type": "date" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100 }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "contract_type", "resource_id", "client_partner_id"],
        "searchFields": ["contract_no"],
        "defaultSort": "-start_date"
    }'::jsonb,
    '["contract_no", "contract_type", "resource_id", "client_partner_id"]'::jsonb,
    NULL,
    '{
        "prefix": "CT-",
        "dateFormat": "YYYY",
        "sequence": "seq_contract_code",
        "padding": 4,
        "field": "contract_no"
    }'::jsonb,
    '{
        "description": "派遣/SES/请负契约管理",
        "useCases": ["契约登记", "精算管理", "续签跟踪"]
    }'::jsonb
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 5. 勤怠集计 (staffing_timesheet_summary)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_timesheet_summary',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["contract_id", "resource_id", "year_month"],
        "properties": {
            "contract_id": { "type": "string", "format": "uuid" },
            "resource_id": { "type": "string", "format": "uuid" },
            "year_month": { "type": "string", "pattern": "^\\d{4}-\\d{2}$" },
            "scheduled_hours": { "type": "number", "default": 0 },
            "actual_hours": { "type": "number", "default": 0 },
            "overtime_hours": { "type": "number", "default": 0 },
            "billable_hours": { "type": "number", "default": 0 },
            "settlement_hours": { "type": "number", "default": 0 },
            "settlement_adjustment": { "type": "number", "default": 0 },
            "base_amount": { "type": "number", "default": 0 },
            "overtime_amount": { "type": "number", "default": 0 },
            "adjustment_amount": { "type": "number", "default": 0 },
            "total_billing_amount": { "type": "number", "default": 0 },
            "total_cost_amount": { "type": "number", "default": 0 },
            "status": { 
                "type": "string", 
                "enum": ["open", "confirmed", "invoiced", "closed"],
                "default": "open"
            },
            "submitted_at": { "type": "string", "format": "date-time" },
            "confirmed_at": { "type": "string", "format": "date-time" }
        }
    }'::jsonb,
    '{
        "title": "勤怠集计",
        "titleJa": "勤怠集計",
        "listColumns": [
            { "field": "year_month", "label": "年月", "labelJa": "年月", "width": 100 },
            { "field": "resource_id", "label": "资源", "labelJa": "リソース", "type": "relation", "relation": "resource" },
            { "field": "contract_id", "label": "契约", "labelJa": "契約", "type": "relation", "relation": "staffing_contract" },
            { "field": "actual_hours", "label": "实际工时", "labelJa": "実労働時間", "type": "number" },
            { "field": "overtime_hours", "label": "残业时间", "labelJa": "残業時間", "type": "number" },
            { "field": "total_billing_amount", "label": "请求总额", "labelJa": "請求総額", "type": "currency" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 80 }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "year_month", "contract_id", "resource_id"],
        "defaultSort": "-year_month"
    }'::jsonb,
    '["contract_id", "resource_id", "year_month"]'::jsonb,
    NULL,
    NULL,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 6. 请求书 (staffing_invoice)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_invoice',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["invoice_no", "client_partner_id", "billing_year_month"],
        "properties": {
            "invoice_no": { "type": "string", "maxLength": 30 },
            "client_partner_id": { "type": "string", "format": "uuid" },
            "billing_year_month": { "type": "string", "pattern": "^\\d{4}-\\d{2}$" },
            "billing_period_start": { "type": "string", "format": "date" },
            "billing_period_end": { "type": "string", "format": "date" },
            "subtotal": { "type": "number" },
            "tax_rate": { "type": "number", "default": 0.10 },
            "tax_amount": { "type": "number" },
            "total_amount": { "type": "number" },
            "invoice_date": { "type": "string", "format": "date" },
            "due_date": { "type": "string", "format": "date" },
            "status": { 
                "type": "string", 
                "enum": ["draft", "confirmed", "issued", "sent", "paid", "partial_paid", "overdue", "cancelled"],
                "default": "draft"
            },
            "confirmed_at": { "type": "string", "format": "date-time" },
            "issued_at": { "type": "string", "format": "date-time" },
            "sent_at": { "type": "string", "format": "date-time" },
            "sent_method": { "type": "string", "enum": ["email", "mail", "portal"] },
            "paid_amount": { "type": "number", "default": 0 },
            "last_payment_date": { "type": "string", "format": "date" },
            "document_url": { "type": "string", "format": "uri" },
            "notes": { "type": "string" }
        }
    }'::jsonb,
    '{
        "title": "请求书管理",
        "titleJa": "請求書管理",
        "listColumns": [
            { "field": "invoice_no", "label": "请求书号", "labelJa": "請求書番号", "width": 120 },
            { "field": "client_partner_id", "label": "客户", "labelJa": "クライアント", "type": "relation", "relation": "businesspartner" },
            { "field": "billing_year_month", "label": "请求年月", "labelJa": "請求年月", "width": 100 },
            { "field": "total_amount", "label": "请求总额", "labelJa": "請求総額", "type": "currency" },
            { "field": "due_date", "label": "支付期限", "labelJa": "支払期限", "type": "date" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100 },
            { "field": "paid_amount", "label": "已入金", "labelJa": "入金済", "type": "currency" }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "client_partner_id", "billing_year_month"],
        "searchFields": ["invoice_no"],
        "defaultSort": "-billing_year_month"
    }'::jsonb,
    '["invoice_no", "client_partner_id", "billing_year_month"]'::jsonb,
    NULL,
    '{
        "prefix": "INV-",
        "dateFormat": "YYYYMM",
        "sequence": "seq_staffing_invoice",
        "padding": 4,
        "field": "invoice_no"
    }'::jsonb,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 7. 邮件模板 (staffing_email_template)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_email_template',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["template_code", "template_name", "subject_template", "body_template"],
        "properties": {
            "template_code": { "type": "string", "maxLength": 50 },
            "template_name": { "type": "string", "maxLength": 100 },
            "category": { 
                "type": "string", 
                "enum": ["project", "contract", "invoice", "timesheet", "general"],
                "default": "general"
            },
            "subject_template": { "type": "string" },
            "body_template": { "type": "string" },
            "variables": { "type": "array", "items": { "type": "object" } },
            "is_active": { "type": "boolean", "default": true }
        }
    }'::jsonb,
    '{
        "title": "邮件模板",
        "titleJa": "メールテンプレート",
        "listColumns": [
            { "field": "template_code", "label": "模板编号", "labelJa": "コード", "width": 120 },
            { "field": "template_name", "label": "模板名称", "labelJa": "名前", "width": 200 },
            { "field": "category", "label": "分类", "labelJa": "カテゴリ", "width": 100 },
            { "field": "is_active", "label": "启用", "labelJa": "有効", "type": "boolean" }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["category", "is_active"],
        "searchFields": ["template_code", "template_name"],
        "defaultSort": "template_code"
    }'::jsonb,
    '["template_code", "template_name"]'::jsonb,
    NULL,
    NULL,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 8. 个人事业主注文书 (staffing_purchase_order)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_purchase_order',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["order_no", "resource_id", "order_date", "period_start", "period_end", "unit_price"],
        "properties": {
            "order_no": { "type": "string", "maxLength": 30 },
            "resource_id": { "type": "string", "format": "uuid" },
            "contract_id": { "type": "string", "format": "uuid" },
            "order_date": { "type": "string", "format": "date" },
            "period_start": { "type": "string", "format": "date" },
            "period_end": { "type": "string", "format": "date" },
            "unit_price": { "type": "number" },
            "settlement_type": { "type": "string", "enum": ["monthly", "hourly", "range"], "default": "monthly" },
            "min_hours": { "type": "number" },
            "max_hours": { "type": "number" },
            "status": { 
                "type": "string", 
                "enum": ["draft", "sent", "accepted", "rejected"],
                "default": "draft"
            },
            "sent_at": { "type": "string", "format": "date-time" },
            "accepted_at": { "type": "string", "format": "date-time" },
            "document_url": { "type": "string", "format": "uri" }
        }
    }'::jsonb,
    '{
        "title": "注文书",
        "titleJa": "注文書",
        "listColumns": [
            { "field": "order_no", "label": "注文书号", "labelJa": "注文書番号", "width": 120 },
            { "field": "resource_id", "label": "个人事业主", "labelJa": "個人事業主", "type": "relation", "relation": "resource" },
            { "field": "period_start", "label": "开始日", "labelJa": "開始日", "type": "date" },
            { "field": "period_end", "label": "终了日", "labelJa": "終了日", "type": "date" },
            { "field": "unit_price", "label": "单价", "labelJa": "単価", "type": "currency" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100 }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "resource_id"],
        "searchFields": ["order_no"],
        "defaultSort": "-order_date"
    }'::jsonb,
    '["order_no", "resource_id"]'::jsonb,
    NULL,
    '{
        "prefix": "PO-",
        "dateFormat": "YYYYMM",
        "sequence": "seq_purchase_order",
        "padding": 4,
        "field": "order_no"
    }'::jsonb,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 9. 个人事业主请求书 (staffing_freelancer_invoice)
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
    NULL,
    'staffing_freelancer_invoice',
    1,
    TRUE,
    '{
        "type": "object",
        "required": ["invoice_no", "resource_id", "period_start", "period_end", "total_amount"],
        "properties": {
            "invoice_no": { "type": "string", "maxLength": 30 },
            "resource_id": { "type": "string", "format": "uuid" },
            "period_start": { "type": "string", "format": "date" },
            "period_end": { "type": "string", "format": "date" },
            "subtotal": { "type": "number" },
            "tax_rate": { "type": "number", "default": 0.10 },
            "tax_amount": { "type": "number" },
            "total_amount": { "type": "number" },
            "status": { 
                "type": "string", 
                "enum": ["draft", "submitted", "approved", "rejected", "paid"],
                "default": "draft"
            },
            "submitted_at": { "type": "string", "format": "date-time" },
            "approved_at": { "type": "string", "format": "date-time" },
            "paid_at": { "type": "string", "format": "date-time" },
            "paid_amount": { "type": "number" },
            "document_url": { "type": "string", "format": "uri" }
        }
    }'::jsonb,
    '{
        "title": "个人事业主请求书",
        "titleJa": "フリーランス請求書",
        "listColumns": [
            { "field": "invoice_no", "label": "请求书号", "labelJa": "請求書番号", "width": 120 },
            { "field": "resource_id", "label": "个人事业主", "labelJa": "個人事業主", "type": "relation", "relation": "resource" },
            { "field": "period_start", "label": "期间开始", "labelJa": "期間開始", "type": "date" },
            { "field": "period_end", "label": "期间结束", "labelJa": "期間終了", "type": "date" },
            { "field": "total_amount", "label": "请求总额", "labelJa": "請求総額", "type": "currency" },
            { "field": "status", "label": "状态", "labelJa": "状態", "width": 100 }
        ]
    }'::jsonb,
    '{
        "allowedFilters": ["status", "resource_id"],
        "searchFields": ["invoice_no"],
        "defaultSort": "-period_start"
    }'::jsonb,
    '["invoice_no", "resource_id"]'::jsonb,
    NULL,
    '{
        "prefix": "FI-",
        "dateFormat": "YYYYMM",
        "sequence": "seq_freelancer_invoice",
        "padding": 4,
        "field": "invoice_no"
    }'::jsonb,
    NULL
)
ON CONFLICT (name, company_code, version) DO NOTHING;

-- 添加唯一约束（如果不存在）
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'schemas_name_company_version_key'
    ) THEN
        ALTER TABLE schemas ADD CONSTRAINT schemas_name_company_version_key 
            UNIQUE (name, company_code, version);
    END IF;
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Constraint already exists or other error: %', SQLERRM;
END $$;


-- 销售告警和任务表
-- 用于存储销售监控检测到的异常和对应的处理任务

-- 销售告警表
CREATE TABLE IF NOT EXISTS sales_alerts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    alert_type TEXT NOT NULL, -- overdue_delivery, overdue_payment, customer_churn, inventory_shortage
    severity TEXT NOT NULL DEFAULT 'medium', -- low, medium, high, critical
    status TEXT NOT NULL DEFAULT 'open', -- open, acknowledged, resolved, dismissed
    
    -- 关联信息
    so_no TEXT,
    delivery_no TEXT,
    invoice_no TEXT,
    customer_code TEXT,
    customer_name TEXT,
    material_code TEXT,
    material_name TEXT,
    
    -- 告警详情
    title TEXT NOT NULL,
    description TEXT,
    amount NUMERIC(18,2),
    due_date DATE,
    overdue_days INTEGER,
    
    -- 处理信息
    assigned_to TEXT,
    task_id UUID,
    resolved_at TIMESTAMPTZ,
    resolved_by TEXT,
    resolution_note TEXT,
    
    -- 通知状态
    notified_wecom BOOLEAN DEFAULT false,
    notified_at TIMESTAMPTZ,
    
    -- 扩展数据
    payload JSONB DEFAULT '{}',
    
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_sales_alerts_company_status ON sales_alerts(company_code, status);
CREATE INDEX IF NOT EXISTS idx_sales_alerts_company_type ON sales_alerts(company_code, alert_type);
CREATE INDEX IF NOT EXISTS idx_sales_alerts_customer ON sales_alerts(company_code, customer_code);
CREATE INDEX IF NOT EXISTS idx_sales_alerts_created ON sales_alerts(company_code, created_at DESC);

-- 告警任务表
CREATE TABLE IF NOT EXISTS alert_tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    alert_id UUID REFERENCES sales_alerts(id) ON DELETE CASCADE,
    task_type TEXT NOT NULL, -- follow_up, collection, restock, contact
    title TEXT NOT NULL,
    description TEXT,
    priority TEXT DEFAULT 'medium', -- low, medium, high, urgent
    status TEXT DEFAULT 'pending', -- pending, in_progress, completed, cancelled
    assigned_to TEXT,
    due_date DATE,
    completed_at TIMESTAMPTZ,
    completed_by TEXT,
    completion_note TEXT,
    payload JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 创建索引
CREATE INDEX IF NOT EXISTS idx_alert_tasks_company_status ON alert_tasks(company_code, status);
CREATE INDEX IF NOT EXISTS idx_alert_tasks_assigned ON alert_tasks(company_code, assigned_to, status);
CREATE INDEX IF NOT EXISTS idx_alert_tasks_alert ON alert_tasks(alert_id);

-- 监控规则配置表
CREATE TABLE IF NOT EXISTS sales_monitor_rules (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    rule_type TEXT NOT NULL, -- overdue_delivery, overdue_payment, customer_churn, inventory_shortage
    rule_name TEXT NOT NULL,
    is_active BOOLEAN DEFAULT true,
    
    -- 规则参数
    params JSONB NOT NULL DEFAULT '{}',
    -- 例如:
    -- overdue_delivery: { "thresholdDays": 0 }
    -- overdue_payment: { "thresholdDays": 1 }
    -- customer_churn: { "inactiveDays": 30, "minOrdersInPeriod": 3, "lookbackDays": 180 }
    -- inventory_shortage: { "lookAheadDays": 14, "avgInboundDays": 90 }
    
    -- 通知设置
    notification_channels JSONB DEFAULT '["wecom", "task"]', -- wecom, email, task
    notification_users JSONB, -- 接收通知的用户列表
    
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_sales_monitor_rules ON sales_monitor_rules(company_code, rule_type);

-- 插入默认规则
INSERT INTO sales_monitor_rules (company_code, rule_type, rule_name, is_active, params)
VALUES
    ('JP01', 'overdue_delivery', '納期超過検知', true, '{"thresholdDays": 0}'),
    ('JP01', 'overdue_payment', '入金超過検知', true, '{"thresholdWorkDays": 1, "skipWeekends": true, "skipHolidays": true}'),
    ('JP01', 'customer_churn', '顧客離脱検知', true, '{"naturalLanguageQuery": "最近1ヶ月注文がない、過去半年に3回以上注文した顧客", "inactiveDays": 30, "minOrdersInPeriod": 3, "lookbackDays": 180}'),
    ('JP01', 'inventory_shortage', '在庫不足検知', true, '{"lookAheadDays": 14, "avgInboundDays": 30}')
ON CONFLICT (company_code, rule_type) DO NOTHING;

-- 显示创建结果
SELECT 'sales_alerts' as table_name, COUNT(*) as row_count FROM sales_alerts
UNION ALL
SELECT 'alert_tasks', COUNT(*) FROM alert_tasks
UNION ALL
SELECT 'sales_monitor_rules', COUNT(*) FROM sales_monitor_rules;


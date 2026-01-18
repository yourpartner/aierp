-- 为 JP01 公司创建 Moneytree 银行明细定时同步任务
-- 每天日本时间 8:00 和 18:00 执行同步，同步最近 7 天的数据

INSERT INTO scheduler_tasks (company_code, payload, status, next_run_at, created_at, updated_at) 
VALUES (
    'JP01', 
    '{
        "nlSpec": "每天8点和18点同步银行明细",
        "plan": {
            "action": "moneytree.sync",
            "daysBack": 7
        },
        "schedule": {
            "kind": "daily",
            "timezone": "Asia/Tokyo",
            "times": ["08:00", "18:00"]
        },
        "status": "pending"
    }'::jsonb, 
    'pending', 
    now(), 
    now(), 
    now()
)
ON CONFLICT DO NOTHING
RETURNING id, company_code, payload;


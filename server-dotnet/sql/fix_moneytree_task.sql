-- 将 Moneytree 定时任务状态恢复为 pending，重置重试计数器
UPDATE scheduler_tasks 
SET payload = jsonb_set(
    jsonb_set(payload, '{status}', '"pending"'),
    '{result,retry,attempts}', '0'
),
next_run_at = now() + interval '1 minute'
WHERE id = '36149f93-34d0-4ef3-8e86-8afd4d093d3d';

-- 验证更新结果
SELECT 
    id, 
    company_code, 
    status, 
    next_run_at, 
    payload->>'status' as payload_status, 
    payload->'result'->'retry'->>'attempts' as retry_attempts 
FROM scheduler_tasks 
WHERE id = '36149f93-34d0-4ef3-8e86-8afd4d093d3d';

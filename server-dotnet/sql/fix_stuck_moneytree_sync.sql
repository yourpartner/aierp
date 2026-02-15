-- =========================================================================
-- 诊断和修复卡死的 Moneytree 同步定时任务
-- 问题：如果服务重启时任务正在执行，status 会卡在 'running'，
--       调度器只拾取 status='pending' 的任务，导致永久停止。
-- =========================================================================

-- Step 1: 诊断 - 查看 moneytree.sync 任务的当前状态
SELECT id, company_code, status, 
       payload->>'status' AS payload_status,
       next_run_at,
       last_run_at,
       locked_by,
       locked_at,
       payload->'result'->>'lastSuccessEnd' AS last_success_end,
       updated_at
FROM scheduler_tasks
WHERE payload->'plan'->>'action' = 'moneytree.sync';

-- Step 2: 修复 - 将卡死的任务恢复为 pending 状态，并设置立即执行
UPDATE scheduler_tasks
SET status = 'pending',
    payload = jsonb_set(
      jsonb_set(COALESCE(payload, '{}'::jsonb), '{status}', '"pending"'),
      '{result,retry,attempts}', '0'
    ),
    next_run_at = now(),  -- 立即执行
    locked_by = NULL,
    locked_at = NULL,
    updated_at = now()
WHERE payload->'plan'->>'action' = 'moneytree.sync'
  AND (status != 'pending' OR locked_by IS NOT NULL);

-- Step 3: 确认修复结果
SELECT id, company_code, status,
       payload->>'status' AS payload_status,
       next_run_at,
       locked_by
FROM scheduler_tasks
WHERE payload->'plan'->>'action' = 'moneytree.sync';

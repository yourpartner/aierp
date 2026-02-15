-- =========================================================================
-- 诊断和修复卡死的 Moneytree 同步定时任务 (Azure 生产环境版)
-- 注意：在 Azure 环境中，status 是基于 payload->'status' 的生成列
-- =========================================================================

-- Step 1: 诊断
SELECT id, company_code, status, 
       payload->>'status' AS payload_status,
       next_run_at,
       last_run_at,
       locked_by,
       locked_at,
       updated_at
FROM scheduler_tasks
WHERE payload->'plan'->>'action' = 'moneytree.sync';

-- Step 2: 修复 - 通过更新 payload 来改变生成列 status 的值
UPDATE scheduler_tasks
SET payload = jsonb_set(
      jsonb_set(COALESCE(payload, '{}'::jsonb), '{status}', '"pending"'),
      '{result,retry,attempts}', '0'
    ),
    next_run_at = now(),  -- 立即执行
    locked_by = NULL,
    locked_at = NULL,
    updated_at = now()
WHERE payload->'plan'->>'action' = 'moneytree.sync'
  AND (payload->>'status' != 'pending' OR locked_by IS NOT NULL);

-- Step 3: 确认修复结果
SELECT id, company_code, status,
       payload->>'status' AS payload_status,
       next_run_at,
       locked_by
FROM scheduler_tasks
WHERE payload->'plan'->>'action' = 'moneytree.sync';

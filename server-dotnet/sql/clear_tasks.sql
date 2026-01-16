-- 清除任务面板中的所有任务（新表）
-- 注意：会同时清除与任务关联的消息、会话，便于从 0 开始测试。

DELETE FROM ai_messages;
DELETE FROM ai_messages_archive;
DELETE FROM ai_tasks;
DELETE FROM ai_sessions;

SELECT 'Tasks + chat cleared successfully' as result;


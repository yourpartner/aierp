-- 清除 chatbot 中的所有消息和会话
DELETE FROM ai_messages;
DELETE FROM ai_messages_archive;
DELETE FROM ai_sessions;

SELECT 'Chat history cleared successfully' as result;


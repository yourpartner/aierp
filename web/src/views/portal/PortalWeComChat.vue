<template>
  <div class="wecom-chat-page">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><ChatDotRound /></el-icon>
        <h1>企业微信 AI助手 (测试)</h1>
      </div>
      <div class="header-right">
        <el-input v-model="userId" placeholder="WeChat User ID" style="width: 200px" size="small" />
      </div>
    </div>

    <div class="chat-container">
      <!-- Chat Messages -->
      <div class="chat-messages" ref="chatMessagesRef">
        <div class="system-message">
          <div class="system-bubble">
            这是企业微信员工 AI 助手的测试界面。您可以模拟员工在企业微信中发送消息。<br><br>
            可以尝试以下指令：<br>
            • 今天 9:00-18:00 (录入工时)<br>
            • 本周一到五 9:00-18:00 (批量录入)<br>
            • 查看本月工时<br>
            • 提交本月工时<br>
            • 查看工资<br>
            • 申请在职证明
          </div>
        </div>
        
        <template v-for="(msg, i) in messages" :key="i">
          <div :class="['message-row', msg.direction === 'out' ? 'ai-message' : 'user-message']">
            <div class="avatar">
              <el-icon v-if="msg.direction === 'out'"><Monitor /></el-icon>
              <el-icon v-else><User /></el-icon>
            </div>
            <div class="message-content">
              <div class="bubble" :class="msg.direction === 'out' ? 'ai-bubble' : 'user-bubble'">
                <pre class="message-text">{{ msg.content }}</pre>
              </div>
              <div class="message-meta">
                <span v-if="msg.intent" class="intent-tag">{{ msg.intent }}</span>
                <span class="time">{{ msg.time }}</span>
              </div>
            </div>
          </div>
        </template>

        <div v-if="sending" class="message-row ai-message">
          <div class="avatar"><el-icon><Monitor /></el-icon></div>
          <div class="message-content">
            <div class="bubble ai-bubble">
              <div class="typing-indicator">
                <span></span><span></span><span></span>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Input Area -->
      <div class="chat-input-area">
        <el-input
          v-model="inputText"
          placeholder="输入消息..."
          @keyup.enter="sendMessage"
          :disabled="sending"
          size="large"
        >
          <template #append>
            <el-button :icon="Promotion" @click="sendMessage" :loading="sending" type="primary" />
          </template>
        </el-input>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { ArrowLeft, ChatDotRound, Monitor, User, Promotion } from '@element-plus/icons-vue'
import api from '../../api'

interface ChatMessage {
  direction: 'in' | 'out'
  content: string
  intent?: string
  time: string
}

const userId = ref('test-employee-001')
const inputText = ref('')
const sending = ref(false)
const messages = ref<ChatMessage[]>([])
const chatMessagesRef = ref<HTMLElement>()

const scrollToBottom = async () => {
  await nextTick()
  if (chatMessagesRef.value) {
    chatMessagesRef.value.scrollTop = chatMessagesRef.value.scrollHeight
  }
}

const sendMessage = async () => {
  const text = inputText.value.trim()
  if (!text || sending.value) return

  // Add user message
  messages.value.push({
    direction: 'in',
    content: text,
    time: new Date().toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })
  })
  inputText.value = ''
  sending.value = true
  await scrollToBottom()

  try {
    const res = await api.post('/portal/wecom-employee-message', {
      userId: userId.value,
      content: text,
      msgType: 'text'
    })

    messages.value.push({
      direction: 'out',
      content: res.data.reply,
      intent: res.data.intent,
      time: new Date().toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })
    })
  } catch (e: any) {
    messages.value.push({
      direction: 'out',
      content: '系统错误：' + (e.response?.data?.error || e.message),
      intent: 'error',
      time: new Date().toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })
    })
    ElMessage.error('发送失败')
  } finally {
    sending.value = false
    await scrollToBottom()
  }
}
</script>

<style scoped>
.wecom-chat-page {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.back-link {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: white;
  color: #606266;
  text-decoration: none;
  transition: all 0.2s;
}

.back-link:hover {
  background: #ecf5ff;
  color: var(--el-color-primary);
}

.header-icon {
  font-size: 24px;
  color: #07c160;
}

.header-left h1 {
  margin: 0;
  font-size: 20px;
}

.chat-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  background: white;
  border-radius: 12px;
  overflow: hidden;
  box-shadow: 0 2px 12px rgba(0,0,0,0.06);
  max-height: calc(100vh - 140px);
}

.chat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
  background: #ebebeb;
}

.system-message {
  display: flex;
  justify-content: center;
  margin-bottom: 16px;
}

.system-bubble {
  background: rgba(0,0,0,0.05);
  color: #666;
  padding: 12px 16px;
  border-radius: 8px;
  font-size: 13px;
  line-height: 1.6;
  max-width: 400px;
}

.message-row {
  display: flex;
  gap: 10px;
  margin-bottom: 16px;
}

.message-row.user-message {
  flex-direction: row-reverse;
}

.avatar {
  width: 36px;
  height: 36px;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  font-size: 18px;
}

.user-message .avatar {
  background: #07c160;
  color: white;
}

.ai-message .avatar {
  background: white;
  color: #07c160;
  box-shadow: 0 1px 3px rgba(0,0,0,0.1);
}

.message-content {
  max-width: 70%;
}

.bubble {
  padding: 10px 14px;
  border-radius: 4px;
  line-height: 1.5;
  word-break: break-word;
}

.user-bubble {
  background: #95ec69;
  color: #000;
  border-radius: 4px 0 4px 4px;
}

.ai-bubble {
  background: white;
  color: #333;
  border-radius: 0 4px 4px 4px;
  box-shadow: 0 1px 2px rgba(0,0,0,0.05);
}

.message-text {
  margin: 0;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  white-space: pre-wrap;
  font-size: 14px;
}

.message-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 4px;
  font-size: 11px;
  color: #999;
}

.user-message .message-meta {
  justify-content: flex-end;
}

.intent-tag {
  background: rgba(0,0,0,0.06);
  padding: 1px 6px;
  border-radius: 3px;
  font-size: 10px;
}

.typing-indicator {
  display: flex;
  gap: 4px;
  padding: 4px 0;
}

.typing-indicator span {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #ccc;
  animation: typing 1.4s infinite ease-in-out;
}

.typing-indicator span:nth-child(2) { animation-delay: 0.2s; }
.typing-indicator span:nth-child(3) { animation-delay: 0.4s; }

@keyframes typing {
  0%, 100% { transform: translateY(0); opacity: 0.4; }
  50% { transform: translateY(-4px); opacity: 1; }
}

.chat-input-area {
  padding: 12px 16px;
  background: #f7f7f7;
  border-top: 1px solid #e5e5e5;
}

.chat-input-area :deep(.el-input__wrapper) {
  border-radius: 20px;
}

.chat-input-area :deep(.el-input-group__append) {
  border-radius: 0 20px 20px 0;
  padding: 0 16px;
}
</style>

<template>
  <div class="wechat-container">
    <div class="wechat-header">
      <div class="back-btn">&lt;</div>
      <div class="title">企業アシスタント</div>
      <div class="more-btn">...</div>
    </div>
    
    <div class="chat-area" ref="chatArea">
      <div class="message system">
        <div class="content">企業アシスタントへようこそ！下のメニューからサービスをご利用ください。</div>
      </div>
      
      <div v-for="(msg, index) in messages" :key="index" :class="['message', msg.type]">
        <div class="avatar" v-if="msg.type === 'received'">
          <div class="avatar-icon">企</div>
        </div>
        <div class="content" v-if="!msg.isCard && !msg.isFile">{{ msg.text }}</div>
        
        <!-- 动态生成的卡片消息 -->
        <div class="content push-card" v-if="msg.isCard">
          <div class="card-title">{{ msg.cardData?.title }}</div>
          <div class="card-date">{{ msg.cardData?.date }}</div>
          <div class="card-body">
            <p v-if="msg.cardData?.desc">{{ msg.cardData?.desc }}</p>
            <template v-if="msg.cardData?.items">
              <div class="card-item" v-for="(item, i) in msg.cardData.items" :key="i">
                <span>{{ item.label }}</span>
                <span :class="{ highlight: item.highlight }">{{ item.value }}</span>
              </div>
            </template>
          </div>
          <div class="card-footer" @click="handleAction(msg.cardData?.action || 'payslip')">
            <span>詳細を確認する</span>
            <span>&gt;</span>
          </div>
        </div>

        <!-- 动态生成的文件消息 -->
        <div class="content file-card" v-if="msg.isFile">
          <div class="file-info">
            <div class="file-name">{{ msg.fileData?.name }}</div>
            <div class="file-size">{{ msg.fileData?.size }}</div>
          </div>
          <div class="file-icon">📄</div>
        </div>
        
        <div class="avatar" v-if="msg.type === 'sent'">
          <div class="avatar-icon user">私</div>
        </div>
      </div>
    </div>
    
    <div class="bottom-menu">
      <div class="keyboard-btn">
        <span class="icon-keyboard">⌨️</span>
      </div>
      <div class="menu-btn" @click="toggleMenu('salary')">
        <span>給与照会</span>
        <div class="sub-menu" v-if="activeMenu === 'salary'">
          <div class="sub-item" @click.stop="handleAction('payslip_current')">当月分を確認</div>
          <div class="sub-item" @click.stop="handleAction('payslip_history')">過去の明細</div>
        </div>
      </div>
      <div class="menu-divider"></div>
      <div class="menu-btn" @click="toggleMenu('hr')">
        <span>人事サービス</span>
        <div class="sub-menu" v-if="activeMenu === 'hr'">
          <div class="sub-item" @click.stop="handleAction('timesheet')">勤怠入力</div>
          <div class="sub-item" @click.stop="handleAction('cert')">各種証明書</div>
        </div>
      </div>
      <div class="menu-divider"></div>
      <div class="menu-btn" @click="handleAction('dashboard')">
        <span>マイページ</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const activeMenu = ref<string | null>(null)
const chatArea = ref<HTMLElement | null>(null)

interface Message {
  type: 'sent' | 'received' | 'system';
  text: string;
  isCard?: boolean;
  cardData?: any;
  isFile?: boolean;
  fileData?: {
    name: string;
    size: string;
    type: string;
  };
}

const messages = ref<Message[]>([])

const scrollToBottom = () => {
  nextTick(() => {
    if (chatArea.value) {
      chatArea.value.scrollTop = chatArea.value.scrollHeight
    }
  })
}

const initScenario = () => {
  messages.value = []
  const scenario = route.query.scenario
  
  if (scenario === 'payslip' || !scenario) {
    // 默认或工资单场景：推送工资单卡片
    messages.value.push({
      type: 'received',
      text: '',
      isCard: true,
      cardData: {
        title: '給与明細発行のお知らせ',
        date: '2026年02月分',
        desc: '今月の給与明細が発行されました。以下のボタンからご確認ください。',
        items: [
          { label: '差引支給額', value: '¥ 325,000', highlight: true },
          { label: '支給日', value: '2026-03-01' }
        ],
        action: 'payslip_push'
      }
    })
  } else if (scenario === 'cert') {
    // 证明书申请场景：预先展示申请流程
    messages.value.push({ type: 'sent', text: '在職証明書を発行してほしいです。' })
    messages.value.push({ type: 'received', text: '証明書の発行リクエストを受け付けました。担当者が確認後、3営業日以内にこちらへPDFでお送りします。しばらくお待ちください。' })
    messages.value.push({ type: 'received', text: 'お待たせいたしました。在職証明書の発行が完了しました。' })
    messages.value.push({
      type: 'received',
      text: '',
      isFile: true,
      fileData: { name: '在職証明書_20260302.pdf', size: '156 KB', type: 'pdf' }
    })
  }
  scrollToBottom()
}

onMounted(() => {
  initScenario()
})

watch(() => route.query.scenario, () => {
  initScenario()
})

const toggleMenu = (menu: string) => {
  if (activeMenu.value === menu) {
    activeMenu.value = null
  } else {
    activeMenu.value = menu
  }
}

const handleAction = (action: string) => {
  activeMenu.value = null
  
  if (action === 'payslip_push' || action === 'payslip') {
    // 触发自定义事件，通知父组件切换到详情视图
    const event = new CustomEvent('navigate-to-detail', { detail: { view: 'payslip', platform: 'wechat' } })
    window.dispatchEvent(event)
    return
  }
  
  let actionText = ''
  let replyText = ''
  let isCardReply = false
  let cardData = null
  let targetView = ''
  
  switch(action) {
    case 'payslip_current': 
      actionText = '当月分の給与明細を確認'; 
      isCardReply = true;
      cardData = {
        title: '給与明細照会結果',
        date: '2026年02月分',
        desc: '2026年02月分の給与明細が見つかりました。以下のボタンからご確認ください。',
        action: 'payslip'
      };
      break;
    case 'payslip_history': 
      actionText = '過去の明細を確認'; 
      replyText = '給与明細履歴ページを開いています...';
      targetView = 'payslip_history';
      break;
    case 'timesheet': 
      actionText = '勤怠を入力'; 
      replyText = '勤怠入力ページを開いています...';
      targetView = 'timesheet';
      break;
    case 'cert': 
      actionText = '在職証明書を発行してほしいです。'; 
      replyText = '証明書の発行リクエストを受け付けました。担当者が確認後、3営業日以内にこちらへPDFでお送りします。しばらくお待ちください。';
      
      // 模拟管理员审批并发送 PDF
      setTimeout(() => {
        messages.value.push({ type: 'received', text: 'お待たせいたしました。在職証明書の発行が完了しました。' })
        scrollToBottom()
        setTimeout(() => {
          messages.value.push({
            type: 'received',
            text: '',
            isFile: true,
            fileData: { name: '在職証明書_20260302.pdf', size: '156 KB', type: 'pdf' }
          })
          scrollToBottom()
        }, 500)
      }, 3000)
      break;
    case 'dashboard': 
      actionText = 'マイページを開く'; 
      replyText = 'マイページを開いています...';
      targetView = 'dashboard';
      break;
  }
  
  messages.value.push({ type: 'sent', text: actionText })
  scrollToBottom()
  
  setTimeout(() => {
    if (isCardReply) {
      messages.value.push({ 
        type: 'received', 
        text: '',
        isCard: true,
        cardData: cardData
      })
    } else {
      messages.value.push({ type: 'received', text: replyText })
    }
    scrollToBottom()
    
    if (targetView) {
      setTimeout(() => {
        const event = new CustomEvent('navigate-to-detail', { detail: { view: targetView, platform: 'wechat' } })
        window.dispatchEvent(event)
      }, 1000)
    }
  }, 600)
}
</script>

<style scoped>
.wechat-container {
  display: flex;
  flex-direction: column;
  height: 100%;
  background-color: #ededed;
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
}

.wechat-header {
  height: 44px;
  background-color: #ededed;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 16px;
  border-bottom: 1px solid #e5e5e5;
  font-size: 17px;
  font-weight: 500;
  position: sticky;
  top: 0;
  z-index: 10;
}

.back-btn, .more-btn {
  font-size: 20px;
  cursor: pointer;
}

.chat-area {
  flex: 1;
  padding: 16px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.message {
  display: flex;
  max-width: 80%;
}

.message.system {
  align-self: center;
  max-width: 90%;
  background-color: #dadada;
  color: #fff;
  padding: 4px 8px;
  border-radius: 4px;
  font-size: 12px;
}

.message.received {
  align-self: flex-start;
}

.message.sent {
  align-self: flex-end;
  flex-direction: row-reverse;
}

.avatar {
  width: 40px;
  height: 40px;
  border-radius: 4px;
  background-color: #3370ff;
  margin: 0 8px;
  flex-shrink: 0;
  display: flex;
  justify-content: center;
  align-items: center;
}

.avatar-icon {
  color: white;
  font-size: 20px;
  font-weight: bold;
}

.avatar-icon.user {
  color: #333;
}

.message.received .avatar {
  background-color: #3370ff;
}

.message.sent .avatar {
  background-color: #f0f0f0;
}

.message .content {
  padding: 10px 14px;
  border-radius: 8px;
  font-size: 15px;
  line-height: 1.4;
  word-break: break-all;
  position: relative;
}

.message.received .content {
  background-color: #fff;
  border: 1px solid #e5e5e5;
}

.message.sent .content {
  background-color: #95ec69;
}

/* 消息卡片样式 */
.message .content.push-card {
  padding: 0;
  width: 240px;
  background-color: #fff;
  border-radius: 8px;
  overflow: hidden;
}

/* 文件消息样式 */
.message .content.file-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 200px;
  background-color: #fff;
  border: 1px solid #e5e5e5;
  border-radius: 4px;
  padding: 12px;
}

.file-info {
  flex: 1;
  overflow: hidden;
}

.file-name {
  font-size: 15px;
  color: #333;
  white-space: nowrap;
  text-overflow: ellipsis;
  overflow: hidden;
  margin-bottom: 4px;
}

.file-size {
  font-size: 12px;
  color: #999;
}

.file-icon {
  font-size: 32px;
  margin-left: 12px;
}

.card-title {
  padding: 12px 16px 4px;
  font-size: 16px;
  font-weight: 500;
  color: #333;
}

.card-date {
  padding: 0 16px 12px;
  font-size: 12px;
  color: #999;
}

.card-body {
  padding: 0 16px 12px;
  font-size: 14px;
  color: #666;
}

.card-body p {
  margin: 0 0 12px 0;
}

.card-item {
  display: flex;
  justify-content: space-between;
  margin-bottom: 6px;
  font-size: 13px;
}

.card-item .highlight {
  color: #ff4d4f;
  font-weight: 500;
  font-size: 15px;
}

.card-footer {
  padding: 12px 16px;
  border-top: 1px solid #f0f0f0;
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  color: #333;
  cursor: pointer;
}

.card-footer:active {
  background-color: #f9f9f9;
}

.bottom-menu {
  height: 50px;
  background-color: #f7f7f7;
  border-top: 1px solid #e5e5e5;
  display: flex;
  align-items: center;
  position: relative;
}

.keyboard-btn {
  width: 40px;
  height: 100%;
  display: flex;
  justify-content: center;
  align-items: center;
  border-right: 1px solid #e5e5e5;
  cursor: pointer;
}

.icon-keyboard {
  font-size: 20px;
  color: #666;
}

.menu-btn {
  flex: 1;
  height: 100%;
  display: flex;
  justify-content: center;
  align-items: center;
  font-size: 15px;
  position: relative;
  cursor: pointer;
}

.menu-divider {
  width: 1px;
  height: 24px;
  background-color: #e5e5e5;
}

.sub-menu {
  position: absolute;
  bottom: 60px;
  left: 50%;
  transform: translateX(-50%);
  background-color: #fff;
  border: 1px solid #e5e5e5;
  border-radius: 4px;
  box-shadow: 0 2px 8px rgba(0,0,0,0.1);
  width: 120px;
  display: flex;
  flex-direction: column;
}

.sub-menu::after {
  content: '';
  position: absolute;
  bottom: -6px;
  left: 50%;
  transform: translateX(-50%);
  border-left: 6px solid transparent;
  border-right: 6px solid transparent;
  border-top: 6px solid #fff;
}

.sub-item {
  padding: 12px 0;
  text-align: center;
  border-bottom: 1px solid #f0f0f0;
  font-size: 14px;
}

.sub-item:last-child {
  border-bottom: none;
}
</style>

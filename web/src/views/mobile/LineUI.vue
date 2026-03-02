<template>
  <div class="line-container">
    <div class="line-header">
      <div class="back-btn">&lt;</div>
      <div class="title-area">
        <div class="logo"></div>
        <div class="title">会社公式アカウント</div>
      </div>
      <div class="more-btn">≡</div>
    </div>
    
    <div class="chat-area" ref="chatArea">
      <div class="message system">
        <div class="content">今日</div>
      </div>
      
      <div class="message received">
        <div class="avatar"></div>
        <div class="message-content">
          <div class="name">会社公式アカウント</div>
          <div class="bubble">ようこそ！以下のリッチメニューからサービスをご利用ください。</div>
        </div>
      </div>
      
      <div v-for="(msg, index) in messages" :key="index" :class="['message', msg.type]">
        <div class="avatar" v-if="msg.type === 'received'"></div>
        <div class="message-content" v-if="msg.type === 'received'">
          <div class="name">会社公式アカウント</div>
          
          <div class="bubble" v-if="!msg.isCard && !msg.isFile">{{ msg.text }}</div>
          
          <!-- 动态生成的卡片消息 -->
          <div class="bubble card-bubble" v-if="msg.isCard">
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
            <div class="card-action" @click="handleAction(msg.cardData?.action || 'payslip')">詳細を確認する</div>
          </div>

          <!-- 动态生成的文件消息 -->
          <div class="bubble file-bubble" v-if="msg.isFile">
            <div class="file-icon-wrapper"><span class="icon">📄</span></div>
            <div class="file-info">
              <div class="file-name">{{ msg.fileData?.name }}</div>
              <div class="file-size">{{ msg.fileData?.size }}</div>
            </div>
          </div>
        </div>
        <div class="bubble" v-else>{{ msg.text }}</div>
      </div>
    </div>
    
    <div class="rich-menu-container">
      <div class="rich-menu-toggle" @click="isMenuOpen = !isMenuOpen">
        メニュー {{ isMenuOpen ? '▼' : '▲' }}
      </div>
      <div class="rich-menu" v-show="isMenuOpen">
        <div class="menu-grid">
          <div class="menu-item" @click="showSubMenu('salary')">
            <div class="icon">💰</div>
            <div>給与照会</div>
          </div>
          <div class="menu-item" @click="handleAction('timesheet')">
            <div class="icon">⏱️</div>
            <div>勤怠入力</div>
          </div>
          <div class="menu-item" @click="handleAction('cert')">
            <div class="icon">📄</div>
            <div>各種証明書</div>
          </div>
          <div class="menu-item" @click="handleAction('dashboard')">
            <div class="icon">👤</div>
            <div>マイページ</div>
          </div>
          <div class="menu-item" @click="handleAction('help')">
            <div class="icon">❓</div>
            <div>ヘルプ</div>
          </div>
          <div class="menu-item" @click="handleAction('settings')">
            <div class="icon">⚙️</div>
            <div>設定</div>
          </div>
        </div>
      </div>
      
      <!-- 模拟 Line 的子菜单弹窗 -->
      <div class="action-sheet-overlay" v-if="activeSubMenu" @click="activeSubMenu = null">
        <div class="action-sheet" @click.stop>
          <div class="action-title">給与照会</div>
          <div class="action-item" @click="handleAction('payslip_current')">当月分を確認</div>
          <div class="action-item" @click="handleAction('payslip_history')">過去の明細を確認</div>
          <div class="action-cancel" @click="activeSubMenu = null">キャンセル</div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const isMenuOpen = ref(true)
const activeSubMenu = ref<string | null>(null)
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

const showSubMenu = (menu: string) => {
  activeSubMenu.value = menu
}

const handleAction = (action: string) => {
  activeSubMenu.value = null
  
  if (action === 'payslip_push' || action === 'payslip') {
    const event = new CustomEvent('navigate-to-detail', { detail: { view: 'payslip', platform: 'line' } })
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
      actionText = '在職証明書の発行をお願いします。'; 
      replyText = 'リクエストを受け付けました！\n3営業日以内に発行し、このトークルームでお渡しします。少々お待ちください🙇';
      
      // 模拟管理员审批并发送 PDF
      setTimeout(() => {
        messages.value.push({ type: 'received', text: 'お待たせいたしました✨\n在職証明書の発行が完了しました。以下のファイルをご確認ください。' })
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
    case 'help': 
      actionText = 'ヘルプセンター'; 
      replyText = 'ヘルプセンターを開いています...';
      break;
    case 'settings': 
      actionText = '設定'; 
      replyText = '設定ページを開いています...';
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
        const event = new CustomEvent('navigate-to-detail', { detail: { view: targetView, platform: 'line' } })
        window.dispatchEvent(event)
      }, 1000)
    }
  }, 600)
}
</script>

<style scoped>
.line-container {
  display: flex;
  flex-direction: column;
  height: 100%;
  background-color: #849ebf; /* Line chat background color */
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  position: relative;
}

.line-header {
  height: 50px;
  background-color: #2c3e50;
  color: white;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 16px;
  position: sticky;
  top: 0;
  z-index: 10;
}

.title-area {
  display: flex;
  align-items: center;
  gap: 8px;
}

.logo {
  width: 28px;
  height: 28px;
  background-color: #00c300;
  border-radius: 50%;
}

.title {
  font-size: 16px;
  font-weight: bold;
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
  max-width: 85%;
}

.message.system {
  align-self: center;
  max-width: 90%;
  background-color: rgba(0,0,0,0.2);
  color: #fff;
  padding: 4px 12px;
  border-radius: 12px;
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
  border-radius: 50%;
  background-color: #00c300;
  margin-right: 8px;
  flex-shrink: 0;
}

.message-content {
  display: flex;
  flex-direction: column;
}

.name {
  font-size: 12px;
  color: #fff;
  margin-bottom: 4px;
}

.bubble {
  padding: 10px 14px;
  border-radius: 16px;
  font-size: 15px;
  line-height: 1.4;
  word-break: break-all;
  position: relative;
}

.message.received .bubble {
  background-color: #fff;
  border-top-left-radius: 4px;
}

.message.sent .bubble {
  background-color: #85e249;
  border-top-right-radius: 4px;
}

/* Line 卡片消息样式 */
.bubble.card-bubble {
  padding: 0;
  width: 240px;
  overflow: hidden;
}

/* 文件消息样式 */
.bubble.file-bubble {
  display: flex;
  align-items: center;
  width: 220px;
  padding: 12px;
  background-color: #fff;
  border-radius: 12px;
}

.file-icon-wrapper {
  width: 40px;
  height: 40px;
  background-color: #f0f0f0;
  border-radius: 8px;
  display: flex;
  justify-content: center;
  align-items: center;
  margin-right: 12px;
  font-size: 20px;
}

.file-info {
  flex: 1;
  overflow: hidden;
}

.file-name {
  font-size: 14px;
  color: #111;
  font-weight: bold;
  white-space: nowrap;
  text-overflow: ellipsis;
  overflow: hidden;
  margin-bottom: 4px;
}

.file-size {
  font-size: 12px;
  color: #888;
}

.card-title {
  padding: 12px 16px 4px;
  font-size: 15px;
  font-weight: bold;
  color: #333;
}

.card-date {
  padding: 0 16px 12px;
  font-size: 12px;
  color: #999;
}

.card-body {
  padding: 0 16px 12px;
  font-size: 13px;
  color: #666;
}

.card-body p {
  margin: 0 0 12px 0;
}

.card-item {
  display: flex;
  justify-content: space-between;
  margin-bottom: 6px;
}

.card-item .highlight {
  color: #e53935;
  font-weight: bold;
  font-size: 14px;
}

.card-action {
  padding: 12px;
  text-align: center;
  border-top: 1px solid #eee;
  color: #00c300;
  font-weight: bold;
  font-size: 14px;
  cursor: pointer;
  background-color: #fafafa;
}

.card-action:active {
  background-color: #f0f0f0;
}

.rich-menu-container {
  background-color: #fff;
  border-top: 1px solid #e5e5e5;
  position: relative;
}

.rich-menu-toggle {
  height: 30px;
  display: flex;
  justify-content: center;
  align-items: center;
  font-size: 12px;
  color: #666;
  background-color: #f5f5f5;
  cursor: pointer;
}

.rich-menu {
  height: 220px;
  padding: 10px;
}

.menu-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  grid-template-rows: repeat(2, 1fr);
  gap: 10px;
  height: 100%;
}

.menu-item {
  background-color: #f8f9fa;
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: center;
  font-size: 13px;
  color: #333;
  cursor: pointer;
  box-shadow: 0 1px 3px rgba(0,0,0,0.1);
}

.menu-item:active {
  background-color: #e9ecef;
}

.icon {
  font-size: 24px;
  margin-bottom: 8px;
}

/* Action Sheet 样式 */
.action-sheet-overlay {
  position: absolute;
  top: -600px; /* 覆盖整个聊天区域 */
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0,0,0,0.4);
  z-index: 100;
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
}

.action-sheet {
  background-color: #f2f2f2;
  border-top-left-radius: 12px;
  border-top-right-radius: 12px;
  overflow: hidden;
}

.action-title {
  padding: 12px;
  text-align: center;
  font-size: 12px;
  color: #888;
  background-color: #fff;
  border-bottom: 1px solid #eee;
}

.action-item {
  padding: 16px;
  text-align: center;
  font-size: 16px;
  color: #007aff;
  background-color: #fff;
  border-bottom: 1px solid #eee;
  cursor: pointer;
}

.action-item:active {
  background-color: #e5e5e5;
}

.action-cancel {
  margin-top: 8px;
  padding: 16px;
  text-align: center;
  font-size: 16px;
  font-weight: bold;
  color: #007aff;
  background-color: #fff;
  cursor: pointer;
}
</style>

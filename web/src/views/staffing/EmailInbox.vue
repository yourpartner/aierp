<template>
  <div class="email-inbox">
    <div class="inbox-header">
      <div class="header-left">
        <el-icon class="header-icon"><Message /></el-icon>
        <h1>メール受信箱</h1>
      </div>
      <div class="header-right">
        <el-select v-model="filters.status" placeholder="ステータス" clearable style="width: 120px">
          <el-option label="新規" value="new" />
          <el-option label="解析済" value="parsed" />
          <el-option label="処理済" value="processed" />
        </el-select>
        <el-button type="primary" @click="syncEmails" :loading="syncing">
          <el-icon><Refresh /></el-icon>
          同期
        </el-button>
      </div>
    </div>

    <div class="inbox-layout">
      <!-- 邮件列表 -->
      <div class="email-list" v-loading="loading">
        <div 
          v-for="email in emails" 
          :key="email.id" 
          class="email-item"
          :class="{ 'unread': !email.isRead, 'selected': selectedEmail?.id === email.id }"
          @click="selectEmail(email)"
        >
          <div class="email-sender">
            <span class="sender-name">{{ email.fromName || email.fromAddress }}</span>
            <span class="email-time">{{ formatTime(email.receivedAt) }}</span>
          </div>
          <div class="email-subject">{{ email.subject || '(件名なし)' }}</div>
          <div class="email-preview">{{ getPreview(email.bodyText) }}</div>
          <div class="email-tags">
            <el-tag v-if="email.parsedIntent" size="small" :type="getIntentType(email.parsedIntent)">
              {{ getIntentLabel(email.parsedIntent) }}
            </el-tag>
            <el-tag v-if="email.linkedEntityType" size="small" type="success">
              {{ email.linkedEntityType }}
            </el-tag>
          </div>
        </div>
        <el-empty v-if="emails.length === 0" description="メールがありません" />
      </div>

      <!-- 邮件详情 -->
      <div class="email-detail" v-if="selectedEmail">
        <div class="detail-header">
          <h2>{{ selectedEmail.subject || '(件名なし)' }}</h2>
          <div class="detail-actions">
            <el-button size="small" @click="parseEmail(selectedEmail.id)" :loading="parsing">
              <el-icon><MagicStick /></el-icon>
              AI解析
            </el-button>
            <el-button size="small" type="primary" @click="replyEmail(selectedEmail)">
              <el-icon><ChatLineRound /></el-icon>
              返信
            </el-button>
          </div>
        </div>

        <div class="detail-meta">
          <div class="meta-row">
            <span class="meta-label">差出人:</span>
            <span>{{ selectedEmail.fromName }} &lt;{{ selectedEmail.fromAddress }}&gt;</span>
          </div>
          <div class="meta-row">
            <span class="meta-label">宛先:</span>
            <span>{{ selectedEmail.toAddresses }}</span>
          </div>
          <div class="meta-row">
            <span class="meta-label">受信日時:</span>
            <span>{{ formatDateTime(selectedEmail.receivedAt) }}</span>
          </div>
        </div>

        <!-- AI解析结果 -->
        <div class="parsed-result" v-if="selectedEmail.parsedIntent">
          <div class="result-header">
            <el-icon><DataAnalysis /></el-icon>
            AI解析結果
          </div>
          <div class="result-content">
            <div class="result-item">
              <span class="result-label">意図:</span>
              <el-tag :type="getIntentType(selectedEmail.parsedIntent)">
                {{ getIntentLabel(selectedEmail.parsedIntent) }}
              </el-tag>
            </div>
            <div class="result-item" v-if="selectedEmail.parsedData">
              <span class="result-label">抽出データ:</span>
              <pre>{{ JSON.stringify(JSON.parse(selectedEmail.parsedData), null, 2) }}</pre>
            </div>
            <div class="result-actions" v-if="selectedEmail.parsedIntent === 'project_request'">
              <el-button size="small" type="success">
                <el-icon><Plus /></el-icon>
                案件として登録
              </el-button>
            </div>
          </div>
        </div>

        <el-divider />

        <div class="detail-body" v-html="selectedEmail.bodyHtml || formatPlainText(selectedEmail.bodyText)"></div>
      </div>
      <div class="email-detail-empty" v-else>
        <el-empty description="メールを選択してください" />
      </div>
    </div>

    <!-- 返信ダイアログ -->
    <el-dialog v-model="replyDialogVisible" title="メール返信" width="600px">
      <el-form :model="replyForm" label-position="top">
        <el-form-item label="宛先">
          <el-input v-model="replyForm.to" />
        </el-form-item>
        <el-form-item label="件名">
          <el-input v-model="replyForm.subject" />
        </el-form-item>
        <el-form-item label="テンプレート">
          <el-select v-model="replyForm.templateCode" placeholder="テンプレートを選択" clearable style="width: 100%">
            <el-option 
              v-for="tpl in templates" 
              :key="tpl.templateCode" 
              :label="tpl.templateName" 
              :value="tpl.templateCode" 
            />
          </el-select>
        </el-form-item>
        <el-form-item label="本文">
          <el-input v-model="replyForm.body" type="textarea" :rows="8" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="replyDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="sendReply" :loading="sending">送信</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Message, Refresh, MagicStick, ChatLineRound, DataAnalysis, Plus } from '@element-plus/icons-vue'
import api from '../../api'

interface Email {
  id: string
  fromAddress: string
  fromName?: string
  toAddresses?: string
  subject?: string
  bodyText?: string
  bodyHtml?: string
  receivedAt: string
  status: string
  isRead: boolean
  parsedIntent?: string
  parsedData?: string
  linkedEntityType?: string
  linkedEntityId?: string
}

const loading = ref(false)
const syncing = ref(false)
const parsing = ref(false)
const sending = ref(false)

const emails = ref<Email[]>([])
const selectedEmail = ref<Email | null>(null)
const templates = ref<any[]>([])

const filters = reactive({
  status: ''
})

const replyDialogVisible = ref(false)
const replyForm = reactive({
  to: '',
  subject: '',
  templateCode: '',
  body: ''
})

const loadEmails = async () => {
  loading.value = true
  try {
    const params: any = { folder: 'inbox' }
    if (filters.status) params.status = filters.status
    const res = await api.get('/staffing/email/inbox', { params })
    emails.value = res.data.data || []
  } catch (e: any) {
    console.error('Load emails error:', e)
  } finally {
    loading.value = false
  }
}

const loadTemplates = async () => {
  try {
    const res = await api.get('/staffing/email/templates')
    templates.value = res.data.data || []
  } catch (e) {
    console.error('Load templates error:', e)
  }
}

const syncEmails = async () => {
  syncing.value = true
  try {
    // TODO: 调用同步API
    ElMessage.info('メール同期機能は実装中です')
    await loadEmails()
  } finally {
    syncing.value = false
  }
}

const selectEmail = async (email: Email) => {
  selectedEmail.value = email
  if (!email.isRead) {
    try {
      await api.put(`/staffing/email/inbox/${email.id}/read`)
      email.isRead = true
    } catch (e) {
      console.error('Mark read error:', e)
    }
  }
}

const parseEmail = async (id: string) => {
  parsing.value = true
  try {
    const res = await api.post(`/staffing/email/inbox/${id}/parse`)
    ElMessage.success('解析完了')
    if (selectedEmail.value) {
      selectedEmail.value.parsedIntent = res.data.intent
      selectedEmail.value.parsedData = JSON.stringify(res.data.matchedPartner)
    }
    await loadEmails()
  } catch (e: any) {
    ElMessage.error('解析失敗')
  } finally {
    parsing.value = false
  }
}

const replyEmail = (email: Email) => {
  replyForm.to = email.fromAddress
  replyForm.subject = `Re: ${email.subject || ''}`
  replyForm.body = ''
  replyForm.templateCode = ''
  replyDialogVisible.value = true
}

const sendReply = async () => {
  if (!replyForm.to || !replyForm.subject) {
    ElMessage.warning('宛先と件名を入力してください')
    return
  }
  sending.value = true
  try {
    await api.post('/staffing/email/send', {
      toAddresses: replyForm.to,
      subject: replyForm.subject,
      bodyHtml: replyForm.body,
      linkedEntityType: 'email',
      linkedEntityId: selectedEmail.value?.id
    })
    ElMessage.success('送信キューに追加しました')
    replyDialogVisible.value = false
  } catch (e: any) {
    ElMessage.error('送信失敗')
  } finally {
    sending.value = false
  }
}

const formatTime = (dateStr: string) => {
  const date = new Date(dateStr)
  const today = new Date()
  if (date.toDateString() === today.toDateString()) {
    return date.toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit' })
  }
  return date.toLocaleDateString('ja-JP', { month: 'short', day: 'numeric' })
}

const formatDateTime = (dateStr: string) => {
  return new Date(dateStr).toLocaleString('ja-JP')
}

const getPreview = (text?: string) => {
  if (!text) return ''
  return text.substring(0, 80) + (text.length > 80 ? '...' : '')
}

const formatPlainText = (text?: string) => {
  if (!text) return ''
  return text.replace(/\n/g, '<br>')
}

const getIntentLabel = (intent: string) => {
  const map: Record<string, string> = {
    project_request: '案件依頼',
    contract_confirm: '契約確認',
    invoice_related: '請求関連',
    payment_confirm: '入金確認',
    unknown: '不明'
  }
  return map[intent] || intent
}

const getIntentType = (intent: string) => {
  const map: Record<string, string> = {
    project_request: 'primary',
    contract_confirm: 'success',
    invoice_related: 'warning',
    payment_confirm: 'info',
    unknown: 'info'
  }
  return map[intent] || 'info'
}

onMounted(() => {
  loadEmails()
  loadTemplates()
})
</script>

<style scoped>
.email-inbox {
  padding: 20px;
  height: calc(100vh - 60px);
  display: flex;
  flex-direction: column;
}

.inbox-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.header-icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.header-left h1 {
  margin: 0;
  font-size: 20px;
}

.header-right {
  display: flex;
  gap: 12px;
}

.inbox-layout {
  flex: 1;
  display: flex;
  gap: 20px;
  overflow: hidden;
}

.email-list {
  width: 360px;
  flex-shrink: 0;
  overflow-y: auto;
  background: white;
  border-radius: 8px;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.06);
}

.email-item {
  padding: 16px;
  border-bottom: 1px solid #f0f0f0;
  cursor: pointer;
  transition: background 0.2s;
}

.email-item:hover {
  background: #f5f7fa;
}

.email-item.selected {
  background: #ecf5ff;
  border-left: 3px solid var(--el-color-primary);
}

.email-item.unread {
  background: #fafafa;
}

.email-item.unread .email-sender .sender-name {
  font-weight: 600;
}

.email-sender {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 4px;
}

.sender-name {
  font-size: 14px;
  color: #303133;
}

.email-time {
  font-size: 12px;
  color: #909399;
}

.email-subject {
  font-size: 13px;
  color: #606266;
  margin-bottom: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.email-preview {
  font-size: 12px;
  color: #909399;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.email-tags {
  display: flex;
  gap: 6px;
  margin-top: 8px;
}

.email-detail,
.email-detail-empty {
  flex: 1;
  background: white;
  border-radius: 8px;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.06);
  overflow-y: auto;
}

.email-detail {
  padding: 24px;
}

.email-detail-empty {
  display: flex;
  align-items: center;
  justify-content: center;
}

.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
}

.detail-header h2 {
  margin: 0;
  font-size: 18px;
  flex: 1;
}

.detail-actions {
  display: flex;
  gap: 8px;
}

.detail-meta {
  background: #f5f7fa;
  padding: 12px 16px;
  border-radius: 6px;
  margin-bottom: 16px;
}

.meta-row {
  display: flex;
  gap: 12px;
  font-size: 13px;
  margin-bottom: 4px;
}

.meta-row:last-child {
  margin-bottom: 0;
}

.meta-label {
  color: #909399;
  width: 60px;
  flex-shrink: 0;
}

.parsed-result {
  background: linear-gradient(135deg, #667eea10 0%, #764ba210 100%);
  border: 1px solid #667eea30;
  border-radius: 8px;
  padding: 16px;
  margin-bottom: 16px;
}

.result-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
  color: #667eea;
  margin-bottom: 12px;
}

.result-item {
  display: flex;
  gap: 12px;
  margin-bottom: 8px;
}

.result-label {
  color: #606266;
  font-size: 13px;
}

.result-item pre {
  margin: 0;
  font-size: 12px;
  background: white;
  padding: 8px;
  border-radius: 4px;
  max-height: 100px;
  overflow: auto;
}

.result-actions {
  margin-top: 12px;
}

.detail-body {
  font-size: 14px;
  line-height: 1.6;
  color: #303133;
}
</style>


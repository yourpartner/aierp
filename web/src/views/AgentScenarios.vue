<template>
  <div class="page page-large agent-scenarios">
    <!-- クイック作成カード -->
    <el-card class="quick-create-card" shadow="hover">
      <div class="quick-create-header">
        <div class="quick-create-icon">✨</div>
        <div class="quick-create-text">
          <h3>{{ text.tables.agentScenarios.quickCreateTitle || 'シナリオを素早く作成' }}</h3>
          <p>{{ text.tables.agentScenarios.quickCreateDesc || '自然言語で機能を説明すると、AIが自動設定します' }}</p>
        </div>
      </div>
      <div class="quick-create-input">
        <el-input
          v-model="quickPrompt"
          type="textarea"
          :rows="2"
          :placeholder="text.tables.agentScenarios.quickPlaceholder || '例：飲食店の領収書をアップロードすると、自動的に金額を認識して会議費の仕訳を作成'"
          resize="none"
        />
        <el-button 
          type="primary" 
          size="large"
          :loading="quickLoading" 
          :disabled="!quickPrompt.trim()"
          @click="quickCreate"
        >
          <el-icon class="btn-icon"><MagicStick /></el-icon>
          {{ text.tables.agentScenarios.quickCreateBtn || 'AI作成' }}
        </el-button>
      </div>
      
      <!-- テンプレート -->
      <div class="templates-section">
        <div class="templates-label">{{ text.tables.agentScenarios.templatesLabel || 'またはテンプレートを選択：' }}</div>
        <div class="templates-grid">
          <div 
            v-for="tpl in templates" 
            :key="tpl.key"
            class="template-item"
            @click="useTemplate(tpl)"
          >
            <div class="template-icon">{{ tpl.icon }}</div>
            <div class="template-name">{{ tpl.name }}</div>
          </div>
        </div>
      </div>
    </el-card>

    <!-- シナリオ一覧 -->
    <el-card class="scenarios-card">
      <template #header>
        <div class="panel-header">
          <div class="panel-title">
            {{ text.tables.agentScenarios.listTitle || '設定済みのシナリオ' }}
            <el-tag size="small" type="info">{{ scenarios.length }}</el-tag>
          </div>
          <div class="panel-actions">
            <el-button :loading="loading" @click="loadScenarios">
              <el-icon><Refresh /></el-icon>
            </el-button>
          </div>
        </div>
      </template>

      <div v-if="scenarios.length === 0 && !loading" class="empty-state">
        <el-empty :description="text.tables.agentScenarios.emptyDesc || 'シナリオが設定されていません'">
          <el-button type="primary" @click="openSimpleCreate">{{ text.tables.agentScenarios.createFirst || '最初のシナリオを作成' }}</el-button>
        </el-empty>
      </div>

      <div v-else class="scenarios-list">
        <div 
          v-for="item in scenarios" 
          :key="item.scenarioKey"
          class="scenario-item"
          :class="{ inactive: !item.isActive }"
        >
          <div class="scenario-main">
            <div class="scenario-header">
              <span class="scenario-title">{{ item.title }}</span>
              <el-tag v-if="!item.isActive" size="small" type="info">{{ text.common.disabled || '無効' }}</el-tag>
            </div>
            <div class="scenario-desc" v-if="item.description">{{ item.description }}</div>
            <div class="scenario-meta">
              <span class="meta-item" v-if="item.toolHints?.length">
                <el-icon><Tools /></el-icon>
                {{ item.toolHints.slice(0, 3).join(', ') }}
                <span v-if="item.toolHints.length > 3">+{{ item.toolHints.length - 3 }}</span>
              </span>
              <span class="meta-item">
                <el-icon><Timer /></el-icon>
                {{ formatDate(item.updatedAt) }}
              </span>
            </div>
          </div>
          <div class="scenario-actions">
            <el-button size="small" @click="openEdit(item)">
              <el-icon><Edit /></el-icon>
            </el-button>
            <el-button size="small" @click="toggleActive(item)">
              <el-icon><component :is="item.isActive ? 'VideoPause' : 'VideoPlay'" /></el-icon>
            </el-button>
            <el-popconfirm 
              :title="text.tables.agentScenarios.deleteConfirm || 'このシナリオを削除しますか？'" 
              @confirm="removeScenario(item)"
            >
              <template #reference>
                <el-button size="small" type="danger">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </template>
            </el-popconfirm>
          </div>
        </div>
      </div>
    </el-card>

    <!-- 作成/編集ダイアログ -->
    <el-dialog 
      v-model="editor.visible" 
      :title="editor.isNew ? (text.tables.agentScenarios.createTitle || 'シナリオ作成') : (text.tables.agentScenarios.editTitle || 'シナリオ編集')"
      width="600px"
      append-to-body
    >
      <el-form 
        ref="formRef"
        :model="form" 
        label-position="top" 
        class="simple-form"
      >
        <!-- シナリオ名 -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldName || 'シナリオ名'"
          prop="title"
          :rules="[{ required: true, message: text.tables.agentScenarios.nameRequired || 'シナリオ名を入力してください' }]"
        >
          <el-input 
            v-model="form.title" 
            :placeholder="text.tables.agentScenarios.namePlaceholder || '例：飲食店領収書の認識'"
            maxlength="60"
            show-word-limit
          />
        </el-form-item>

        <!-- トリガー条件 -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldTrigger || 'いつトリガーしますか？'"
          prop="trigger"
        >
          <el-input 
            v-model="form.trigger" 
            type="textarea"
            :rows="2"
            :placeholder="text.tables.agentScenarios.triggerPlaceholder || '例：ユーザーが領収書や請求書をアップロードした時'"
          />
          <div class="field-hint">{{ text.tables.agentScenarios.triggerHint || 'このシナリオを使用する条件を説明' }}</div>
        </el-form-item>

        <!-- 実行アクション -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldAction || 'AIは何をすべきですか？'"
          prop="action"
        >
          <el-input 
            v-model="form.action" 
            type="textarea"
            :rows="3"
            :placeholder="text.tables.agentScenarios.actionPlaceholder || '例：請求書の金額・日付・店舗名を認識し、会議費の仕訳伝票を作成'"
          />
          <div class="field-hint">{{ text.tables.agentScenarios.actionHint || 'AIが実行すべき具体的なタスクを記述' }}</div>
        </el-form-item>

        <!-- 詳細オプション（折りたたみ） -->
        <el-collapse v-model="advancedOpen" class="advanced-collapse">
          <el-collapse-item name="advanced">
            <template #title>
              <span class="advanced-title">
                <el-icon><Setting /></el-icon>
                {{ text.tables.agentScenarios.advancedOptions || '詳細オプション' }}
              </span>
            </template>
            <div class="advanced-content">
              <el-form-item :label="text.tables.agentScenarios.fieldKey || 'シナリオキー（一意識別子）'">
                <el-input 
                  v-model="form.scenarioKey" 
                  :disabled="!editor.isNew"
                  :placeholder="text.tables.agentScenarios.keyPlaceholder || '自動生成、手動指定も可能'"
                />
              </el-form-item>
              <div class="inline-fields">
                <el-form-item :label="text.tables.agentScenarios.fieldPriority || '優先度'">
                  <el-input-number v-model="form.priority" :min="1" :max="999" />
                </el-form-item>
                <el-form-item :label="text.tables.agentScenarios.fieldActive || '有効'">
                  <el-switch v-model="form.isActive" />
                </el-form-item>
              </div>
            </div>
          </el-collapse-item>
        </el-collapse>
      </el-form>

      <template #footer>
        <div class="dialog-footer">
          <el-button @click="editor.visible = false">{{ text.common.cancel || 'キャンセル' }}</el-button>
          <el-button type="primary" :loading="editor.saving" @click="saveScenario">
            <el-icon class="btn-icon"><Check /></el-icon>
            {{ text.common.save || '保存' }}
          </el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'
import { 
  MagicStick, Refresh, Edit, Delete, Setting, Check, Tools, Timer,
  VideoPlay, VideoPause
} from '@element-plus/icons-vue'
import { useI18n } from '../i18n'
import {
  createAgentScenario,
  deleteAgentScenario,
  listAgentScenarios,
  updateAgentScenario,
  interpretAgentScenario,
  type AgentScenario,
  type ScenarioInterpretResult
} from '../api/agentScenarios'

const { text } = useI18n()

// 状態
const loading = ref(false)
const scenarios = ref<AgentScenario[]>([])
const quickPrompt = ref('')
const quickLoading = ref(false)
const advancedOpen = ref<string[]>([])
const formRef = ref()

// テンプレート
const templates = [
  { key: 'invoice', icon: '🧾', name: '請求書認識', prompt: 'ユーザーが請求書画像をアップロードした時、請求書の内容（金額、日付、仕入先）を自動認識し、対応する仕訳伝票を作成' },
  { key: 'receipt', icon: '🍽️', name: '飲食費精算', prompt: 'ユーザーが飲食店の領収書をアップロードした時、消費金額と店舗情報を認識し、会議費または交際費の仕訳を作成' },
  { key: 'transport', icon: '🚗', name: '交通費', prompt: 'ユーザーがタクシー・電車・航空券をアップロードした時、交通費を認識し、旅費の仕訳を作成' },
  { key: 'sales', icon: '📦', name: '受注', prompt: 'ユーザーが自然言語で注文内容を記述した時（例：「A社にB製品10個の受注」）、自動的に受注伝票を作成' }
]

// エディター状態
const editor = reactive({
  visible: false,
  isNew: true,
  saving: false,
  originalKey: ''
})

// フォーム
const form = reactive({
  scenarioKey: '',
  title: '',
  trigger: '',
  action: '',
  priority: 100,
  isActive: true
})

// メソッド
function formatDate(value: string | null | undefined) {
  if (!value) return '-'
  return dayjs(value).format('MM-DD HH:mm')
}

function resetForm() {
  form.scenarioKey = ''
  form.title = ''
  form.trigger = ''
  form.action = ''
  form.priority = 100
  form.isActive = true
  advancedOpen.value = []
}

async function loadScenarios() {
  loading.value = true
  try {
    const resp = await listAgentScenarios(true)
    scenarios.value = (resp.data as AgentScenario[]) || []
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '読み込み失敗')
  } finally {
    loading.value = false
  }
}

function openSimpleCreate() {
  resetForm()
  editor.isNew = true
  editor.originalKey = ''
  editor.visible = true
}

function openEdit(item: AgentScenario) {
  resetForm()
  editor.isNew = false
  editor.originalKey = item.scenarioKey
  
  form.scenarioKey = item.scenarioKey
  form.title = item.title
  form.priority = item.priority ?? 100
  form.isActive = item.isActive ?? true
  
  // instructionsとdescriptionからtriggerとactionを復元
  form.trigger = extractTrigger(item)
  form.action = item.instructions || item.description || ''
  
  editor.visible = true
}

function extractTrigger(item: AgentScenario): string {
  // metadata.matcherからトリガー条件を抽出
  const metadata = item.metadata as any
  const matcher = metadata?.matcher
  if (matcher) {
    const parts: string[] = []
    if (matcher.messageContains?.length) {
      parts.push(`メッセージに含む：${matcher.messageContains.join('、')}`)
    }
    if (matcher.mimeTypes?.length) {
      parts.push(`ファイル種別：${matcher.mimeTypes.join('、')}`)
    }
    if (matcher.contentContains?.length) {
      parts.push(`内容に含む：${matcher.contentContains.join('、')}`)
    }
    if (parts.length) return parts.join('；')
  }
  return item.description || ''
}

async function quickCreate() {
  if (!quickPrompt.value.trim()) return
  
  quickLoading.value = true
  try {
    const resp = await interpretAgentScenario(quickPrompt.value.trim())
    const data = resp.data as ScenarioInterpretResult
    
    // フォームに入力
    resetForm()
    form.scenarioKey = data.scenarioKey || ''
    form.title = data.title || ''
    form.trigger = quickPrompt.value.trim()
    form.action = data.instructions || data.description || ''
    form.priority = data.priority ?? 100
    form.isActive = data.isActive ?? true
    
    editor.isNew = true
    editor.originalKey = ''
    editor.visible = true
    
    ElMessage.success(text.value.tables.agentScenarios.generateSuccess || 'AIが設定を生成しました。確認後保存してください')
    quickPrompt.value = ''
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '生成失敗')
  } finally {
    quickLoading.value = false
  }
}

function useTemplate(tpl: typeof templates[0]) {
  quickPrompt.value = tpl.prompt
  quickCreate()
}

async function saveScenario() {
  if (!formRef.value) return
  
  try {
    await formRef.value.validate()
  } catch {
    return
  }
  
  editor.saving = true
  try {
    // シナリオ設定を構築
    const scenarioKey = form.scenarioKey.trim() || generateKey(form.title)
    
    // triggerとactionからmetadataを構築
    const metadata = buildMetadataFromSimple(form.trigger, form.action)
    
    const payload = {
      scenarioKey,
      title: form.title.trim(),
      description: form.trigger.trim() || undefined,
      instructions: form.action.trim() || undefined,
      toolHints: inferToolHints(form.action),
      priority: form.priority,
      isActive: form.isActive,
      metadata: Object.keys(metadata).length ? metadata : undefined
    }
    
    if (editor.isNew) {
      await createAgentScenario(payload)
      ElMessage.success(text.value.common.saved || '保存しました')
    } else {
      await updateAgentScenario(editor.originalKey, payload)
      ElMessage.success(text.value.common.saved || '保存しました')
    }
    
    editor.visible = false
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '保存失敗')
  } finally {
    editor.saving = false
  }
}

function generateKey(title: string): string {
  // タイトルからキーを生成
  const base = title
    .toLowerCase()
    .replace(/[^\w\u3040-\u309f\u30a0-\u30ff\u4e00-\u9fa5]/g, '.')
    .replace(/\.+/g, '.')
    .replace(/^\.|\.$/, '')
    .slice(0, 32)
  return base || `scenario.${Date.now()}`
}

function buildMetadataFromSimple(trigger: string, action: string): any {
  const metadata: any = {}
  const matcher: any = {}
  
  const triggerLower = trigger.toLowerCase()
  
  if (triggerLower.includes('請求書') || triggerLower.includes('invoice')) {
    matcher.contentContains = ['請求書', '税額', 'invoice']
  }
  if (triggerLower.includes('領収書') || triggerLower.includes('receipt')) {
    matcher.contentContains = [...(matcher.contentContains || []), '領収書', 'receipt']
  }
  if (triggerLower.includes('画像') || triggerLower.includes('アップロード') || triggerLower.includes('upload')) {
    matcher.mimeTypes = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf']
  }
  
  // メッセージキーワード
  const keywords = extractKeywords(trigger)
  if (keywords.length) {
    matcher.messageContains = keywords
  }
  
  if (Object.keys(matcher).length) {
    metadata.matcher = matcher
  }
  
  return metadata
}

function extractKeywords(text: string): string[] {
  const keywords: string[] = []
  const patterns = [
    /請求書/g, /領収書/g, /精算/g, /仕訳/g,
    /受注/g, /売上/g, /仕入/g,
    /交通費/g, /飲食/g, /会議費/g,
    /経費/g, /伝票/g, /勘定/g
  ]
  patterns.forEach(p => {
    const match = text.match(p)
    if (match) keywords.push(...match)
  })
  return [...new Set(keywords)]
}

function inferToolHints(action: string): string[] {
  const hints: string[] = []
  const actionLower = action.toLowerCase()
  
  if (actionLower.includes('仕訳') || actionLower.includes('伝票') || actionLower.includes('voucher')) {
    hints.push('create_voucher')
  }
  if (actionLower.includes('認識') || actionLower.includes('抽出') || actionLower.includes('解析')) {
    hints.push('extract_invoice_data')
  }
  if (actionLower.includes('受注') || actionLower.includes('order')) {
    hints.push('create_sales_order')
  }
  if (actionLower.includes('得意先') || actionLower.includes('顧客')) {
    hints.push('lookup_customer')
  }
  if (actionLower.includes('品目') || actionLower.includes('材料')) {
    hints.push('lookup_material')
  }
  if (actionLower.includes('勘定科目') || actionLower.includes('科目')) {
    hints.push('lookup_account')
  }
  if (actionLower.includes('インボイス') || actionLower.includes('適格請求書')) {
    hints.push('verify_invoice_registration')
  }
  if (actionLower.includes('会計期間') || actionLower.includes('期間')) {
    hints.push('check_accounting_period')
  }
  if (actionLower.includes('伝票番号')) {
    hints.push('get_voucher_by_number')
  }
  if (actionLower.includes('取引先') || actionLower.includes('business partner')) {
    hints.push('create_business_partner')
  }
  if (actionLower.includes('url') || actionLower.includes('website')) {
    hints.push('fetch_webpage')
  }
  
  return [...new Set(hints)]
}

async function toggleActive(item: AgentScenario) {
  try {
    await updateAgentScenario(item.scenarioKey, {
      ...item,
      isActive: !item.isActive
    })
    ElMessage.success(item.isActive ? '無効にしました' : '有効にしました')
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '操作失敗')
  }
}

async function removeScenario(item: AgentScenario) {
  try {
    await deleteAgentScenario(item.scenarioKey)
    ElMessage.success(text.value.common.deleted || '削除しました')
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '削除失敗')
  }
}

onMounted(loadScenarios)
</script>

<style scoped>
.agent-scenarios {
  display: flex;
  flex-direction: column;
  gap: 20px;
  max-width: 900px;
  margin: 0 auto;
}

/* クイック作成カード */
.quick-create-card {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  border: none;
  border-radius: 16px;
}

.quick-create-card :deep(.el-card__body) {
  padding: 24px;
}

.quick-create-header {
  display: flex;
  align-items: flex-start;
  gap: 16px;
  margin-bottom: 20px;
}

.quick-create-icon {
  font-size: 40px;
  line-height: 1;
}

.quick-create-text h3 {
  margin: 0 0 4px;
  font-size: 20px;
  font-weight: 600;
  color: #fff;
}

.quick-create-text p {
  margin: 0;
  font-size: 14px;
  color: rgba(255, 255, 255, 0.8);
}

.quick-create-input {
  display: flex;
  gap: 12px;
  align-items: flex-end;
}

.quick-create-input :deep(.el-textarea__inner) {
  border-radius: 12px;
  border: 2px solid rgba(255, 255, 255, 0.3);
  background: rgba(255, 255, 255, 0.95);
  font-size: 15px;
}

.quick-create-input :deep(.el-textarea__inner:focus) {
  border-color: rgba(255, 255, 255, 0.6);
}

.quick-create-input .el-button {
  height: 54px;
  padding: 0 24px;
  border-radius: 12px;
  font-size: 16px;
  font-weight: 500;
  background: #fff;
  color: #667eea;
  border: none;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

.quick-create-input .el-button:hover {
  background: #f8f9ff;
}

.btn-icon {
  margin-right: 6px;
}

/* テンプレートエリア */
.templates-section {
  margin-top: 24px;
  padding-top: 20px;
  border-top: 1px solid rgba(255, 255, 255, 0.2);
}

.templates-label {
  font-size: 13px;
  color: rgba(255, 255, 255, 0.7);
  margin-bottom: 12px;
}

.templates-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}

.template-item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 16px 12px;
  background: rgba(255, 255, 255, 0.15);
  border-radius: 12px;
  cursor: pointer;
  transition: all 0.2s;
}

.template-item:hover {
  background: rgba(255, 255, 255, 0.25);
  transform: translateY(-2px);
}

.template-icon {
  font-size: 28px;
}

.template-name {
  font-size: 13px;
  color: #fff;
  font-weight: 500;
}

/* シナリオ一覧カード */
.scenarios-card {
  border-radius: 16px;
}

.scenarios-card :deep(.el-card__header) {
  padding: 16px 20px;
  border-bottom: 1px solid #f0f0f0;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.panel-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 16px;
  font-weight: 600;
  color: #1f2937;
}

.empty-state {
  padding: 40px 0;
}

/* シナリオリスト */
.scenarios-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.scenario-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  background: #fafafa;
  border-radius: 12px;
  transition: all 0.2s;
}

.scenario-item:hover {
  background: #f3f4f6;
}

.scenario-item.inactive {
  opacity: 0.6;
}

.scenario-main {
  flex: 1;
  min-width: 0;
}

.scenario-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.scenario-title {
  font-size: 15px;
  font-weight: 600;
  color: #1f2937;
}

.scenario-desc {
  font-size: 13px;
  color: #6b7280;
  margin-bottom: 8px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.scenario-meta {
  display: flex;
  gap: 16px;
  font-size: 12px;
  color: #9ca3af;
}

.meta-item {
  display: flex;
  align-items: center;
  gap: 4px;
}

.scenario-actions {
  display: flex;
  gap: 8px;
  margin-left: 16px;
}

/* ダイアログボディ溢れ防止 */
.agent-scenarios :deep(.el-dialog__body) {
  max-height: calc(100vh - 200px);
  overflow-y: auto;
}

/* 編集フォーム */
.simple-form {
  padding: 0 4px;
}

.simple-form :deep(.el-form-item__label) {
  font-weight: 500;
  color: #374151;
  padding-bottom: 6px;
}

.simple-form :deep(.el-input__inner),
.simple-form :deep(.el-textarea__inner) {
  border-radius: 8px;
}

.field-hint {
  margin-top: 4px;
  font-size: 12px;
  color: #9ca3af;
}

/* 詳細オプション折りたたみ */
.advanced-collapse {
  margin-top: 16px;
  border: 1px dashed #e5e7eb;
  border-radius: 8px;
}

.advanced-collapse :deep(.el-collapse-item__header) {
  padding: 0 16px;
  background: transparent;
  border: none;
}

.advanced-collapse :deep(.el-collapse-item__content) {
  padding: 0 16px 16px;
}

.advanced-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #6b7280;
}

.advanced-content {
  padding-top: 8px;
}

.inline-fields {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

/* ダイアログフッター */
.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}

/* レスポンシブ */
@media (max-width: 640px) {
  .templates-grid {
    grid-template-columns: repeat(2, 1fr);
  }
  
  .quick-create-input {
    flex-direction: column;
  }
  
  .quick-create-input .el-button {
    width: 100%;
  }
  
  .scenario-item {
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
  }
  
  .scenario-actions {
    margin-left: 0;
    width: 100%;
    justify-content: flex-end;
  }
}
</style>

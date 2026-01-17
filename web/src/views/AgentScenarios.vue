<template>
  <div class="page page-large agent-scenarios">
    <!-- 快速创建卡片 -->
    <el-card class="quick-create-card" shadow="hover">
      <div class="quick-create-header">
        <div class="quick-create-icon">✨</div>
        <div class="quick-create-text">
          <h3>{{ text.tables.agentScenarios.quickCreateTitle || '快速创建场景' }}</h3>
          <p>{{ text.tables.agentScenarios.quickCreateDesc || '用自然语言描述你想要的功能，AI 会自动配置' }}</p>
        </div>
      </div>
      <div class="quick-create-input">
        <el-input
          v-model="quickPrompt"
          type="textarea"
          :rows="2"
          :placeholder="text.tables.agentScenarios.quickPlaceholder || '例如：当用户上传餐饮发票时，自动识别金额并创建会议费凭证'"
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
          {{ text.tables.agentScenarios.quickCreateBtn || '智能创建' }}
        </el-button>
      </div>
      
      <!-- 常用模板 -->
      <div class="templates-section">
        <div class="templates-label">{{ text.tables.agentScenarios.templatesLabel || '或选择常用模板：' }}</div>
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

    <!-- 已有场景列表 -->
    <el-card class="scenarios-card">
      <template #header>
        <div class="panel-header">
          <div class="panel-title">
            {{ text.tables.agentScenarios.listTitle || '已配置的场景' }}
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
        <el-empty :description="text.tables.agentScenarios.emptyDesc || '还没有配置任何场景'">
          <el-button type="primary" @click="openSimpleCreate">{{ text.tables.agentScenarios.createFirst || '创建第一个场景' }}</el-button>
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
              <el-tag v-if="!item.isActive" size="small" type="info">{{ text.common.disabled || '已禁用' }}</el-tag>
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
              :title="text.tables.agentScenarios.deleteConfirm || '确定删除此场景？'" 
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

    <!-- 极简创建/编辑弹窗 -->
    <el-dialog 
      v-model="editor.visible" 
      :title="editor.isNew ? (text.tables.agentScenarios.createTitle || '创建场景') : (text.tables.agentScenarios.editTitle || '编辑场景')"
      width="600px"
      :close-on-click-modal="false"
    >
      <el-form 
        ref="formRef"
        :model="form" 
        label-position="top" 
        class="simple-form"
      >
        <!-- 场景名称 -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldName || '场景名称'"
          prop="title"
          :rules="[{ required: true, message: text.tables.agentScenarios.nameRequired || '请输入场景名称' }]"
        >
          <el-input 
            v-model="form.title" 
            :placeholder="text.tables.agentScenarios.namePlaceholder || '例如：餐饮发票识别'"
            maxlength="60"
            show-word-limit
          />
        </el-form-item>

        <!-- 触发条件 -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldTrigger || '什么时候触发？'"
          prop="trigger"
        >
          <el-input 
            v-model="form.trigger" 
            type="textarea"
            :rows="2"
            :placeholder="text.tables.agentScenarios.triggerPlaceholder || '例如：用户上传餐厅收据或发票时'"
          />
          <div class="field-hint">{{ text.tables.agentScenarios.triggerHint || '描述什么情况下应该使用这个场景' }}</div>
        </el-form-item>

        <!-- 执行动作 -->
        <el-form-item 
          :label="text.tables.agentScenarios.fieldAction || 'AI 应该做什么？'"
          prop="action"
        >
          <el-input 
            v-model="form.action" 
            type="textarea"
            :rows="3"
            :placeholder="text.tables.agentScenarios.actionPlaceholder || '例如：识别发票上的金额、日期、店铺名称，然后创建一张会议费的会计凭证'"
          />
          <div class="field-hint">{{ text.tables.agentScenarios.actionHint || '描述 AI 需要完成的具体任务' }}</div>
        </el-form-item>

        <!-- 高级选项（折叠） -->
        <el-collapse v-model="advancedOpen" class="advanced-collapse">
          <el-collapse-item name="advanced">
            <template #title>
              <span class="advanced-title">
                <el-icon><Setting /></el-icon>
                {{ text.tables.agentScenarios.advancedOptions || '高级选项' }}
              </span>
            </template>
            <div class="advanced-content">
              <el-form-item :label="text.tables.agentScenarios.fieldKey || '场景键（唯一标识）'">
                <el-input 
                  v-model="form.scenarioKey" 
                  :disabled="!editor.isNew"
                  :placeholder="text.tables.agentScenarios.keyPlaceholder || '自动生成，也可手动指定'"
                />
              </el-form-item>
              <div class="inline-fields">
                <el-form-item :label="text.tables.agentScenarios.fieldPriority || '优先级'">
                  <el-input-number v-model="form.priority" :min="1" :max="999" />
                </el-form-item>
                <el-form-item :label="text.tables.agentScenarios.fieldActive || '启用'">
                  <el-switch v-model="form.isActive" />
                </el-form-item>
              </div>
            </div>
          </el-collapse-item>
        </el-collapse>
      </el-form>

      <template #footer>
        <div class="dialog-footer">
          <el-button @click="editor.visible = false">{{ text.common.cancel || '取消' }}</el-button>
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

// 状态
const loading = ref(false)
const scenarios = ref<AgentScenario[]>([])
const quickPrompt = ref('')
const quickLoading = ref(false)
const advancedOpen = ref<string[]>([])
const formRef = ref()

// 常用模板
const templates = [
  { key: 'invoice', icon: '🧾', name: '发票识别', prompt: '当用户上传发票图片时，自动识别发票内容（金额、日期、供应商），并创建对应的会计凭证' },
  { key: 'receipt', icon: '🍽️', name: '餐饮报销', prompt: '当用户上传餐厅收据时，识别消费金额和店铺信息，创建会议费或交际费凭证' },
  { key: 'transport', icon: '🚗', name: '交通费', prompt: '当用户上传出租车票、火车票或机票时，识别交通费用并创建旅费凭证' },
  { key: 'sales', icon: '📦', name: '销售订单', prompt: '当用户用自然语言描述订单需求时（如"给张三公司下10个产品A的订单"），自动创建销售订单' }
]

// 编辑器状态
const editor = reactive({
  visible: false,
  isNew: true,
  saving: false,
  originalKey: ''
})

// 表单
const form = reactive({
  scenarioKey: '',
  title: '',
  trigger: '',
  action: '',
  priority: 100,
  isActive: true
})

// 方法
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
    ElMessage.error(err?.response?.data?.error || err?.message || '加载失败')
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
  
  // 从 instructions 和 description 还原 trigger 和 action
  form.trigger = extractTrigger(item)
  form.action = item.instructions || item.description || ''
  
  editor.visible = true
}

function extractTrigger(item: AgentScenario): string {
  // 尝试从 metadata.matcher 提取触发条件描述
  const metadata = item.metadata as any
  const matcher = metadata?.matcher
  if (matcher) {
    const parts: string[] = []
    if (matcher.messageContains?.length) {
      parts.push(`消息包含：${matcher.messageContains.join('、')}`)
    }
    if (matcher.mimeTypes?.length) {
      parts.push(`文件类型：${matcher.mimeTypes.join('、')}`)
    }
    if (matcher.contentContains?.length) {
      parts.push(`内容包含：${matcher.contentContains.join('、')}`)
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
    
    // 填充表单
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
    
    ElMessage.success(text.value.tables.agentScenarios.generateSuccess || 'AI 已生成配置，请确认后保存')
    quickPrompt.value = ''
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '生成失败')
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
    // 构建完整的场景配置
    const scenarioKey = form.scenarioKey.trim() || generateKey(form.title)
    
    // 从 trigger 和 action 构建 metadata
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
      ElMessage.success(text.value.common.saved || '保存成功')
    } else {
      await updateAgentScenario(editor.originalKey, payload)
      ElMessage.success(text.value.common.saved || '保存成功')
    }
    
    editor.visible = false
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '保存失败')
  } finally {
    editor.saving = false
  }
}

function generateKey(title: string): string {
  // 从标题生成 key
  const base = title
    .toLowerCase()
    .replace(/[^\w\u4e00-\u9fa5]/g, '.')
    .replace(/\.+/g, '.')
    .replace(/^\.|\.$/, '')
    .slice(0, 32)
  return base || `scenario.${Date.now()}`
}

function buildMetadataFromSimple(trigger: string, action: string): any {
  const metadata: any = {}
  const matcher: any = {}
  
  // 从 trigger 文本推断 matcher
  const triggerLower = trigger.toLowerCase()
  
  // 文件类型推断
  if (triggerLower.includes('发票') || triggerLower.includes('invoice')) {
    matcher.contentContains = ['发票', '税额', 'invoice']
  }
  if (triggerLower.includes('收据') || triggerLower.includes('receipt')) {
    matcher.contentContains = [...(matcher.contentContains || []), '收据', 'receipt']
  }
  if (triggerLower.includes('图片') || triggerLower.includes('上传')) {
    matcher.mimeTypes = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf']
  }
  
  // 消息关键词
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
    /发票/g, /收据/g, /报销/g, /凭证/g,
    /订单/g, /销售/g, /采购/g,
    /交通/g, /餐饮/g, /会议/g
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
  
  if (actionLower.includes('凭证') || actionLower.includes('仕訳') || actionLower.includes('voucher')) {
    hints.push('create_voucher')
  }
  if (actionLower.includes('识别') || actionLower.includes('提取') || actionLower.includes('解析')) {
    hints.push('extract_invoice_data')
  }
  if (actionLower.includes('订单') || actionLower.includes('受注') || actionLower.includes('order')) {
    hints.push('create_sales_order')
  }
  if (actionLower.includes('客户') || actionLower.includes('得意先')) {
    hints.push('lookup_customer')
  }
  if (actionLower.includes('物料') || actionLower.includes('品目')) {
    hints.push('lookup_material')
  }
  if (actionLower.includes('科目') || actionLower.includes('勘定科目')) {
    hints.push('lookup_account')
  }
  if (actionLower.includes('发票登记') || actionLower.includes('インボイス')) {
    hints.push('verify_invoice_registration')
  }
  if (actionLower.includes('会计期间') || actionLower.includes('会計期間')) {
    hints.push('check_accounting_period')
  }
  if (actionLower.includes('凭证号') || actionLower.includes('伝票番号')) {
    hints.push('get_voucher_by_number')
  }
  if (actionLower.includes('取引先') || actionLower.includes('业务伙伴') || actionLower.includes('business partner')) {
    hints.push('create_business_partner')
  }
  if (actionLower.includes('网址') || actionLower.includes('url') || actionLower.includes('website') || actionLower.includes('网站')) {
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
    ElMessage.success(item.isActive ? '已禁用' : '已启用')
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '操作失败')
  }
}

async function removeScenario(item: AgentScenario) {
  try {
    await deleteAgentScenario(item.scenarioKey)
    ElMessage.success(text.value.common.deleted || '已删除')
    await loadScenarios()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '删除失败')
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

/* 快速创建卡片 */
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

/* 模板区域 */
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

/* 场景列表卡片 */
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

/* 场景列表 */
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

/* 简化的编辑表单 */
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

/* 高级选项折叠 */
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

/* 弹窗底部 */
.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}

/* 响应式 */
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

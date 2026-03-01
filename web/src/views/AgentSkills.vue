<template>
  <div class="page page-large agent-skills">
    <!-- Header Card -->
    <el-card class="header-card" :body-style="{ padding: '16px 20px' }">
      <div class="page-header">
        <div class="page-header-left">
          <span class="page-header-title">AI 技能管理</span>
          <el-tag v-if="skills.length > 0" size="small" type="info">{{ skills.length }}</el-tag>
        </div>
        <div class="page-actions">
          <el-checkbox v-model="showAll" @change="loadSkills">显示已禁用</el-checkbox>
          <el-button :icon="Refresh" :loading="loading" @click="loadSkills">刷新</el-button>
          <el-button type="primary" :icon="Plus" @click="openCreate">新建技能</el-button>
        </div>
      </div>
    </el-card>

    <!-- Empty State -->
    <el-card v-if="skills.length === 0 && !loading" class="empty-card">
      <el-empty description="还没有配置任何技能">
        <el-button type="primary" @click="openCreate">创建第一个技能</el-button>
      </el-empty>
    </el-card>

    <!-- Skills Grid -->
    <div v-else class="skills-grid">
      <el-card
        v-for="skill in skills"
        :key="skill.id"
        class="skill-card"
        :class="{ inactive: !skill.isActive }"
        shadow="hover"
        @click="openDetail(skill)"
      >
        <div class="skill-card-body">
          <div class="skill-card-header">
            <span class="skill-icon">{{ skill.icon || '🤖' }}</span>
            <div class="skill-card-title-area">
              <div class="skill-name">{{ skill.name }}</div>
              <div class="skill-key">{{ skill.skillKey }}</div>
            </div>
            <div class="skill-card-actions" @click.stop>
              <el-tag :type="categoryTagType(skill.category)" size="small">{{ skill.category || 'general' }}</el-tag>
              <el-switch
                v-model="skill.isActive"
                size="small"
                @change="(val: boolean) => toggleSkillActive(skill, val)"
              />
            </div>
          </div>
          <div v-if="skill.description" class="skill-desc">{{ skill.description }}</div>
          <div class="skill-meta">
            <span v-if="skill.enabledTools?.length" class="meta-item">
              🔧 {{ skill.enabledTools.slice(0, 3).join(', ') }}
              <span v-if="skill.enabledTools.length > 3">+{{ skill.enabledTools.length - 3 }}</span>
            </span>
            <span class="meta-item">⚡ {{ skill.priority }}</span>
            <span class="meta-item">{{ formatDate(skill.updatedAt) }}</span>
          </div>
        </div>
      </el-card>
    </div>

    <!-- Skill Detail Dialog -->
    <el-dialog
      v-model="detail.visible"
      :title="detail.isNew ? '新建技能' : `编辑技能 - ${detail.form.name}`"
      width="90%"
      top="3vh"
      destroy-on-close
      class="skill-detail-dialog"
    >
      <el-tabs v-model="detail.activeTab" class="detail-tabs">
        <!-- Tab 1: Basic -->
        <el-tab-pane label="基本信息" name="basic">
          <el-form label-position="top" class="detail-form">
            <div class="form-grid">
              <el-form-item label="技能键 (skill_key)" required>
                <el-input v-model="detail.form.skillKey" :disabled="!detail.isNew" placeholder="例如：invoice.booking" />
              </el-form-item>
              <el-form-item label="名称" required>
                <el-input v-model="detail.form.name" placeholder="技能名称" maxlength="120" show-word-limit />
              </el-form-item>
              <el-form-item label="类别">
                <el-select v-model="detail.form.category" placeholder="选择类别">
                  <el-option label="general" value="general" />
                  <el-option label="finance" value="finance" />
                  <el-option label="hr" value="hr" />
                  <el-option label="sales" value="sales" />
                  <el-option label="approval" value="approval" />
                </el-select>
              </el-form-item>
              <el-form-item label="图标">
                <el-input v-model="detail.form.icon" placeholder="例如：📊 或 emoji" />
              </el-form-item>
              <el-form-item label="优先级">
                <el-input-number v-model="detail.form.priority" :min="1" :max="9999" />
              </el-form-item>
              <el-form-item label="启用">
                <el-switch v-model="detail.form.isActive" />
              </el-form-item>
            </div>
            <el-form-item label="描述">
              <el-input v-model="detail.form.description" type="textarea" :rows="2" placeholder="技能描述" />
            </el-form-item>
            <el-form-item label="触发条件 (triggers JSON)">
              <el-input
                v-model="detail.form.triggersText"
                type="textarea"
                :rows="5"
                placeholder='{"keywords": ["发票", "记账"], "intents": ["invoice_booking"]}'
                class="json-textarea"
              />
            </el-form-item>
          </el-form>
        </el-tab-pane>

        <!-- Tab 2: Prompts -->
        <el-tab-pane label="Prompt 配置" name="prompts">
          <el-form label-position="top" class="detail-form">
            <el-form-item label="System Prompt">
              <el-input
                v-model="detail.form.systemPrompt"
                type="textarea"
                :rows="8"
                placeholder="系统提示词..."
                class="prompt-textarea"
              />
            </el-form-item>
            <el-form-item label="Extraction Prompt">
              <el-input
                v-model="detail.form.extractionPrompt"
                type="textarea"
                :rows="6"
                placeholder="数据提取提示词..."
                class="prompt-textarea"
              />
            </el-form-item>
            <el-form-item label="Followup Prompt">
              <el-input
                v-model="detail.form.followupPrompt"
                type="textarea"
                :rows="4"
                placeholder="跟进提示词..."
                class="prompt-textarea"
              />
            </el-form-item>
          </el-form>
        </el-tab-pane>

        <!-- Tab 3: Tools -->
        <el-tab-pane label="工具配置" name="tools">
          <el-form label-position="top" class="detail-form">
            <el-form-item label="启用的工具 (enabled_tools)">
              <el-select
                v-model="detail.form.enabledTools"
                multiple
                filterable
                allow-create
                default-first-option
                placeholder="输入工具名并回车添加"
                style="width: 100%"
              >
                <el-option v-for="t in commonTools" :key="t" :label="t" :value="t" />
              </el-select>
            </el-form-item>
            <div class="hint-text">已配置 {{ detail.form.enabledTools.length }} 个工具</div>
          </el-form>
        </el-tab-pane>

        <!-- Tab 4: Model Config -->
        <el-tab-pane label="模型配置" name="model">
          <el-form label-position="top" class="detail-form">
            <el-form-item label="模型配置 (modelConfig JSON)">
              <el-input
                v-model="detail.form.modelConfigText"
                type="textarea"
                :rows="8"
                placeholder='{"model": "gpt-4o", "extractionModel": "gpt-4o-mini", "temperature": 0.3}'
                class="json-textarea"
              />
            </el-form-item>
          </el-form>
        </el-tab-pane>

        <!-- Tab 5: Behavior -->
        <el-tab-pane label="行为配置" name="behavior">
          <el-form label-position="top" class="detail-form">
            <el-form-item label="行为配置 (behaviorConfig JSON)">
              <el-input
                v-model="detail.form.behaviorConfigText"
                type="textarea"
                :rows="8"
                placeholder='{"confidenceThreshold": 0.7, "autoExecute": false, "requireConfirmation": true}'
                class="json-textarea"
              />
            </el-form-item>
          </el-form>
        </el-tab-pane>

        <!-- Tab 6: Rules -->
        <el-tab-pane label="业务规则" name="rules">
          <div class="sub-section">
            <div class="sub-header">
              <span>规则列表</span>
              <el-button type="primary" size="small" :icon="Plus" @click="openRuleEditor(null)">添加规则</el-button>
            </div>
            <el-table :data="detail.rules" stripe size="small" v-loading="detail.rulesLoading" class="sub-table">
              <el-table-column prop="name" label="名称" min-width="160" show-overflow-tooltip />
              <el-table-column prop="ruleKey" label="Key" width="140" show-overflow-tooltip />
              <el-table-column prop="priority" label="优先级" width="80" align="center" />
              <el-table-column prop="isActive" label="启用" width="70" align="center">
                <template #default="{ row }">
                  <el-tag :type="row.isActive ? 'success' : 'info'" size="small">{{ row.isActive ? '是' : '否' }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="操作" width="140" fixed="right">
                <template #default="{ row }">
                  <el-button size="small" type="primary" link @click="openRuleEditor(row)">编辑</el-button>
                  <el-popconfirm title="确定删除此规则？" @confirm="deleteRule(row)">
                    <template #reference>
                      <el-button size="small" type="danger" link>删除</el-button>
                    </template>
                  </el-popconfirm>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </el-tab-pane>

        <!-- Tab 7: Examples -->
        <el-tab-pane label="示例库" name="examples">
          <div class="sub-section">
            <div class="sub-header">
              <span>示例列表</span>
              <el-button type="primary" size="small" :icon="Plus" @click="openExampleEditor(null)">添加示例</el-button>
            </div>
            <el-table :data="detail.examples" stripe size="small" v-loading="detail.examplesLoading" class="sub-table">
              <el-table-column prop="name" label="名称" min-width="160" show-overflow-tooltip />
              <el-table-column prop="inputType" label="输入类型" width="120" />
              <el-table-column prop="isActive" label="启用" width="70" align="center">
                <template #default="{ row }">
                  <el-tag :type="row.isActive ? 'success' : 'info'" size="small">{{ row.isActive ? '是' : '否' }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="createdAt" label="创建时间" width="160">
                <template #default="{ row }">{{ formatDate(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column label="操作" width="140" fixed="right">
                <template #default="{ row }">
                  <el-button size="small" type="primary" link @click="openExampleEditor(row)">编辑</el-button>
                  <el-popconfirm title="确定删除此示例？" @confirm="deleteExample(row)">
                    <template #reference>
                      <el-button size="small" type="danger" link>删除</el-button>
                    </template>
                  </el-popconfirm>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </el-tab-pane>
      </el-tabs>

      <template #footer>
        <div class="dialog-footer">
          <el-button @click="detail.visible = false">取消</el-button>
          <el-button v-if="!detail.isNew" type="danger" @click="deleteSkill">删除技能</el-button>
          <el-button type="primary" :loading="detail.saving" @click="saveSkill">保存</el-button>
        </div>
      </template>
    </el-dialog>

    <!-- Rule Editor Dialog -->
    <el-dialog
      v-model="ruleEditor.visible"
      :title="ruleEditor.isNew ? '添加规则' : '编辑规则'"
      width="640px"
      append-to-body
    >
      <el-form label-position="top" class="detail-form">
        <el-form-item label="名称" required>
          <el-input v-model="ruleEditor.form.name" placeholder="规则名称" maxlength="120" show-word-limit />
        </el-form-item>
        <el-form-item label="Key">
          <el-input v-model="ruleEditor.form.ruleKey" placeholder="规则键（可选）" />
        </el-form-item>
        <div class="form-grid-2">
          <el-form-item label="优先级">
            <el-input-number v-model="ruleEditor.form.priority" :min="1" :max="9999" />
          </el-form-item>
          <el-form-item label="启用">
            <el-switch v-model="ruleEditor.form.isActive" />
          </el-form-item>
        </div>
        <el-form-item label="条件 (conditions JSON)">
          <el-input
            v-model="ruleEditor.form.conditionsText"
            type="textarea"
            :rows="5"
            placeholder='{"type": "amount_check", "threshold": 10000}'
            class="json-textarea"
          />
        </el-form-item>
        <el-form-item label="动作 (actions JSON)">
          <el-input
            v-model="ruleEditor.form.actionsText"
            type="textarea"
            :rows="5"
            placeholder='{"action": "require_approval", "notify": ["manager"]}'
            class="json-textarea"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="ruleEditor.visible = false">取消</el-button>
        <el-button type="primary" :loading="ruleEditor.saving" @click="saveRule">保存</el-button>
      </template>
    </el-dialog>

    <!-- Example Editor Dialog -->
    <el-dialog
      v-model="exampleEditor.visible"
      :title="exampleEditor.isNew ? '添加示例' : '编辑示例'"
      width="640px"
      append-to-body
    >
      <el-form label-position="top" class="detail-form">
        <el-form-item label="名称">
          <el-input v-model="exampleEditor.form.name" placeholder="示例名称" maxlength="120" show-word-limit />
        </el-form-item>
        <div class="form-grid-2">
          <el-form-item label="输入类型">
            <el-select v-model="exampleEditor.form.inputType" placeholder="选择类型">
              <el-option label="text" value="text" />
              <el-option label="image" value="image" />
              <el-option label="file" value="file" />
              <el-option label="structured" value="structured" />
            </el-select>
          </el-form-item>
          <el-form-item label="启用">
            <el-switch v-model="exampleEditor.form.isActive" />
          </el-form-item>
        </div>
        <el-form-item label="输入数据 (inputData JSON)">
          <el-input
            v-model="exampleEditor.form.inputDataText"
            type="textarea"
            :rows="6"
            placeholder='{"message": "请帮我记账，金额1000日元"}'
            class="json-textarea"
          />
        </el-form-item>
        <el-form-item label="期望输出 (expectedOutput JSON)">
          <el-input
            v-model="exampleEditor.form.expectedOutputText"
            type="textarea"
            :rows="6"
            placeholder='{"action": "create_voucher", "amount": 1000}'
            class="json-textarea"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="exampleEditor.visible = false">取消</el-button>
        <el-button type="primary" :loading="exampleEditor.saving" @click="saveExample">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Plus } from '@element-plus/icons-vue'
import dayjs from 'dayjs'
import api from '../api'

// ====================== Types ======================

interface Skill {
  id: string
  companyCode: string | null
  skillKey: string
  name: string
  description: string | null
  category: string
  icon: string | null
  triggers: Record<string, any> | null
  systemPrompt: string | null
  extractionPrompt: string | null
  followupPrompt: string | null
  enabledTools: string[] | null
  modelConfig: Record<string, any> | null
  behaviorConfig: Record<string, any> | null
  priority: number
  isActive: boolean
  version: number
  parentId: string | null
  createdAt: string
  updatedAt: string
}

interface SkillRule {
  id: string
  skillId: string
  ruleKey: string | null
  name: string
  conditions: Record<string, any>
  actions: Record<string, any>
  priority: number
  isActive: boolean
  createdAt: string
  updatedAt: string
}

interface SkillExample {
  id: string
  skillId: string
  name: string | null
  inputType: string
  inputData: Record<string, any>
  expectedOutput: Record<string, any>
  isActive: boolean
  createdAt: string
}

// ====================== State ======================

const loading = ref(false)
const showAll = ref(true)
const skills = ref<Skill[]>([])

const commonTools = [
  'create_voucher', 'search_vouchers', 'create_invoice',
  'search_employees', 'calculate_payroll', 'approve_request',
  'create_leave', 'search_timesheets', 'generate_certificate',
  'search_accounts', 'create_sales_order', 'search_partners'
]

// Detail dialog
const detail = reactive({
  visible: false,
  isNew: true,
  saving: false,
  activeTab: 'basic',
  skillId: '',
  form: {
    skillKey: '',
    name: '',
    description: '',
    category: 'general',
    icon: '',
    priority: 100,
    isActive: true,
    triggersText: '',
    systemPrompt: '',
    extractionPrompt: '',
    followupPrompt: '',
    enabledTools: [] as string[],
    modelConfigText: '',
    behaviorConfigText: ''
  },
  rules: [] as SkillRule[],
  rulesLoading: false,
  examples: [] as SkillExample[],
  examplesLoading: false
})

// Rule editor
const ruleEditor = reactive({
  visible: false,
  isNew: true,
  saving: false,
  id: '',
  form: {
    name: '',
    ruleKey: '',
    priority: 100,
    isActive: true,
    conditionsText: '',
    actionsText: ''
  }
})

// Example editor
const exampleEditor = reactive({
  visible: false,
  isNew: true,
  saving: false,
  id: '',
  form: {
    name: '',
    inputType: 'text',
    isActive: true,
    inputDataText: '',
    expectedOutputText: ''
  }
})

// ====================== Helpers ======================

function formatDate(value: string | null | undefined) {
  if (!value) return '-'
  return dayjs(value).format('YYYY-MM-DD HH:mm')
}

function categoryTagType(category: string | null): '' | 'success' | 'warning' | 'info' | 'danger' {
  switch (category) {
    case 'finance': return 'warning'
    case 'hr': return 'success'
    case 'sales': return ''  // primary
    case 'approval': return 'info'
    default: return 'info'
  }
}

function safeJsonStringify(obj: any): string {
  if (!obj || (typeof obj === 'object' && Object.keys(obj).length === 0)) return ''
  try { return JSON.stringify(obj, null, 2) } catch { return '' }
}

function safeJsonParse(text: string): Record<string, any> | null {
  const trimmed = text.trim()
  if (!trimmed) return null
  try { return JSON.parse(trimmed) } catch { return null }
}

function handleError(err: any, fallback = '操作失败') {
  const msg = err?.response?.data?.error || err?.message || fallback
  ElMessage.error(msg)
}

// ====================== Skills CRUD ======================

async function loadSkills() {
  if (loading.value) return
  loading.value = true
  try {
    const params: Record<string, any> = {}
    if (showAll.value) params.all = 1
    const resp = await api.get('/ai/agent/skills', { params })
    skills.value = Array.isArray(resp.data) ? resp.data : []
  } catch (err: any) {
    handleError(err, '加载技能失败')
  } finally {
    loading.value = false
  }
}

function openCreate() {
  resetDetailForm()
  detail.isNew = true
  detail.skillId = ''
  detail.activeTab = 'basic'
  detail.rules = []
  detail.examples = []
  detail.visible = true
}

async function openDetail(skill: Skill) {
  resetDetailForm()
  detail.isNew = false
  detail.skillId = skill.id
  detail.activeTab = 'basic'

  detail.form.skillKey = skill.skillKey
  detail.form.name = skill.name
  detail.form.description = skill.description || ''
  detail.form.category = skill.category || 'general'
  detail.form.icon = skill.icon || ''
  detail.form.priority = skill.priority
  detail.form.isActive = skill.isActive
  detail.form.triggersText = safeJsonStringify(skill.triggers)
  detail.form.systemPrompt = skill.systemPrompt || ''
  detail.form.extractionPrompt = skill.extractionPrompt || ''
  detail.form.followupPrompt = skill.followupPrompt || ''
  detail.form.enabledTools = skill.enabledTools ? [...skill.enabledTools] : []
  detail.form.modelConfigText = safeJsonStringify(skill.modelConfig)
  detail.form.behaviorConfigText = safeJsonStringify(skill.behaviorConfig)

  detail.visible = true
  loadRules()
  loadExamples()
}

function resetDetailForm() {
  detail.form.skillKey = ''
  detail.form.name = ''
  detail.form.description = ''
  detail.form.category = 'general'
  detail.form.icon = ''
  detail.form.priority = 100
  detail.form.isActive = true
  detail.form.triggersText = ''
  detail.form.systemPrompt = ''
  detail.form.extractionPrompt = ''
  detail.form.followupPrompt = ''
  detail.form.enabledTools = []
  detail.form.modelConfigText = ''
  detail.form.behaviorConfigText = ''
}

async function saveSkill() {
  const f = detail.form
  if (!f.skillKey.trim() || !f.name.trim()) {
    ElMessage.warning('请填写 skill_key 和名称')
    return
  }

  // Validate JSON fields
  if (f.triggersText.trim()) {
    const parsed = safeJsonParse(f.triggersText)
    if (!parsed) { ElMessage.warning('triggers JSON 格式错误'); return }
  }
  if (f.modelConfigText.trim()) {
    const parsed = safeJsonParse(f.modelConfigText)
    if (!parsed) { ElMessage.warning('modelConfig JSON 格式错误'); return }
  }
  if (f.behaviorConfigText.trim()) {
    const parsed = safeJsonParse(f.behaviorConfigText)
    if (!parsed) { ElMessage.warning('behaviorConfig JSON 格式错误'); return }
  }

  detail.saving = true
  try {
    const payload: Record<string, any> = {
      skillKey: f.skillKey.trim(),
      name: f.name.trim(),
      description: f.description.trim() || undefined,
      category: f.category || 'general',
      icon: f.icon.trim() || undefined,
      priority: f.priority,
      isActive: f.isActive,
      triggers: safeJsonParse(f.triggersText) || undefined,
      systemPrompt: f.systemPrompt.trim() || undefined,
      extractionPrompt: f.extractionPrompt.trim() || undefined,
      followupPrompt: f.followupPrompt.trim() || undefined,
      enabledTools: f.enabledTools.length ? f.enabledTools : undefined,
      modelConfig: safeJsonParse(f.modelConfigText) || undefined,
      behaviorConfig: safeJsonParse(f.behaviorConfigText) || undefined
    }
    if (!detail.isNew) {
      payload.id = detail.skillId
    }
    const resp = await api.post('/ai/agent/skills', payload)
    if (detail.isNew && resp.data?.id) {
      detail.skillId = resp.data.id
      detail.isNew = false
    }
    ElMessage.success('保存成功')
    await loadSkills()
  } catch (err: any) {
    handleError(err, '保存失败')
  } finally {
    detail.saving = false
  }
}

async function deleteSkill() {
  if (!detail.skillId) return
  try {
    await ElMessageBox.confirm('确定删除此技能？此操作不可恢复。', '确认', { type: 'warning' })
  } catch { return }

  try {
    await api.delete(`/ai/agent/skills/${detail.skillId}`)
    ElMessage.success('已删除')
    detail.visible = false
    await loadSkills()
  } catch (err: any) {
    handleError(err, '删除失败')
  }
}

async function toggleSkillActive(skill: Skill, val: boolean) {
  try {
    await api.post('/ai/agent/skills', {
      id: skill.id,
      skillKey: skill.skillKey,
      name: skill.name,
      isActive: val
    })
    ElMessage.success(val ? '已启用' : '已禁用')
  } catch (err: any) {
    skill.isActive = !val
    handleError(err, '操作失败')
  }
}

// ====================== Rules CRUD ======================

async function loadRules() {
  if (!detail.skillId) return
  detail.rulesLoading = true
  try {
    const resp = await api.get(`/ai/agent/skills/${detail.skillId}/rules`)
    detail.rules = Array.isArray(resp.data) ? resp.data : []
  } catch (err: any) {
    handleError(err, '加载规则失败')
  } finally {
    detail.rulesLoading = false
  }
}

function openRuleEditor(rule: SkillRule | null) {
  ruleEditor.isNew = !rule
  ruleEditor.id = rule?.id || ''
  ruleEditor.form.name = rule?.name || ''
  ruleEditor.form.ruleKey = rule?.ruleKey || ''
  ruleEditor.form.priority = rule?.priority ?? 100
  ruleEditor.form.isActive = rule?.isActive ?? true
  ruleEditor.form.conditionsText = rule ? safeJsonStringify(rule.conditions) : ''
  ruleEditor.form.actionsText = rule ? safeJsonStringify(rule.actions) : ''
  ruleEditor.visible = true
}

async function saveRule() {
  const f = ruleEditor.form
  if (!f.name.trim()) {
    ElMessage.warning('请填写规则名称')
    return
  }
  if (f.conditionsText.trim() && !safeJsonParse(f.conditionsText)) {
    ElMessage.warning('conditions JSON 格式错误')
    return
  }
  if (f.actionsText.trim() && !safeJsonParse(f.actionsText)) {
    ElMessage.warning('actions JSON 格式错误')
    return
  }

  ruleEditor.saving = true
  try {
    const payload: Record<string, any> = {
      name: f.name.trim(),
      ruleKey: f.ruleKey.trim() || undefined,
      priority: f.priority,
      isActive: f.isActive,
      conditions: safeJsonParse(f.conditionsText) || {},
      actions: safeJsonParse(f.actionsText) || {}
    }
    if (!ruleEditor.isNew) {
      payload.id = ruleEditor.id
    }
    await api.post(`/ai/agent/skills/${detail.skillId}/rules`, payload)
    ElMessage.success('保存成功')
    ruleEditor.visible = false
    await loadRules()
  } catch (err: any) {
    handleError(err, '保存规则失败')
  } finally {
    ruleEditor.saving = false
  }
}

async function deleteRule(rule: SkillRule) {
  try {
    await api.delete(`/ai/agent/skills/rules/${rule.id}`)
    ElMessage.success('已删除')
    await loadRules()
  } catch (err: any) {
    handleError(err, '删除规则失败')
  }
}

// ====================== Examples CRUD ======================

async function loadExamples() {
  if (!detail.skillId) return
  detail.examplesLoading = true
  try {
    const resp = await api.get(`/ai/agent/skills/${detail.skillId}/examples`)
    detail.examples = Array.isArray(resp.data) ? resp.data : []
  } catch (err: any) {
    handleError(err, '加载示例失败')
  } finally {
    detail.examplesLoading = false
  }
}

function openExampleEditor(example: SkillExample | null) {
  exampleEditor.isNew = !example
  exampleEditor.id = example?.id || ''
  exampleEditor.form.name = example?.name || ''
  exampleEditor.form.inputType = example?.inputType || 'text'
  exampleEditor.form.isActive = example?.isActive ?? true
  exampleEditor.form.inputDataText = example ? safeJsonStringify(example.inputData) : ''
  exampleEditor.form.expectedOutputText = example ? safeJsonStringify(example.expectedOutput) : ''
  exampleEditor.visible = true
}

async function saveExample() {
  const f = exampleEditor.form
  if (f.inputDataText.trim() && !safeJsonParse(f.inputDataText)) {
    ElMessage.warning('inputData JSON 格式错误')
    return
  }
  if (f.expectedOutputText.trim() && !safeJsonParse(f.expectedOutputText)) {
    ElMessage.warning('expectedOutput JSON 格式错误')
    return
  }

  exampleEditor.saving = true
  try {
    const payload: Record<string, any> = {
      name: f.name.trim() || undefined,
      inputType: f.inputType,
      isActive: f.isActive,
      inputData: safeJsonParse(f.inputDataText) || {},
      expectedOutput: safeJsonParse(f.expectedOutputText) || {}
    }
    if (!exampleEditor.isNew) {
      payload.id = exampleEditor.id
    }
    await api.post(`/ai/agent/skills/${detail.skillId}/examples`, payload)
    ElMessage.success('保存成功')
    exampleEditor.visible = false
    await loadExamples()
  } catch (err: any) {
    handleError(err, '保存示例失败')
  } finally {
    exampleEditor.saving = false
  }
}

async function deleteExample(example: SkillExample) {
  try {
    await api.delete(`/ai/agent/skills/examples/${example.id}`)
    ElMessage.success('已删除')
    await loadExamples()
  } catch (err: any) {
    handleError(err, '删除示例失败')
  }
}

// ====================== Lifecycle ======================

onMounted(() => {
  loadSkills()
})
</script>

<style scoped>
.agent-skills {
  padding: 20px;
  max-width: 1400px;
  margin: 0 auto;
}

/* Header */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}
.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}
.page-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

/* Empty */
.empty-card {
  margin-top: 16px;
}

/* Skills Grid */
.skills-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
  gap: 16px;
  margin-top: 16px;
}

.skill-card {
  cursor: pointer;
  transition: all 0.2s ease;
  border-left: 3px solid var(--el-color-primary);
}
.skill-card:hover {
  transform: translateY(-2px);
}
.skill-card.inactive {
  opacity: 0.6;
  border-left-color: var(--el-color-info-light-5);
}

.skill-card-body {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.skill-card-header {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}
.skill-icon {
  font-size: 28px;
  line-height: 1;
  flex-shrink: 0;
}
.skill-card-title-area {
  flex: 1;
  min-width: 0;
}
.skill-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  line-height: 1.3;
}
.skill-key {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  font-family: monospace;
  margin-top: 2px;
}
.skill-card-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.skill-desc {
  font-size: 13px;
  color: var(--el-text-color-regular);
  line-height: 1.5;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.skill-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
.meta-item {
  display: flex;
  align-items: center;
  gap: 4px;
}

/* Detail Dialog */
.skill-detail-dialog .el-dialog__body {
  padding: 0 20px;
}
.detail-tabs {
  min-height: 400px;
}
.detail-form {
  max-width: 800px;
}
.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 0 20px;
}
.form-grid-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 20px;
}

.json-textarea :deep(.el-textarea__inner) {
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 13px;
  line-height: 1.5;
  tab-size: 2;
}
.prompt-textarea :deep(.el-textarea__inner) {
  font-size: 13px;
  line-height: 1.6;
}

.hint-text {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-top: -8px;
  margin-bottom: 16px;
}

/* Sub-sections for Rules / Examples */
.sub-section {
  padding: 4px 0;
}
.sub-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
  font-size: 14px;
  font-weight: 500;
}
.sub-table {
  width: 100%;
}

/* Footer */
.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.dialog-footer .el-button--danger {
  margin-right: auto;
}
</style>

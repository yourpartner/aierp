<template>
  <div class="page page-large workflow-rules">
    <el-card class="rules-panel" :body-style="{ padding: '16px 20px' }">
      <template #header>
        <div class="panel-header">
          <div class="panel-title">{{ text.tables.workflowRules.title }}</div>
          <div class="panel-actions">
            <el-button type="primary" :loading="loading" @click="loadRules">{{ text.buttons.refresh }}</el-button>
            <el-button type="success" @click="openNew">{{ text.tables.workflowRules.new }}</el-button>
          </div>
        </div>
      </template>

      <div class="ai-generator">
        <div class="ai-generator-header">{{ text.tables.workflowRules.generatorTitle }}</div>
        <el-input
          v-model="prompt"
          type="textarea"
          :rows="4"
          :placeholder="text.tables.workflowRules.generatorPlaceholder"
        />
        <div class="ai-generator-actions">
          <el-button type="primary" :loading="generating" :disabled="!canGenerate" @click="generateFromPrompt">
            {{ text.tables.workflowRules.generateButton }}
          </el-button>
          <span class="ai-generator-tip">{{ text.tables.workflowRules.generatorTip }}</span>
        </div>
      </div>

      <el-table
        v-loading="loading"
        :data="rules"
        border
        class="rules-table"
        size="small"
        @row-dblclick="row => openEdit(row as WorkflowRuleRow)"
      >
        <el-table-column prop="ruleKey" :label="text.tables.workflowRules.key" width="220" show-overflow-tooltip />
        <el-table-column prop="title" :label="text.tables.workflowRules.titleCol" min-width="200" show-overflow-tooltip />
        <el-table-column prop="description" :label="text.tables.workflowRules.description" min-width="260" show-overflow-tooltip />
        <el-table-column prop="priority" :label="text.tables.workflowRules.priority" width="80" align="center" />
        <el-table-column prop="isActive" :label="text.tables.workflowRules.active" width="100" align="center">
          <template #default="scope">
            <el-tag :type="scope.row.isActive ? 'success' : 'info'">{{ scope.row.isActive ? text.common.enabled : text.common.disabled }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="updatedAt" :label="text.tables.workflowRules.updated" width="160">
          <template #default="scope">
            {{ formatDate(scope.row.updatedAt) }}
          </template>
        </el-table-column>
        <el-table-column fixed="right" :label="text.tables.workflowRules.actions" width="220">
          <template #default="scope">
            <el-button size="small" type="primary" @click="openEdit(scope.row)">{{ text.common.edit }}</el-button>
            <el-button size="small" type="info" @click="openTest(scope.row)">{{ text.tables.workflowRules.test }}</el-button>
            <el-popconfirm :title="text.tables.workflowRules.deleteConfirm" @confirm="removeRule(scope.row)">
              <template #reference>
                <el-button size="small" type="danger">{{ text.common.delete }}</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-drawer v-model="editor.visible" size="480px" :title="editorTitle">
      <el-form label-position="top" class="editor-form">
        <el-form-item :label="text.tables.workflowRules.key">
          <el-input v-model="editor.form.ruleKey" :disabled="!editor.isNew" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.titleCol">
          <el-input v-model="editor.form.title" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.description">
          <el-input v-model="editor.form.description" type="textarea" :rows="2" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.instructions">
          <el-input v-model="editor.form.instructions" type="textarea" :rows="3" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.priority">
          <el-input-number v-model="editor.form.priority" :min="1" :max="999" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.active">
          <el-switch v-model="editor.form.isActive" />
        </el-form-item>
        <el-form-item :label="text.tables.workflowRules.actionsField">
          <el-input
            v-model="editor.actionsText"
            type="textarea"
            :placeholder="actionsPlaceholder"
            :rows="12"
            spellcheck="false"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <div class="drawer-footer">
          <el-button @click="editor.visible = false">{{ text.common.cancel }}</el-button>
          <el-button type="primary" :loading="saving" @click="saveRule">{{ text.common.save }}</el-button>
        </div>
      </template>
    </el-drawer>

    <el-dialog v-model="tester.visible" width="620px" :title="testerTitle">
      <div class="tester-body">
        <div class="tester-section">
          <div class="tester-label">{{ text.tables.workflowRules.testPayload }}</div>
          <el-input
            v-model="tester.payloadText"
            type="textarea"
            :rows="12"
            :placeholder="text.tables.workflowRules.testPayloadPlaceholder"
          />
        </div>
        <div class="tester-actions">
          <el-button type="primary" :loading="tester.loading" @click="runTest">{{ text.tables.workflowRules.runTest }}</el-button>
        </div>
        <div class="tester-result" v-if="tester.result">
          <div class="tester-label">{{ text.tables.workflowRules.testResult }}</div>
          <pre>{{ tester.result }}</pre>
        </div>
      </div>
      <template #footer>
        <el-button @click="tester.visible = false">{{ text.common.close }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import dayjs from 'dayjs'
import { useI18n } from '../i18n'
import {
  createWorkflowRule,
  deleteWorkflowRule,
  interpretWorkflowRule,
  listWorkflowRules,
  testWorkflowRule,
  updateWorkflowRule,
  type WorkflowRulePayload
} from '../api/workflows'

interface WorkflowRuleRow {
  ruleKey: string
  title: string
  description: string
  instructions: string
  priority: number
  isActive: boolean
  updatedAt: string | null
  actions: any[]
}

const { text } = useI18n()

const loading = ref(false)
const saving = ref(false)
const generating = ref(false)
const rules = ref<WorkflowRuleRow[]>([])
const prompt = ref('')
const tester = reactive({
  visible: false,
  loading: false,
  ruleKey: '',
  payloadText: '',
  result: ''
})

const editor = reactive({
  visible: false,
  isNew: true,
  form: {
    ruleKey: '',
    title: '',
    description: '',
    instructions: '',
    priority: 100,
    isActive: true
  },
  actionsText: '[]'
})

const canGenerate = computed(() => prompt.value.trim().length > 0)
const editorTitle = computed(() => editor.isNew ? text.value.tables.workflowRules.editorCreate : text.value.tables.workflowRules.editorEdit)
const testerTitle = computed(() => tester.ruleKey ? `${text.value.tables.workflowRules.testFor} ${tester.ruleKey}` : text.value.tables.workflowRules.test)
const actionsPlaceholder = computed(() => text.value.tables.workflowRules.actionsPlaceholder)

function resetEditor() {
  editor.form = {
    ruleKey: '',
    title: '',
    description: '',
    instructions: '',
    priority: 100,
    isActive: true
  }
  editor.actionsText = '[\n  {\n    "type": "voucher.autoCreate",\n    "params": {\n      "header": {},\n      "lines": []\n    }\n  }\n]'
}

function formatDate(val: string | null) {
  if (!val) return ''
  return dayjs(val).format('YYYY-MM-DD HH:mm')
}

async function loadRules() {
  loading.value = true
  try {
    const resp = await listWorkflowRules()
    const data = Array.isArray(resp.data) ? resp.data : []
    rules.value = data.map((it: any) => ({
      ruleKey: it.ruleKey,
      title: it.title,
      description: it.description,
      instructions: it.instructions,
      priority: Number.isFinite(it.priority) ? it.priority : 100,
      isActive: it.isActive !== false,
      updatedAt: it.updatedAt,
      actions: Array.isArray(it.actions) ? it.actions : []
    }))
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

function openNew() {
  editor.isNew = true
  resetEditor()
  editor.visible = true
}

function openEdit(row: WorkflowRuleRow) {
  editor.isNew = false
  editor.form = {
    ruleKey: row.ruleKey,
    title: row.title,
    description: row.description,
    instructions: row.instructions,
    priority: row.priority,
    isActive: row.isActive
  }
  editor.actionsText = JSON.stringify(row.actions ?? [], null, 2)
  editor.visible = true
}

function openTest(row: WorkflowRuleRow) {
  tester.visible = true
  tester.ruleKey = row.ruleKey
  tester.result = ''
  tester.payloadText = JSON.stringify({
    document: {
      type: 'invoice',
      description: row.description,
      sample: true
    }
  }, null, 2)
}

async function generateFromPrompt() {
  if (!canGenerate.value) return
  generating.value = true
  try {
    const resp = await interpretWorkflowRule(prompt.value.trim())
    const data = resp.data || {}
    editor.isNew = true
    editor.form = {
      ruleKey: data.ruleKey || '',
      title: data.title || '',
      description: data.description || '',
      instructions: data.instructions || '',
      priority: Number.isFinite(data.priority) ? data.priority : 100,
      isActive: data.isActive !== false
    }
    editor.actionsText = JSON.stringify(data.actions ?? [], null, 2)
    editor.visible = true
    ElMessage.success(text.value.tables.workflowRules.generateSuccess)
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.tables.workflowRules.generateFail)
  } finally {
    generating.value = false
  }
}

async function saveRule() {
  let actions: any[] = []
  try {
    const parsed = JSON.parse(editor.actionsText || '[]')
    if (!Array.isArray(parsed)) throw new Error('actions should be array')
    actions = parsed
  } catch (err: any) {
    ElMessage.error(text.value.tables.workflowRules.actionsInvalid)
    return
  }
  const payload: WorkflowRulePayload = {
    ruleKey: editor.form.ruleKey.trim(),
    title: editor.form.title,
    description: editor.form.description,
    instructions: editor.form.instructions,
    priority: editor.form.priority,
    isActive: editor.form.isActive,
    actions
  }
  if (!payload.ruleKey) {
    ElMessage.error(text.value.tables.workflowRules.keyRequired)
    return
  }
  saving.value = true
  try {
    if (editor.isNew) {
      await createWorkflowRule(payload)
    } else {
      await updateWorkflowRule(editor.form.ruleKey, payload)
    }
    ElMessage.success(text.value.common.saved)
    editor.visible = false
    loadRules()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.common.saveFailed)
  } finally {
    saving.value = false
  }
}

async function removeRule(row: WorkflowRuleRow) {
  try {
    await deleteWorkflowRule(row.ruleKey)
    ElMessage.success(text.value.common.deleted)
    loadRules()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.common.deleteFailed)
  }
}

async function runTest() {
  if (!tester.ruleKey) return
  let payload: any
  try {
    payload = JSON.parse(tester.payloadText || '{}')
  } catch (e: any) {
    ElMessage.error(text.value.tables.workflowRules.testPayloadInvalid)
    return
  }
  tester.loading = true
  tester.result = ''
  try {
    const resp = await testWorkflowRule(tester.ruleKey, payload)
    tester.result = JSON.stringify(resp.data, null, 2)
  } catch (e: any) {
    tester.result = e?.response?.data?.error || e?.message || 'ERROR'
  } finally {
    tester.loading = false
  }
}

onMounted(loadRules)
</script>

<style scoped>
.workflow-rules {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.rules-panel {
  width: 100%;
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.panel-title {
  font-size: 16px;
  font-weight: 600;
}

.panel-actions {
  display: flex;
  gap: 8px;
}

.ai-generator {
  margin-bottom: 16px;
  padding: 12px;
  background: var(--el-bg-color-overlay);
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
}

.ai-generator-header {
  font-size: 14px;
  font-weight: 500;
  margin-bottom: 8px;
}

.ai-generator-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 12px;
}

.ai-generator-tip {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.rules-table {
  width: 100%;
}

.editor-form {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.drawer-footer {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}

.tester-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.tester-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tester-label {
  font-weight: 500;
}

.tester-actions {
  display: flex;
  justify-content: flex-end;
}

.tester-result {
  background: var(--el-color-info-light-9);
  border-radius: 6px;
  padding: 10px;
  font-family: 'Fira Code', Consolas, monospace;
  font-size: 12px;
}
</style>

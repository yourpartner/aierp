<template>
  <div class="page page-large agent-rules">
    <el-card class="rules-card" :body-style="{ padding: '16px 20px 24px' }">
      <template #header>
        <div class="panel-header">
          <div class="panel-title">{{ text.value.nav.agentRules || 'AI 会计规则' }}</div>
          <div class="panel-actions">
            <el-checkbox v-model="showAll" @change="loadRules">{{ text.value.tables.agentRules.showInactive }}</el-checkbox>
            <el-button class="action-btn" :icon="RefreshRight" :loading="loading" @click="loadRules">
              {{ text.value.buttons.refresh }}
            </el-button>
            <el-button class="action-btn" type="success" :icon="Plus" @click="openCreate">
              {{ text.value.tables.agentRules.new }}
            </el-button>
          </div>
        </div>
      </template>

      <el-table
        :data="filteredRules"
        v-loading="loading"
        class="rules-table"
        stripe
        border
        size="small"
        @row-dblclick="row => openEdit(row as RuleRow)"
      >
        <el-table-column prop="title" :label="text.value.tables.agentRules.titleCol" min-width="220" show-overflow-tooltip />
        <el-table-column :label="text.value.tables.agentRules.keywords" min-width="200">
          <template #default="scope">
            <div class="hint-tags" v-if="scope.row.keywords.length">
              <el-tag v-for="item in scope.row.keywords" :key="item" size="small" type="info">{{ item }}</el-tag>
            </div>
            <span v-else class="placeholder">-</span>
          </template>
        </el-table-column>
        <el-table-column :label="text.value.tables.agentRules.account" min-width="180">
          <template #default="scope">
            <span v-if="scope.row.accountCode || scope.row.accountName">
              {{ formatAccount(scope.row) }}
            </span>
            <span v-else class="placeholder">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="priority" :label="text.value.tables.agentRules.priority" width="90" align="center" />
        <el-table-column prop="isActive" :label="text.value.tables.agentRules.active" width="110" align="center">
          <template #default="scope">
            <el-tag :type="scope.row.isActive ? 'success' : 'info'">
              {{ scope.row.isActive ? text.value.common.enabled : text.value.common.disabled }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="updatedAt" :label="text.value.tables.agentRules.updated" width="170">
          <template #default="scope">
            {{ formatDate(scope.row.updatedAt) }}
          </template>
        </el-table-column>
        <el-table-column fixed="right" :label="text.value.tables.agentRules.actions" width="200">
          <template #default="scope">
            <el-button size="small" type="primary" @click="openEdit(scope.row)">{{ text.value.common.edit }}</el-button>
            <el-button size="small" type="danger" @click="removeRule(scope.row)">{{ text.value.common.delete }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-drawer v-model="editor.visible" size="520px" :title="editorTitle">
      <div class="drawer-body" v-loading="editor.saving">
        <el-form label-position="top" size="small" class="editor-form">
          <el-form-item :label="text.value.tables.agentRules.formTitle" :error="errors.title">
            <el-input v-model="form.title" maxlength="120" show-word-limit />
          </el-form-item>
          <el-form-item :label="text.value.tables.agentRules.formDescription">
            <el-input v-model="form.description" type="textarea" :rows="2" />
          </el-form-item>
          <el-form-item :label="text.value.tables.agentRules.formKeywords">
            <el-select
              v-model="form.keywords"
              multiple
              filterable
              allow-create
              default-first-option
              :placeholder="text.value.tables.agentRules.formKeywordsPlaceholder"
            />
          </el-form-item>
          <el-form-item :label="text.value.tables.agentRules.formAccountCode">
            <el-input v-model="form.accountCode" maxlength="32" />
          </el-form-item>
          <el-form-item :label="text.value.tables.agentRules.formAccountName">
            <el-input v-model="form.accountName" maxlength="120" />
          </el-form-item>
          <el-form-item :label="text.value.tables.agentRules.formNote">
            <el-input v-model="form.note" maxlength="120" />
          </el-form-item>
          <div class="form-row">
            <el-form-item :label="text.value.tables.agentRules.priority" class="priority-item">
              <el-input-number v-model="form.priority" :min="1" :max="9999" />
            </el-form-item>
            <el-form-item :label="text.value.tables.agentRules.active" class="active-item">
              <el-switch v-model="form.isActive" />
            </el-form-item>
          </div>
          <el-form-item :label="text.value.tables.agentRules.formOptions">
            <el-input
              v-model="form.optionsText"
              type="textarea"
              :rows="6"
              :placeholder="text.value.tables.agentRules.formOptionsPlaceholder"
            />
          </el-form-item>
        </el-form>
      </div>
      <template #footer>
        <div class="drawer-footer">
          <el-button @click="editor.visible = false">{{ text.value.common.cancel }}</el-button>
          <el-button type="primary" :loading="editor.saving" @click="submitRule">{{ text.value.common.save }}</el-button>
        </div>
      </template>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import dayjs from 'dayjs'
import { ElMessage, ElMessageBox } from 'element-plus'
import { RefreshRight, Plus } from '@element-plus/icons-vue'
import { useI18n } from '../i18n'
import {
  createAgentAccountingRule,
  deleteAgentAccountingRule,
  listAgentAccountingRules,
  updateAgentAccountingRule,
  type AgentAccountingRule
} from '../api/agentAccountingRules'

interface RuleRow extends AgentAccountingRule {}

const { text } = useI18n()

const loading = ref(false)
const showAll = ref(true)
const rules = ref<RuleRow[]>([])

const editor = reactive({
  visible: false,
  saving: false,
  isNew: true,
  id: ''
})

const form = reactive({
  title: '',
  description: '',
  keywords: [] as string[],
  accountCode: '',
  accountName: '',
  note: '',
  priority: 100,
  isActive: true,
  optionsText: ''
})

const errors = reactive({
  title: ''
})

const filteredRules = computed(() => (showAll.value ? rules.value : rules.value.filter(item => item.isActive)))
const editorTitle = computed(() => editor.isNew ? text.value.tables.agentRules.editorCreate : text.value.tables.agentRules.editorEdit)

function formatDate(value: string | null | undefined) {
  if (!value) return '-'
  return dayjs(value).format('YYYY-MM-DD HH:mm')
}

function formatAccount(rule: RuleRow) {
  const code = rule.accountCode?.trim()
  const name = rule.accountName?.trim()
  if (code && name) return `${code} ${name}`
  return code || name || '-'
}

function resetForm() {
  form.title = ''
  form.description = ''
  form.keywords = []
  form.accountCode = ''
  form.accountName = ''
  form.note = ''
  form.priority = 100
  form.isActive = true
  form.optionsText = ''
  errors.title = ''
}

async function loadRules() {
  if (loading.value) return
  loading.value = true
  try {
    const resp = await listAgentAccountingRules(showAll.value)
    rules.value = Array.isArray(resp.data) ? resp.data : []
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

function openCreate() {
  resetForm()
  editor.isNew = true
  editor.id = ''
  editor.visible = true
}

function openEdit(rule: RuleRow) {
  resetForm()
  editor.isNew = false
  editor.id = rule.id
  form.title = rule.title
  form.description = rule.description || ''
  form.keywords = Array.isArray(rule.keywords) ? [...rule.keywords] : []
  form.accountCode = rule.accountCode || ''
  form.accountName = rule.accountName || ''
  form.note = rule.note || ''
  form.priority = rule.priority ?? 100
  form.isActive = rule.isActive !== false
  form.optionsText = rule.options ? safeStringify(rule.options) : ''
  editor.visible = true
}

function safeStringify(value: any) {
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value ?? '')
  }
}

async function submitRule() {
  if (editor.saving) return
  if (!form.title.trim()) {
    errors.title = text.value.tables.agentRules.titleRequired
    return
  }
  errors.title = ''

  let options: any = undefined
  if (form.optionsText && form.optionsText.trim()) {
    try {
      options = JSON.parse(form.optionsText)
    } catch (err: any) {
      ElMessage.error(text.value.tables.agentRules.optionsInvalid)
      return
    }
  }

  const payload = {
    title: form.title.trim(),
    description: form.description?.trim() || undefined,
    keywords: form.keywords.filter(item => !!item?.trim()),
    accountCode: form.accountCode?.trim() || undefined,
    accountName: form.accountName?.trim() || undefined,
    note: form.note?.trim() || undefined,
    priority: form.priority,
    isActive: form.isActive,
    options
  }

  editor.saving = true
  try {
    if (editor.isNew) {
      await createAgentAccountingRule(payload)
      ElMessage.success(text.value.tables.agentRules.createSuccess)
    } else {
      await updateAgentAccountingRule(editor.id, payload)
      ElMessage.success(text.value.tables.agentRules.updateSuccess)
    }
    editor.visible = false
    await loadRules()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || text.value.common.saveFailed)
  } finally {
    editor.saving = false
  }
}

async function removeRule(rule: RuleRow) {
  try {
    await ElMessageBox.confirm(text.value.tables.agentRules.deleteConfirm, text.value.common.confirm, {
      type: 'warning'
    })
  } catch {
    return
  }

  try {
    await deleteAgentAccountingRule(rule.id)
    ElMessage.success(text.value.tables.agentRules.deleteSuccess)
    await loadRules()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || text.value.common.deleteFailed)
  }
}

onMounted(() => {
  loadRules()
})
</script>

<style scoped>
.agent-rules {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.rules-card {
  width: 100%;
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.panel-title {
  font-size: 18px;
  font-weight: 600;
  color: #0f172a;
}

.panel-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.action-btn {
  min-width: 110px;
}

.rules-table {
  margin-top: 8px;
}

.hint-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.placeholder {
  color: #94a3b8;
}

.drawer-body {
  padding-right: 8px;
}

.editor-form {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.form-row {
  display: flex;
  align-items: flex-end;
  gap: 16px;
}

.priority-item {
  flex: 1;
}

.active-item {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.drawer-footer {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}
</style>


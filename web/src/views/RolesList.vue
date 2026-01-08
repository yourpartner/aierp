<template>
  <div class="roles-list">
    <el-card>
      <template #header>
        <div class="roles-list-header">
          <div class="roles-list-header__title">{{ text.title }}</div>
          <div class="roles-list-header__actions">
            <el-button size="small" @click="runAudit" :loading="auditing">
              <el-icon><Warning /></el-icon> {{ text.audit }}
            </el-button>
            <el-button size="small" type="success" @click="showAiDialog=true">
              <el-icon><MagicStick /></el-icon> {{ text.aiCreate }}
            </el-button>
            <el-button size="small" type="primary" @click="showCreate=true">{{ text.create }}</el-button>
          </div>
        </div>
      </template>
      
      <!-- 审计结果 -->
      <el-alert v-if="auditResult" :type="auditResult.highRiskCount > 0 ? 'error' : (auditResult.mediumRiskCount > 0 ? 'warning' : 'success')" 
                :title="`${text.auditResult}: ${auditResult.totalRoles}个角色, 高风险${auditResult.highRiskCount}个, 中风险${auditResult.mediumRiskCount}个`"
                show-icon closable @close="auditResult=null" style="margin-bottom:12px" />

      <el-table :data="rows" size="small" border @row-dblclick="onEdit">
        <el-table-column type="index" width="60" />
        <el-table-column :label="text.roleCode" prop="roleCode" width="180" />
        <el-table-column :label="text.roleName" prop="roleName" width="160" />
        <el-table-column :label="text.roleType" width="100">
          <template #default="{ row }">
            <el-tag :type="getRoleTypeTag(row.roleType)" size="small">{{ getRoleTypeLabel(row.roleType) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="text.capsCount" width="100">
          <template #default="{ row }">{{ row.caps?.length || 0 }}</template>
        </el-table-column>
        <el-table-column :label="text.userCount" width="100">
          <template #default="{ row }">{{ row.userCount || 0 }}</template>
        </el-table-column>
        <el-table-column :label="text.description" min-width="200">
          <template #default="{ row }">{{ row.description || '-' }}</template>
        </el-table-column>
        <el-table-column :label="text.actions" width="180">
          <template #default="{ row }">
            <el-button size="small" @click="openEdit(row.id)">{{ text.edit }}</el-button>
            <el-button size="small" @click="checkRole(row.id)">{{ text.check }}</el-button>
            <el-popconfirm v-if="row.roleType !== 'builtin'" :title="text.confirmDelete" @confirm="deleteRole(row.id)">
              <template #reference>
                <el-button size="small" type="danger" :disabled="row.userCount > 0">{{ text.delete }}</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 创建/编辑角色对话框 -->
    <el-dialog v-model="showCreate" :title="editId ? text.editRole : text.createRole" width="800px" destroy-on-close>
      <el-form :model="form" label-width="120px" size="small">
        <el-form-item :label="text.roleCode" required>
          <el-input v-model="form.roleCode" :disabled="!!editId" :placeholder="text.roleCodePlaceholder" />
        </el-form-item>
        <el-form-item :label="text.roleName">
          <el-input v-model="form.roleName" />
        </el-form-item>
        <el-form-item :label="text.description">
          <el-input v-model="form.description" type="textarea" :rows="2" />
        </el-form-item>
        <el-form-item :label="text.caps">
          <div class="caps-selector">
            <div v-for="mod in modules" :key="mod.moduleCode" class="caps-module">
              <div class="caps-module-title">{{ getModuleName(mod) }}</div>
              <el-checkbox-group v-model="form.caps">
                <el-checkbox v-for="cap in getModuleCaps(mod.moduleCode)" :key="cap.capCode" :label="cap.capCode" class="cap-checkbox">
                  <span>{{ getCapName(cap) }}</span>
                  <el-tag v-if="cap.isSensitive" type="danger" size="small" style="margin-left:4px">{{ text.sensitive }}</el-tag>
                </el-checkbox>
              </el-checkbox-group>
            </div>
          </div>
        </el-form-item>
        <el-form-item :label="text.dataScopes">
          <div class="data-scopes">
            <div v-for="(scope, idx) in form.dataScopes" :key="idx" class="data-scope-row">
              <el-select v-model="scope.entityType" size="small" :placeholder="text.entityType" style="width:150px">
                <el-option :label="text.entityVoucher" value="voucher" />
                <el-option :label="text.entityEmployee" value="employee" />
                <el-option :label="text.entityCustomer" value="customer" />
                <el-option :label="text.entityOrder" value="order" />
              </el-select>
              <el-select v-model="scope.scopeType" size="small" style="width:150px">
                <el-option :label="text.scopeAll" value="all" />
                <el-option :label="text.scopeDept" value="department" />
                <el-option :label="text.scopeSelf" value="self" />
              </el-select>
              <el-button size="small" type="danger" @click="form.dataScopes.splice(idx, 1)">{{ text.remove }}</el-button>
            </div>
            <el-button size="small" @click="form.dataScopes.push({entityType:'',scopeType:'all'})">{{ text.addScope }}</el-button>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate=false">{{ text.cancel }}</el-button>
        <el-button type="primary" @click="saveRole" :loading="saving">{{ text.save }}</el-button>
      </template>
    </el-dialog>

    <!-- AI辅助创建角色对话框 -->
    <el-dialog v-model="showAiDialog" :title="text.aiCreateTitle" width="700px" destroy-on-close>
      <div class="ai-dialog-content">
        <el-alert type="info" :closable="false" style="margin-bottom:16px">
          <template #title>{{ text.aiHint }}</template>
        </el-alert>
        
        <el-input v-model="aiPrompt" type="textarea" :rows="5" :placeholder="text.aiPlaceholder" />
        
        <div v-if="aiGenerating" style="text-align:center;padding:20px">
          <el-icon class="is-loading" :size="32"><Loading /></el-icon>
          <div>{{ text.aiGenerating }}</div>
        </div>
        
        <div v-if="aiResult" class="ai-result">
          <el-divider>{{ text.aiResult }}</el-divider>
          <el-descriptions :column="1" border size="small">
            <el-descriptions-item :label="text.roleCode">{{ aiResult.roleCode }}</el-descriptions-item>
            <el-descriptions-item :label="text.roleName">{{ aiResult.roleName }}</el-descriptions-item>
            <el-descriptions-item :label="text.description">{{ aiResult.description }}</el-descriptions-item>
            <el-descriptions-item :label="text.caps">
              <el-tag v-for="c in aiResult.capabilities" :key="c" size="small" style="margin:2px">{{ c }}</el-tag>
            </el-descriptions-item>
            <el-descriptions-item v-if="aiResult.dataScopes?.length" :label="text.dataScopes">
              <span v-for="ds in aiResult.dataScopes" :key="ds.entityType">{{ ds.entityType }}:{{ ds.scopeType }} </span>
            </el-descriptions-item>
          </el-descriptions>
          
          <el-alert v-if="aiResult.warnings?.length" type="warning" style="margin-top:12px">
            <template #title>{{ text.aiWarnings }}</template>
            <ul style="margin:0;padding-left:20px">
              <li v-for="(w, i) in aiResult.warnings" :key="i">{{ w }}</li>
            </ul>
          </el-alert>
        </div>
      </div>
      <template #footer>
        <el-button @click="showAiDialog=false">{{ text.cancel }}</el-button>
        <el-button type="success" @click="generateFromAi" :loading="aiGenerating" :disabled="!aiPrompt.trim()">
          <el-icon><MagicStick /></el-icon> {{ text.generate }}
        </el-button>
        <el-button v-if="aiResult && aiGenerationId" type="primary" @click="applyAiResult" :loading="aiApplying">
          {{ text.applyConfig }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 合规检查结果对话框 -->
    <el-dialog v-model="showCheckDialog" :title="text.checkResult" width="600px">
      <div v-if="checkResult">
        <el-descriptions :column="2" border size="small">
          <el-descriptions-item :label="text.roleName">{{ checkResult.roleName }}</el-descriptions-item>
          <el-descriptions-item :label="text.capsCount">{{ checkResult.capsCount }}</el-descriptions-item>
          <el-descriptions-item :label="text.sensitiveCaps">{{ checkResult.sensitiveCapsCount }}</el-descriptions-item>
        </el-descriptions>
        
        <div v-if="checkResult.issues?.length" style="margin-top:16px">
          <div style="font-weight:600;margin-bottom:8px">{{ text.issues }}:</div>
          <el-alert v-for="(issue, i) in checkResult.issues" :key="i" 
                    :type="issue.severity === 'warning' ? 'warning' : 'info'" 
                    :title="issue.message" show-icon style="margin-bottom:8px" />
        </div>
        
        <div v-if="checkResult.aiAnalysis" style="margin-top:16px">
          <div style="font-weight:600;margin-bottom:8px">{{ text.aiAnalysis }}:</div>
          <el-card shadow="never">{{ checkResult.aiAnalysis }}</el-card>
        </div>
        
        <el-alert v-if="!checkResult.issues?.length" type="success" :title="text.noIssues" style="margin-top:16px" />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { ElMessage } from 'element-plus'
import { Warning, MagicStick, Loading } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'

const { lang } = useI18n()
const texts: Record<string, Record<string, string>> = {
  ja: {
    title: 'ロール管理',
    audit: '監査',
    aiCreate: 'AI作成',
    create: '新規作成',
    roleCode: 'ロールコード',
    roleName: 'ロール名',
    roleType: '種別',
    capsCount: '権限数',
    userCount: 'ユーザー数',
    description: '説明',
    actions: '操作',
    edit: '編集',
    check: 'チェック',
    delete: '削除',
    confirmDelete: 'このロールを削除しますか？',
    createRole: 'ロール作成',
    editRole: 'ロール編集',
    caps: '権限',
    dataScopes: 'データ範囲',
    cancel: 'キャンセル',
    save: '保存',
    saveSuccess: '保存しました',
    deleteSuccess: '削除しました',
    aiCreateTitle: 'AIでロール作成',
    aiHint: '自然言語でロールの職責と権限を説明してください。AIが適切な権限設定を提案します。',
    aiPlaceholder: '例：経理担当者のロールを作成してください。仕訳の作成・編集・参照ができますが、転記や銀行支払いはできません。財務レポートは参照のみ可能です。',
    aiGenerating: 'AI分析中...',
    aiResult: 'AI提案設定',
    aiWarnings: '注意事項',
    generate: '生成',
    applyConfig: '設定を適用',
    checkResult: '権限チェック結果',
    sensitiveCaps: '機密権限数',
    issues: '問題点',
    aiAnalysis: 'AI分析',
    noIssues: '問題は検出されませんでした',
    auditResult: '監査結果',
    builtin: 'システム',
    custom: 'カスタム',
    ai_generated: 'AI生成',
    roleCodePlaceholder: '例：BRANCH_ACCOUNTANT',
    sensitive: '機密',
    entityType: 'データ種別',
    entityVoucher: '仕訳',
    entityEmployee: '従業員',
    entityCustomer: '顧客',
    entityOrder: '注文',
    scopeAll: '全データ',
    scopeDept: '自部署',
    scopeSelf: '自分のみ',
    remove: '削除',
    addScope: 'データ範囲追加',
    requiredRoleCode: 'ロールコードは必須です'
  },
  zh: {
    title: '角色管理',
    audit: '审计',
    aiCreate: 'AI创建',
    create: '新建',
    roleCode: '角色编码',
    roleName: '角色名称',
    roleType: '类型',
    capsCount: '权限数',
    userCount: '用户数',
    description: '描述',
    actions: '操作',
    edit: '编辑',
    check: '检查',
    delete: '删除',
    confirmDelete: '确定删除该角色吗？',
    createRole: '创建角色',
    editRole: '编辑角色',
    caps: '权限',
    dataScopes: '数据范围',
    cancel: '取消',
    save: '保存',
    saveSuccess: '保存成功',
    deleteSuccess: '删除成功',
    aiCreateTitle: 'AI辅助创建角色',
    aiHint: '用自然语言描述角色的职责和权限需求，AI会生成合适的权限配置。',
    aiPlaceholder: '例：创建一个分公司财务角色，可以创建和编辑凭证，查看财务报表，但不能过账和执行银行操作。只能看到本部门的数据。',
    aiGenerating: 'AI分析中...',
    aiResult: 'AI生成的配置',
    aiWarnings: '注意事项',
    generate: '生成',
    applyConfig: '应用配置',
    checkResult: '权限检查结果',
    sensitiveCaps: '敏感权限数',
    issues: '问题',
    aiAnalysis: 'AI分析',
    noIssues: '未发现问题',
    auditResult: '审计结果',
    builtin: '系统',
    custom: '自定义',
    ai_generated: 'AI生成',
    roleCodePlaceholder: '如 BRANCH_ACCOUNTANT',
    sensitive: '敏感',
    entityType: '数据类型',
    entityVoucher: '凭证',
    entityEmployee: '员工',
    entityCustomer: '客户',
    entityOrder: '订单',
    scopeAll: '全部数据',
    scopeDept: '本部门',
    scopeSelf: '仅自己',
    remove: '删除',
    addScope: '添加数据范围',
    requiredRoleCode: '角色编码必填'
  },
  en: {
    title: 'Role Management',
    audit: 'Audit',
    aiCreate: 'AI Create',
    create: 'Create',
    roleCode: 'Role Code',
    roleName: 'Role Name',
    roleType: 'Type',
    capsCount: 'Caps',
    userCount: 'Users',
    description: 'Description',
    actions: 'Actions',
    edit: 'Edit',
    check: 'Check',
    delete: 'Delete',
    confirmDelete: 'Delete this role?',
    createRole: 'Create Role',
    editRole: 'Edit Role',
    caps: 'Capabilities',
    dataScopes: 'Data Scopes',
    cancel: 'Cancel',
    save: 'Save',
    saveSuccess: 'Saved successfully',
    deleteSuccess: 'Deleted successfully',
    aiCreateTitle: 'AI Create Role',
    aiHint: 'Describe the role responsibilities and permissions in natural language. AI will generate appropriate permission settings.',
    aiPlaceholder: 'Example: Create a branch accountant role that can create and edit vouchers, view financial reports, but cannot post or execute bank operations.',
    aiGenerating: 'AI analyzing...',
    aiResult: 'AI Generated Config',
    aiWarnings: 'Warnings',
    generate: 'Generate',
    applyConfig: 'Apply Config',
    checkResult: 'Permission Check Result',
    sensitiveCaps: 'Sensitive Caps',
    issues: 'Issues',
    aiAnalysis: 'AI Analysis',
    noIssues: 'No issues found',
    auditResult: 'Audit Result',
    builtin: 'System',
    custom: 'Custom',
    ai_generated: 'AI Generated',
    roleCodePlaceholder: 'e.g. BRANCH_ACCOUNTANT',
    sensitive: 'Sensitive',
    entityType: 'Entity Type',
    entityVoucher: 'Voucher',
    entityEmployee: 'Employee',
    entityCustomer: 'Customer',
    entityOrder: 'Order',
    scopeAll: 'All Data',
    scopeDept: 'Department',
    scopeSelf: 'Self Only',
    remove: 'Remove',
    addScope: 'Add Data Scope',
    requiredRoleCode: 'Role code is required'
  }
}
const text = computed(() => texts[lang.value] || texts.ja)

const rows = ref<any[]>([])
const modules = ref<any[]>([])
const caps = ref<any[]>([])

const showCreate = ref(false)
const editId = ref<string>('')
const saving = ref(false)
const form = ref({
  roleCode: '',
  roleName: '',
  description: '',
  caps: [] as string[],
  dataScopes: [] as { entityType: string; scopeType: string }[]
})

const showAiDialog = ref(false)
const aiPrompt = ref('')
const aiGenerating = ref(false)
const aiResult = ref<any>(null)
const aiGenerationId = ref<string>('')
const aiApplying = ref(false)

const showCheckDialog = ref(false)
const checkResult = ref<any>(null)

const auditing = ref(false)
const auditResult = ref<any>(null)

async function load() {
  try {
    const res = await api.get('/api/roles')
    rows.value = res.data || []
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function loadPermissions() {
  try {
    const [modsRes, capsRes] = await Promise.all([
      api.get('/api/permissions/modules'),
      api.get('/api/permissions/caps')
    ])
    modules.value = modsRes.data || []
    caps.value = capsRes.data || []
  } catch (e) {
    console.error('Failed to load permissions', e)
  }
}

function getModuleName(mod: any) {
  if (mod.moduleName) {
    const l = lang.value === 'ja' ? 'ja' : (lang.value === 'en' ? 'en' : 'zh')
    return mod.moduleName[l] || mod.moduleName.zh || mod.moduleCode
  }
  return mod.moduleCode
}

function getModuleCaps(moduleCode: string) {
  return caps.value.filter((c: any) => c.moduleCode === moduleCode)
}

function getCapName(cap: any) {
  if (cap.capName) {
    const l = lang.value === 'ja' ? 'ja' : (lang.value === 'en' ? 'en' : 'zh')
    return cap.capName[l] || cap.capName.zh || cap.capCode
  }
  return cap.capCode
}

function getRoleTypeTag(type: string) {
  if (type === 'builtin') return 'info'
  if (type === 'ai_generated') return 'success'
  return ''
}

function getRoleTypeLabel(type: string) {
  return text.value[type] || type
}

function onEdit(row: any) {
  openEdit(row.id)
}

async function openEdit(id: string) {
  editId.value = id
  try {
    const res = await api.get(`/api/roles/${id}`)
    const role = res.data.role
    const dataScopes = res.data.dataScopes || []
    form.value = {
      roleCode: role.roleCode || '',
      roleName: role.roleName || '',
      description: role.description || '',
      caps: role.caps || [],
      dataScopes: dataScopes.map((ds: any) => ({ entityType: ds.entityType, scopeType: ds.scopeType }))
    }
    showCreate.value = true
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function saveRole() {
  if (!form.value.roleCode) {
    ElMessage.warning(text.value.requiredRoleCode)
    return
  }
  
  saving.value = true
  try {
    const payload = {
      roleCode: form.value.roleCode,
      roleName: form.value.roleName,
      description: form.value.description,
      caps: form.value.caps,
      dataScopes: form.value.dataScopes.filter(ds => ds.entityType)
    }
    
    if (editId.value) {
      await api.put(`/api/roles/${editId.value}`, payload)
    } else {
      await api.post('/api/roles', payload)
    }
    
    ElMessage.success(text.value.saveSuccess)
    showCreate.value = false
    editId.value = ''
    resetForm()
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  } finally {
    saving.value = false
  }
}

async function deleteRole(id: string) {
  try {
    await api.delete(`/api/roles/${id}`)
    ElMessage.success(text.value.deleteSuccess)
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function generateFromAi() {
  if (!aiPrompt.value.trim()) return
  
  aiGenerating.value = true
  aiResult.value = null
  aiGenerationId.value = ''
  
  try {
    const res = await api.post('/api/roles/ai-generate', { prompt: aiPrompt.value })
    aiResult.value = res.data.config
    aiGenerationId.value = res.data.generationId
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  } finally {
    aiGenerating.value = false
  }
}

async function applyAiResult() {
  if (!aiGenerationId.value) return
  
  aiApplying.value = true
  try {
    const res = await api.post('/api/roles/ai-apply', { generationId: aiGenerationId.value })
    ElMessage.success(text.value.saveSuccess)
    showAiDialog.value = false
    aiPrompt.value = ''
    aiResult.value = null
    aiGenerationId.value = ''
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  } finally {
    aiApplying.value = false
  }
}

async function checkRole(id: string) {
  try {
    const res = await api.post('/api/roles/ai-check', { roleId: id })
    checkResult.value = res.data
    showCheckDialog.value = true
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function runAudit() {
  auditing.value = true
  try {
    const res = await api.get('/api/roles/ai-audit')
    auditResult.value = res.data
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  } finally {
    auditing.value = false
  }
}

function resetForm() {
  form.value = {
    roleCode: '',
    roleName: '',
    description: '',
    caps: [],
    dataScopes: []
  }
}

onMounted(() => {
  load()
  loadPermissions()
})
</script>

<style scoped>
.roles-list {
  padding: 0;
}
.roles-list-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.roles-list-header__title {
  font-size: 16px;
  font-weight: 600;
}
.roles-list-header__actions {
  display: flex;
  gap: 8px;
}
.caps-selector {
  max-height: 400px;
  overflow-y: auto;
  border: 1px solid #eee;
  border-radius: 4px;
  padding: 12px;
}
.caps-module {
  margin-bottom: 16px;
}
.caps-module-title {
  font-weight: 600;
  margin-bottom: 8px;
  padding-bottom: 4px;
  border-bottom: 1px solid #eee;
}
.cap-checkbox {
  display: block;
  margin: 4px 0;
}
.data-scopes {
  border: 1px solid #eee;
  border-radius: 4px;
  padding: 12px;
}
.data-scope-row {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
}
.ai-dialog-content {
  min-height: 200px;
}
.ai-result {
  margin-top: 16px;
}
</style>


<template>
  <div class="email-rules">
    <div class="page-header">
      <div class="header-left">
        <el-icon class="header-icon"><Setting /></el-icon>
        <h1>メール自動化ルール</h1>
      </div>
      <el-button type="primary" @click="showCreateDialog">
        <el-icon><Plus /></el-icon>
        新規ルール
      </el-button>
    </div>

    <el-card v-loading="loading">
      <el-table :data="rules">
        <el-table-column label="ルール名" prop="ruleName" min-width="180" />
        <el-table-column label="トリガー" prop="triggerType" width="140">
          <template #default="{ row }">
            <el-tag size="small" :type="getTriggerType(row.triggerType)">
              {{ getTriggerLabel(row.triggerType) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="アクション" prop="actionType" width="140">
          <template #default="{ row }">
            {{ getActionLabel(row.actionType) }}
          </template>
        </el-table-column>
        <el-table-column label="テンプレート" width="160">
          <template #default="{ row }">
            <span v-if="row.templateId">{{ getTemplateName(row.templateId) }}</span>
            <span v-else class="text-muted">-</span>
          </template>
        </el-table-column>
        <el-table-column label="状態" width="80" align="center">
          <template #default="{ row }">
            <el-switch 
              v-model="row.isActive" 
              size="small"
              @change="toggleRule(row)"
            />
          </template>
        </el-table-column>
        <el-table-column label="実行回数" prop="executionCount" width="90" align="center" />
        <el-table-column label="操作" width="100" align="center">
          <template #default="{ row }">
            <el-button size="small" link type="primary" @click="editRule(row)">編集</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 編集ダイアログ -->
    <el-dialog v-model="dialogVisible" :title="isEdit ? 'ルール編集' : '新規ルール'" width="700px">
      <el-form :model="form" label-position="top">
        <el-form-item label="ルール名" required>
          <el-input v-model="form.ruleName" placeholder="契約締結時に確認メール送信" />
        </el-form-item>
        
        <el-divider content-position="left">トリガー設定</el-divider>
        
        <el-form-item label="トリガータイプ" required>
          <el-select v-model="form.triggerType" style="width: 100%">
            <el-option label="メール受信時" value="email_received" />
            <el-option label="業務オブジェクト作成時" value="entity_created" />
            <el-option label="業務オブジェクト更新時" value="entity_updated" />
            <el-option label="スケジュール" value="schedule" />
          </el-select>
        </el-form-item>
        
        <el-form-item label="トリガー条件" v-if="form.triggerType === 'email_received'">
          <el-row :gutter="12">
            <el-col :span="12">
              <el-input v-model="triggerConditions.intent" placeholder="意図（project_request等）" />
            </el-col>
            <el-col :span="12">
              <el-input v-model="triggerConditions.fromDomain" placeholder="送信元ドメイン" />
            </el-col>
          </el-row>
        </el-form-item>
        
        <el-form-item label="対象エンティティ" v-if="form.triggerType === 'entity_created' || form.triggerType === 'entity_updated'">
          <el-select v-model="triggerConditions.entityType" style="width: 100%">
            <el-option label="契約" value="contract" />
            <el-option label="案件" value="project" />
            <el-option label="請求書" value="invoice" />
            <el-option label="勤怠" value="timesheet" />
          </el-select>
        </el-form-item>
        
        <el-divider content-position="left">アクション設定</el-divider>
        
        <el-form-item label="アクションタイプ" required>
          <el-select v-model="form.actionType" style="width: 100%">
            <el-option label="メール送信" value="send_email" />
            <el-option label="業務オブジェクト作成" value="create_entity" />
            <el-option label="通知送信" value="notify" />
          </el-select>
        </el-form-item>
        
        <el-form-item label="使用テンプレート" v-if="form.actionType === 'send_email'">
          <el-select v-model="form.templateId" placeholder="テンプレートを選択" style="width: 100%" clearable>
            <el-option 
              v-for="tpl in templates" 
              :key="tpl.id" 
              :label="tpl.templateName" 
              :value="tpl.id"
            />
          </el-select>
        </el-form-item>
        
        <el-form-item label="送信先" v-if="form.actionType === 'send_email'">
          <el-select v-model="actionConfig.recipientType" style="width: 100%">
            <el-option label="メール送信者（返信）" value="sender" />
            <el-option label="関連取引先" value="partner" />
            <el-option label="関連リソース" value="resource" />
            <el-option label="指定アドレス" value="custom" />
          </el-select>
        </el-form-item>
        
        <el-form-item v-if="actionConfig.recipientType === 'custom'">
          <el-input v-model="actionConfig.customEmail" placeholder="送信先メールアドレス" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="saveRule" :loading="saving">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Setting, Plus } from '@element-plus/icons-vue'
import api from '../../api'

interface Rule {
  id: string
  ruleName: string
  triggerType: string
  triggerConditions?: string
  actionType: string
  actionConfig?: string
  templateId?: string
  isActive: boolean
  executionCount: number
}

const loading = ref(false)
const saving = ref(false)
const rules = ref<Rule[]>([])
const templates = ref<any[]>([])

const dialogVisible = ref(false)
const isEdit = ref(false)

const form = reactive({
  id: '',
  ruleName: '',
  triggerType: 'email_received',
  actionType: 'send_email',
  templateId: ''
})

const triggerConditions = reactive({
  intent: '',
  fromDomain: '',
  entityType: ''
})

const actionConfig = reactive({
  recipientType: 'sender',
  customEmail: ''
})

const loadRules = async () => {
  loading.value = true
  try {
    const res = await api.get('/staffing/email/rules')
    rules.value = res.data.data || []
  } catch (e) {
    console.error('Load rules error:', e)
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

const showCreateDialog = () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    ruleName: '',
    triggerType: 'email_received',
    actionType: 'send_email',
    templateId: ''
  })
  Object.assign(triggerConditions, { intent: '', fromDomain: '', entityType: '' })
  Object.assign(actionConfig, { recipientType: 'sender', customEmail: '' })
  dialogVisible.value = true
}

const editRule = (row: Rule) => {
  isEdit.value = true
  Object.assign(form, {
    id: row.id,
    ruleName: row.ruleName,
    triggerType: row.triggerType,
    actionType: row.actionType,
    templateId: row.templateId || ''
  })
  
  if (row.triggerConditions) {
    const tc = JSON.parse(row.triggerConditions)
    Object.assign(triggerConditions, tc)
  }
  if (row.actionConfig) {
    const ac = JSON.parse(row.actionConfig)
    Object.assign(actionConfig, ac)
  }
  
  dialogVisible.value = true
}

const saveRule = async () => {
  if (!form.ruleName || !form.triggerType || !form.actionType) {
    ElMessage.warning('必須項目を入力してください')
    return
  }
  saving.value = true
  try {
    const data = {
      ...form,
      triggerConditions: JSON.stringify(triggerConditions),
      actionConfig: JSON.stringify(actionConfig)
    }
    
    if (isEdit.value) {
      await api.put(`/staffing/email/rules/${form.id}`, data)
    } else {
      await api.post('/staffing/email/rules', data)
    }
    ElMessage.success('保存しました')
    dialogVisible.value = false
    await loadRules()
  } catch (e: any) {
    ElMessage.error('保存失敗')
  } finally {
    saving.value = false
  }
}

const toggleRule = async (row: Rule) => {
  // TODO: API call to toggle rule
  ElMessage.success(row.isActive ? 'ルールを有効化しました' : 'ルールを無効化しました')
}

const getTriggerLabel = (type: string) => {
  const map: Record<string, string> = {
    email_received: 'メール受信',
    entity_created: 'オブジェクト作成',
    entity_updated: 'オブジェクト更新',
    schedule: 'スケジュール',
    manual: '手動'
  }
  return map[type] || type
}

const getTriggerType = (type: string) => {
  const map: Record<string, string> = {
    email_received: 'primary',
    entity_created: 'success',
    entity_updated: 'warning',
    schedule: 'info'
  }
  return map[type] || ''
}

const getActionLabel = (type: string) => {
  const map: Record<string, string> = {
    send_email: 'メール送信',
    create_entity: 'オブジェクト作成',
    update_entity: 'オブジェクト更新',
    notify: '通知送信'
  }
  return map[type] || type
}

const getTemplateName = (templateId: string) => {
  const tpl = templates.value.find(t => t.id === templateId)
  return tpl?.templateName || templateId
}

onMounted(() => {
  loadRules()
  loadTemplates()
})
</script>

<style scoped>
.email-rules {
  padding: 20px;
}

.page-header {
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

.text-muted {
  color: #c0c4cc;
}
</style>


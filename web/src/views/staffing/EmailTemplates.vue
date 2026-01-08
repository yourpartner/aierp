<template>
  <div class="email-templates">
    <div class="page-header">
      <div class="header-left">
        <el-icon class="header-icon"><Document /></el-icon>
        <h1>メールテンプレート</h1>
      </div>
      <el-button type="primary" @click="showCreateDialog">
        <el-icon><Plus /></el-icon>
        新規作成
      </el-button>
    </div>

    <el-card v-loading="loading">
      <el-table :data="templates">
        <el-table-column label="テンプレートコード" prop="templateCode" width="160" />
        <el-table-column label="テンプレート名" prop="templateName" min-width="180" />
        <el-table-column label="カテゴリ" prop="category" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="getCategoryType(row.category)">
              {{ getCategoryLabel(row.category) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="件名" prop="subjectTemplate" min-width="200" />
        <el-table-column label="状態" width="80" align="center">
          <template #default="{ row }">
            <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
              {{ row.isActive ? '有効' : '無効' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="120" align="center">
          <template #default="{ row }">
            <el-button size="small" link type="primary" @click="editTemplate(row)">編集</el-button>
            <el-button size="small" link @click="previewTemplate(row)">プレビュー</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 編集ダイアログ -->
    <el-dialog v-model="dialogVisible" :title="isEdit ? 'テンプレート編集' : '新規テンプレート'" width="800px">
      <el-form :model="form" label-position="top">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="テンプレートコード" required>
              <el-input v-model="form.templateCode" :disabled="isEdit" placeholder="contract_confirm" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="テンプレート名" required>
              <el-input v-model="form.templateName" placeholder="契約確認メール" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="カテゴリ">
              <el-select v-model="form.category" style="width: 100%">
                <el-option label="一般" value="general" />
                <el-option label="案件" value="project" />
                <el-option label="契約" value="contract" />
                <el-option label="請求" value="invoice" />
                <el-option label="勤怠" value="timesheet" />
                <el-option label="リマインダー" value="reminder" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="状態">
              <el-switch v-model="form.isActive" active-text="有効" inactive-text="無効" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="件名テンプレート" required>
          <el-input v-model="form.subjectTemplate" placeholder="【{{companyName}}】契約書送付のご連絡" />
          <div class="form-tip">変数: <code v-pre>{{変数名}}</code> の形式で使用できます</div>
        </el-form-item>
        <el-form-item label="本文テンプレート" required>
          <el-input 
            v-model="form.bodyTemplate" 
            type="textarea" 
            :rows="12"
            placeholder="{{clientName}} 様&#10;&#10;お世話になっております。&#10;..."
          />
        </el-form-item>
        <el-form-item label="利用可能な変数">
          <div class="variable-chips">
            <el-tag 
              v-for="v in commonVariables" 
              :key="v" 
              size="small" 
              class="variable-chip"
              @click="insertVariable(v)"
            >
              {{ '{' + '{' + v + '}' + '}' }}
            </el-tag>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="saveTemplate" :loading="saving">保存</el-button>
      </template>
    </el-dialog>

    <!-- プレビューダイアログ -->
    <el-dialog v-model="previewVisible" title="テンプレートプレビュー" width="600px">
      <div class="preview-section">
        <div class="preview-label">件名:</div>
        <div class="preview-subject">{{ previewData.subject }}</div>
      </div>
      <el-divider />
      <div class="preview-body" v-html="previewData.body"></div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Document, Plus } from '@element-plus/icons-vue'
import api from '../../api'

interface Template {
  id: string
  templateCode: string
  templateName: string
  category: string
  subjectTemplate: string
  bodyTemplate: string
  isActive: boolean
}

const loading = ref(false)
const saving = ref(false)
const templates = ref<Template[]>([])

const dialogVisible = ref(false)
const isEdit = ref(false)
const form = reactive({
  id: '',
  templateCode: '',
  templateName: '',
  category: 'general',
  subjectTemplate: '',
  bodyTemplate: '',
  isActive: true
})

const previewVisible = ref(false)
const previewData = reactive({
  subject: '',
  body: ''
})

const commonVariables = [
  'clientName', 'companyName', 'resourceName', 'contractNo', 
  'projectName', 'startDate', 'endDate', 'amount', 'yearMonth',
  'dueDate', 'invoiceNo'
]

const loadTemplates = async () => {
  loading.value = true
  try {
    const res = await api.get('/staffing/email/templates')
    templates.value = res.data.data || []
  } catch (e) {
    console.error('Load templates error:', e)
  } finally {
    loading.value = false
  }
}

const showCreateDialog = () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    templateCode: '',
    templateName: '',
    category: 'general',
    subjectTemplate: '',
    bodyTemplate: '',
    isActive: true
  })
  dialogVisible.value = true
}

const editTemplate = (row: Template) => {
  isEdit.value = true
  Object.assign(form, row)
  dialogVisible.value = true
}

const saveTemplate = async () => {
  if (!form.templateCode || !form.templateName || !form.subjectTemplate || !form.bodyTemplate) {
    ElMessage.warning('必須項目を入力してください')
    return
  }
  saving.value = true
  try {
    if (isEdit.value) {
      await api.put(`/staffing/email/templates/${form.id}`, form)
    } else {
      await api.post('/staffing/email/templates', form)
    }
    ElMessage.success('保存しました')
    dialogVisible.value = false
    await loadTemplates()
  } catch (e: any) {
    ElMessage.error('保存失敗')
  } finally {
    saving.value = false
  }
}

const insertVariable = (varName: string) => {
  form.bodyTemplate += `{{${varName}}}`
}

const previewTemplate = (row: Template) => {
  // 简单替换示例变量
  const sampleData: Record<string, string> = {
    clientName: '株式会社サンプル',
    companyName: '弊社',
    resourceName: '山田太郎',
    contractNo: 'CT-2024-0001',
    projectName: 'Webシステム開発',
    startDate: '2024年4月1日',
    endDate: '2024年9月30日',
    amount: '500,000',
    yearMonth: '2024年1月',
    dueDate: '2024年2月28日',
    invoiceNo: 'INV-2024-0001'
  }
  
  let subject = row.subjectTemplate
  let body = row.bodyTemplate
  
  for (const [key, value] of Object.entries(sampleData)) {
    const regex = new RegExp(`\\{\\{${key}\\}\\}`, 'g')
    subject = subject.replace(regex, value)
    body = body.replace(regex, value)
  }
  
  previewData.subject = subject
  previewData.body = body.replace(/\n/g, '<br>')
  previewVisible.value = true
}

const getCategoryLabel = (cat: string) => {
  const map: Record<string, string> = {
    general: '一般',
    project: '案件',
    contract: '契約',
    invoice: '請求',
    timesheet: '勤怠',
    reminder: 'リマインダー'
  }
  return map[cat] || cat
}

const getCategoryType = (cat: string) => {
  const map: Record<string, string> = {
    project: 'primary',
    contract: 'success',
    invoice: 'warning',
    timesheet: 'info',
    reminder: 'danger'
  }
  return map[cat] || ''
}

onMounted(() => {
  loadTemplates()
})
</script>

<style scoped>
.email-templates {
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

.form-tip {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}

.variable-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.variable-chip {
  cursor: pointer;
}

.variable-chip:hover {
  background: var(--el-color-primary);
  color: white;
}

.preview-section {
  margin-bottom: 12px;
}

.preview-label {
  font-size: 12px;
  color: #909399;
  margin-bottom: 4px;
}

.preview-subject {
  font-size: 16px;
  font-weight: 600;
}

.preview-body {
  font-size: 14px;
  line-height: 1.8;
  padding: 16px;
  background: #f5f7fa;
  border-radius: 6px;
}
</style>


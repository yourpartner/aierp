<template>
  <div class="portal-certificates">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Document /></el-icon>
        <h1>証明書申請</h1>
      </div>
      <el-button type="primary" @click="showRequestDialog">
        <el-icon><Plus /></el-icon>
        新規申請
      </el-button>
    </div>

    <el-card v-loading="loading">
      <el-table :data="certificates">
        <el-table-column label="申請日" prop="requestedAt" width="120">
          <template #default="{ row }">
            {{ formatDate(row.requestedAt) }}
          </template>
        </el-table-column>
        <el-table-column label="証明書種類" prop="requestType" min-width="180">
          <template #default="{ row }">
            {{ getTypeLabel(row.requestType) }}
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="120" align="center">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="完了日" prop="completedAt" width="120">
          <template #default="{ row }">
            {{ row.completedAt ? formatDate(row.completedAt) : '-' }}
          </template>
        </el-table-column>
        <el-table-column label="備考" prop="notes" />
        <el-table-column label="操作" width="120" align="center">
          <template #default="{ row }">
            <el-button 
              v-if="row.status === 'completed' && row.documentUrl" 
              size="small" 
              type="primary"
              @click="downloadDocument(row)"
            >
              ダウンロード
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && certificates.length === 0" description="申請履歴がありません" />
    </el-card>

    <!-- 申請ダイアログ -->
    <el-dialog v-model="dialogVisible" title="証明書申請" width="500px">
      <el-form :model="form" label-position="top">
        <el-form-item label="証明書種類" required>
          <el-select v-model="form.requestType" placeholder="選択してください" style="width: 100%">
            <el-option label="在籍証明書" value="employment" />
            <el-option label="給与証明書" value="salary" />
            <el-option label="源泉徴収票" value="withholding" />
            <el-option label="退職証明書" value="resignation" />
            <el-option label="その他" value="other" />
          </el-select>
        </el-form-item>
        <el-form-item label="使用目的">
          <el-input v-model="form.purpose" placeholder="例：住宅ローン審査のため" />
        </el-form-item>
        <el-form-item label="備考">
          <el-input v-model="form.notes" type="textarea" :rows="3" placeholder="特記事項があればご記入ください" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="submitRequest" :loading="submitting">申請</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { ArrowLeft, Document, Plus } from '@element-plus/icons-vue'
import api from '../../api'

interface Certificate {
  id: string
  requestType: string
  status: string
  requestedAt: string
  completedAt?: string
  documentUrl?: string
  notes?: string
}

const loading = ref(false)
const submitting = ref(false)
const dialogVisible = ref(false)

const certificates = ref<Certificate[]>([])

const form = reactive({
  requestType: '',
  purpose: '',
  notes: ''
})

const loadCertificates = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/certificates')
    certificates.value = res.data.data || []
  } catch (e) {
    console.error('Load certificates error:', e)
  } finally {
    loading.value = false
  }
}

const showRequestDialog = () => {
  Object.assign(form, { requestType: '', purpose: '', notes: '' })
  dialogVisible.value = true
}

const submitRequest = async () => {
  if (!form.requestType) {
    ElMessage.warning('証明書種類を選択してください')
    return
  }
  submitting.value = true
  try {
    await api.post('/portal/certificates', form)
    ElMessage.success('申請を受け付けました')
    dialogVisible.value = false
    await loadCertificates()
  } catch (e: any) {
    ElMessage.error('申請に失敗しました')
  } finally {
    submitting.value = false
  }
}

const downloadDocument = (row: Certificate) => {
  if (row.documentUrl) {
    window.open(row.documentUrl, '_blank')
  }
}

const formatDate = (dateStr: string) => {
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const getTypeLabel = (type: string) => {
  const map: Record<string, string> = {
    employment: '在籍証明書',
    salary: '給与証明書',
    withholding: '源泉徴収票',
    resignation: '退職証明書',
    other: 'その他'
  }
  return map[type] || type
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    pending: '申請中',
    processing: '作成中',
    completed: '完了',
    rejected: '却下'
  }
  return map[status] || status
}

const getStatusType = (status: string) => {
  const map: Record<string, string> = {
    pending: 'warning',
    processing: 'primary',
    completed: 'success',
    rejected: 'danger'
  }
  return map[status] || 'info'
}

onMounted(() => {
  loadCertificates()
})
</script>

<style scoped>
.portal-certificates {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;
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

.back-link {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: white;
  color: #606266;
  text-decoration: none;
}

.back-link:hover {
  background: #ecf5ff;
  color: var(--el-color-primary);
}

.header-icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.header-left h1 {
  margin: 0;
  font-size: 20px;
}
</style>


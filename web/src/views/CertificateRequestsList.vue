<template>
  <div class="cert-requests-list">
    <el-card class="cert-requests-card">
      <template #header>
        <div class="cert-requests-header">
          <div class="cert-requests-header__left">
            <el-icon class="cert-requests-header__icon"><List /></el-icon>
            <span class="cert-requests-header__title">証明書申請履歴</span>
            <el-tag size="small" type="info" class="cert-requests-header__count">{{ rows.length }}件</el-tag>
          </div>
          <div class="cert-requests-header__right">
            <el-button type="primary" @click="$router.push('/cert/request')">
              <el-icon><Plus /></el-icon>
              <span>新規申請</span>
            </el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" border stripe highlight-current-row class="cert-requests-table" v-loading="loading">
        <el-table-column type="index" width="60" label="#" align="center" />
        <el-table-column label="申請日" width="120" align="center">
          <template #default="{ row }">
            {{ formatDate(row.created_at) }}
          </template>
        </el-table-column>
        <el-table-column label="種類" width="140">
          <template #default="{ row }">
            <span class="cert-type">{{ extractCertType(row) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="用途・備考" min-width="200">
          <template #default="{ row }">
            <span class="purpose-text">{{ row.payload?.purpose || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="受信メール" min-width="180">
          <template #default="{ row }">
            <span class="email-text">{{ row.payload?.toEmail || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状況" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="statusTagType(getStatus(row))">
              {{ statusLabel(getStatus(row)) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="アクション" width="120" align="center" fixed="right">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click="downloadPdf(row)">
              <el-icon><Download /></el-icon>
              PDF
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import api from '../api'
import { List, Plus, Download } from '@element-plus/icons-vue'

const rows = reactive<any[]>([])
const loading = ref(false)

async function load(){
  loading.value = true
  try{
    rows.splice(0, rows.length)
    const r = await api.post('/objects/certificate_request/search', { 
      page: 1, 
      pageSize: 100, 
      where: [], 
      orderBy: [{ field: 'created_at', dir: 'DESC' }] 
    })
    const data = (r.data?.data || []) as any[]
    for (const x of data) rows.push(x)
    
    // 各レコードの承認ステップ情報を読み込む
    await loadApprovalSteps()
  } finally { 
    loading.value = false 
  }
}

// 関連する承認タスクステップを読み込む
async function loadApprovalSteps() {
  for (const row of rows) {
    try {
      const resp = await api.post('/objects/approval_task/search', {
        page: 1,
        pageSize: 10,
        where: [
          { field: 'entity', op: 'eq', value: 'certificate_request' },
          { field: 'object_id', op: 'eq', value: row.id }
        ],
        orderBy: [{ field: 'step_no', dir: 'ASC' }]
      })
      const tasks = resp.data?.data || []
      if (tasks.length > 0) {
        const pending = tasks.find((t: any) => t.status === 'pending')
        const rejected = tasks.find((t: any) => t.status === 'rejected')
        if (rejected) {
          row._approvalStep = `却下: ${rejected.step_name || 'Step ' + rejected.step_no}`
        } else if (pending) {
          row._approvalStep = `${pending.step_name || 'Step ' + pending.step_no}`
        } else {
          row._approvalStep = '完了'
        }
      }
    } catch {
      // ignore
    }
  }
}

// 日付フォーマット
function formatDate(dateStr: string | undefined) {
  if (!dateStr) return '-'
  try {
    const d = new Date(dateStr)
    if (isNaN(d.getTime())) return '-'
    return d.toISOString().slice(0, 10)
  } catch {
    return '-'
  }
}

// 証明書タイプを抽出（複数のフィールドをチェック）
function extractCertType(row: any) {
  const p = row?.payload || {}
  const statusValues = ['approved', 'pending', 'rejected', 'draft']
  
  // 1. type フィールドをチェック（ステータス値でなければ使用）
  const typeVal = (p.type || '').toString()
  if (typeVal && !statusValues.includes(typeVal.toLowerCase())) {
    return typeVal
  }
  
  // 2. language フィールドに種類が入っている場合がある
  const langVal = (p.language || '').toString()
  const certKeywords = ['証明', '证明']
  if (langVal && certKeywords.some(k => langVal.includes(k))) {
    return langVal
  }
  
  // 3. 他の可能なフィールド
  return p.certType || p.certificateType || p.kind || '-'
}

// ステータスを取得（複数の場所をチェック）
function getStatus(row: any) {
  const p = row?.payload || {}
  const statusValues = ['approved', 'pending', 'rejected', 'draft']
  
  // 1. status フィールド
  if (p.status && statusValues.includes(p.status.toLowerCase())) {
    return p.status
  }
  
  // 2. type フィールドにステータスが入っている場合
  const typeVal = (p.type || '').toString().toLowerCase()
  if (statusValues.includes(typeVal)) {
    return p.type
  }
  
  return p.status || 'pending'
}

// ステータスタグタイプ
function statusTagType(status: string | undefined) {
  switch (status) {
    case 'approved': return 'success'
    case 'rejected': return 'danger'
    case 'pending': return 'warning'
    default: return 'info'
  }
}

// ステータスラベル
function statusLabel(status: string | undefined) {
  switch (status) {
    case 'approved': return '承認済'
    case 'rejected': return '却下'
    case 'pending': return '審査中'
    default: return status || '-'
  }
}

async function downloadPdf(row:any){
  const id = row?.id; if (!id) return
  const resp = await api.get(`/operations/certificate_request/${id}/pdf`, { responseType: 'blob' })
  const blob = new Blob([resp.data], { type: 'application/pdf' })
  const fn = (row?.payload?.pdf?.filename) || 'certificate.pdf'
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = fn
  document.body.appendChild(a)
  a.click()
  URL.revokeObjectURL(a.href)
  document.body.removeChild(a)
}

onMounted(load)
</script>

<style scoped>
.cert-requests-list {
  padding: 16px;
  max-width: 1200px;
  margin: 0 auto;
}

.cert-requests-card {
  border-radius: 12px;
  overflow: hidden;
}

.cert-requests-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.cert-requests-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.cert-requests-header__icon {
  font-size: 22px;
  color: #909399;
}

.cert-requests-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.cert-requests-header__count {
  font-weight: 500;
}

.cert-requests-header__right {
  display: flex;
  gap: 8px;
}

/* テーブル */
.cert-requests-table {
  width: 100%;
  border-radius: 8px;
  overflow: hidden;
}

.cert-requests-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

.step-info {
  font-size: 12px;
  color: #606266;
}

.text-muted {
  color: #909399;
  font-size: 12px;
}

.cert-type {
  font-weight: 500;
  color: #303133;
}

.purpose-text {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 13px;
  color: #606266;
}

.email-text {
  font-size: 13px;
  color: #606266;
  word-break: break-all;
}
</style>



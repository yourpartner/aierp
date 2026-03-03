<template>
  <div class="wage-ledger-page">
    <!-- ヘッダー -->
    <div class="page-header">
      <div class="title-area">
        <h2 class="page-title">賃金台帳</h2>
        <el-tooltip content="従業員の年次賃金台帳を確認・ダウンロードします" placement="right">
          <el-icon class="info-icon"><InfoFilled /></el-icon>
        </el-tooltip>
      </div>
      <div class="action-area">
        <el-date-picker
          v-model="year"
          type="year"
          placeholder="対象年"
          size="small"
          style="width: 100px"
          value-format="YYYY"
          @change="loadEmployees"
        />
        <el-select v-model="deptFilter" placeholder="部門" size="small" style="width: 140px" clearable @change="loadEmployees">
          <el-option v-for="d in departments" :key="d" :label="d" :value="d" />
        </el-select>
        <el-button type="primary" size="small" :loading="bulkLoading" @click="downloadBulk">
          <el-icon><Download /></el-icon> 一括ダウンロード
        </el-button>
      </div>
    </div>

    <!-- 社員グリッド -->
    <div v-loading="loading" class="employee-grid">
      <div
        v-for="emp in filteredEmployees"
        :key="emp.code"
        class="employee-card"
        :class="{ 'is-downloading': downloadingCodes.has(emp.code) }"
        @click="downloadSingle(emp)"
      >
        <!-- ダウンロード中オーバーレイ -->
        <div v-if="downloadingCodes.has(emp.code)" class="card-overlay">
          <div class="card-overlay__spinner"></div>
          <span class="card-overlay__text">生成中...</span>
        </div>

        <!-- アバター -->
        <div class="emp-avatar" :style="{ background: avatarColor(emp.code) }">
          {{ emp.name.charAt(0) }}
        </div>

        <!-- 情報 -->
        <div class="emp-info">
          <div class="emp-name">{{ emp.name }}</div>
          <div class="emp-code-badge">{{ emp.code }}</div>
          <div v-if="emp.position" class="emp-position">{{ emp.position }}</div>
        </div>

        <!-- ダウンロードアイコン（ホバー時） -->
        <div class="emp-dl-icon">
          <el-icon><Download /></el-icon>
        </div>
      </div>
    </div>

    <div v-if="!loading && filteredEmployees.length === 0" class="empty-state">
      <el-empty description="対象データがありません" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { InfoFilled, Download } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../../api'

interface Employee {
  code: string
  name: string
  department: string
  position: string
  gender: string
}

const year = ref(String(new Date().getFullYear()))
const deptFilter = ref('')
const loading = ref(false)
const bulkLoading = ref(false)
const employees = ref<Employee[]>([])
const downloadingCodes = ref(new Set<string>())

const AVATAR_COLORS = [
  '#4f81bd', '#2e74b5', '#5b9bd5', '#70ad47',
  '#ed7d31', '#a9d18e', '#7030a0', '#0070c0',
  '#00b0f0', '#ff0000', '#595959', '#c55a11',
]

function avatarColor(code: string): string {
  let h = 0
  for (let i = 0; i < code.length; i++) h = (h * 31 + code.charCodeAt(i)) >>> 0
  return AVATAR_COLORS[h % AVATAR_COLORS.length]
}

const departments = computed(() => {
  const set = new Set(employees.value.map(e => e.department).filter(Boolean))
  return Array.from(set).sort()
})

const filteredEmployees = computed(() => {
  if (!deptFilter.value) return employees.value
  return employees.value.filter(e => e.department === deptFilter.value)
})

async function loadEmployees() {
  if (!year.value) return
  loading.value = true
  try {
    const res = await api.get('/payroll/wage-ledger/employees', { params: { year: year.value } })
    employees.value = res.data || []
  } catch {
    ElMessage.error('従業員データの取得に失敗しました')
    employees.value = []
  } finally {
    loading.value = false
  }
}

async function downloadSingle(emp: Employee) {
  if (downloadingCodes.value.has(emp.code)) return
  downloadingCodes.value = new Set(downloadingCodes.value).add(emp.code)
  try {
    const res = await api.get('/payroll/wage-ledger/excel', {
      params: { year: year.value, employeeCode: emp.code },
      responseType: 'blob'
    })
    const url = URL.createObjectURL(new Blob([res.data]))
    const a = document.createElement('a')
    a.href = url
    a.download = `${emp.code}_${emp.name}_賃金台帳_${year.value}.xlsx`
    a.click()
    URL.revokeObjectURL(url)
  } catch {
    ElMessage.error(`${emp.name} のダウンロードに失敗しました`)
  } finally {
    const next = new Set(downloadingCodes.value)
    next.delete(emp.code)
    downloadingCodes.value = next
  }
}

async function downloadBulk() {
  bulkLoading.value = true
  try {
    const params: Record<string, string> = { year: year.value }
    if (deptFilter.value) params.department = deptFilter.value
    const res = await api.get('/payroll/wage-ledger/bulk-excel', {
      params,
      responseType: 'blob'
    })
    const url = URL.createObjectURL(new Blob([res.data]))
    const a = document.createElement('a')
    a.href = url
    a.download = `賃金台帳_${year.value}.xlsx`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('一括ダウンロードが完了しました')
  } catch {
    ElMessage.error('一括ダウンロードに失敗しました')
  } finally {
    bulkLoading.value = false
  }
}

onMounted(() => loadEmployees())
</script>

<style scoped>
/* ─── ページ全体 ─── */
.wage-ledger-page {
  background: #fff;
  border-radius: 16px;
  overflow: hidden;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.12);
  min-width: 720px;
  max-width: 1100px;
}

/* ─── ヘッダー ─── */
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 18px 24px 16px;
  border-bottom: 1px solid #e8edf2;
  background: #fff;
}

.title-area {
  display: flex;
  align-items: center;
  gap: 8px;
}

.page-title {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: #1e293b;
}

.info-icon {
  color: #94a3b8;
  cursor: pointer;
  font-size: 15px;
}

.action-area {
  display: flex;
  gap: 10px;
  align-items: center;
}

/* ─── グリッド ─── */
.employee-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
  gap: 12px;
  padding: 20px 24px 24px;
  background: #f1f5f9;
  min-height: 200px;
}

/* ─── 社員カード ─── */
.employee-card {
  position: relative;
  background: #fff;
  border-radius: 12px;
  padding: 16px 14px 14px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  overflow: hidden;
  transition: transform 0.18s, box-shadow 0.18s;
  box-shadow: 0 1px 3px rgba(0,0,0,0.08), 0 1px 6px rgba(0,0,0,0.04);
  user-select: none;
}

.employee-card::before {
  content: '';
  position: absolute;
  top: 0; left: 0; right: 0;
  height: 4px;
  background: linear-gradient(90deg, #3b82f6, #6366f1);
  opacity: 0;
  transition: opacity 0.18s;
}

.employee-card:hover {
  transform: translateY(-3px);
  box-shadow: 0 8px 20px rgba(59,130,246,0.14), 0 2px 8px rgba(0,0,0,0.06);
}

.employee-card:hover::before {
  opacity: 1;
}

.employee-card:active {
  transform: translateY(-1px);
}

.employee-card.is-downloading {
  pointer-events: none;
}

/* ─── ダウンロード中オーバーレイ ─── */
.card-overlay {
  position: absolute;
  inset: 0;
  background: rgba(255, 255, 255, 0.88);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  z-index: 2;
  border-radius: 12px;
}

.card-overlay__spinner {
  width: 26px;
  height: 26px;
  border: 3px solid #e2e8f0;
  border-top-color: #3b82f6;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.card-overlay__text {
  font-size: 12px;
  color: #3b82f6;
  font-weight: 600;
  letter-spacing: 0.02em;
}

/* ─── アバター ─── */
.emp-avatar {
  width: 52px;
  height: 52px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 22px;
  font-weight: 700;
  color: #fff;
  flex-shrink: 0;
  letter-spacing: -0.02em;
}

/* ─── 情報エリア ─── */
.emp-info {
  width: 100%;
  text-align: center;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.emp-name {
  font-weight: 700;
  font-size: 15px;
  line-height: 1.3;
  color: #1e293b;
  word-break: break-all;
}

.emp-code-badge {
  display: inline-block;
  font-size: 11px;
  color: #64748b;
  background: #f1f5f9;
  border-radius: 4px;
  padding: 1px 6px;
  font-family: monospace;
}

.emp-position {
  font-size: 12px;
  color: #94a3b8;
  line-height: 1.4;
}

/* ─── ダウンロードアイコン ─── */
.emp-dl-icon {
  position: absolute;
  top: 10px;
  right: 10px;
  color: #cbd5e1;
  font-size: 14px;
  transition: color 0.15s;
}

.employee-card:hover .emp-dl-icon {
  color: #3b82f6;
}

/* ─── 空状態 ─── */
.empty-state {
  padding: 40px 24px;
}
</style>

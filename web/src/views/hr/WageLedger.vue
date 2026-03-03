<template>
  <div class="wage-ledger-page">
    <!-- ヘッダー -->
    <div class="page-header">
      <div class="title-area">
        <h2 class="page-title">賃金台帳</h2>
        <span class="page-subtitle">{{ year }}年度</span>
      </div>
      <div class="action-area">
        <el-date-picker
          v-model="year"
          type="year"
          placeholder="対象年"
          size="small"
          style="width: 96px"
          value-format="YYYY"
          @change="loadEmployees"
        />
        <el-select v-model="deptFilter" placeholder="部門で絞り込み" size="small" style="width: 150px" clearable @change="loadEmployees">
          <el-option v-for="d in departments" :key="d" :label="d" :value="d" />
        </el-select>
        <el-button size="small" :loading="bulkLoading" @click="downloadBulk" class="bulk-btn">
          <el-icon><Download /></el-icon>一括ダウンロード
        </el-button>
      </div>
    </div>

    <!-- 件数バー -->
    <div class="count-bar" v-if="!loading">
      <span class="count-label">{{ filteredEmployees.length }}名</span>
      <span class="count-hint">クリックで個人の賃金台帳をダウンロードします</span>
    </div>

    <!-- 社員グリッド -->
    <div v-loading="loading" class="employee-grid">
      <div
        v-for="emp in filteredEmployees"
        :key="emp.code"
        class="emp-tile"
        :class="{ 'emp-tile--loading': downloadingCodes.has(emp.code) }"
        @click="downloadSingle(emp)"
      >
        <!-- ダウンロード中表示 -->
        <template v-if="downloadingCodes.has(emp.code)">
          <div class="tile-spinner"></div>
          <div class="tile-loading-text">生成中</div>
        </template>

        <!-- 通常表示 -->
        <template v-else>
          <div class="tile-avatar">{{ emp.name.charAt(0) }}</div>
          <div class="tile-name">{{ emp.name }}</div>
          <div class="tile-sub">
            <span class="tile-code">{{ emp.code }}</span>
            <span v-if="emp.position" class="tile-pos">{{ emp.position }}</span>
          </div>
        </template>
      </div>
    </div>

    <div v-if="!loading && filteredEmployees.length === 0" class="empty-state">
      <el-empty description="対象データがありません" :image-size="60" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Download } from '@element-plus/icons-vue'
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
/* ── ページ全体 ── */
.wage-ledger-page {
  background: #fff;
  border-radius: 12px;
  overflow: hidden;
  box-shadow: 0 2px 12px rgba(0,0,0,0.10);
  min-width: 680px;
  max-width: 1060px;
}

/* ── ヘッダー ── */
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 20px 24px 18px;
  border-bottom: 1px solid #eaeaea;
}

.title-area {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

.page-title {
  margin: 0;
  font-size: 17px;
  font-weight: 700;
  color: #222;
  letter-spacing: 0.02em;
}

.page-subtitle {
  font-size: 13px;
  color: #999;
}

.action-area {
  display: flex;
  gap: 8px;
  align-items: center;
}

.bulk-btn {
  border-color: #d0d0d0;
  color: #444;
  background: #fafafa;
}
.bulk-btn:hover {
  border-color: #409eff;
  color: #409eff;
  background: #f0f7ff;
}

/* ── 件数バー ── */
.count-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 24px;
  background: #fafafa;
  border-bottom: 1px solid #eaeaea;
}

.count-label {
  font-size: 13px;
  font-weight: 600;
  color: #333;
}

.count-hint {
  font-size: 12px;
  color: #aaa;
}

/* ── グリッド ── */
.employee-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 1px;
  background: #eaeaea;
  min-height: 180px;
}

/* ── 社員タイル ── */
.emp-tile {
  position: relative;
  background: #fff;
  padding: 18px 12px 14px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  transition: background 0.12s;
  user-select: none;
  min-height: 110px;
  justify-content: center;
}

.emp-tile:hover {
  background: #f5f8ff;
}

.emp-tile:active {
  background: #edf2ff;
}

.emp-tile--loading {
  pointer-events: none;
  background: #fafafa;
}

/* ── アバター ── */
.tile-avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  background: #e8edf5;
  color: #4a6fa5;
  font-size: 18px;
  font-weight: 700;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.emp-tile:hover .tile-avatar {
  background: #dae4f5;
}

/* ── テキスト ── */
.tile-name {
  font-size: 14px;
  font-weight: 600;
  color: #1a1a1a;
  text-align: center;
  line-height: 1.4;
  word-break: break-all;
}

.tile-sub {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
}

.tile-code {
  font-size: 11px;
  color: #999;
  font-family: 'Consolas', monospace;
}

.tile-pos {
  font-size: 11px;
  color: #bbb;
}

/* ── ローディング ── */
.tile-spinner {
  width: 22px;
  height: 22px;
  border: 2px solid #e0e0e0;
  border-top-color: #409eff;
  border-radius: 50%;
  animation: spin 0.75s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.tile-loading-text {
  font-size: 11px;
  color: #409eff;
  letter-spacing: 0.05em;
}

/* ── 空状態 ── */
.empty-state {
  padding: 48px 24px;
  background: #fff;
}
</style>

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
        @click="downloadSingle(emp)"
        :title="`${emp.code} ${emp.name} をダウンロード`"
      >
        <span class="emp-name">{{ emp.name }}</span>
        <span class="emp-meta">{{ emp.code }}<template v-if="emp.position"><br/>{{ emp.position }}</template></span>
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
  } catch (e: any) {
    ElMessage.error('従業員データの取得に失敗しました')
    employees.value = []
  } finally {
    loading.value = false
  }
}

async function downloadSingle(emp: Employee) {
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
  } catch (e: any) {
    ElMessage.error(`${emp.name} のダウンロードに失敗しました`)
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
  } catch (e: any) {
    ElMessage.error('一括ダウンロードに失敗しました')
  } finally {
    bulkLoading.value = false
  }
}

onMounted(() => loadEmployees())
</script>

<style scoped>
/* ─── ページ全体：丸角カード（embed-dialog内で角丸ダイアログ風に見せる） ─── */
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

/* ─── 社員グリッド ─── */
.employee-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 10px;
  padding: 20px 24px 24px;
  background: #f8fafc;
  min-height: 200px;
}

/* ─── 社員カード ─── */
.employee-card {
  background: #fff;
  border: 1px solid #e2e8f0;
  border-left: 4px solid #3b82f6;
  border-radius: 10px;
  padding: 12px 14px 12px 12px;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  cursor: pointer;
  transition: transform 0.15s, box-shadow 0.15s, border-color 0.15s;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.05);
  user-select: none;
  min-height: 70px;
}

.employee-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 14px rgba(59, 130, 246, 0.18);
  border-color: #93c5fd;
  border-left-color: #2563eb;
}

.employee-card:active {
  transform: translateY(0);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.08);
}

.emp-name {
  font-weight: 700;
  font-size: 15px;
  line-height: 1.35;
  color: #1e293b;
  word-break: break-all;
}

.emp-meta {
  font-size: 13px;
  color: #64748b;
  line-height: 1.5;
}

.empty-state {
  padding: 40px 24px;
}
</style>

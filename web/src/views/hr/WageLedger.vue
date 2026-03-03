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
        <span class="emp-code">{{ emp.code }}</span>
        <span class="emp-name">{{ emp.name }}</span>
        <span class="emp-position">「{{ emp.position || '社員' }}」</span>
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
.wage-ledger-page {
  padding: 20px;
  background: #f8fafc;
  min-height: 100%;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
  padding: 14px 18px;
  background: #fff;
  border-radius: 10px;
  box-shadow: 0 1px 4px rgba(0,0,0,0.06);
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
  color: #64748b;
  cursor: pointer;
}

.action-area {
  display: flex;
  gap: 10px;
  align-items: center;
}

.employee-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 10px;
  min-height: 200px;
}

.employee-card {
  background: linear-gradient(135deg, #4ade80 0%, #22c55e 100%);
  border-radius: 8px;
  padding: 12px 14px;
  display: flex;
  align-items: center;
  gap: 5px;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
  transition: transform 0.15s, box-shadow 0.15s, filter 0.15s;
  box-shadow: 0 2px 6px rgba(34,197,94,0.25);
  user-select: none;
}

.employee-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 6px 14px rgba(34,197,94,0.35);
  filter: brightness(1.05);
}

.employee-card:active {
  transform: translateY(0);
}

.emp-code {
  font-weight: 600;
  white-space: nowrap;
}

.emp-name {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 90px;
}

.emp-position {
  white-space: nowrap;
  opacity: 0.9;
  font-size: 12px;
}

.empty-state {
  margin-top: 40px;
}
</style>

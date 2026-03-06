<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Money /></el-icon>
            <span class="page-header-title">賞与計算</span>
          </div>
          <div class="page-actions">
            <el-date-picker v-model="month" type="month" value-format="YYYY-MM" placeholder="支給月" style="width:140px" @change="onMonthChange" />
            <el-select v-model="bonusType" style="width:120px">
              <el-option label="夏季賞与" value="summer" />
              <el-option label="冬季賞与" value="winter" />
              <el-option label="特別賞与" value="special" />
            </el-select>
            <el-button type="primary" :loading="calculating" :disabled="!canRun" @click="runCalc">
              <el-icon><CaretRight /></el-icon>計算実行
            </el-button>
            <el-button type="success" :loading="saving" :disabled="!results.length" @click="saveResults">
              <el-icon><FolderChecked /></el-icon>保存
            </el-button>
          </div>
        </div>
      </template>

      <el-alert v-if="error" type="error" :title="error" show-icon closable @close="error=''" style="margin-bottom:12px" />

      <!-- 社員選択＋金額入力 -->
      <div class="bonus-input-section" v-if="!results.length">
        <el-skeleton v-if="loadingEmployees" :rows="4" animated />
        <template v-else>
          <div class="bonus-toolbar">
            <el-input v-model="filterText" placeholder="社員を検索…" clearable :prefix-icon="Search" style="width:240px" />
            <el-button size="small" @click="applyUniformAmount">一括金額設定</el-button>
            <el-input-number v-model="uniformAmount" :min="0" :step="10000" :precision="0" :controls="false" placeholder="一括金額" style="width:150px" />
            <span class="count-label">{{ selectedCount }}名選択</span>
          </div>
          <el-table :data="filteredEmployees" stripe size="small" max-height="500" @selection-change="onSelectionChange">
            <el-table-column type="selection" width="40" />
            <el-table-column prop="employeeCode" label="社員番号" width="100" />
            <el-table-column prop="name" label="氏名" min-width="140" />
            <el-table-column prop="departmentName" label="部門" min-width="120" />
            <el-table-column label="賞与額" width="180">
              <template #default="{ row }">
                <el-input-number
                  v-model="row._bonusAmount"
                  :min="0"
                  :step="10000"
                  :precision="0"
                  :controls="false"
                  size="small"
                  style="width:140px"
                />
              </template>
            </el-table-column>
          </el-table>
        </template>
      </div>

      <!-- 計算結果 -->
      <div v-if="results.length" class="bonus-results">
        <div class="results-header">
          <span class="results-title">計算結果 ({{ results.length }}名)</span>
          <el-button size="small" @click="results = []">入力に戻る</el-button>
        </div>

        <el-collapse v-model="activePanels">
          <el-collapse-item v-for="r in results" :key="r.employeeId" :name="r.employeeId">
            <template #title>
              <div class="result-title-row">
                <span class="emp-name">{{ r.employeeName }} ({{ r.employeeCode }})</span>
                <span class="emp-dept">{{ r.departmentName || '' }}</span>
                <el-tag type="success" size="small">賞与 ¥{{ fmt(r.bonusAmount) }}</el-tag>
                <el-tag type="warning" size="small">控除 ¥{{ fmt(r.bonusAmount - r.netAmount) }}</el-tag>
                <el-tag type="primary" size="small">手取 ¥{{ fmt(r.netAmount) }}</el-tag>
              </div>
            </template>

            <div class="result-detail">
              <el-descriptions :column="3" size="small" border>
                <el-descriptions-item label="賞与額">¥{{ fmt(r.bonusAmount) }}</el-descriptions-item>
                <el-descriptions-item label="健康保険">¥{{ fmt(r.healthIns) }}</el-descriptions-item>
                <el-descriptions-item label="介護保険">¥{{ fmt(r.careIns) }}</el-descriptions-item>
                <el-descriptions-item label="厚生年金">¥{{ fmt(r.pension) }}</el-descriptions-item>
                <el-descriptions-item label="雇用保険">¥{{ fmt(r.empIns) }}</el-descriptions-item>
                <el-descriptions-item label="源泉徴収税">¥{{ fmt(r.withholdingTax) }}</el-descriptions-item>
                <el-descriptions-item label="税率">{{ (r.taxRate * 100).toFixed(3) }}%</el-descriptions-item>
                <el-descriptions-item label="差引支給額" :span="2">
                  <span style="font-weight:700;font-size:15px;color:#409eff">¥{{ fmt(r.netAmount) }}</span>
                </el-descriptions-item>
              </el-descriptions>

              <div class="draft-section" v-if="r.accountingDraft?.length">
                <div class="draft-title">仕訳ドラフト</div>
                <el-table :data="r.accountingDraft" size="small" stripe>
                  <el-table-column prop="accountCode" label="科目コード" width="100" />
                  <el-table-column prop="accountName" label="科目名" min-width="140" />
                  <el-table-column prop="drcr" label="借/貸" width="60" />
                  <el-table-column label="金額" width="140" align="right">
                    <template #default="{ row }">¥{{ fmt(row.amount) }}</template>
                  </el-table-column>
                </el-table>
              </div>
            </div>
          </el-collapse-item>
        </el-collapse>

        <!-- 合計 -->
        <div class="total-bar">
          <span>合計: 賞与 ¥{{ fmt(totalBonus) }} / 控除 ¥{{ fmt(totalDeductions) }} / 手取 ¥{{ fmt(totalNet) }}</span>
        </div>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Money, CaretRight, FolderChecked, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../../api'

const month = ref('')
const bonusType = ref('summer')
const error = ref('')
const calculating = ref(false)
const saving = ref(false)
const loadingEmployees = ref(false)
const filterText = ref('')
const uniformAmount = ref(0)

const employees = ref<any[]>([])
const selectedEmployees = ref<any[]>([])
const results = ref<any[]>([])
const activePanels = ref<string[]>([])

const selectedCount = computed(() => selectedEmployees.value.length)
const canRun = computed(() => selectedEmployees.value.length > 0 && month.value && selectedEmployees.value.some((e: any) => e._bonusAmount > 0))
const totalBonus = computed(() => results.value.reduce((s: number, r: any) => s + (r.bonusAmount || 0), 0))
const totalDeductions = computed(() => results.value.reduce((s: number, r: any) => s + (r.bonusAmount - r.netAmount), 0))
const totalNet = computed(() => results.value.reduce((s: number, r: any) => s + (r.netAmount || 0), 0))

const filteredEmployees = computed(() => {
  if (!filterText.value) return employees.value
  const q = filterText.value.toLowerCase()
  return employees.value.filter((e: any) =>
    (e.name || '').toLowerCase().includes(q) ||
    (e.employeeCode || '').toLowerCase().includes(q)
  )
})

function fmt(v: number) {
  return Number(v || 0).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

function onSelectionChange(rows: any[]) {
  selectedEmployees.value = rows
}

function applyUniformAmount() {
  if (!uniformAmount.value) return
  for (const emp of employees.value) {
    emp._bonusAmount = uniformAmount.value
  }
}

async function loadEmployees() {
  loadingEmployees.value = true
  try {
    let page = 1
    let all: any[] = []
    while (true) {
      const r = await api.post('/objects/employee/search', {
        page, pageSize: 500,
        where: [{ field: '__employment_status__', op: 'eq', value: 'active' }],
        orderBy: [{ field: 'employee_code', dir: 'ASC' }]
      })
      const list = r.data?.data || []
      all = all.concat(list)
      if (list.length < 500) break
      page++
    }
    employees.value = all.map((e: any) => ({
      id: e.id,
      employeeCode: e.payload?.code || e.employee_code || '',
      name: e.payload?.nameKanji || e.payload?.name || '',
      departmentName: e.payload?.primaryDepartmentName || '',
      _bonusAmount: 0,
    }))
  } catch (e: any) {
    error.value = '社員データの取得に失敗しました'
  } finally {
    loadingEmployees.value = false
  }
}

function onMonthChange() {
  results.value = []
}

async function runCalc() {
  const inputs = selectedEmployees.value
    .filter((e: any) => e._bonusAmount > 0)
    .map((e: any) => ({ employeeId: e.id, bonusAmount: e._bonusAmount }))

  if (!inputs.length) {
    error.value = '賞与額が0より大きい社員を選択してください'
    return
  }

  calculating.value = true
  error.value = ''
  try {
    const r = await api.post('/payroll/bonus/run', {
      month: month.value,
      bonusType: bonusType.value,
      employees: inputs,
    })
    results.value = r.data?.entries || []
    activePanels.value = results.value.map((r: any) => r.employeeId)
  } catch (e: any) {
    error.value = e?.response?.data?.error || '計算に失敗しました'
  } finally {
    calculating.value = false
  }
}

async function saveResults() {
  if (!results.value.length) return

  try {
    await ElMessageBox.confirm(
      `${results.value.length}名の賞与計算結果を保存しますか？`,
      '確認',
      { confirmButtonText: '保存', cancelButtonText: 'キャンセル', type: 'info' }
    )
  } catch { return }

  saving.value = true
  error.value = ''
  try {
    const body = {
      month: month.value,
      overwrite: true,
      runType: 'bonus',
      entries: results.value.map((r: any) => ({
        employeeId: r.employeeId,
        employeeCode: r.employeeCode,
        employeeName: r.employeeName,
        departmentCode: r.departmentCode,
        totalAmount: r.netAmount,
        payrollSheet: r.payrollSheet,
        accountingDraft: r.accountingDraft,
      }))
    }
    await api.post('/payroll/bonus/save', body)
    ElMessage.success(`${results.value.length}名の賞与を保存しました`)
  } catch (e: any) {
    error.value = e?.response?.data?.error || '保存に失敗しました'
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  const now = new Date()
  month.value = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
  // 6-7月なら夏季、11-12月なら冬季
  const m = now.getMonth() + 1
  if (m >= 6 && m <= 7) bonusType.value = 'summer'
  else if (m >= 11 || m <= 1) bonusType.value = 'winter'
  else bonusType.value = 'special'
  loadEmployees()
})
</script>

<style scoped>
.bonus-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}
.count-label {
  font-size: 13px;
  color: #909399;
}
.results-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}
.results-title {
  font-size: 15px;
  font-weight: 600;
  color: #303133;
}
.result-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
}
.emp-name {
  font-weight: 500;
  min-width: 140px;
}
.emp-dept {
  font-size: 12px;
  color: #909399;
  min-width: 80px;
}
.result-detail {
  padding: 8px 0;
}
.draft-section {
  margin-top: 12px;
}
.draft-title {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  margin-bottom: 6px;
}
.total-bar {
  margin-top: 16px;
  padding: 12px 16px;
  background: #f5f7fa;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 600;
  color: #303133;
}
</style>

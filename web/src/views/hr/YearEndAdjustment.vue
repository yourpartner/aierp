<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Document /></el-icon>
            <span class="page-header-title">年末調整</span>
          </div>
          <div class="page-actions">
            <el-select v-model="year" style="width:120px" @change="onYearChange">
              <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
            </el-select>
            <el-button type="primary" :loading="calculating" :disabled="!selectedEmployees.length" @click="runCalc">
              <el-icon><CaretRight /></el-icon>計算実行
            </el-button>
            <el-button type="success" :loading="saving" :disabled="!results.length" @click="saveResults">
              <el-icon><FolderChecked /></el-icon>保存
            </el-button>
          </div>
        </div>
      </template>

      <el-alert v-if="error" type="error" :title="error" show-icon closable @close="error=''" style="margin-bottom:12px" />

      <el-tabs v-model="activeTab">
        <!-- Tab 1: 控除情報入力 -->
        <el-tab-pane label="控除情報入力" name="input">
          <el-skeleton v-if="loadingEmployees" :rows="4" animated />
          <template v-else>
            <div class="input-toolbar">
              <el-input v-model="filterText" placeholder="社員を検索…" clearable :prefix-icon="Search" style="width:240px" />
              <span class="count-label">{{ employees.length }}名</span>
            </div>

            <el-table :data="filteredEmployees" stripe size="small" max-height="600" @selection-change="onSelectionChange">
              <el-table-column type="selection" width="40" />
              <el-table-column prop="employeeCode" label="社員番号" width="100" />
              <el-table-column prop="name" label="氏名" width="130" />
              <el-table-column label="配偶者" width="60">
                <template #default="{ row }">
                  <el-checkbox v-model="row._hasSpouse" size="small" />
                </template>
              </el-table-column>
              <el-table-column label="配偶者所得" width="120">
                <template #default="{ row }">
                  <el-input-number v-model="row._spouseIncome" :min="0" :precision="0" :controls="false" size="small" style="width:100px" :disabled="!row._hasSpouse" />
                </template>
              </el-table-column>
              <el-table-column label="生命保険料" width="120">
                <template #default="{ row }">
                  <el-input-number v-model="row._lifeIns" :min="0" :precision="0" :controls="false" size="small" style="width:100px" />
                </template>
              </el-table-column>
              <el-table-column label="介護医療保険" width="120">
                <template #default="{ row }">
                  <el-input-number v-model="row._medicalIns" :min="0" :precision="0" :controls="false" size="small" style="width:100px" />
                </template>
              </el-table-column>
              <el-table-column label="個人年金保険" width="120">
                <template #default="{ row }">
                  <el-input-number v-model="row._pensionIns" :min="0" :precision="0" :controls="false" size="small" style="width:100px" />
                </template>
              </el-table-column>
              <el-table-column label="地震保険料" width="120">
                <template #default="{ row }">
                  <el-input-number v-model="row._earthquakeIns" :min="0" :precision="0" :controls="false" size="small" style="width:100px" />
                </template>
              </el-table-column>
              <el-table-column label="住宅ﾛｰﾝ残高" width="130">
                <template #default="{ row }">
                  <el-input-number v-model="row._housingLoan" :min="0" :precision="0" :controls="false" size="small" style="width:110px" />
                </template>
              </el-table-column>
            </el-table>
          </template>
        </el-tab-pane>

        <!-- Tab 2: 計算結果 -->
        <el-tab-pane label="計算結果" name="results" :disabled="!results.length">
          <el-collapse v-model="activePanels">
            <el-collapse-item v-for="r in results" :key="r.employeeId" :name="r.employeeId">
              <template #title>
                <div class="result-title-row">
                  <span class="emp-name">{{ r.employeeName }} ({{ r.employeeCode }})</span>
                  <el-tag :type="r.adjustmentType === 'refund' ? 'success' : r.adjustmentType === 'collect' ? 'danger' : 'info'" size="small">
                    {{ r.adjustmentType === 'refund' ? '還付' : r.adjustmentType === 'collect' ? '徴収' : '差額なし' }}
                    ¥{{ fmt(Math.abs(r.adjustment)) }}
                  </el-tag>
                </div>
              </template>

              <div class="result-detail">
                <!-- 警告 -->
                <el-alert v-for="(w, i) in (r.warnings || [])" :key="i" type="warning" :title="w" show-icon :closable="false" style="margin-bottom:8px" />

                <!-- 収入集計 -->
                <div class="section-title">収入集計</div>
                <el-descriptions :column="3" size="small" border>
                  <el-descriptions-item label="年間給与収入">¥{{ fmt(r.annualGrossIncome) }}</el-descriptions-item>
                  <el-descriptions-item label="年間賞与収入">¥{{ fmt(r.annualBonusIncome) }}</el-descriptions-item>
                  <el-descriptions-item label="給与収入合計">¥{{ fmt(r.totalIncome) }}</el-descriptions-item>
                  <el-descriptions-item label="給与所得控除">¥{{ fmt(r.employmentIncomeDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="給与所得金額" :span="2">
                    <b>¥{{ fmt(r.grossIncome) }}</b>
                  </el-descriptions-item>
                </el-descriptions>

                <!-- 所得控除 -->
                <div class="section-title">所得控除</div>
                <el-descriptions :column="3" size="small" border>
                  <el-descriptions-item label="基礎控除">¥{{ fmt(r.basicDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="配偶者(特別)控除">¥{{ fmt(r.spouseDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="扶養控除">¥{{ fmt(r.dependentDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="社会保険料控除">¥{{ fmt(r.socialInsuranceDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="生命保険料控除">¥{{ fmt(r.lifeInsuranceDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="地震保険料控除">¥{{ fmt(r.earthquakeDeduction) }}</el-descriptions-item>
                  <el-descriptions-item label="所得控除合計" :span="3">
                    <b>¥{{ fmt(r.totalDeductions) }}</b>
                  </el-descriptions-item>
                </el-descriptions>

                <!-- 税額計算 -->
                <div class="section-title">税額計算</div>
                <el-descriptions :column="3" size="small" border>
                  <el-descriptions-item label="課税所得金額">¥{{ fmt(r.taxableIncome) }}</el-descriptions-item>
                  <el-descriptions-item label="算出年税額">¥{{ fmt(r.calculatedTax) }}</el-descriptions-item>
                  <el-descriptions-item label="住宅ﾛｰﾝ控除">¥{{ fmt(r.housingLoanCredit) }}</el-descriptions-item>
                  <el-descriptions-item label="年調年税額（復興税込）">
                    <b>¥{{ fmt(r.finalAnnualTax) }}</b>
                  </el-descriptions-item>
                  <el-descriptions-item label="年間源泉徴収額">¥{{ fmt(r.totalWithheld) }}</el-descriptions-item>
                  <el-descriptions-item label="過不足額">
                    <span :style="{ color: r.adjustmentType === 'refund' ? '#67c23a' : r.adjustmentType === 'collect' ? '#f56c6c' : '#909399', fontWeight: 700, fontSize: '15px' }">
                      {{ r.adjustmentType === 'refund' ? '△' : '' }}¥{{ fmt(Math.abs(r.adjustment)) }}
                      {{ r.adjustmentType === 'refund' ? '（還付）' : r.adjustmentType === 'collect' ? '（徴収）' : '' }}
                    </span>
                  </el-descriptions-item>
                </el-descriptions>

                <!-- 仕訳ドラフト -->
                <div class="draft-section" v-if="r.accountingDraft?.length">
                  <div class="section-title">仕訳ドラフト</div>
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
          <div class="total-bar" v-if="results.length">
            <div class="total-row">
              <span>還付合計: <b style="color:#67c23a">¥{{ fmt(totalRefund) }}</b></span>
              <span>徴収合計: <b style="color:#f56c6c">¥{{ fmt(totalCollect) }}</b></span>
              <span>差引: <b>¥{{ fmt(totalRefund - totalCollect) }}</b></span>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Document, CaretRight, FolderChecked, Search } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../../api'

const currentYear = new Date().getFullYear()
const years = Array.from({ length: 5 }, (_, i) => currentYear - i)
const year = ref(currentYear - 1)
const activeTab = ref('input')
const error = ref('')
const calculating = ref(false)
const saving = ref(false)
const loadingEmployees = ref(false)
const filterText = ref('')

const employees = ref<any[]>([])
const selectedEmployees = ref<any[]>([])
const results = ref<any[]>([])
const activePanels = ref<string[]>([])

const totalRefund = computed(() => results.value.filter((r: any) => r.adjustmentType === 'refund').reduce((s: number, r: any) => s + Math.abs(r.adjustment), 0))
const totalCollect = computed(() => results.value.filter((r: any) => r.adjustmentType === 'collect').reduce((s: number, r: any) => s + Math.abs(r.adjustment), 0))

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

function onYearChange() {
  results.value = []
}

async function loadEmployees() {
  loadingEmployees.value = true
  try {
    let page = 1
    let all: any[] = []
    while (true) {
      const r = await api.post('/objects/employee/search', {
        page, pageSize: 500,
        where: [],
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
      _hasSpouse: false,
      _spouseIncome: 0,
      _lifeIns: 0,
      _medicalIns: 0,
      _pensionIns: 0,
      _earthquakeIns: 0,
      _housingLoan: 0,
    }))
  } catch {
    error.value = '社員データの取得に失敗しました'
  } finally {
    loadingEmployees.value = false
  }
}

async function runCalc() {
  if (!selectedEmployees.value.length) {
    error.value = '社員を選択してください'
    return
  }

  calculating.value = true
  error.value = ''
  try {
    const body = {
      year: year.value,
      employees: selectedEmployees.value.map((e: any) => ({
        employeeId: e.id,
        lifeInsurancePremium: e._lifeIns || 0,
        medicalInsurancePremium: e._medicalIns || 0,
        pensionInsurancePremium: e._pensionIns || 0,
        earthquakeInsurancePremium: e._earthquakeIns || 0,
        housingLoanBalance: e._housingLoan || 0,
        housingLoanDeductionRate: e._housingLoan > 0 ? 0.007 : 0,
        isFirstYearHousingLoan: false,
        hasSpouse: e._hasSpouse || false,
        spouseIncome: e._spouseIncome || 0,
        otherDeductions: 0,
      }))
    }
    const r = await api.post('/payroll/year-end/run', body)
    results.value = r.data?.entries || []
    activePanels.value = results.value.map((r: any) => r.employeeId)
    activeTab.value = 'results'
  } catch (e: any) {
    error.value = e?.response?.data?.error || '計算に失敗しました'
  } finally {
    calculating.value = false
  }
}

async function saveResults() {
  if (!results.value.length) return

  const refundCount = results.value.filter((r: any) => r.adjustmentType === 'refund').length
  const collectCount = results.value.filter((r: any) => r.adjustmentType === 'collect').length

  try {
    await ElMessageBox.confirm(
      `年末調整結果を保存しますか？\n還付: ${refundCount}名 / 徴収: ${collectCount}名`,
      '確認',
      { confirmButtonText: '保存', cancelButtonText: 'キャンセル', type: 'info' }
    )
  } catch { return }

  saving.value = true
  error.value = ''
  try {
    const body = {
      month: `${year.value}-12`,
      overwrite: true,
      runType: 'year_end_adjustment',
      entries: results.value.map((r: any) => ({
        employeeId: r.employeeId,
        employeeCode: r.employeeCode,
        employeeName: r.employeeName,
        totalAmount: r.adjustment,
        payrollSheet: r.payrollSheet,
        accountingDraft: r.accountingDraft,
      }))
    }
    await api.post('/payroll/year-end/save', body)
    ElMessage.success(`${results.value.length}名の年末調整を保存しました`)
  } catch (e: any) {
    error.value = e?.response?.data?.error || '保存に失敗しました'
  } finally {
    saving.value = false
  }
}

onMounted(loadEmployees)
</script>

<style scoped>
.input-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}
.count-label {
  font-size: 13px;
  color: #909399;
}
.result-title-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1;
}
.emp-name {
  font-weight: 500;
  min-width: 160px;
}
.result-detail {
  padding: 8px 0;
}
.section-title {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  margin: 12px 0 6px;
}
.section-title:first-child {
  margin-top: 0;
}
.draft-section {
  margin-top: 8px;
}
.total-bar {
  margin-top: 16px;
  padding: 12px 16px;
  background: #f5f7fa;
  border-radius: 6px;
}
.total-row {
  display: flex;
  gap: 24px;
  font-size: 14px;
  color: #303133;
}
</style>

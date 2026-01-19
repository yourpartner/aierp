<template>
  <div class="consumption-tax-return">
    <!-- 页面标题 -->
    <div class="page-header">
      <div class="page-header-left">
        <el-icon class="page-header-icon"><Money /></el-icon>
        <span class="page-header-title">消費税申告書</span>
      </div>
      <div class="header-actions">
        <el-button @click="showSettings = true">
          <el-icon><Setting /></el-icon>
          設定
        </el-button>
      </div>
    </div>

    <!-- 计算条件 -->
    <el-card class="calc-card" shadow="never">
      <template #header>
        <span class="card-title">集計条件</span>
      </template>
      <el-form :model="calcForm" label-position="left" label-width="120px" inline>
        <el-form-item label="対象期間">
          <el-date-picker
            v-model="calcForm.dateRange"
            type="daterange"
            range-separator="～"
            start-placeholder="開始日"
            end-placeholder="終了日"
            value-format="YYYY-MM-DD"
            style="width: 280px"
          />
        </el-form-item>
        <el-form-item label="課税方式">
          <el-select v-model="calcForm.taxationMethod" style="width: 180px">
            <el-option label="原則課税" value="general" />
            <el-option label="簡易課税" value="simplified" />
            <el-option label="2割特例" value="special_20pct" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="calcForm.taxationMethod === 'simplified'" label="事業区分">
          <el-select v-model="calcForm.simplifiedCategory" style="width: 180px">
            <el-option label="第一種（卸売業）90%" :value="1" />
            <el-option label="第二種（小売業）80%" :value="2" />
            <el-option label="第三種（製造業等）70%" :value="3" />
            <el-option label="第四種（その他）60%" :value="4" />
            <el-option label="第五種（サービス業）50%" :value="5" />
            <el-option label="第六種（不動産業）40%" :value="6" />
          </el-select>
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="calculate" :loading="calculating">
            <el-icon><DataAnalysis /></el-icon>
            集計実行
          </el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 集计结果 -->
    <div v-if="calculation" class="result-section">
      <!-- 売上の部 -->
      <el-card class="result-card" shadow="never">
        <template #header>
          <span class="card-title">売上の部</span>
        </template>
        <el-table :data="salesTableData" border size="small" show-summary :summary-method="getSalesSummary">
          <el-table-column prop="category" label="区分" width="200" />
          <el-table-column prop="netAmount" label="税抜金額" align="right" width="180">
            <template #default="{ row }">
              <span 
                class="amount-link" 
                @click="drillDown(row.drillKey)"
                v-if="row.netAmount !== 0"
              >
                {{ formatCurrency(row.netAmount) }}
              </span>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column prop="taxAmount" label="消費税額" align="right" width="150">
            <template #default="{ row }">
              {{ row.taxAmount ? formatCurrency(row.taxAmount) : '-' }}
            </template>
          </el-table-column>
        </el-table>
      </el-card>

      <!-- 仕入の部 -->
      <el-card class="result-card" shadow="never">
        <template #header>
          <span class="card-title">仕入の部</span>
        </template>
        <el-table :data="purchasesTableData" border size="small" show-summary :summary-method="getPurchasesSummary">
          <el-table-column prop="category" label="区分" width="200" />
          <el-table-column prop="netAmount" label="税抜金額" align="right" width="180">
            <template #default="{ row }">
              <span 
                class="amount-link" 
                @click="drillDown(row.drillKey)"
                v-if="row.netAmount !== 0"
              >
                {{ formatCurrency(row.netAmount) }}
              </span>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column prop="taxAmount" label="消費税額" align="right" width="150">
            <template #default="{ row }">
              {{ row.taxAmount ? formatCurrency(row.taxAmount) : '-' }}
            </template>
          </el-table-column>
        </el-table>
      </el-card>

      <!-- 税額計算 -->
      <el-card class="result-card tax-calc-card" shadow="never">
        <template #header>
          <span class="card-title">税額計算</span>
        </template>
        <div class="tax-calc-grid">
          <div class="calc-row">
            <span class="calc-label">売上に係る消費税額</span>
            <span class="calc-value">{{ formatCurrency(calculation.calculation.salesTaxTotal) }}</span>
          </div>
          <div class="calc-row">
            <span class="calc-label">仕入税額控除</span>
            <span class="calc-value deduct">△{{ formatCurrency(calculation.calculation.purchaseTaxDeductible) }}</span>
          </div>
          <template v-if="calcForm.taxationMethod === 'simplified'">
            <div class="calc-row hint">
              <span class="calc-label">（みなし仕入率 {{ (calculation.calculation.deemedPurchaseRate * 100).toFixed(0) }}%）</span>
              <span></span>
            </div>
          </template>
          <template v-if="calcForm.taxationMethod === 'special_20pct'">
            <div class="calc-row hint">
              <span class="calc-label">（2割特例: 売上税額 × 80%）</span>
              <span></span>
            </div>
          </template>
          <div class="calc-row">
            <span class="calc-label">差引税額（国税）</span>
            <span class="calc-value">{{ formatCurrency(calculation.calculation.netConsumptionTax) }}</span>
          </div>
          <div class="calc-row">
            <span class="calc-label">地方消費税（22/78）</span>
            <span class="calc-value">{{ formatCurrency(calculation.calculation.localConsumptionTax) }}</span>
          </div>
          <div class="calc-row total">
            <span class="calc-label">納付すべき税額</span>
            <span class="calc-value total-value">{{ formatCurrency(calculation.calculation.totalTaxDue) }}</span>
          </div>
        </div>
        
        <div class="action-buttons">
          <el-button @click="saveReturn" :loading="saving">
            <el-icon><DocumentAdd /></el-icon>
            申告書保存
          </el-button>
          <el-button @click="exportCsv">
            <el-icon><Download /></el-icon>
            CSV出力
          </el-button>
        </div>
      </el-card>
    </div>

    <!-- 保存済み申告書一覧 -->
    <el-card class="returns-card" shadow="never">
      <template #header>
        <div class="card-header-flex">
          <span class="card-title">保存済み申告書</span>
          <el-button size="small" @click="loadReturns">
            <el-icon><Refresh /></el-icon>
          </el-button>
        </div>
      </template>
      <el-table :data="savedReturns" border size="small" v-loading="loadingReturns">
        <el-table-column prop="fiscalYear" label="事業年度" width="120" />
        <el-table-column prop="periodType" label="申告区分" width="120">
          <template #default="{ row }">
            {{ getPeriodTypeLabel(row.periodType) }}
          </template>
        </el-table-column>
        <el-table-column prop="taxationMethod" label="課税方式" width="120">
          <template #default="{ row }">
            {{ getTaxationMethodLabel(row.taxationMethod) }}
          </template>
        </el-table-column>
        <el-table-column label="納付税額" align="right" width="150">
          <template #default="{ row }">
            {{ formatCurrency(row.calculation?.calculation?.totalTaxDue || 0) }}
          </template>
        </el-table-column>
        <el-table-column prop="status" label="ステータス" width="100">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="createdAt" label="作成日時" width="160" />
        <el-table-column label="操作" width="150">
          <template #default="{ row }">
            <el-button size="small" link type="primary" @click="viewReturn(row)">詳細</el-button>
            <el-button size="small" link type="danger" @click="deleteReturn(row)" v-if="row.status !== 'submitted'">削除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 设定弹窗 -->
    <el-dialog v-model="showSettings" title="消費税設定" width="600px" append-to-body class="consumption-tax-dialog">
      <el-form :model="settings" label-position="left" label-width="180px">
        <el-form-item label="適格請求書番号">
          <el-input v-model="settings.invoiceRegistrationNo" placeholder="T1234567890123" style="width: 250px" />
        </el-form-item>
        <el-form-item label="課税方式">
          <el-radio-group v-model="settings.taxationMethod">
            <el-radio label="general">原則課税</el-radio>
            <el-radio label="simplified">簡易課税</el-radio>
            <el-radio label="special_20pct">2割特例</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="settings.taxationMethod === 'simplified'" label="事業区分">
          <el-select v-model="settings.simplifiedCategory" style="width: 250px">
            <el-option label="第一種（卸売業）90%" :value="1" />
            <el-option label="第二種（小売業）80%" :value="2" />
            <el-option label="第三種（製造業等）70%" :value="3" />
            <el-option label="第四種（その他）60%" :value="4" />
            <el-option label="第五種（サービス業）50%" :value="5" />
            <el-option label="第六種（不動産業）40%" :value="6" />
          </el-select>
        </el-form-item>
        <el-form-item label="事業年度開始">
          <el-input v-model="settings.fiscalYearStart" placeholder="04-01" style="width: 120px" />
          <span style="margin-left: 8px; color: #909399">（月-日）</span>
        </el-form-item>
        <el-form-item label="基準期間の課税売上高">
          <el-input-number v-model="settings.basePeriodSales" :controls="false" style="width: 200px" />
          <span style="margin-left: 8px; color: #909399">円</span>
        </el-form-item>
        <el-form-item>
          <el-alert 
            v-if="settings.basePeriodSales && settings.basePeriodSales <= 50000000"
            type="success" 
            :closable="false"
            show-icon
          >
            簡易課税適用可能（基準期間の課税売上高が5,000万円以下）
          </el-alert>
          <el-alert 
            v-else-if="settings.basePeriodSales && settings.basePeriodSales > 50000000"
            type="warning" 
            :closable="false"
            show-icon
          >
            簡易課税適用不可（基準期間の課税売上高が5,000万円超）
          </el-alert>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showSettings = false">キャンセル</el-button>
        <el-button type="primary" @click="saveSettings" :loading="savingSettings">保存</el-button>
      </template>
    </el-dialog>

    <!-- 明细弹窗 -->
    <el-dialog v-model="showDetails" :title="detailsTitle" width="900px" append-to-body class="consumption-tax-dialog">
      <el-table :data="detailsData" border size="small" max-height="500" v-loading="loadingDetails">
        <el-table-column prop="voucherNo" label="伝票番号" width="140">
          <template #default="{ row }">
            <el-button link type="primary" @click="openVoucher(row.voucherId, row.voucherNo)">
              {{ row.voucherNo }}
            </el-button>
          </template>
        </el-table-column>
        <el-table-column prop="postingDate" label="日付" width="110">
          <template #default="{ row }">
            {{ formatDate(row.postingDate) }}
          </template>
        </el-table-column>
        <el-table-column prop="summary" label="摘要" min-width="200" />
        <el-table-column prop="accountName" label="科目" width="150" />
        <el-table-column prop="amount" label="税抜金額" align="right" width="130">
          <template #default="{ row }">
            {{ formatCurrency(row.amount) }}
          </template>
        </el-table-column>
        <el-table-column prop="taxAmount" label="消費税額" align="right" width="110">
          <template #default="{ row }">
            {{ formatCurrency(row.taxAmount) }}
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList
          v-if="voucherDialogVisible && (currentVoucherId || currentVoucherNo)"
          class="voucher-detail-embed"
          :allow-edit="false"
          :initial-voucher-id="currentVoucherId || undefined"
          :initial-voucher-no="currentVoucherNo || undefined"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Setting, DataAnalysis, DocumentAdd, Download, Refresh, Money } from '@element-plus/icons-vue'
import api from '../api'
import VouchersList from './VouchersList.vue'

// 计算表单
const calcForm = ref({
  dateRange: [] as string[],
  taxationMethod: 'general',
  simplifiedCategory: 5
})

// 设定
const settings = ref({
  invoiceRegistrationNo: '',
  taxationMethod: 'general',
  simplifiedCategory: 5,
  fiscalYearStart: '04-01',
  basePeriodSales: 0
})

// 状态
const calculating = ref(false)
const saving = ref(false)
const savingSettings = ref(false)
const loadingReturns = ref(false)
const loadingDetails = ref(false)
const showSettings = ref(false)
const showDetails = ref(false)

// 凭证详情弹窗
const voucherDialogVisible = ref(false)
const currentVoucherId = ref<string>('')
const currentVoucherNo = ref<string>('')

// 数据
const calculation = ref<any>(null)
const savedReturns = ref<any[]>([])
const detailsData = ref<any[]>([])
const detailsTitle = ref('')
const currentReturnId = ref<string>('')

// 销售表格数据
const salesTableData = computed(() => {
  if (!calculation.value?.sales) return []
  const s = calculation.value.sales
  return [
    { category: '課税売上（10%）', netAmount: s.taxable10?.netAmount || 0, taxAmount: s.taxable10?.taxAmount || 0, drillKey: 'sales_taxable_10' },
    { category: '課税売上（8%軽減）', netAmount: s.taxable8?.netAmount || 0, taxAmount: s.taxable8?.taxAmount || 0, drillKey: 'sales_taxable_8' },
    { category: '免税売上', netAmount: s.exempt?.netAmount || 0, taxAmount: 0, drillKey: 'sales_exempt' },
    { category: '輸出免税', netAmount: s.export?.netAmount || 0, taxAmount: 0, drillKey: 'sales_export' },
  ]
})

// 仕入表格数据
const purchasesTableData = computed(() => {
  if (!calculation.value?.purchases) return []
  const p = calculation.value.purchases
  const rows = [
    { category: '課税仕入（10%）', netAmount: p.taxable10?.netAmount || 0, taxAmount: p.taxable10?.taxAmount || 0, drillKey: 'purchase_taxable_10' },
    { category: '課税仕入（8%軽減）', netAmount: p.taxable8?.netAmount || 0, taxAmount: p.taxable8?.taxAmount || 0, drillKey: 'purchase_taxable_8' },
  ]
  // 控除対象外（経過措置）：インボイス未登録事業者からの仕入で控除できない税額
  // 税額がある場合のみ表示
  const nonDeductibleTax = p.nonDeductible?.taxAmount || 0
  if (nonDeductibleTax > 0) {
    rows.push({ category: '控除対象外（経過措置）', netAmount: 0, taxAmount: nonDeductibleTax, drillKey: '' })
  }
  return rows
})

// 格式化金额
function formatCurrency(value: number): string {
  if (value === 0) return '0'
  return new Intl.NumberFormat('ja-JP', { style: 'currency', currency: 'JPY' }).format(value)
}

function formatDate(date: string): string {
  if (!date) return ''
  return new Date(date).toLocaleDateString('ja-JP')
}

// 销售合计
function getSalesSummary({ columns, data }: any) {
  const sums: string[] = []
  columns.forEach((column: any, index: number) => {
    if (index === 0) {
      sums[index] = '課税売上計'
      return
    }
    const s = calculation.value?.sales
    if (column.property === 'netAmount') {
      const taxable = (s?.taxable10?.netAmount || 0) + (s?.taxable8?.netAmount || 0)
      sums[index] = formatCurrency(taxable)
    } else if (column.property === 'taxAmount') {
      const tax = (s?.taxable10?.taxAmount || 0) + (s?.taxable8?.taxAmount || 0)
      sums[index] = formatCurrency(tax)
    } else {
      sums[index] = ''
    }
  })
  return sums
}

// 仕入合计
function getPurchasesSummary({ columns, data }: any) {
  const sums: string[] = []
  columns.forEach((column: any, index: number) => {
    if (index === 0) {
      sums[index] = '控除税額計'
      return
    }
    const p = calculation.value?.purchases
    if (column.property === 'netAmount') {
      sums[index] = formatCurrency(p?.total?.netAmount || 0)
    } else if (column.property === 'taxAmount') {
      sums[index] = formatCurrency(p?.total?.taxAmount || 0)
    } else {
      sums[index] = ''
    }
  })
  return sums
}

// 集计计算
async function calculate() {
  if (!calcForm.value.dateRange || calcForm.value.dateRange.length !== 2) {
    ElMessage.warning('対象期間を選択してください')
    return
  }
  
  calculating.value = true
  try {
    const resp = await api.post('/consumption-tax/calculate', {
      from: calcForm.value.dateRange[0],
      to: calcForm.value.dateRange[1],
      taxationMethod: calcForm.value.taxationMethod,
      simplifiedCategory: calcForm.value.simplifiedCategory
    })
    calculation.value = resp.data.calculation
    ElMessage.success('集計が完了しました')
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '集計に失敗しました')
  } finally {
    calculating.value = false
  }
}

// 保存申告书
async function saveReturn() {
  if (!calculation.value) {
    ElMessage.warning('先に集計を実行してください')
    return
  }
  
  saving.value = true
  try {
    const toDate = new Date(calcForm.value.dateRange[1])
    const fiscalYear = `${toDate.getFullYear()}-${String(toDate.getMonth() + 1).padStart(2, '0')}`
    
    const resp = await api.post('/consumption-tax/calculate', {
      from: calcForm.value.dateRange[0],
      to: calcForm.value.dateRange[1],
      taxationMethod: calcForm.value.taxationMethod,
      simplifiedCategory: calcForm.value.simplifiedCategory,
      save: true,
      fiscalYear: fiscalYear,
      periodType: 'annual'
    })
    
    currentReturnId.value = resp.data.savedId
    ElMessage.success('申告書を保存しました')
    await loadReturns()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

// 加载保存的申告书
async function loadReturns() {
  loadingReturns.value = true
  try {
    const resp = await api.get('/consumption-tax/returns')
    savedReturns.value = resp.data
  } catch (e: any) {
    console.error('Failed to load returns:', e)
  } finally {
    loadingReturns.value = false
  }
}

// 查看申告书详情
function viewReturn(row: any) {
  calculation.value = row.calculation
  currentReturnId.value = row.id
  calcForm.value.taxationMethod = row.taxationMethod
  if (row.calculation?.period) {
    calcForm.value.dateRange = [
      row.calculation.period.from?.split('T')[0],
      row.calculation.period.to?.split('T')[0]
    ]
  }
}

// 删除申告书
async function deleteReturn(row: any) {
  try {
    await ElMessageBox.confirm('この申告書を削除しますか？', '確認', { type: 'warning' })
    await api.delete(`/consumption-tax/returns/${row.id}`)
    ElMessage.success('削除しました')
    await loadReturns()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '削除に失敗しました')
    }
  }
}

// 钻取明细
async function drillDown(category: string) {
  if (!category) return
  
  // 需要有日期范围
  if (!calcForm.value.dateRange || calcForm.value.dateRange.length !== 2) {
    ElMessage.warning('期間を選択してください')
    return
  }
  
  detailsTitle.value = getCategoryLabel(category)
  loadingDetails.value = true
  showDetails.value = true
  
  try {
    // 直接使用日期范围查询，不需要保存的申告书 ID
    const resp = await api.get('/consumption-tax/details', {
      params: { 
        from: calcForm.value.dateRange[0],
        to: calcForm.value.dateRange[1],
        category 
      }
    })
    detailsData.value = resp.data.items || []
  } catch (e: any) {
    ElMessage.error('明細の取得に失敗しました')
    detailsData.value = []
  } finally {
    loadingDetails.value = false
  }
}

function getCategoryLabel(category: string): string {
  const labels: Record<string, string> = {
    'sales_taxable_10': '課税売上（10%）明細',
    'sales_taxable_8': '課税売上（8%軽減）明細',
    'sales_exempt': '免税売上明細',
    'sales_export': '輸出免税明細',
    'purchase_taxable_10': '課税仕入（10%）明細',
    'purchase_taxable_8': '課税仕入（8%軽減）明細'
  }
  return labels[category] || '明細'
}

// 加载设定
async function loadSettings() {
  try {
    const resp = await api.get('/consumption-tax/settings')
    if (resp.data) {
      Object.assign(settings.value, resp.data)
      // 同步到计算表单
      calcForm.value.taxationMethod = settings.value.taxationMethod || 'general'
      calcForm.value.simplifiedCategory = settings.value.simplifiedCategory || 5
    }
  } catch (e) {
    console.error('Failed to load settings:', e)
  }
}

// 保存设定
async function saveSettings() {
  savingSettings.value = true
  try {
    await api.put('/consumption-tax/settings', settings.value)
    ElMessage.success('設定を保存しました')
    showSettings.value = false
    // 同步到计算表单
    calcForm.value.taxationMethod = settings.value.taxationMethod
    calcForm.value.simplifiedCategory = settings.value.simplifiedCategory
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '保存に失敗しました')
  } finally {
    savingSettings.value = false
  }
}

// 导出CSV
function exportCsv() {
  if (!calculation.value) {
    ElMessage.warning('先に集計を実行してください')
    return
  }
  
  // 简单的CSV导出
  const rows = [
    ['消費税申告書データ'],
    ['対象期間', `${calcForm.value.dateRange[0]} ～ ${calcForm.value.dateRange[1]}`],
    ['課税方式', getTaxationMethodLabel(calcForm.value.taxationMethod)],
    [''],
    ['【売上の部】'],
    ['区分', '税抜金額', '消費税額'],
    ['課税売上（10%）', calculation.value.sales?.taxable10?.netAmount || 0, calculation.value.sales?.taxable10?.taxAmount || 0],
    ['課税売上（8%）', calculation.value.sales?.taxable8?.netAmount || 0, calculation.value.sales?.taxable8?.taxAmount || 0],
    ['免税売上', calculation.value.sales?.exempt?.netAmount || 0, ''],
    ['輸出免税', calculation.value.sales?.export?.netAmount || 0, ''],
    [''],
    ['【仕入の部】'],
    ['区分', '税抜金額', '消費税額'],
    ['課税仕入（10%）', calculation.value.purchases?.taxable10?.netAmount || 0, calculation.value.purchases?.taxable10?.taxAmount || 0],
    ['課税仕入（8%）', calculation.value.purchases?.taxable8?.netAmount || 0, calculation.value.purchases?.taxable8?.taxAmount || 0],
    [''],
    ['【税額計算】'],
    ['売上に係る消費税額', calculation.value.calculation?.salesTaxTotal || 0],
    ['仕入税額控除', calculation.value.calculation?.purchaseTaxDeductible || 0],
    ['差引税額（国税）', calculation.value.calculation?.netConsumptionTax || 0],
    ['地方消費税', calculation.value.calculation?.localConsumptionTax || 0],
    ['納付すべき税額', calculation.value.calculation?.totalTaxDue || 0],
  ]
  
  const csv = rows.map(row => row.join(',')).join('\n')
  const blob = new Blob(['\ufeff' + csv], { type: 'text/csv;charset=utf-8;' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `consumption_tax_${calcForm.value.dateRange[0]}_${calcForm.value.dateRange[1]}.csv`
  a.click()
  URL.revokeObjectURL(url)
}

// 打开凭证
function openVoucher(voucherId?: string, voucherNo?: string) {
  if (!voucherId && !voucherNo) return
  currentVoucherId.value = voucherId || ''
  currentVoucherNo.value = voucherNo || ''
  voucherDialogVisible.value = true
}

// 标签转换
function getPeriodTypeLabel(type: string): string {
  const labels: Record<string, string> = {
    'annual': '確定申告',
    'interim_q1': '中間（1回目）',
    'interim_q2': '中間（2回目）',
    'interim_q3': '中間（3回目）'
  }
  return labels[type] || type
}

function getTaxationMethodLabel(method: string): string {
  const labels: Record<string, string> = {
    'general': '原則課税',
    'simplified': '簡易課税',
    'special_20pct': '2割特例'
  }
  return labels[method] || method
}

function getStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    'draft': '下書き',
    'calculated': '計算済',
    'submitted': '申告済',
    'accepted': '受理済'
  }
  return labels[status] || status
}

function getStatusType(status: string): string {
  const types: Record<string, string> = {
    'draft': 'info',
    'calculated': 'warning',
    'submitted': 'success',
    'accepted': 'success'
  }
  return types[status] || 'info'
}

// 初始化
onMounted(async () => {
  // 设置默认期间为当年度
  const now = new Date()
  const year = now.getMonth() < 3 ? now.getFullYear() - 1 : now.getFullYear()
  calcForm.value.dateRange = [
    `${year}-04-01`,
    `${year + 1}-03-31`
  ]
  
  await loadSettings()
  await loadReturns()
})
</script>

<style scoped>
.consumption-tax-return {
  padding: 20px;
  max-width: 1200px;
  margin: 0 auto;
  background-color: #fff;
  border-radius: 8px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.1);
  min-height: 400px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #67c23a;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-header h2 {
  margin: 0;
  font-size: 1.5rem;
  font-weight: 600;
  color: #303133;
}

.card-title {
  font-weight: 600;
  font-size: 1rem;
}

.card-header-flex {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.calc-card {
  margin-bottom: 20px;
}

.result-section {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 20px;
  margin-bottom: 20px;
}

.result-card {
  min-height: 200px;
}

.tax-calc-card {
  grid-column: 1 / -1;
}

.tax-calc-grid {
  max-width: 500px;
}

.calc-row {
  display: flex;
  justify-content: space-between;
  padding: 8px 0;
  border-bottom: 1px solid #EBEEF5;
  font-size: 12px;
}

.calc-row.hint {
  border-bottom: none;
  padding: 4px 0;
}

.calc-row.hint .calc-label {
  color: #909399;
  font-size: 12px;
}

.calc-row.total {
  border-bottom: none;
  border-top: 2px solid #303133;
  margin-top: 8px;
  padding-top: 12px;
}

.calc-label {
  color: #606266;
  font-size: 12px;
}

.calc-value {
  font-weight: 500;
  font-family: 'Roboto Mono', monospace;
  font-size: 12px;
}

.calc-value.deduct {
  color: #F56C6C;
}

.calc-value.total-value {
  font-size: 14px;
  font-weight: 700;
  color: #409EFF;
}

.action-buttons {
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid #EBEEF5;
}

.returns-card {
  margin-top: 20px;
}

.amount-link {
  color: #409EFF;
  cursor: pointer;
  text-decoration: underline;
}

.amount-link:hover {
  color: #66b1ff;
}

:deep(.el-table .el-table__footer-wrapper td) {
  font-weight: 600;
  background-color: #f5f7fa;
}
</style>

<!-- 弹窗使用 append-to-body，需要非 scoped 样式 -->
<style>
.el-dialog.consumption-tax-dialog {
  background-color: #fff !important;
  border-radius: 8px !important;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15) !important;
}

.el-dialog.consumption-tax-dialog .el-dialog__header {
  background-color: #fff !important;
  border-bottom: 1px solid #e4e7ed;
  padding: 16px 20px;
  margin-right: 0;
}

.el-dialog.consumption-tax-dialog .el-dialog__title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.el-dialog.consumption-tax-dialog .el-dialog__body {
  background-color: #fff !important;
  padding: 20px;
}

.el-dialog.consumption-tax-dialog .el-dialog__footer {
  background-color: #fff !important;
  border-top: 1px solid #e4e7ed;
  padding: 12px 20px;
}
</style>


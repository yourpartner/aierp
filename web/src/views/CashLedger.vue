<template>
  <div class="cash-ledger">
    <!-- 页面标题 -->
    <div class="page-header">
      <div class="page-header-left">
        <el-icon class="page-header-icon"><Wallet /></el-icon>
        <span class="page-header-title">現金出納帳</span>
      </div>
      <div class="header-actions">
        <el-button @click="exportCsv" :disabled="!transactions.length">
          <el-icon><Download /></el-icon>
          CSV出力
        </el-button>
      </div>
    </div>

    <!-- 查询条件 -->
    <el-card class="filter-card" shadow="never">
      <el-form :model="filter" inline>
        <el-form-item label="現金口座">
          <el-select v-model="filter.cashCode" style="width: 260px" @change="loadTransactions" placeholder="選択してください">
            <el-option-group label="現金">
              <el-option 
                v-for="acc in cashAccounts.filter(a => a.isCash)" 
                :key="acc.cashCode" 
                :label="`${acc.name} (${acc.cashCode})`" 
                :value="acc.cashCode" 
              />
            </el-option-group>
            <el-option-group label="銀行">
              <el-option 
                v-for="acc in cashAccounts.filter(a => a.isBank)" 
                :key="acc.cashCode" 
                :label="`${acc.name} (${acc.cashCode})`" 
                :value="acc.cashCode" 
              />
            </el-option-group>
          </el-select>
        </el-form-item>
        <el-form-item label="期間">
          <el-date-picker
            v-model="filter.dateRange"
            type="daterange"
            range-separator="～"
            start-placeholder="開始日"
            end-placeholder="終了日"
            value-format="YYYY-MM-DD"
            style="width: 280px"
            @change="loadTransactions"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="showReceiptForm = true" :disabled="!filter.cashCode">
            <el-icon><Plus /></el-icon>
            入金
          </el-button>
          <el-button type="warning" @click="showPaymentForm = true" :disabled="!filter.cashCode">
            <el-icon><Minus /></el-icon>
            出金
          </el-button>
          <el-button @click="showCountForm = true" :disabled="!filter.cashCode">
            <el-icon><Check /></el-icon>
            実査
          </el-button>
          <el-button type="success" @click="openReplenishForm" :disabled="!filter.cashCode">
            <el-icon><Wallet /></el-icon>
            補充
          </el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 残高サマリー -->
    <el-card v-if="filter.cashCode && currentAccount" class="summary-card" shadow="never">
      <div class="summary-grid">
        <div class="summary-item">
          <span class="summary-label">現金口座</span>
          <span class="summary-value">{{ currentAccount.name }}</span>
        </div>
        <div class="summary-item">
          <span class="summary-label">前期繰越</span>
          <span class="summary-value">{{ formatCurrency(openingBalance) }}</span>
        </div>
        <div class="summary-item">
          <span class="summary-label">当期入金</span>
          <span class="summary-value receipt">+{{ formatCurrency(totalReceipt) }}</span>
        </div>
        <div class="summary-item">
          <span class="summary-label">当期出金</span>
          <span class="summary-value payment">-{{ formatCurrency(totalPayment) }}</span>
        </div>
        <div class="summary-item">
          <span class="summary-label">現在残高</span>
          <span class="summary-value balance">{{ formatCurrency(currentBalance) }}</span>
        </div>
      </div>
    </el-card>

    <!-- 取引一覧 -->
    <el-card class="ledger-card" shadow="never">
      <el-table :data="ledgerData" border size="small" v-loading="loading" show-summary :summary-method="getSummary">
        <el-table-column prop="transactionDate" label="日付" width="110" />
        <el-table-column prop="transactionNo" label="No." width="130" />
        <el-table-column prop="description" label="摘要" min-width="200">
          <template #default="{ row }">
            <span>{{ row.counterparty ? `${row.counterparty} ` : '' }}{{ row.description }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="category" label="カテゴリ" width="100">
          <template #default="{ row }">
            {{ getCategoryName(row.category) }}
          </template>
        </el-table-column>
        <el-table-column label="入金" align="right" width="120">
          <template #default="{ row }">
            <span v-if="row.transactionType === 'receipt' || row.transactionType === 'replenish'" class="amount receipt">
              {{ formatCurrency(row.amount) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column label="出金" align="right" width="120">
          <template #default="{ row }">
            <span v-if="row.transactionType === 'payment'" class="amount payment">
              {{ formatCurrency(row.amount) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column prop="balanceAfter" label="残高" align="right" width="130">
          <template #default="{ row }">
            <span class="amount-balance">{{ formatCurrency(row.balanceAfter) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="伝票" width="120">
          <template #default="{ row }">
            <el-button v-if="row.voucherNo" link type="primary" size="small" @click="openVoucher(row.voucherId, row.voucherNo)">
              {{ row.voucherNo }}
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 入金フォーム -->
    <el-dialog v-model="showReceiptForm" title="入金登録" width="500px" destroy-on-close append-to-body class="cash-ledger-dialog">
      <el-form :model="receiptForm" label-width="100px">
        <el-form-item label="日付" required>
          <el-date-picker v-model="receiptForm.transactionDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item label="金額" required>
          <el-input-number v-model="receiptForm.amount" :min="0" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item label="相手先">
          <el-input v-model="receiptForm.counterparty" />
        </el-form-item>
        <el-form-item label="摘要" required>
          <el-input v-model="receiptForm.description" />
        </el-form-item>
        <el-form-item label="仕訳作成">
          <el-switch v-model="receiptForm.createVoucher" />
        </el-form-item>
        <template v-if="receiptForm.createVoucher">
          <el-form-item label="借方科目">
            <el-select v-model="receiptForm.debitAccountCode" style="width: 100%" filterable>
              <el-option v-for="acc in accounts" :key="acc.code" :label="`${acc.name} (${acc.code})`" :value="acc.code" />
            </el-select>
          </el-form-item>
          <el-form-item label="貸方科目">
            <el-input :model-value="currentAccount?.accountCode" disabled />
            <span class="hint">（現金科目）</span>
          </el-form-item>
        </template>
      </el-form>
      <template #footer>
        <el-button @click="showReceiptForm = false">キャンセル</el-button>
        <el-button type="primary" @click="submitReceipt" :loading="submitting">登録</el-button>
      </template>
    </el-dialog>

    <!-- 出金フォーム -->
    <el-dialog v-model="showPaymentForm" title="出金登録" width="500px" destroy-on-close append-to-body class="cash-ledger-dialog">
      <el-form :model="paymentForm" label-width="100px">
        <el-form-item label="日付" required>
          <el-date-picker v-model="paymentForm.transactionDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item label="金額" required>
          <el-input-number v-model="paymentForm.amount" :min="0" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item label="カテゴリ">
          <el-select v-model="paymentForm.category" style="width: 100%" clearable>
            <el-option v-for="cat in expenseCategories" :key="cat.code" :label="cat.name" :value="cat.code" />
          </el-select>
        </el-form-item>
        <el-form-item label="相手先">
          <el-input v-model="paymentForm.counterparty" />
        </el-form-item>
        <el-form-item label="摘要" required>
          <el-input v-model="paymentForm.description" />
        </el-form-item>
        <el-form-item label="仕訳作成">
          <el-switch v-model="paymentForm.createVoucher" />
        </el-form-item>
        <template v-if="paymentForm.createVoucher">
          <el-form-item label="借方科目">
            <el-select v-model="paymentForm.debitAccountCode" style="width: 100%" filterable>
              <el-option v-for="acc in accounts" :key="acc.code" :label="`${acc.name} (${acc.code})`" :value="acc.code" />
            </el-select>
          </el-form-item>
          <el-form-item label="貸方科目">
            <el-input :model-value="currentAccount?.accountCode" disabled />
            <span class="hint">（現金科目）</span>
          </el-form-item>
        </template>
      </el-form>
      <template #footer>
        <el-button @click="showPaymentForm = false">キャンセル</el-button>
        <el-button type="warning" @click="submitPayment" :loading="submitting">登録</el-button>
      </template>
    </el-dialog>

    <!-- 実査フォーム -->
    <el-dialog v-model="showCountForm" title="現金実査" width="500px" destroy-on-close append-to-body class="cash-ledger-dialog">
      <el-form :model="countForm" label-width="120px">
        <el-form-item label="実査日" required>
          <el-date-picker v-model="countForm.countDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item label="帳簿残高">
          <el-input :model-value="formatCurrency(currentBalance)" disabled />
        </el-form-item>
        <el-form-item label="実際残高" required>
          <el-input-number v-model="countForm.actualBalance" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item label="差異">
          <el-input :model-value="formatCurrency(countForm.actualBalance - currentBalance)" disabled :class="{ 'is-error': countForm.actualBalance !== currentBalance }" />
        </el-form-item>
        <el-form-item v-if="countForm.actualBalance !== currentBalance" label="差異理由">
          <el-input v-model="countForm.adjustmentReason" placeholder="釣銭誤差など" />
        </el-form-item>
        <el-form-item v-if="countForm.actualBalance !== currentBalance" label="調整仕訳">
          <el-switch v-model="countForm.createAdjustmentVoucher" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCountForm = false">キャンセル</el-button>
        <el-button type="primary" @click="submitCount" :loading="submitting">実査完了</el-button>
      </template>
    </el-dialog>

    <!-- 補充フォーム -->
    <el-dialog v-model="showReplenishForm" title="現金補充" width="560px" destroy-on-close append-to-body class="cash-ledger-dialog">
      <el-form :model="replenishForm" label-width="120px">
        <el-form-item label="補充先">
          <el-input :model-value="`${currentAccount?.name || ''} (${filter.cashCode})`" disabled />
        </el-form-item>
        <el-form-item label="現在残高">
          <el-input :model-value="formatCurrency(currentBalance)" disabled />
        </el-form-item>
        <el-form-item v-if="imprestInfo?.imprestSystem" label="定額">
          <el-input :model-value="formatCurrency(imprestInfo.imprestAmount)" disabled />
          <span class="hint">（定額資金前渡制）</span>
        </el-form-item>
        <el-form-item v-if="imprestInfo?.imprestSystem" label="推奨補充額">
          <div class="recommended-amount">
            <span class="amount">{{ formatCurrency(imprestInfo.recommendedAmount) }}</span>
            <el-button size="small" @click="replenishForm.amount = imprestInfo.recommendedAmount" :disabled="!imprestInfo.recommendedAmount">
              適用
            </el-button>
          </div>
        </el-form-item>
        <el-divider />
        <el-form-item label="補充日" required>
          <el-date-picker v-model="replenishForm.replenishDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item label="補充元" required>
          <el-select v-model="replenishForm.sourceAccountCode" style="width: 100%" placeholder="選択してください">
            <el-option-group label="銀行口座">
              <el-option 
                v-for="src in replenishSources.filter(s => s.isBank)" 
                :key="src.code" 
                :label="`${src.name} (${src.code})`" 
                :value="src.code" 
              />
            </el-option-group>
            <el-option-group label="現金口座">
              <el-option 
                v-for="src in replenishSources.filter(s => s.isCash)" 
                :key="src.code" 
                :label="`${src.name} (${src.code})`" 
                :value="src.code" 
              />
            </el-option-group>
          </el-select>
        </el-form-item>
        <el-form-item label="補充金額" required>
          <el-input-number v-model="replenishForm.amount" :min="0" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item label="摘要">
          <el-input v-model="replenishForm.memo" placeholder="銀行から補充、本社より等" />
        </el-form-item>
        <el-form-item label="仕訳作成">
          <el-switch v-model="replenishForm.createVoucher" />
          <span class="hint" style="margin-left: 12px;">
            借方：{{ filter.cashCode }} / 貸方：{{ replenishForm.sourceAccountCode || '(補充元)' }}
          </span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showReplenishForm = false">キャンセル</el-button>
        <el-button type="success" @click="submitReplenish" :loading="submitting">補充実行</el-button>
      </template>
    </el-dialog>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList v-if="voucherDialogVisible" ref="voucherDetailRef" class="voucher-detail-embed" :allow-edit="false" />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch, nextTick } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, Plus, Minus, Check, Wallet } from '@element-plus/icons-vue'
import api from '../api'
import VouchersList from './VouchersList.vue'

// 状态
const loading = ref(false)
const submitting = ref(false)
const showReceiptForm = ref(false)
const showPaymentForm = ref(false)
const showCountForm = ref(false)
const showReplenishForm = ref(false)
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)

// 数据
const cashAccounts = ref<any[]>([])
const transactions = ref<any[]>([])
const openingBalance = ref(0)
const accounts = ref<any[]>([])
const expenseCategories = ref<any[]>([])
const replenishSources = ref<any[]>([])
const imprestInfo = ref<any>(null)

// 筛选条件
const filter = ref({
  cashCode: '',
  dateRange: [] as string[]
})

// 表单
const receiptForm = ref({
  transactionDate: new Date().toISOString().split('T')[0],
  amount: 0,
  counterparty: '',
  description: '',
  createVoucher: true,
  debitAccountCode: ''
})

const paymentForm = ref({
  transactionDate: new Date().toISOString().split('T')[0],
  amount: 0,
  category: '',
  counterparty: '',
  description: '',
  createVoucher: true,
  debitAccountCode: ''
})

const countForm = ref({
  countDate: new Date().toISOString().split('T')[0],
  actualBalance: 0,
  adjustmentReason: '',
  createAdjustmentVoucher: true
})

const replenishForm = ref({
  replenishDate: new Date().toISOString().split('T')[0],
  sourceAccountCode: '',
  amount: 0,
  memo: '',
  createVoucher: true
})

// 计算属性
const currentAccount = computed(() => {
  return cashAccounts.value.find(a => a.cashCode === filter.value.cashCode)
})

const currentBalance = computed(() => {
  // 期末残高 = 期首残高 + 入金 - 出金
  return openingBalance.value + totalReceipt.value - totalPayment.value
})

const totalReceipt = computed(() => {
  return transactions.value
    .filter(t => t.transactionType === 'receipt' || t.transactionType === 'replenish')
    .reduce((sum, t) => sum + (t.amount || 0), 0)
})

const totalPayment = computed(() => {
  return transactions.value
    .filter(t => t.transactionType === 'payment')
    .reduce((sum, t) => sum + (t.amount || 0), 0)
})

const ledgerData = computed(() => {
  // 添加期首行
  const data = []
  if (transactions.value.length > 0 || openingBalance.value !== 0) {
    data.push({
      transactionDate: filter.value.dateRange[0],
      transactionNo: '-',
      description: '前期繰越',
      transactionType: 'opening',
      amount: 0,
      balanceAfter: openingBalance.value
    })
  }
  return [...data, ...transactions.value]
})

// 格式化
function formatCurrency(value: number): string {
  return new Intl.NumberFormat('ja-JP', { style: 'currency', currency: 'JPY' }).format(value || 0)
}

function getCategoryName(code: string): string {
  const cat = expenseCategories.value.find(c => c.code === code)
  return cat?.name || code || ''
}

// 合计
function getSummary({ columns, data }: any) {
  const sums: string[] = []
  columns.forEach((column: any, index: number) => {
    if (index === 0) {
      sums[index] = '合計'
      return
    }
    if (column.label === '入金') {
      sums[index] = formatCurrency(totalReceipt.value)
    } else if (column.label === '出金') {
      sums[index] = formatCurrency(totalPayment.value)
    } else {
      sums[index] = ''
    }
  })
  return sums
}

// 加载数据
async function loadCashAccounts() {
  try {
    // 从勘定科目中获取现金或银行类科目
    // 分别查询 isCash 和 isBank 为 true 的科目
    const [cashRes, bankRes] = await Promise.all([
      api.post('/objects/account/search', { 
        page: 1,
        pageSize: 200,
        where: [{ json: 'isCash', op: 'eq', value: true }],
        orderBy: [{ field: 'account_code', dir: 'ASC' }]
      }),
      api.post('/objects/account/search', { 
        page: 1,
        pageSize: 200,
        where: [{ json: 'isBank', op: 'eq', value: true }],
        orderBy: [{ field: 'account_code', dir: 'ASC' }]
      })
    ])
    
    const cashRows = Array.isArray(cashRes.data?.data) ? cashRes.data.data : []
    const bankRows = Array.isArray(bankRes.data?.data) ? bankRes.data.data : []
    
    // 合并并去重
    const seen = new Set<string>()
    const items: any[] = []
    
    for (const a of cashRows) {
      const code = a.account_code || a.payload?.code
      if (!code || seen.has(code)) continue
      seen.add(code)
      items.push({
        cashCode: code,
        accountCode: code,
        name: a.payload?.name || code,
        isCash: true,
        isBank: false,
        currentBalance: 0
      })
    }
    
    for (const a of bankRows) {
      const code = a.account_code || a.payload?.code
      if (!code || seen.has(code)) continue
      seen.add(code)
      items.push({
        cashCode: code,
        accountCode: code,
        name: a.payload?.name || code,
        isCash: false,
        isBank: true,
        currentBalance: 0
      })
    }
    
    cashAccounts.value = items
    if (cashAccounts.value.length > 0 && !filter.value.cashCode) {
      filter.value.cashCode = cashAccounts.value[0].cashCode
    }
  } catch (e: any) {
    console.error('Failed to load cash accounts:', e)
  }
}

async function loadTransactions() {
  if (!filter.value.cashCode || !filter.value.dateRange?.length) return
  
  loading.value = true
  try {
    const resp = await api.get(`/cash-accounts/${filter.value.cashCode}/transactions`, {
      params: {
        from: filter.value.dateRange[0],
        to: filter.value.dateRange[1]
      }
    })
    transactions.value = resp.data.transactions || []
    openingBalance.value = resp.data.openingBalance || 0
  } catch (e: any) {
    ElMessage.error('取引の取得に失敗しました')
  } finally {
    loading.value = false
  }
}

async function loadAccounts() {
  try {
    const resp = await api.post('/objects/account/search', { 
      page: 1,
      pageSize: 500,
      where: [],
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    accounts.value = rows.map((a: any) => ({
      code: a.account_code || a.payload?.code,
      name: a.payload?.name || a.account_code
    }))
  } catch (e) {
    console.error('Failed to load accounts:', e)
  }
}

async function loadExpenseCategories() {
  try {
    const resp = await api.get('/cash/expense-categories')
    expenseCategories.value = resp.data || []
  } catch (e) {
    console.error('Failed to load expense categories:', e)
  }
}

// 提交入金
async function submitReceipt() {
  if (!receiptForm.value.amount || !receiptForm.value.description) {
    ElMessage.warning('金額と摘要を入力してください')
    return
  }
  
  submitting.value = true
  try {
    await api.post(`/cash-accounts/${filter.value.cashCode}/transactions`, {
      payload: {
        transactionDate: receiptForm.value.transactionDate,
        transactionType: 'receipt',
        amount: receiptForm.value.amount,
        counterparty: receiptForm.value.counterparty,
        description: receiptForm.value.description
      },
      createVoucher: receiptForm.value.createVoucher,
      debitAccountCode: currentAccount.value?.accountCode,
      creditAccountCode: receiptForm.value.debitAccountCode
    })
    
    ElMessage.success('入金を登録しました')
    showReceiptForm.value = false
    resetReceiptForm()
    await loadCashAccounts()
    await loadTransactions()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '登録に失敗しました')
  } finally {
    submitting.value = false
  }
}

// 提交出金
async function submitPayment() {
  if (!paymentForm.value.amount || !paymentForm.value.description) {
    ElMessage.warning('金額と摘要を入力してください')
    return
  }
  
  submitting.value = true
  try {
    const debitAccount = paymentForm.value.debitAccountCode || 
      expenseCategories.value.find(c => c.code === paymentForm.value.category)?.accountCode
    
    await api.post(`/cash-accounts/${filter.value.cashCode}/transactions`, {
      payload: {
        transactionDate: paymentForm.value.transactionDate,
        transactionType: 'payment',
        amount: paymentForm.value.amount,
        category: paymentForm.value.category,
        counterparty: paymentForm.value.counterparty,
        description: paymentForm.value.description
      },
      createVoucher: paymentForm.value.createVoucher,
      debitAccountCode: debitAccount,
      creditAccountCode: currentAccount.value?.accountCode
    })
    
    ElMessage.success('出金を登録しました')
    showPaymentForm.value = false
    resetPaymentForm()
    await loadCashAccounts()
    await loadTransactions()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '登録に失敗しました')
  } finally {
    submitting.value = false
  }
}

// 提交实查
async function submitCount() {
  submitting.value = true
  try {
    await api.post(`/cash-accounts/${filter.value.cashCode}/counts`, {
      payload: {
        countDate: countForm.value.countDate,
        actualBalance: countForm.value.actualBalance,
        adjustmentReason: countForm.value.adjustmentReason
      },
      createAdjustmentVoucher: countForm.value.createAdjustmentVoucher && countForm.value.actualBalance !== currentBalance.value
    })
    
    ElMessage.success('実査を登録しました')
    showCountForm.value = false
    resetCountForm()
    await loadCashAccounts()
    await loadTransactions()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '登録に失敗しました')
  } finally {
    submitting.value = false
  }
}

// 打开补充对话框
async function openReplenishForm() {
  if (!filter.value.cashCode) return
  
  // 重置表单
  replenishForm.value = {
    replenishDate: new Date().toISOString().split('T')[0],
    sourceAccountCode: '',
    amount: 0,
    memo: '',
    createVoucher: true
  }
  
  showReplenishForm.value = true
  
  // 并行加载补充源和定额信息
  try {
    const [sourcesRes, imprestRes] = await Promise.all([
      api.get(`/cash-accounts/${filter.value.cashCode}/replenish-sources`),
      api.get(`/cash-accounts/${filter.value.cashCode}/imprest-info`)
    ])
    
    replenishSources.value = Array.isArray(sourcesRes.data) ? sourcesRes.data : []
    imprestInfo.value = imprestRes.data || null
    
    console.log('Replenish sources:', replenishSources.value)
    
    // 如果有推荐补充额，默认填入
    if (imprestInfo.value?.recommendedAmount > 0) {
      replenishForm.value.amount = imprestInfo.value.recommendedAmount
    }
    
    // 默认选择第一个银行口座
    const defaultSource = replenishSources.value.find((s: any) => s.isBank) || replenishSources.value[0]
    if (defaultSource) {
      replenishForm.value.sourceAccountCode = defaultSource.code
    }
    
    // 如果没有补充源，显示提示
    if (replenishSources.value.length === 0) {
      ElMessage.warning('補充可能な口座（銀行または他の現金口座）がありません。勘定科目の設定を確認してください。')
    }
  } catch (e: any) {
    console.error('Failed to load replenish info:', e)
    ElMessage.error(e.response?.data?.error || '補充情報の取得に失敗しました')
  }
}

// 提交补充
async function submitReplenish() {
  if (!replenishForm.value.sourceAccountCode) {
    ElMessage.warning('補充元を選択してください')
    return
  }
  if (!replenishForm.value.amount || replenishForm.value.amount <= 0) {
    ElMessage.warning('補充金額を入力してください')
    return
  }
  
  submitting.value = true
  try {
    const resp = await api.post(`/cash-accounts/${filter.value.cashCode}/replenish`, {
      sourceAccountCode: replenishForm.value.sourceAccountCode,
      amount: replenishForm.value.amount,
      replenishDate: replenishForm.value.replenishDate,
      memo: replenishForm.value.memo,
      createVoucher: replenishForm.value.createVoucher
    })
    
    const result = resp.data
    let message = `補充が完了しました。新残高: ${formatCurrency(result.newBalance)}`
    if (result.voucherNo) {
      message += ` 伝票: ${result.voucherNo}`
    }
    ElMessage.success(message)
    
    showReplenishForm.value = false
    await loadTransactions()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '補充に失敗しました')
  } finally {
    submitting.value = false
  }
}

// 重置表单
function resetReceiptForm() {
  receiptForm.value = {
    transactionDate: new Date().toISOString().split('T')[0],
    amount: 0,
    counterparty: '',
    description: '',
    createVoucher: true,
    debitAccountCode: ''
  }
}

function resetPaymentForm() {
  paymentForm.value = {
    transactionDate: new Date().toISOString().split('T')[0],
    amount: 0,
    category: '',
    counterparty: '',
    description: '',
    createVoucher: true,
    debitAccountCode: ''
  }
}

function resetCountForm() {
  countForm.value = {
    countDate: new Date().toISOString().split('T')[0],
    actualBalance: currentBalance.value,
    adjustmentReason: '',
    createAdjustmentVoucher: true
  }
}

// 打开凭证详情弹窗
function openVoucher(voucherId: string, voucherNo?: string) {
  if (!voucherId && !voucherNo) return
  
  voucherDialogVisible.value = true
  nextTick(() => {
    const isUuid = voucherId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(voucherId)
    voucherDetailRef.value?.applyIntent?.({
      ...(isUuid ? { voucherId } : { voucherNo }),
      detailOnly: true
    })
  })
}

// 导出CSV
function exportCsv() {
  const rows = [
    ['現金出納帳'],
    [`現金口座: ${currentAccount.value?.name || ''}`],
    [`期間: ${filter.value.dateRange[0]} ～ ${filter.value.dateRange[1]}`],
    [],
    ['日付', 'No.', '摘要', 'カテゴリ', '入金', '出金', '残高'],
    ...ledgerData.value.map(t => [
      t.transactionDate,
      t.transactionNo,
      `${t.counterparty || ''} ${t.description || ''}`.trim(),
      getCategoryName(t.category),
      t.transactionType === 'receipt' || t.transactionType === 'replenish' ? t.amount : '',
      t.transactionType === 'payment' ? t.amount : '',
      t.balanceAfter
    ])
  ]
  
  const csv = rows.map(row => row.join(',')).join('\n')
  const blob = new Blob(['\ufeff' + csv], { type: 'text/csv;charset=utf-8;' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `cash_ledger_${filter.value.cashCode}_${filter.value.dateRange[0]}_${filter.value.dateRange[1]}.csv`
  a.click()
  URL.revokeObjectURL(url)
}

// 监听实查表单打开
watch(showCountForm, (val) => {
  if (val) {
    countForm.value.actualBalance = currentBalance.value
  }
})

// 监听出金表单的分类变化
watch(() => paymentForm.value.category, (cat) => {
  if (cat) {
    const category = expenseCategories.value.find(c => c.code === cat)
    if (category) {
      paymentForm.value.debitAccountCode = category.accountCode
    }
  }
})

// 初始化
onMounted(async () => {
  // 设置默认日期为当月
  const now = new Date()
  const firstDay = new Date(now.getFullYear(), now.getMonth(), 1)
  const lastDay = new Date(now.getFullYear(), now.getMonth() + 1, 0)
  filter.value.dateRange = [
    firstDay.toISOString().split('T')[0],
    lastDay.toISOString().split('T')[0]
  ]
  
  await Promise.all([
    loadCashAccounts(),
    loadAccounts(),
    loadExpenseCategories()
  ])
  
  if (filter.value.cashCode) {
    await loadTransactions()
  }
})
</script>

<style scoped>
.cash-ledger {
  padding: 20px;
  max-width: 1400px;
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
  color: #409eff;
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
}

.filter-card {
  margin-bottom: 16px;
}

.summary-card {
  margin-bottom: 16px;
}

.summary-grid {
  display: flex;
  gap: 32px;
  flex-wrap: wrap;
}

.summary-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.summary-label {
  font-size: 13px;
  color: #606266;
  font-weight: 500;
}

.summary-value {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.summary-value.receipt {
  color: #67C23A;
}

.summary-value.payment {
  color: #F56C6C;
}

.summary-value.balance {
  color: #409EFF;
  font-size: 18px;
}

.ledger-card {
  margin-bottom: 20px;
}

.amount {
  font-weight: 500;
}

.amount.receipt {
  color: #67C23A;
}

.amount.payment {
  color: #F56C6C;
}

.amount-balance {
  font-weight: 500;
  color: #303133;
}

/* 表格金额列统一字体 */
:deep(.el-table td .cell) {
  font-size: 13px;
}

:deep(.el-table th .cell) {
  font-size: 13px;
  font-weight: 600;
}

.hint {
  margin-left: 8px;
  color: #909399;
  font-size: 12px;
}

:deep(.el-table .el-table__footer-wrapper td) {
  font-weight: 600;
  background-color: #f5f7fa;
}

:deep(.is-error .el-input__inner) {
  color: #F56C6C;
}

.recommended-amount {
  display: flex;
  align-items: center;
  gap: 12px;
}

.recommended-amount .amount {
  font-size: 1.1rem;
  font-weight: 600;
  color: #67C23A;
  font-family: 'Roboto Mono', monospace;
}

/* 凭证弹窗样式 */
.voucher-dialog-card-wrap {
  min-width: 800px;
  max-width: 1200px;
  max-height: 80vh;
  overflow: auto;
  background-color: #fff;
  border-radius: 8px;
}

.voucher-detail-embed {
  padding: 0;
}

:deep(.voucher-detail-embed .el-card) {
  box-shadow: none;
  border: none;
}
</style>

<!-- 弹窗使用 append-to-body，需要非 scoped 样式 -->
<style>
.el-dialog.cash-ledger-dialog {
  background-color: #fff !important;
  border-radius: 8px !important;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15) !important;
}

.el-dialog.cash-ledger-dialog .el-dialog__header {
  background-color: #fff !important;
  border-bottom: 1px solid #e4e7ed;
  padding: 16px 20px;
  margin-right: 0;
}

.el-dialog.cash-ledger-dialog .el-dialog__title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.el-dialog.cash-ledger-dialog .el-dialog__body {
  background-color: #fff !important;
  padding: 20px;
}

.el-dialog.cash-ledger-dialog .el-dialog__footer {
  background-color: #fff !important;
  border-top: 1px solid #e4e7ed;
  padding: 12px 20px;
}

/* 确保遮罩层正确显示 */
.el-overlay-dialog:has(.cash-ledger-dialog) {
  background-color: rgba(0, 0, 0, 0.5);
}

/* 凭证详情弹窗样式 */
.el-dialog.voucher-detail-dialog {
  background-color: #fff !important;
  border-radius: 8px !important;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15) !important;
}

.el-dialog.voucher-detail-dialog .el-dialog__header {
  display: none;
}

.el-dialog.voucher-detail-dialog .el-dialog__body {
  padding: 0;
  background-color: #fff;
}
</style>


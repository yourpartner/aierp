<template>
  <div class="portal-payments">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Wallet /></el-icon>
        <h1>入金確認</h1>
      </div>
    </div>

    <!-- サマリカード -->
    <el-row :gutter="20" class="summary-row">
      <el-col :span="8">
        <div class="summary-card paid">
          <div class="summary-icon">
            <el-icon><CircleCheck /></el-icon>
          </div>
          <div class="summary-content">
            <div class="summary-value">¥{{ formatNumber(summary.totalPaid) }}</div>
            <div class="summary-label">入金済み</div>
          </div>
        </div>
      </el-col>
      <el-col :span="8">
        <div class="summary-card pending">
          <div class="summary-icon">
            <el-icon><Clock /></el-icon>
          </div>
          <div class="summary-content">
            <div class="summary-value">¥{{ formatNumber(summary.totalPending) }}</div>
            <div class="summary-label">入金待ち</div>
          </div>
        </div>
      </el-col>
      <el-col :span="8">
        <div class="summary-card total">
          <div class="summary-icon">
            <el-icon><TrendCharts /></el-icon>
          </div>
          <div class="summary-content">
            <div class="summary-value">¥{{ formatNumber(summary.totalPaid + summary.totalPending) }}</div>
            <div class="summary-label">累計請求額</div>
          </div>
        </div>
      </el-col>
    </el-row>

    <el-card v-loading="loading">
      <template #header>
        <span>入金履歴</span>
      </template>
      <el-table :data="payments">
        <el-table-column label="請求書番号" prop="invoiceNo" width="160" />
        <el-table-column label="請求期間" min-width="180">
          <template #default="{ row }">
            {{ formatDate(row.periodStart) }} ~ {{ formatDate(row.periodEnd) }}
          </template>
        </el-table-column>
        <el-table-column label="請求額" prop="totalAmount" width="130" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.totalAmount) }}
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="row.status === 'paid' ? 'success' : 'warning'" size="small">
              {{ row.status === 'paid' ? '入金済' : '入金待ち' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="入金日" prop="paidAt" width="120">
          <template #default="{ row }">
            {{ row.paidAt ? formatDate(row.paidAt) : '-' }}
          </template>
        </el-table-column>
        <el-table-column label="入金額" prop="paidAmount" width="130" align="right">
          <template #default="{ row }">
            <span v-if="row.paidAmount" class="paid-amount">¥{{ formatNumber(row.paidAmount) }}</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && payments.length === 0" description="入金履歴がありません" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ArrowLeft, Wallet, CircleCheck, Clock, TrendCharts } from '@element-plus/icons-vue'
import api from '../../api'

interface Payment {
  id: string
  invoiceNo: string
  periodStart: string
  periodEnd: string
  totalAmount: number
  status: string
  paidAt?: string
  paidAmount?: number
}

const loading = ref(false)
const payments = ref<Payment[]>([])
const summary = reactive({
  totalPaid: 0,
  totalPending: 0
})

const loadPayments = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/payments')
    payments.value = res.data.data || []
    summary.totalPaid = res.data.summary?.totalPaid || 0
    summary.totalPending = res.data.summary?.totalPending || 0
  } catch (e) {
    console.error('Load payments error:', e)
  } finally {
    loading.value = false
  }
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

onMounted(() => {
  loadPayments()
})
</script>

<style scoped>
.portal-payments {
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

.summary-row {
  margin-bottom: 20px;
}

.summary-card {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 24px;
  background: white;
  border-radius: 12px;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
}

.summary-card.paid {
  border-left: 4px solid #67c23a;
}

.summary-card.pending {
  border-left: 4px solid #e6a23c;
}

.summary-card.total {
  border-left: 4px solid #409eff;
}

.summary-icon {
  width: 48px;
  height: 48px;
  border-radius: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 24px;
}

.summary-card.paid .summary-icon {
  background: #f0f9eb;
  color: #67c23a;
}

.summary-card.pending .summary-icon {
  background: #fdf6ec;
  color: #e6a23c;
}

.summary-card.total .summary-icon {
  background: #ecf5ff;
  color: #409eff;
}

.summary-content {
  flex: 1;
}

.summary-value {
  font-size: 24px;
  font-weight: 700;
  color: #303133;
}

.summary-label {
  font-size: 13px;
  color: #909399;
  margin-top: 4px;
}

.paid-amount {
  font-weight: 600;
  color: #67c23a;
}
</style>


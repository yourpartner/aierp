<template>
  <div class="portal-payslips">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Money /></el-icon>
        <h1>給与明細</h1>
      </div>
      <div class="header-right">
        <el-select v-model="selectedYear" style="width: 100px" @change="loadPayslips">
          <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
        </el-select>
      </div>
    </div>

    <el-row :gutter="20">
      <!-- 年間サマリ -->
      <el-col :span="8">
        <el-card class="summary-card">
          <template #header>
            <span class="card-title">{{ selectedYear }}年 サマリ</span>
          </template>
          <div class="summary-item">
            <span class="label">総支給額</span>
            <span class="value">¥{{ formatNumber(summary.totalGross) }}</span>
          </div>
          <div class="summary-item">
            <span class="label">控除合計</span>
            <span class="value deduction">-¥{{ formatNumber(summary.totalDeductions) }}</span>
          </div>
          <el-divider />
          <div class="summary-item total">
            <span class="label">手取り合計</span>
            <span class="value">¥{{ formatNumber(summary.totalNet) }}</span>
          </div>
        </el-card>
      </el-col>

      <!-- 月別一覧 -->
      <el-col :span="16">
        <el-card v-loading="loading">
          <el-table :data="payslips">
            <el-table-column label="支給月" prop="payPeriod" width="120">
              <template #default="{ row }">
                {{ formatPayPeriod(row.payPeriod) }}
              </template>
            </el-table-column>
            <el-table-column label="総支給額" prop="grossSalary" width="130" align="right">
              <template #default="{ row }">
                ¥{{ formatNumber(row.grossSalary) }}
              </template>
            </el-table-column>
            <el-table-column label="控除" prop="totalDeductions" width="110" align="right">
              <template #default="{ row }">
                <span class="deduction">-¥{{ formatNumber(row.totalDeductions) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="手取り" prop="netSalary" width="130" align="right">
              <template #default="{ row }">
                <span class="net-salary">¥{{ formatNumber(row.netSalary) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="支払日" prop="paidAt" width="110">
              <template #default="{ row }">
                {{ formatDate(row.paidAt) }}
              </template>
            </el-table-column>
            <el-table-column label="操作" width="100" align="center">
              <template #default="{ row }">
                <el-button size="small" type="primary" link @click="viewDetail(row)">
                  詳細
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>

    <!-- 詳細ダイアログ -->
    <el-dialog v-model="detailVisible" :title="`${formatPayPeriod(selectedPayslip?.payPeriod)} 給与明細`" width="600px">
      <div class="payslip-detail" v-if="selectedPayslip">
        <div class="detail-section">
          <h4>支給</h4>
          <div class="detail-row" v-for="(item, key) in parseDetails(selectedPayslip.details)?.earnings" :key="key">
            <span class="detail-label">{{ item.label }}</span>
            <span class="detail-value">¥{{ formatNumber(item.amount) }}</span>
          </div>
          <div class="detail-row total">
            <span class="detail-label">支給額計</span>
            <span class="detail-value">¥{{ formatNumber(selectedPayslip.grossSalary) }}</span>
          </div>
        </div>

        <div class="detail-section">
          <h4>控除</h4>
          <div class="detail-row" v-for="(item, key) in parseDetails(selectedPayslip.details)?.deductions" :key="key">
            <span class="detail-label">{{ item.label }}</span>
            <span class="detail-value deduction">-¥{{ formatNumber(item.amount) }}</span>
          </div>
          <div class="detail-row total">
            <span class="detail-label">控除額計</span>
            <span class="detail-value deduction">-¥{{ formatNumber(selectedPayslip.totalDeductions) }}</span>
          </div>
        </div>

        <div class="detail-section net">
          <div class="detail-row total">
            <span class="detail-label">差引支給額</span>
            <span class="detail-value net-salary">¥{{ formatNumber(selectedPayslip.netSalary) }}</span>
          </div>
        </div>
      </div>
      <template #footer>
        <el-button @click="detailVisible = false">閉じる</el-button>
        <el-button type="primary" @click="downloadPdf">
          <el-icon><Download /></el-icon>
          PDFダウンロード
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { ArrowLeft, Money, Download } from '@element-plus/icons-vue'
import api from '../../api'

interface Payslip {
  id: string
  payPeriod: string
  grossSalary: number
  totalDeductions: number
  netSalary: number
  status: string
  paidAt?: string
  details?: string
}

const loading = ref(false)
const detailVisible = ref(false)

const currentYear = new Date().getFullYear()
const selectedYear = ref(currentYear)
const years = Array.from({ length: 5 }, (_, i) => currentYear - i)

const payslips = ref<Payslip[]>([])
const selectedPayslip = ref<Payslip | null>(null)

const summary = computed(() => {
  return {
    totalGross: payslips.value.reduce((sum, p) => sum + p.grossSalary, 0),
    totalDeductions: payslips.value.reduce((sum, p) => sum + p.totalDeductions, 0),
    totalNet: payslips.value.reduce((sum, p) => sum + p.netSalary, 0)
  }
})

const loadPayslips = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/payslips', { params: { year: selectedYear.value } })
    payslips.value = res.data.data || []
  } catch (e) {
    console.error('Load payslips error:', e)
  } finally {
    loading.value = false
  }
}

const viewDetail = async (row: Payslip) => {
  try {
    const res = await api.get(`/portal/payslips/${row.id}`)
    selectedPayslip.value = res.data
    detailVisible.value = true
  } catch (e) {
    ElMessage.error('詳細の取得に失敗しました')
  }
}

const parseDetails = (detailsJson?: string) => {
  if (!detailsJson) {
    return {
      earnings: [{ label: '基本給', amount: 0 }],
      deductions: [{ label: '健康保険', amount: 0 }]
    }
  }
  try {
    return JSON.parse(detailsJson)
  } catch {
    return { earnings: [], deductions: [] }
  }
}

const downloadPdf = () => {
  ElMessage.info('PDF生成機能は実装中です')
}

const formatPayPeriod = (period?: string) => {
  if (!period) return ''
  const [y, m] = period.split('-')
  return `${y}年${parseInt(m)}月`
}

const formatDate = (dateStr?: string) => {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num?: number) => {
  if (num === undefined || num === null) return '0'
  return new Intl.NumberFormat('ja-JP').format(num)
}

onMounted(() => {
  loadPayslips()
})
</script>

<style scoped>
.portal-payslips {
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

.summary-card .card-title {
  font-weight: 600;
}

.summary-item {
  display: flex;
  justify-content: space-between;
  padding: 8px 0;
}

.summary-item .label {
  color: #606266;
}

.summary-item .value {
  font-weight: 600;
}

.summary-item.total .value {
  font-size: 20px;
  color: var(--el-color-primary);
}

.deduction {
  color: var(--el-color-danger);
}

.net-salary {
  font-weight: 600;
  color: var(--el-color-primary);
}

.payslip-detail {
  padding: 0 16px;
}

.detail-section {
  margin-bottom: 20px;
}

.detail-section h4 {
  margin: 0 0 12px 0;
  padding-bottom: 8px;
  border-bottom: 1px solid #ebeef5;
  font-size: 14px;
  color: #909399;
}

.detail-row {
  display: flex;
  justify-content: space-between;
  padding: 8px 0;
}

.detail-row.total {
  border-top: 1px solid #ebeef5;
  margin-top: 8px;
  padding-top: 12px;
  font-weight: 600;
}

.detail-section.net {
  background: #f5f7fa;
  padding: 16px;
  border-radius: 8px;
  margin-top: 20px;
}

.detail-section.net .detail-row {
  margin: 0;
  padding: 0;
}

.detail-section.net .detail-value {
  font-size: 24px;
}
</style>


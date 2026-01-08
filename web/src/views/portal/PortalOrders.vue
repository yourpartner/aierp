<template>
  <div class="portal-orders">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Tickets /></el-icon>
        <h1>注文書一覧</h1>
      </div>
      <el-tag type="warning" v-if="pendingCount > 0">
        {{ pendingCount }}件の未確認
      </el-tag>
    </div>

    <el-card v-loading="loading">
      <el-table :data="orders">
        <el-table-column label="注文書番号" prop="orderNo" width="150" />
        <el-table-column label="発注日" prop="orderDate" width="110">
          <template #default="{ row }">
            {{ formatDate(row.orderDate) }}
          </template>
        </el-table-column>
        <el-table-column label="稼働期間" min-width="180">
          <template #default="{ row }">
            {{ formatDate(row.periodStart) }} ~ {{ formatDate(row.periodEnd) }}
          </template>
        </el-table-column>
        <el-table-column label="顧客" prop="clientName" width="140" />
        <el-table-column label="単価" prop="unitPrice" width="110" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.unitPrice) }}
          </template>
        </el-table-column>
        <el-table-column label="精算" prop="settlementType" width="80">
          <template #default="{ row }">
            {{ row.settlementType === 'monthly' ? '月額' : '時給' }}
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="140" align="center">
          <template #default="{ row }">
            <el-button 
              v-if="row.status === 'sent'" 
              size="small" 
              type="success"
              @click="acceptOrder(row)"
            >
              確認・承諾
            </el-button>
            <el-button 
              v-else 
              size="small" 
              link
              @click="viewOrder(row)"
            >
              詳細
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && orders.length === 0" description="注文書がありません" />
    </el-card>

    <!-- 詳細/承諾ダイアログ -->
    <el-dialog v-model="detailVisible" :title="`注文書 ${selectedOrder?.orderNo}`" width="600px">
      <div class="order-detail" v-if="selectedOrder">
        <div class="detail-grid">
          <div class="detail-item">
            <span class="label">発注日</span>
            <span class="value">{{ formatDate(selectedOrder.orderDate) }}</span>
          </div>
          <div class="detail-item">
            <span class="label">契約番号</span>
            <span class="value">{{ selectedOrder.contractNo || '-' }}</span>
          </div>
          <div class="detail-item full">
            <span class="label">稼働期間</span>
            <span class="value">{{ formatDate(selectedOrder.periodStart) }} ~ {{ formatDate(selectedOrder.periodEnd) }}</span>
          </div>
          <div class="detail-item">
            <span class="label">顧客</span>
            <span class="value">{{ selectedOrder.clientName }}</span>
          </div>
          <div class="detail-item">
            <span class="label">ステータス</span>
            <el-tag :type="getStatusType(selectedOrder.status)" size="small">
              {{ getStatusLabel(selectedOrder.status) }}
            </el-tag>
          </div>
        </div>

        <el-divider />

        <div class="price-section">
          <h4>単価・精算条件</h4>
          <div class="price-grid">
            <div class="price-item main">
              <span class="label">単価</span>
              <span class="value">¥{{ formatNumber(selectedOrder.unitPrice) }}<small>/{{ selectedOrder.settlementType === 'monthly' ? '月' : '時間' }}</small></span>
            </div>
            <div class="price-item" v-if="selectedOrder.minHours">
              <span class="label">精算下限</span>
              <span class="value">{{ selectedOrder.minHours }}h</span>
            </div>
            <div class="price-item" v-if="selectedOrder.maxHours">
              <span class="label">精算上限</span>
              <span class="value">{{ selectedOrder.maxHours }}h</span>
            </div>
            <div class="price-item" v-if="selectedOrder.overtimeRate">
              <span class="label">残業単価率</span>
              <span class="value">{{ selectedOrder.overtimeRate * 100 }}%</span>
            </div>
          </div>
        </div>

        <div class="accept-section" v-if="selectedOrder.status === 'sent'">
          <el-divider />
          <el-checkbox v-model="acceptConfirm">
            上記内容を確認し、承諾します
          </el-checkbox>
        </div>
      </div>
      <template #footer>
        <el-button @click="detailVisible = false">閉じる</el-button>
        <el-button 
          v-if="selectedOrder?.status === 'sent'" 
          type="success" 
          @click="confirmAccept"
          :disabled="!acceptConfirm"
          :loading="accepting"
        >
          承諾する
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowLeft, Tickets } from '@element-plus/icons-vue'
import api from '../../api'

interface Order {
  id: string
  orderNo: string
  orderDate: string
  periodStart: string
  periodEnd: string
  unitPrice: number
  settlementType: string
  minHours?: number
  maxHours?: number
  overtimeRate?: number
  status: string
  contractNo?: string
  clientName?: string
  acceptedAt?: string
}

const loading = ref(false)
const accepting = ref(false)
const detailVisible = ref(false)
const acceptConfirm = ref(false)

const orders = ref<Order[]>([])
const selectedOrder = ref<Order | null>(null)

const pendingCount = computed(() => orders.value.filter(o => o.status === 'sent').length)

const loadOrders = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/orders')
    orders.value = res.data.data || []
  } catch (e) {
    console.error('Load orders error:', e)
  } finally {
    loading.value = false
  }
}

const viewOrder = (row: Order) => {
  selectedOrder.value = row
  acceptConfirm.value = false
  detailVisible.value = true
}

const acceptOrder = (row: Order) => {
  selectedOrder.value = row
  acceptConfirm.value = false
  detailVisible.value = true
}

const confirmAccept = async () => {
  if (!selectedOrder.value) return
  
  accepting.value = true
  try {
    await api.post(`/portal/orders/${selectedOrder.value.id}/accept`)
    ElMessage.success('注文書を承諾しました')
    detailVisible.value = false
    await loadOrders()
  } catch (e: any) {
    ElMessage.error('承諾に失敗しました')
  } finally {
    accepting.value = false
  }
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    draft: '作成中',
    sent: '未確認',
    accepted: '承諾済',
    rejected: '却下'
  }
  return map[status] || status
}

const getStatusType = (status: string) => {
  const map: Record<string, string> = {
    draft: 'info',
    sent: 'warning',
    accepted: 'success',
    rejected: 'danger'
  }
  return map[status] || 'info'
}

onMounted(() => {
  loadOrders()
})
</script>

<style scoped>
.portal-orders {
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

.order-detail {
  padding: 0 8px;
}

.detail-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.detail-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.detail-item.full {
  grid-column: span 2;
}

.detail-item .label {
  font-size: 12px;
  color: #909399;
}

.detail-item .value {
  font-size: 14px;
  font-weight: 500;
}

.price-section h4 {
  margin: 0 0 16px 0;
  font-size: 14px;
  color: #606266;
}

.price-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 12px;
}

.price-item {
  padding: 12px 16px;
  background: #f5f7fa;
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.price-item.main {
  grid-column: span 2;
  background: linear-gradient(135deg, #667eea10 0%, #764ba210 100%);
  border: 1px solid #667eea30;
}

.price-item .label {
  font-size: 12px;
  color: #909399;
}

.price-item .value {
  font-size: 18px;
  font-weight: 600;
}

.price-item.main .value {
  font-size: 24px;
  color: var(--el-color-primary);
}

.price-item .value small {
  font-size: 14px;
  font-weight: normal;
  color: #909399;
}

.accept-section {
  padding-top: 16px;
}
</style>


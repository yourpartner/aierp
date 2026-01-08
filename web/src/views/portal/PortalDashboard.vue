<template>
  <div class="portal-dashboard">
    <!-- ヘッダー -->
    <div class="portal-header">
      <div class="welcome-section">
        <div class="avatar">
          <el-avatar :size="64" :style="{ background: 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)' }">
            {{ (dashboard.displayName || 'U').substring(0, 1) }}
          </el-avatar>
        </div>
        <div class="welcome-text">
          <h1>おはようございます、{{ dashboard.displayName || 'ユーザー' }}さん</h1>
          <p class="sub-text">
            <el-tag size="small" :type="getResourceTagType(dashboard.resourceType)">
              {{ getResourceLabel(dashboard.resourceType) }}
            </el-tag>
            <span v-if="dashboard.resourceCode" class="code">{{ dashboard.resourceCode }}</span>
          </p>
        </div>
      </div>
      <div class="current-date">
        {{ formatDate(new Date()) }}
      </div>
    </div>

    <el-row :gutter="20">
      <!-- 今月の勤怠 -->
      <el-col :span="8">
        <el-card class="summary-card timesheet-card">
          <template #header>
            <div class="card-header">
              <el-icon><Calendar /></el-icon>
              <span>今月の勤怠</span>
            </div>
          </template>
          <div class="timesheet-summary">
            <div class="hours-display">
              <span class="hours-value">{{ dashboard.currentMonthHours || 0 }}</span>
              <span class="hours-unit">時間</span>
            </div>
            <el-tag :type="getTimesheetStatusType(dashboard.currentMonthStatus)" size="large">
              {{ getTimesheetStatusLabel(dashboard.currentMonthStatus) }}
            </el-tag>
          </div>
          <div class="card-actions">
            <router-link to="/portal/timesheet">
              <el-button type="primary" plain>勤怠入力</el-button>
            </router-link>
          </div>
        </el-card>
      </el-col>

      <!-- 有効契約 -->
      <el-col :span="16">
        <el-card class="summary-card">
          <template #header>
            <div class="card-header">
              <el-icon><Document /></el-icon>
              <span>有効な契約</span>
            </div>
          </template>
          <el-table :data="dashboard.activeContracts || []" size="small" v-loading="loading">
            <el-table-column label="契約番号" prop="contractNo" width="140" />
            <el-table-column label="タイプ" prop="contractType" width="80">
              <template #default="{ row }">
                <el-tag size="small" :type="getContractTagType(row.contractType)">
                  {{ getContractLabel(row.contractType) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="顧客" prop="clientName" />
            <el-table-column label="期間" width="200">
              <template #default="{ row }">
                {{ formatDateShort(row.startDate) }} ~ {{ formatDateShort(row.endDate) || '未定' }}
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>

    <!-- 待処理項目 -->
    <el-card class="pending-card" v-if="(dashboard.pendingItems || []).length > 0">
      <template #header>
        <div class="card-header warning">
          <el-icon><Bell /></el-icon>
          <span>待処理項目</span>
          <el-badge :value="dashboard.pendingItems?.length" type="danger" />
        </div>
      </template>
      <div class="pending-list">
        <div v-for="(item, idx) in dashboard.pendingItems" :key="idx" class="pending-item">
          <el-icon :class="['pending-icon', item.type]"><Warning /></el-icon>
          <span>{{ item.message }}</span>
          <el-button size="small" type="primary" link>対応する</el-button>
        </div>
      </div>
    </el-card>

    <!-- クイックアクセス -->
    <div class="quick-access">
      <h3>クイックアクセス</h3>
      <div class="quick-links">
        <router-link to="/portal/timesheet" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #667eea 0%, #764ba2 100%)">
            <el-icon><Calendar /></el-icon>
          </div>
          <span>勤怠入力</span>
        </router-link>
        <router-link to="/portal/payslip" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%)">
            <el-icon><Money /></el-icon>
          </div>
          <span>給与明細</span>
        </router-link>
        <router-link to="/portal/certificates" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #4facfe 0%, #00f2fe 100%)">
            <el-icon><Document /></el-icon>
          </div>
          <span>証明書申請</span>
        </router-link>
        <router-link v-if="isFreelancer" to="/portal/orders" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #43e97b 0%, #38f9d7 100%)">
            <el-icon><Tickets /></el-icon>
          </div>
          <span>注文書確認</span>
        </router-link>
        <router-link v-if="isFreelancer" to="/portal/invoices" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #fa709a 0%, #fee140 100%)">
            <el-icon><List /></el-icon>
          </div>
          <span>請求書提出</span>
        </router-link>
        <router-link v-if="isFreelancer" to="/portal/payments" class="quick-link">
          <div class="link-icon" style="background: linear-gradient(135deg, #a8edea 0%, #fed6e3 100%)">
            <el-icon><Wallet /></el-icon>
          </div>
          <span>入金確認</span>
        </router-link>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Calendar, Document, Bell, Warning, Money, Tickets, List, Wallet } from '@element-plus/icons-vue'
import api from '../../api'

const loading = ref(false)
const dashboard = ref<any>({})

const isFreelancer = computed(() => dashboard.value.resourceType === 'freelancer')

const loadDashboard = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/dashboard')
    dashboard.value = res.data
  } catch (e) {
    console.error('Dashboard load error:', e)
  } finally {
    loading.value = false
  }
}

const formatDate = (date: Date) => {
  return date.toLocaleDateString('ja-JP', { 
    year: 'numeric', 
    month: 'long', 
    day: 'numeric',
    weekday: 'long'
  })
}

const formatDateShort = (dateStr?: string) => {
  if (!dateStr) return ''
  return new Date(dateStr).toLocaleDateString('ja-JP', { year: 'numeric', month: 'short', day: 'numeric' })
}

const getResourceLabel = (type?: string) => {
  const map: Record<string, string> = { employee: '自社社員', freelancer: '個人事業主', bp: 'BP' }
  return type ? (map[type] || type) : ''
}

const getResourceTagType = (type?: string) => {
  const map: Record<string, string> = { employee: 'primary', freelancer: 'success', bp: 'warning' }
  return type ? (map[type] || 'info') : 'info'
}

const getContractLabel = (type: string) => {
  const map: Record<string, string> = { dispatch: '派遣', ses: 'SES', contract: '請負' }
  return map[type] || type
}

const getContractTagType = (type: string) => {
  const map: Record<string, string> = { dispatch: 'warning', ses: 'primary', contract: 'success' }
  return map[type] || 'info'
}

const getTimesheetStatusLabel = (status?: string) => {
  const map: Record<string, string> = {
    not_submitted: '未提出',
    open: '入力中',
    submitted: '承認待ち',
    confirmed: '承認済み'
  }
  return status ? (map[status] || status) : '未提出'
}

const getTimesheetStatusType = (status?: string) => {
  const map: Record<string, string> = {
    not_submitted: 'info',
    open: 'warning',
    submitted: 'primary',
    confirmed: 'success'
  }
  return status ? (map[status] || 'info') : 'info'
}

onMounted(() => {
  loadDashboard()
})
</script>

<style scoped>
.portal-dashboard {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;
}

.portal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 24px;
  padding: 24px;
  background: white;
  border-radius: 12px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.05);
}

.welcome-section {
  display: flex;
  align-items: center;
  gap: 20px;
}

.welcome-text h1 {
  margin: 0 0 8px 0;
  font-size: 24px;
  font-weight: 600;
}

.sub-text {
  display: flex;
  align-items: center;
  gap: 12px;
  color: #909399;
}

.code {
  font-family: monospace;
  font-size: 13px;
}

.current-date {
  font-size: 14px;
  color: #606266;
}

.summary-card {
  margin-bottom: 20px;
}

.card-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}

.card-header.warning {
  color: var(--el-color-warning);
}

.timesheet-summary {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 0;
}

.hours-display {
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.hours-value {
  font-size: 48px;
  font-weight: 700;
  color: var(--el-color-primary);
}

.hours-unit {
  font-size: 16px;
  color: #909399;
}

.card-actions {
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.pending-card {
  margin-bottom: 24px;
}

.pending-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.pending-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  background: #fff7e6;
  border-radius: 8px;
}

.pending-icon {
  font-size: 20px;
  color: var(--el-color-warning);
}

.pending-item span {
  flex: 1;
}

.quick-access h3 {
  margin: 0 0 16px 0;
  font-size: 16px;
  font-weight: 600;
}

.quick-links {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
}

.quick-link {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 20px 24px;
  background: white;
  border-radius: 12px;
  text-decoration: none;
  color: #303133;
  transition: transform 0.2s, box-shadow 0.2s;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
}

.quick-link:hover {
  transform: translateY(-4px);
  box-shadow: 0 8px 20px rgba(0, 0, 0, 0.1);
}

.link-icon {
  width: 56px;
  height: 56px;
  border-radius: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: white;
  font-size: 24px;
}

.quick-link span {
  font-size: 13px;
  font-weight: 500;
}
</style>


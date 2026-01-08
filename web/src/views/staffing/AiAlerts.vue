<template>
  <div class="ai-alerts">
    <div class="page-header">
      <div class="header-left">
        <el-icon class="header-icon"><Bell /></el-icon>
        <h1>AI アラート & 予測</h1>
      </div>
      <el-button type="primary" plain @click="loadAlerts">
        <el-icon><Refresh /></el-icon>
        更新
      </el-button>
    </div>

    <!-- サマリカード -->
    <div class="summary-cards" v-if="summary">
      <div class="summary-card critical" v-if="summary.critical > 0">
        <div class="summary-icon">
          <el-icon><Warning /></el-icon>
        </div>
        <div class="summary-content">
          <div class="summary-value">{{ summary.critical }}</div>
          <div class="summary-label">緊急</div>
        </div>
      </div>
      <div class="summary-card high" v-if="summary.high > 0">
        <div class="summary-icon">
          <el-icon><CircleClose /></el-icon>
        </div>
        <div class="summary-content">
          <div class="summary-value">{{ summary.high }}</div>
          <div class="summary-label">要対応</div>
        </div>
      </div>
      <div class="summary-card medium">
        <div class="summary-icon">
          <el-icon><InfoFilled /></el-icon>
        </div>
        <div class="summary-content">
          <div class="summary-value">{{ summary.medium }}</div>
          <div class="summary-label">確認推奨</div>
        </div>
      </div>
      <div class="summary-card total">
        <div class="summary-icon">
          <el-icon><Bell /></el-icon>
        </div>
        <div class="summary-content">
          <div class="summary-value">{{ summary.total }}</div>
          <div class="summary-label">総アラート</div>
        </div>
      </div>
    </div>

    <!-- アラート一覧 -->
    <el-card v-loading="loading">
      <template #header>
        <div class="card-header">
          <span class="card-title">アラート一覧</span>
          <el-radio-group v-model="filter" size="small">
            <el-radio-button label="all">すべて</el-radio-button>
            <el-radio-button label="contract_expiring">契約満期</el-radio-button>
            <el-radio-button label="no_salary_review">単価未見直</el-radio-button>
            <el-radio-button label="excessive_overtime">残業過多</el-radio-button>
          </el-radio-group>
        </div>
      </template>

      <div class="alerts-list">
        <div 
          v-for="alert in filteredAlerts" 
          :key="`${alert.type}-${alert.resourceId || alert.contractId}`"
          class="alert-card"
          :class="alert.severity"
        >
          <div class="alert-icon">
            <el-icon v-if="alert.severity === 'critical'"><Warning /></el-icon>
            <el-icon v-else-if="alert.severity === 'high'"><CircleClose /></el-icon>
            <el-icon v-else><InfoFilled /></el-icon>
          </div>
          
          <div class="alert-content">
            <div class="alert-header">
              <span class="alert-type">{{ getTypeLabel(alert.type) }}</span>
              <el-tag :type="getSeverityType(alert.severity)" size="small">
                {{ getSeverityLabel(alert.severity) }}
              </el-tag>
            </div>
            <div class="alert-message">{{ alert.message }}</div>
            <div class="alert-details">
              <span v-if="alert.resourceName">
                <el-icon><User /></el-icon>
                {{ alert.resourceName }}
              </span>
              <span v-if="alert.clientName">
                <el-icon><OfficeBuilding /></el-icon>
                {{ alert.clientName }}
              </span>
              <span v-if="alert.daysRemaining !== undefined">
                <el-icon><Clock /></el-icon>
                {{ alert.daysRemaining }}日後
              </span>
              <span v-if="alert.overtimeHours">
                <el-icon><Timer /></el-icon>
                残業 {{ alert.overtimeHours }}h
              </span>
              <span v-if="alert.billingRate">
                <el-icon><Money /></el-icon>
                ¥{{ formatNumber(alert.billingRate) }}/月
              </span>
            </div>
          </div>

          <div class="alert-actions">
            <div class="suggested-actions">
              <el-button 
                v-for="action in alert.suggestedActions" 
                :key="action" 
                size="small"
                @click="handleAction(alert, action)"
              >
                {{ action }}
              </el-button>
            </div>
          </div>
        </div>
      </div>

      <el-empty v-if="!loading && filteredAlerts.length === 0" description="アラートはありません" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { 
  Bell, Refresh, Warning, CircleClose, InfoFilled, 
  User, OfficeBuilding, Clock, Timer, Money 
} from '@element-plus/icons-vue'
import api from '../../api'

interface Alert {
  type: string
  severity: string
  contractId?: string
  contractNo?: string
  resourceId?: string
  resourceName?: string
  resourceType?: string
  clientName?: string
  endDate?: string
  daysRemaining?: number
  billingRate?: number
  overtimeHours?: number
  monthsActive?: number
  message: string
  suggestedActions: string[]
}

const loading = ref(false)
const filter = ref('all')
const alerts = ref<Alert[]>([])
const summary = ref<{ critical: number; high: number; medium: number; total: number } | null>(null)

const filteredAlerts = computed(() => {
  if (filter.value === 'all') return alerts.value
  return alerts.value.filter(a => a.type === filter.value)
})

const loadAlerts = async () => {
  loading.value = true
  try {
    const res = await api.get('/staffing/ai/churn-alerts')
    alerts.value = res.data.alerts
    summary.value = res.data.summary
  } catch (e: any) {
    console.error('Load alerts error:', e)
  } finally {
    loading.value = false
  }
}

const handleAction = (alert: Alert, action: string) => {
  ElMessage.info(`${alert.resourceName}に対して「${action}」を実行`)
  // TODO: 实际处理动作
}

const getTypeLabel = (type: string) => {
  const map: Record<string, string> = {
    contract_expiring: '契約満期',
    no_salary_review: '単価未見直',
    excessive_overtime: '残業過多',
    low_performance: 'パフォーマンス',
    churn_risk: '離脱リスク'
  }
  return map[type] || type
}

const getSeverityLabel = (severity: string) => {
  const map: Record<string, string> = {
    critical: '緊急',
    high: '要対応',
    medium: '確認推奨',
    low: '参考'
  }
  return map[severity] || severity
}

const getSeverityType = (severity: string) => {
  const map: Record<string, string> = {
    critical: 'danger',
    high: 'warning',
    medium: 'info',
    low: 'info'
  }
  return map[severity] || 'info'
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

onMounted(() => {
  loadAlerts()
})
</script>

<style scoped>
.ai-alerts {
  padding: 20px;
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

.header-icon {
  font-size: 28px;
  color: #667eea;
}

.header-left h1 {
  margin: 0;
  font-size: 22px;
}

.summary-cards {
  display: flex;
  gap: 16px;
  margin-bottom: 20px;
}

.summary-card {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 20px 24px;
  background: white;
  border-radius: 12px;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
  flex: 1;
}

.summary-card.critical {
  border-left: 4px solid #f56c6c;
}

.summary-card.high {
  border-left: 4px solid #e6a23c;
}

.summary-card.medium {
  border-left: 4px solid #409eff;
}

.summary-card.total {
  border-left: 4px solid #909399;
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

.summary-card.critical .summary-icon {
  background: #fef0f0;
  color: #f56c6c;
}

.summary-card.high .summary-icon {
  background: #fdf6ec;
  color: #e6a23c;
}

.summary-card.medium .summary-icon {
  background: #ecf5ff;
  color: #409eff;
}

.summary-card.total .summary-icon {
  background: #f4f4f5;
  color: #909399;
}

.summary-value {
  font-size: 28px;
  font-weight: 700;
}

.summary-label {
  font-size: 13px;
  color: #909399;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.card-title {
  font-weight: 600;
}

.alerts-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.alert-card {
  display: flex;
  gap: 16px;
  padding: 16px 20px;
  border-radius: 8px;
  background: white;
  border: 1px solid #ebeef5;
}

.alert-card.critical {
  background: #fef0f0;
  border-color: #f56c6c40;
}

.alert-card.high {
  background: #fdf6ec;
  border-color: #e6a23c40;
}

.alert-card.medium {
  background: #f4f4f5;
  border-color: #dcdfe6;
}

.alert-icon {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  flex-shrink: 0;
}

.alert-card.critical .alert-icon {
  background: #f56c6c;
  color: white;
}

.alert-card.high .alert-icon {
  background: #e6a23c;
  color: white;
}

.alert-card.medium .alert-icon {
  background: #909399;
  color: white;
}

.alert-content {
  flex: 1;
  min-width: 0;
}

.alert-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.alert-type {
  font-weight: 600;
  font-size: 14px;
}

.alert-message {
  font-size: 15px;
  margin-bottom: 8px;
}

.alert-details {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  font-size: 13px;
  color: #606266;
}

.alert-details span {
  display: flex;
  align-items: center;
  gap: 4px;
}

.alert-actions {
  display: flex;
  flex-direction: column;
  justify-content: center;
}

.suggested-actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
</style>


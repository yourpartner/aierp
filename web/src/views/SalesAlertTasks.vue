<template>
  <div class="page">
    <el-tabs v-model="activeTab" @tab-change="onTabChange">
      <!-- 告警列表 -->
      <el-tab-pane label="アラート" name="alerts">
        <el-card>
          <template #header>
            <div class="card-header">
              <span>販売アラート</span>
              <div class="filter-group">
                <el-select v-model="alertFilter.type" placeholder="タイプ" clearable size="small" @change="loadAlerts">
                  <el-option label="納期超過" value="overdue_delivery" />
                  <el-option label="入金超過" value="overdue_payment" />
                  <el-option label="顧客離脱" value="customer_churn" />
                  <el-option label="在庫不足" value="inventory_shortage" />
                </el-select>
                <el-select v-model="alertFilter.status" placeholder="ステータス" clearable size="small" @change="loadAlerts">
                  <el-option label="オープン" value="open" />
                  <el-option label="確認済" value="acknowledged" />
                  <el-option label="解決済" value="resolved" />
                  <el-option label="却下" value="dismissed" />
                </el-select>
                <el-button size="small" @click="loadAlerts">
                  <el-icon><Refresh /></el-icon>
                </el-button>
              </div>
            </div>
          </template>

          <el-table :data="alerts" stripe v-loading="alertsLoading">
            <el-table-column label="重要度" width="80" align="center">
              <template #default="{ row }">
                <el-tag :type="severityType(row.severity)" size="small" effect="dark">
                  {{ severityLabel(row.severity) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="タイプ" width="100">
              <template #default="{ row }">
                <el-tag :type="alertTypeTag(row.alert_type)" size="small">
                  {{ alertTypeLabel(row.alert_type) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="title" label="タイトル" min-width="200" />
            <el-table-column label="関連先" width="140">
              <template #default="{ row }">
                {{ row.customer_name || row.material_name || row.so_no || '-' }}
              </template>
            </el-table-column>
            <el-table-column label="金額" width="120" align="right">
              <template #default="{ row }">
                <span v-if="row.amount">¥{{ formatNumber(row.amount) }}</span>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column label="超過日数" width="90" align="center">
              <template #default="{ row }">
                <span v-if="row.overdue_days" class="overdue-days">{{ row.overdue_days }}日</span>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column label="ステータス" width="100" align="center">
              <template #default="{ row }">
                <el-tag :type="statusType(row.status)" size="small">
                  {{ statusLabel(row.status) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="作成日" width="100">
              <template #default="{ row }">
                {{ formatDate(row.created_at) }}
              </template>
            </el-table-column>
            <el-table-column label="操作" width="180">
              <template #default="{ row }">
                <el-button v-if="row.status === 'open'" size="small" @click="acknowledgeAlert(row)">確認</el-button>
                <el-button v-if="row.status !== 'resolved'" size="small" type="success" @click="resolveAlert(row)">解決</el-button>
                <el-button v-if="row.status === 'open'" size="small" type="info" @click="dismissAlert(row)">却下</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-tab-pane>

      <!-- 任务列表 -->
      <el-tab-pane label="タスク" name="tasks">
        <el-card>
          <template #header>
            <div class="card-header">
              <span>アラートタスク</span>
              <div class="filter-group">
                <el-select v-model="taskFilter.status" placeholder="ステータス" clearable size="small" @change="loadTasks">
                  <el-option label="保留中" value="pending" />
                  <el-option label="進行中" value="in_progress" />
                  <el-option label="完了" value="completed" />
                  <el-option label="キャンセル" value="cancelled" />
                </el-select>
                <el-select v-model="taskFilter.priority" placeholder="優先度" clearable size="small" @change="loadTasks">
                  <el-option label="緊急" value="urgent" />
                  <el-option label="高" value="high" />
                  <el-option label="中" value="medium" />
                  <el-option label="低" value="low" />
                </el-select>
                <el-button size="small" @click="loadTasks">
                  <el-icon><Refresh /></el-icon>
                </el-button>
              </div>
            </div>
          </template>

          <el-table :data="tasks" stripe v-loading="tasksLoading">
            <el-table-column label="優先度" width="80" align="center">
              <template #default="{ row }">
                <el-tag :type="priorityType(row.priority)" size="small" effect="dark">
                  {{ priorityLabel(row.priority) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="title" label="タイトル" min-width="250" />
            <el-table-column label="タイプ" width="100">
              <template #default="{ row }">
                {{ taskTypeLabel(row.task_type) }}
              </template>
            </el-table-column>
            <el-table-column label="期限" width="100">
              <template #default="{ row }">
                <span :class="{ 'overdue': isOverdue(row.due_date) }">
                  {{ formatDate(row.due_date) }}
                </span>
              </template>
            </el-table-column>
            <el-table-column label="担当" width="100">
              <template #default="{ row }">
                {{ row.assigned_to || '未割当' }}
              </template>
            </el-table-column>
            <el-table-column label="ステータス" width="100" align="center">
              <template #default="{ row }">
                <el-tag :type="taskStatusType(row.status)" size="small">
                  {{ taskStatusLabel(row.status) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="180">
              <template #default="{ row }">
                <el-button v-if="row.status === 'pending'" size="small" type="primary" @click="startTask(row)">開始</el-button>
                <el-button v-if="row.status === 'in_progress'" size="small" type="success" @click="completeTask(row)">完了</el-button>
                <el-button v-if="row.status !== 'completed'" size="small" type="danger" @click="cancelTask(row)">キャンセル</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-tab-pane>

      <!-- 规则配置 -->
      <el-tab-pane label="ルール設定" name="rules">
        <el-card>
          <template #header>
            <span>監視ルール設定</span>
          </template>
          
          <div class="rule-ai-input">
            <el-input
              v-model="ruleQuery"
              type="textarea"
              :rows="2"
              placeholder="自然言語でルールを記述してください。例：「最近1ヶ月注文がない、過去半年に5回以上注文した顧客を検出」"
            />
            <div class="rule-actions">
              <el-select v-model="selectedRuleType" placeholder="ルールタイプ" style="width: 180px">
                <el-option label="顧客離脱検知" value="customer_churn" />
                <el-option label="入金超過検知" value="overdue_payment" />
                <el-option label="在庫不足検知" value="inventory_shortage" />
              </el-select>
              <el-button type="primary" :loading="parsingRule" @click="parseRule">
                <el-icon><MagicStick /></el-icon> AI解析
              </el-button>
            </div>
          </div>

          <div v-if="parsedRuleResult" class="parsed-result">
            <el-alert
              :title="parsedRuleResult.success ? '解析成功' : '解析失敗'"
              :type="parsedRuleResult.success ? 'success' : 'error'"
              :description="parsedRuleResult.error || ''"
              :closable="false"
            />
            <div v-if="parsedRuleResult.parsedParams" class="params-preview">
              <h4>解析されたパラメータ:</h4>
              <pre>{{ JSON.stringify(parsedRuleResult.parsedParams, null, 2) }}</pre>
              <el-button type="success" @click="applyParsedRule">このルールを適用</el-button>
            </div>
          </div>

          <el-divider />

          <h4>現在のルール設定</h4>
          <el-table :data="monitorRules" stripe size="small" v-loading="rulesLoading">
            <el-table-column prop="rule_name" label="ルール名" width="150" />
            <el-table-column label="タイプ" width="120">
              <template #default="{ row }">{{ alertTypeLabel(row.rule_type) }}</template>
            </el-table-column>
            <el-table-column label="パラメータ" min-width="300">
              <template #default="{ row }">
                <code>{{ formatParams(row.params) }}</code>
              </template>
            </el-table-column>
            <el-table-column label="有効" width="80" align="center">
              <template #default="{ row }">
                <el-switch v-model="row.is_active" @change="toggleRule(row)" />
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-tab-pane>

      <!-- 统计面板 -->
      <el-tab-pane label="統計" name="stats">
        <div class="stats-grid">
          <el-card class="stat-card">
            <div class="stat-icon critical">
              <el-icon><WarningFilled /></el-icon>
            </div>
            <div class="stat-content">
              <div class="stat-value">{{ stats.openCritical }}</div>
              <div class="stat-label">緊急アラート</div>
            </div>
          </el-card>
          <el-card class="stat-card">
            <div class="stat-icon high">
              <el-icon><Warning /></el-icon>
            </div>
            <div class="stat-content">
              <div class="stat-value">{{ stats.openHigh }}</div>
              <div class="stat-label">高優先度アラート</div>
            </div>
          </el-card>
          <el-card class="stat-card">
            <div class="stat-icon pending">
              <el-icon><Clock /></el-icon>
            </div>
            <div class="stat-content">
              <div class="stat-value">{{ stats.pendingTasks }}</div>
              <div class="stat-label">保留タスク</div>
            </div>
          </el-card>
          <el-card class="stat-card">
            <div class="stat-icon resolved">
              <el-icon><CircleCheck /></el-icon>
            </div>
            <div class="stat-content">
              <div class="stat-value">{{ stats.resolvedToday }}</div>
              <div class="stat-label">今日解決</div>
            </div>
          </el-card>
        </div>

        <el-card style="margin-top: 20px;">
          <template #header>アラートタイプ別</template>
          <el-table :data="statsByType" stripe size="small">
            <el-table-column label="タイプ" width="150">
              <template #default="{ row }">{{ alertTypeLabel(row.alert_type) }}</template>
            </el-table-column>
            <el-table-column prop="open_count" label="オープン" width="100" align="right" />
            <el-table-column prop="resolved_count" label="解決済" width="100" align="right" />
            <el-table-column prop="total_count" label="合計" width="100" align="right" />
          </el-table>
        </el-card>
      </el-tab-pane>
    </el-tabs>

    <!-- 解决对话框 -->
    <el-dialog v-model="resolveDialog.visible" title="アラート解決" width="400px">
      <el-form :model="resolveDialog.form" label-width="100px">
        <el-form-item label="解決メモ">
          <el-input v-model="resolveDialog.form.note" type="textarea" :rows="3" placeholder="解決内容を記入" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="resolveDialog.visible = false">キャンセル</el-button>
        <el-button type="success" :loading="resolveDialog.loading" @click="confirmResolve">解決</el-button>
      </template>
    </el-dialog>

    <!-- 完成任务对话框 -->
    <el-dialog v-model="completeDialog.visible" title="タスク完了" width="400px">
      <el-form :model="completeDialog.form" label-width="100px">
        <el-form-item label="完了メモ">
          <el-input v-model="completeDialog.form.note" type="textarea" :rows="3" placeholder="完了内容を記入" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="completeDialog.visible = false">キャンセル</el-button>
        <el-button type="success" :loading="completeDialog.loading" @click="confirmComplete">完了</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Refresh, WarningFilled, Warning, Clock, CircleCheck, MagicStick } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'

const activeTab = ref('alerts')

// Alerts
const alerts = ref<any[]>([])
const alertsLoading = ref(false)
const alertFilter = reactive({ type: '', status: 'open' })

// Tasks
const tasks = ref<any[]>([])
const tasksLoading = ref(false)
const taskFilter = reactive({ status: '', priority: '' })

// Stats
const stats = reactive({
  openCritical: 0,
  openHigh: 0,
  pendingTasks: 0,
  resolvedToday: 0
})
const statsByType = ref<any[]>([])

// Rules
const monitorRules = ref<any[]>([])
const rulesLoading = ref(false)
const ruleQuery = ref('')
const selectedRuleType = ref('customer_churn')
const parsingRule = ref(false)
const parsedRuleResult = ref<any>(null)

// Dialogs
const resolveDialog = reactive({
  visible: false,
  loading: false,
  alertId: '',
  form: { note: '' }
})

const completeDialog = reactive({
  visible: false,
  loading: false,
  taskId: '',
  form: { note: '' }
})

function formatNumber(val: any) {
  const num = Number(val || 0)
  return num.toLocaleString()
}

function formatDate(dateStr: string) {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

function isOverdue(dateStr: string) {
  if (!dateStr) return false
  return new Date(dateStr) < new Date()
}

// Alert helpers
function severityType(s: string) {
  return { critical: 'danger', high: 'warning', medium: 'info', low: 'success' }[s] || 'info'
}
function severityLabel(s: string) {
  return { critical: '緊急', high: '高', medium: '中', low: '低' }[s] || s
}
function alertTypeTag(t: string) {
  return { overdue_delivery: 'warning', overdue_payment: 'danger', customer_churn: 'info', inventory_shortage: '' }[t] || ''
}
function alertTypeLabel(t: string) {
  return { overdue_delivery: '納期超過', overdue_payment: '入金超過', customer_churn: '顧客離脱', inventory_shortage: '在庫不足' }[t] || t
}
function statusType(s: string) {
  return { open: 'danger', acknowledged: 'warning', resolved: 'success', dismissed: 'info' }[s] || 'info'
}
function statusLabel(s: string) {
  return { open: 'オープン', acknowledged: '確認済', resolved: '解決済', dismissed: '却下' }[s] || s
}

// Task helpers
function priorityType(p: string) {
  return { urgent: 'danger', high: 'warning', medium: 'info', low: 'success' }[p] || 'info'
}
function priorityLabel(p: string) {
  return { urgent: '緊急', high: '高', medium: '中', low: '低' }[p] || p
}
function taskTypeLabel(t: string) {
  return { follow_up: 'フォロー', collection: '回収', contact: '連絡', restock: '補充' }[t] || t
}
function taskStatusType(s: string) {
  return { pending: 'info', in_progress: 'warning', completed: 'success', cancelled: 'danger' }[s] || 'info'
}
function taskStatusLabel(s: string) {
  return { pending: '保留', in_progress: '進行中', completed: '完了', cancelled: 'キャンセル' }[s] || s
}

async function loadAlerts() {
  alertsLoading.value = true
  try {
    const params = new URLSearchParams()
    if (alertFilter.type) params.append('type', alertFilter.type)
    if (alertFilter.status) params.append('status', alertFilter.status)
    const res = await api.get(`/sales-alerts?${params.toString()}`)
    alerts.value = res.data || []
  } catch (e) {
    console.error('Failed to load alerts:', e)
  } finally {
    alertsLoading.value = false
  }
}

async function loadTasks() {
  tasksLoading.value = true
  try {
    const params = new URLSearchParams()
    if (taskFilter.status) params.append('status', taskFilter.status)
    if (taskFilter.priority) params.append('priority', taskFilter.priority)
    const res = await api.get(`/alert-tasks?${params.toString()}`)
    tasks.value = res.data || []
  } catch (e) {
    console.error('Failed to load tasks:', e)
  } finally {
    tasksLoading.value = false
  }
}

async function loadStats() {
  try {
    const res = await api.get('/sales-alerts/stats')
    Object.assign(stats, res.data.summary || {})
    statsByType.value = res.data.byType || []
  } catch (e) {
    console.error('Failed to load stats:', e)
  }
}

function onTabChange(tab: string) {
  if (tab === 'alerts') loadAlerts()
  else if (tab === 'tasks') loadTasks()
  else if (tab === 'stats') loadStats()
  else if (tab === 'rules') loadRules()
}

async function loadRules() {
  rulesLoading.value = true
  try {
    const res = await api.get('/sales-monitor-rules')
    monitorRules.value = res.data || []
  } catch (e) {
    console.error('Failed to load rules:', e)
  } finally {
    rulesLoading.value = false
  }
}

async function parseRule() {
  if (!ruleQuery.value.trim()) {
    ElMessage.warning('ルールを入力してください')
    return
  }
  parsingRule.value = true
  parsedRuleResult.value = null
  try {
    const res = await api.post('/sales-alerts/parse-rule', {
      query: ruleQuery.value,
      ruleType: selectedRuleType.value
    })
    parsedRuleResult.value = res.data
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '解析に失敗しました')
  } finally {
    parsingRule.value = false
  }
}

async function applyParsedRule() {
  if (!parsedRuleResult.value?.parsedParams) return
  try {
    await api.post('/sales-monitor-rules/update', {
      ruleType: selectedRuleType.value,
      params: parsedRuleResult.value.parsedParams,
      naturalLanguageQuery: ruleQuery.value
    })
    ElMessage.success('ルールを更新しました')
    parsedRuleResult.value = null
    ruleQuery.value = ''
    loadRules()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
  }
}

async function toggleRule(rule: any) {
  try {
    await api.post(`/sales-monitor-rules/${rule.id}/toggle`, { isActive: rule.is_active })
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
    rule.is_active = !rule.is_active
  }
}

function formatParams(params: any) {
  if (!params) return '-'
  if (typeof params === 'string') {
    try { params = JSON.parse(params) } catch { return params }
  }
  const parts = []
  if (params.inactiveDays) parts.push(`非活動: ${params.inactiveDays}日`)
  if (params.minOrdersInPeriod) parts.push(`最小注文: ${params.minOrdersInPeriod}件`)
  if (params.lookbackDays) parts.push(`参照期間: ${params.lookbackDays}日`)
  if (params.thresholdWorkDays) parts.push(`超過: ${params.thresholdWorkDays}営業日`)
  if (params.avgInboundDays) parts.push(`入庫平均: ${params.avgInboundDays}日`)
  return parts.length > 0 ? parts.join(', ') : JSON.stringify(params)
}

async function acknowledgeAlert(alert: any) {
  try {
    await api.post(`/sales-alerts/${alert.id}/acknowledge`)
    ElMessage.success('確認しました')
    loadAlerts()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
  }
}

function resolveAlert(alert: any) {
  resolveDialog.alertId = alert.id
  resolveDialog.form.note = ''
  resolveDialog.visible = true
}

async function confirmResolve() {
  resolveDialog.loading = true
  try {
    await api.post(`/sales-alerts/${resolveDialog.alertId}/resolve`, resolveDialog.form)
    ElMessage.success('解決しました')
    resolveDialog.visible = false
    loadAlerts()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
  } finally {
    resolveDialog.loading = false
  }
}

async function dismissAlert(alert: any) {
  try {
    await ElMessageBox.confirm('このアラートを却下しますか？', '確認')
    await api.post(`/sales-alerts/${alert.id}/dismiss`)
    ElMessage.success('却下しました')
    loadAlerts()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
    }
  }
}

async function startTask(task: any) {
  try {
    await api.post(`/alert-tasks/${task.id}/start`)
    ElMessage.success('開始しました')
    loadTasks()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
  }
}

function completeTask(task: any) {
  completeDialog.taskId = task.id
  completeDialog.form.note = ''
  completeDialog.visible = true
}

async function confirmComplete() {
  completeDialog.loading = true
  try {
    await api.post(`/alert-tasks/${completeDialog.taskId}/complete`, completeDialog.form)
    ElMessage.success('完了しました')
    completeDialog.visible = false
    loadTasks()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
  } finally {
    completeDialog.loading = false
  }
}

async function cancelTask(task: any) {
  try {
    await ElMessageBox.confirm('このタスクをキャンセルしますか？', '確認')
    await api.post(`/alert-tasks/${task.id}/cancel`)
    ElMessage.success('キャンセルしました')
    loadTasks()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e?.response?.data?.error || '操作に失敗しました')
    }
  }
}

onMounted(() => {
  loadAlerts()
  loadStats()
})
</script>

<style scoped>
.page {
  padding: 20px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.filter-group {
  display: flex;
  gap: 8px;
}

.overdue-days {
  color: #f56c6c;
  font-weight: 600;
}

.overdue {
  color: #f56c6c;
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 16px;
}

.stat-card {
  display: flex;
  align-items: center;
  padding: 16px;
}

.stat-card :deep(.el-card__body) {
  display: flex;
  align-items: center;
  width: 100%;
  padding: 0;
}

.stat-icon {
  width: 56px;
  height: 56px;
  border-radius: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 24px;
  margin-right: 16px;
}

.stat-icon.critical {
  background: #fef0f0;
  color: #f56c6c;
}

.stat-icon.high {
  background: #fdf6ec;
  color: #e6a23c;
}

.stat-icon.pending {
  background: #ecf5ff;
  color: #409eff;
}

.stat-icon.resolved {
  background: #f0f9eb;
  color: #67c23a;
}

.stat-content {
  flex: 1;
}

.stat-value {
  font-size: 28px;
  font-weight: 600;
  color: #303133;
}

.stat-label {
  font-size: 14px;
  color: #909399;
}

@media (max-width: 1000px) {
  .stats-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (max-width: 600px) {
  .stats-grid {
    grid-template-columns: 1fr;
  }
}

.rule-ai-input {
  margin-bottom: 20px;
}

.rule-actions {
  display: flex;
  gap: 12px;
  margin-top: 12px;
}

.parsed-result {
  margin-top: 20px;
  padding: 16px;
  background: #f5f7fa;
  border-radius: 8px;
}

.params-preview {
  margin-top: 12px;
}

.params-preview pre {
  background: white;
  padding: 12px;
  border-radius: 4px;
  font-size: 12px;
  overflow-x: auto;
}

.params-preview h4 {
  margin: 0 0 8px 0;
  font-size: 14px;
}
</style>


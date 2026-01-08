<template>
  <div class="portal-timesheet">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Calendar /></el-icon>
        <h1>勤怠入力</h1>
      </div>
      <div class="header-right">
        <el-select v-model="selectedYear" style="width: 100px" @change="loadTimesheets">
          <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
        </el-select>
      </div>
    </div>

    <!-- 月別一覧 -->
    <el-card v-loading="loading">
      <el-table :data="timesheets">
        <el-table-column label="年月" prop="yearMonth" width="120">
          <template #default="{ row }">
            {{ formatYearMonth(row.yearMonth) }}
          </template>
        </el-table-column>
        <el-table-column label="顧客" prop="clientName" min-width="150" />
        <el-table-column label="所定時間" prop="scheduledHours" width="100" align="right">
          <template #default="{ row }">
            {{ row.scheduledHours }}h
          </template>
        </el-table-column>
        <el-table-column label="実労働" prop="actualHours" width="100" align="right">
          <template #default="{ row }">
            <span class="actual-hours">{{ row.actualHours }}h</span>
          </template>
        </el-table-column>
        <el-table-column label="残業" prop="overtimeHours" width="80" align="right">
          <template #default="{ row }">
            <span v-if="row.overtimeHours > 0" class="overtime">+{{ row.overtimeHours }}h</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="120" align="center">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="160" align="center">
          <template #default="{ row }">
            <el-button 
              v-if="row.status === 'open' || row.status === 'not_submitted'" 
              size="small" 
              type="primary"
              @click="editTimesheet(row)"
            >
              入力
            </el-button>
            <el-button 
              v-if="row.status === 'open'" 
              size="small" 
              type="success"
              @click="submitTimesheet(row)"
            >
              提出
            </el-button>
            <el-button 
              v-if="row.status === 'submitted' || row.status === 'confirmed'" 
              size="small" 
              link
              @click="viewTimesheet(row)"
            >
              詳細
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 入力ダイアログ -->
    <el-dialog v-model="dialogVisible" title="勤怠入力" width="500px">
      <el-form :model="form" label-position="top">
        <el-form-item label="年月">
          <el-input :value="formatYearMonth(form.yearMonth)" disabled />
        </el-form-item>
        <el-form-item label="契約" v-if="activeContracts.length > 1">
          <el-select v-model="form.contractId" style="width: 100%">
            <el-option 
              v-for="c in activeContracts" 
              :key="c.id" 
              :label="`${c.contractNo} - ${c.clientName}`" 
              :value="c.id" 
            />
          </el-select>
        </el-form-item>
        <el-row :gutter="16">
          <el-col :span="8">
            <el-form-item label="所定時間">
              <el-input-number v-model="form.scheduledHours" :min="0" :max="250" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="実労働時間">
              <el-input-number v-model="form.actualHours" :min="0" :max="300" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業時間">
              <el-input-number v-model="form.overtimeHours" :min="0" :max="100" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>
        <div class="form-hint">
          <el-icon><InfoFilled /></el-icon>
          週次の実績を月末にまとめて入力してください
        </div>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="saveTimesheet" :loading="saving">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowLeft, Calendar, InfoFilled } from '@element-plus/icons-vue'
import api from '../../api'

interface Timesheet {
  id?: string
  yearMonth: string
  contractId?: string
  clientName?: string
  scheduledHours: number
  actualHours: number
  overtimeHours: number
  status: string
}

const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)

const currentYear = new Date().getFullYear()
const selectedYear = ref(currentYear)
const years = Array.from({ length: 5 }, (_, i) => currentYear - i)

const timesheets = ref<Timesheet[]>([])
const activeContracts = ref<any[]>([])

const form = reactive({
  id: '',
  yearMonth: '',
  contractId: '',
  scheduledHours: 160,
  actualHours: 0,
  overtimeHours: 0
})

const loadTimesheets = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/timesheets', { params: { year: selectedYear.value } })
    timesheets.value = res.data.data || []
    
    // 如果没有本月记录，添加一个空的
    const currentMonth = new Date().toISOString().substring(0, 7)
    if (!timesheets.value.find(t => t.yearMonth === currentMonth)) {
      timesheets.value.unshift({
        yearMonth: currentMonth,
        scheduledHours: 0,
        actualHours: 0,
        overtimeHours: 0,
        status: 'not_submitted'
      })
    }
  } catch (e) {
    console.error('Load timesheets error:', e)
  } finally {
    loading.value = false
  }
}

const loadContracts = async () => {
  try {
    const res = await api.get('/portal/dashboard')
    activeContracts.value = res.data.activeContracts || []
  } catch (e) {
    console.error('Load contracts error:', e)
  }
}

const editTimesheet = (row: Timesheet) => {
  Object.assign(form, {
    id: row.id || '',
    yearMonth: row.yearMonth,
    contractId: row.contractId || (activeContracts.value[0]?.id || ''),
    scheduledHours: row.scheduledHours || 160,
    actualHours: row.actualHours || 0,
    overtimeHours: row.overtimeHours || 0
  })
  dialogVisible.value = true
}

const viewTimesheet = (row: Timesheet) => {
  ElMessageBox.alert(
    `所定: ${row.scheduledHours}h / 実労働: ${row.actualHours}h / 残業: ${row.overtimeHours}h`,
    `${formatYearMonth(row.yearMonth)} 勤怠詳細`
  )
}

const saveTimesheet = async () => {
  saving.value = true
  try {
    await api.post('/portal/timesheets', form)
    ElMessage.success('保存しました')
    dialogVisible.value = false
    await loadTimesheets()
  } catch (e: any) {
    ElMessage.error('保存に失敗しました')
  } finally {
    saving.value = false
  }
}

const submitTimesheet = async (row: Timesheet) => {
  if (!row.id) {
    ElMessage.warning('まず勤怠を入力してください')
    return
  }
  
  try {
    await ElMessageBox.confirm(
      '勤怠を提出すると修正できなくなります。提出しますか？',
      '確認'
    )
    await api.post(`/portal/timesheets/${row.id}/submit`)
    ElMessage.success('提出しました')
    await loadTimesheets()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error('提出に失敗しました')
    }
  }
}

const formatYearMonth = (ym: string) => {
  if (!ym) return ''
  const [y, m] = ym.split('-')
  return `${y}年${parseInt(m)}月`
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    not_submitted: '未入力',
    open: '入力中',
    submitted: '承認待ち',
    confirmed: '承認済み'
  }
  return map[status] || status
}

const getStatusType = (status: string) => {
  const map: Record<string, string> = {
    not_submitted: 'info',
    open: 'warning',
    submitted: 'primary',
    confirmed: 'success'
  }
  return map[status] || 'info'
}

onMounted(() => {
  loadTimesheets()
  loadContracts()
})
</script>

<style scoped>
.portal-timesheet {
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

.actual-hours {
  font-weight: 600;
  color: var(--el-color-primary);
}

.overtime {
  color: var(--el-color-danger);
}

.form-hint {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 16px;
  background: #f0f9eb;
  border-radius: 6px;
  font-size: 13px;
  color: #67c23a;
}
</style>


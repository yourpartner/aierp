<template>
  <div class="contracts-list">
    <el-card class="contracts-card">
      <template #header>
        <div class="contracts-header">
          <div class="contracts-header__left">
            <el-icon class="contracts-header__icon"><Document /></el-icon>
            <span class="contracts-header__title">契約一覧</span>
            <el-tag size="small" type="info" class="contracts-header__count">{{ total }}件</el-tag>
          </div>
          <div class="contracts-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規契約</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="contracts-filters">
        <el-select v-model="statusFilter" clearable placeholder="ステータス" class="contracts-filters__status">
          <el-option label="有効" value="active" />
          <el-option label="終了" value="ended" />
          <el-option label="解約" value="terminated" />
        </el-select>

        <el-select v-model="contractTypeFilter" clearable placeholder="契約形態" class="contracts-filters__type">
          <el-option label="派遣" value="dispatch" />
          <el-option label="SES（準委任）" value="ses" />
          <el-option label="請負" value="contract" />
        </el-select>

        <el-select 
          v-model="clientFilter" 
          filterable 
          remote
          clearable 
          :remote-method="searchClients"
          placeholder="顧客で絞込"
          class="contracts-filters__client"
        >
          <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- 契約テーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="contracts-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="契約番号" prop="contractNo" width="100">
          <template #default="{ row }">
            <span class="contract-no">{{ row.contractNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="リソース" min-width="140">
          <template #default="{ row }">
            <div v-if="row.resourceName">
              <div class="resource-name">{{ row.resourceName }}</div>
              <div class="resource-code">{{ row.resourceCode }}</div>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="顧客" prop="clientName" width="150">
          <template #default="{ row }">
            <div>
              <div>{{ row.clientName || '-' }}</div>
              <div class="client-code" v-if="row.clientCode">{{ row.clientCode }}</div>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="契約形態" prop="contractType" width="100">
          <template #default="{ row }">
            <el-tag :type="getContractTagType(row.contractType)" size="small">
              {{ getContractLabel(row.contractType) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="契約期間" width="180">
          <template #default="{ row }">
            <div>
              {{ formatDate(row.startDate) }} ～ {{ row.endDate ? formatDate(row.endDate) : '無期限' }}
            </div>
          </template>
        </el-table-column>

        <el-table-column label="請求単価" width="120" align="right">
          <template #default="{ row }">
            <div>
              <span class="rate-value">¥{{ formatNumber(row.billingRate) }}</span>
              <span class="rate-type">/{{ getRateTypeLabel(row.billingRateType) }}</span>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="原価" width="110" align="right">
          <template #default="{ row }">
            <div v-if="row.costRate">
              <span>¥{{ formatNumber(row.costRate) }}</span>
              <span class="rate-type">/{{ getRateTypeLabel(row.costRateType) }}</span>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="勤務地" prop="workLocation" width="100">
          <template #default="{ row }">
            {{ row.workLocation || '-' }}
          </template>
        </el-table-column>

        <el-table-column label="ステータス" prop="status" width="90">
          <template #default="{ row }">
            <el-tag :type="getStatusTagType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="案件" prop="projectName" width="120">
          <template #default="{ row }">
            <div v-if="row.projectName">
              <div class="project-name">{{ row.projectName }}</div>
              <div class="project-code">{{ row.projectCode }}</div>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="180" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="onEdit(row)">編集</el-button>
            <el-button link type="warning" @click="openRenew(row)" v-if="row.status === 'active'">更新</el-button>
            <el-button link type="danger" @click="openTerminate(row)" v-if="row.status === 'active'">終了</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新規・編集ダイアログ -->
    <el-dialog 
      v-model="dialogVisible" 
      :title="isEdit ? '契約編集' : '新規契約登録'"
      width="850px"
      destroy-on-close
    >
      <el-form :model="form" label-width="120px" label-position="right">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="契約形態" required>
              <el-select v-model="form.contractType" style="width: 100%">
                <el-option label="派遣" value="dispatch" />
                <el-option label="SES（準委任）" value="ses" />
                <el-option label="請負" value="contract" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="契約番号" v-if="isEdit">
              <el-input v-model="form.contractNo" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="顧客" required>
              <el-select 
                v-model="form.clientPartnerId" 
                filterable 
                remote
                :remote-method="searchClients"
                placeholder="顧客を選択"
                style="width: 100%"
              >
                <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="リソース">
              <el-select 
                v-model="form.resourceId" 
                filterable 
                remote
                clearable
                :remote-method="searchResources"
                placeholder="リソースを選択"
                style="width: 100%"
              >
                <el-option v-for="opt in resourceOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="案件">
              <el-select 
                v-model="form.projectId" 
                filterable 
                remote
                clearable
                :remote-method="searchProjects"
                placeholder="案件を選択（任意）"
                style="width: 100%"
              >
                <el-option v-for="opt in projectOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>契約期間</el-divider>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="開始日" required>
              <el-date-picker v-model="form.startDate" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="終了日">
              <el-date-picker v-model="form.endDate" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>勤務条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="勤務地">
              <el-input v-model="form.workLocation" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="勤務曜日">
              <el-input v-model="form.workDays" placeholder="例: 月～金" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="月間基準時間">
              <el-input-number v-model="form.monthlyWorkHours" :min="0" :max="300" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="勤務開始">
              <el-time-picker v-model="form.workStartTime" format="HH:mm" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="勤務終了">
              <el-time-picker v-model="form.workEndTime" format="HH:mm" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>請求条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="請求単価" required>
              <el-input-number v-model="form.billingRate" :min="0" :step="10000" controls-position="right" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="単価種別">
              <el-select v-model="form.billingRateType" style="width: 100%">
                <el-option label="月額" value="monthly" />
                <el-option label="日額" value="daily" />
                <el-option label="時給" value="hourly" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業倍率">
              <el-input-number v-model="form.overtimeRateMultiplier" :min="1" :max="2" :step="0.05" :precision="2" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="精算方式">
              <el-select v-model="form.settlementType" style="width: 100%">
                <el-option label="幅精算" value="range" />
                <el-option label="固定" value="fixed" />
                <el-option label="実費" value="actual" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="精算下限H" v-if="form.settlementType === 'range'">
              <el-input-number v-model="form.settlementLowerHours" :min="0" :max="200" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="精算上限H" v-if="form.settlementType === 'range'">
              <el-input-number v-model="form.settlementUpperHours" :min="0" :max="200" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>原価条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="原価単価">
              <el-input-number v-model="form.costRate" :min="0" :step="10000" controls-position="right" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="単価種別">
              <el-select v-model="form.costRateType" style="width: 100%">
                <el-option label="月額" value="monthly" />
                <el-option label="日額" value="daily" />
                <el-option label="時給" value="hourly" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="備考">
          <el-input v-model="form.notes" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="save" :loading="saving">保存</el-button>
      </template>
    </el-dialog>

    <!-- 契約更新ダイアログ -->
    <el-dialog v-model="renewDialogVisible" title="契約更新" width="400px">
      <el-form label-width="100px">
        <el-form-item label="新終了日">
          <el-date-picker v-model="renewEndDate" type="date" style="width: 100%" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="renewDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="renewContract" :loading="saving">更新</el-button>
      </template>
    </el-dialog>

    <!-- 契約終了ダイアログ -->
    <el-dialog v-model="terminateDialogVisible" title="契約終了" width="450px">
      <el-form label-width="100px">
        <el-form-item label="終了日">
          <el-date-picker v-model="terminationDate" type="date" style="width: 100%" />
        </el-form-item>
        <el-form-item label="終了理由">
          <el-input v-model="terminationReason" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="terminateDialogVisible = false">キャンセル</el-button>
        <el-button type="danger" @click="terminateContract" :loading="saving">終了</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Document, Plus, Search } from '@element-plus/icons-vue'
import api from '../../api'

interface ContractRow {
  id: string
  contractNo: string
  contractType: string
  startDate: string
  endDate?: string
  billingRate: number
  billingRateType: string
  costRate?: number
  costRateType?: string
  status: string
  workLocation?: string
  projectId?: string
  projectCode?: string
  projectName?: string
  resourceId?: string
  resourceCode?: string
  resourceName?: string
  clientPartnerId: string
  clientCode?: string
  clientName?: string
}

const loading = ref(false)
const rows = ref<ContractRow[]>([])
const total = ref(0)
const statusFilter = ref('')
const contractTypeFilter = ref('')
const clientFilter = ref('')
const clientOptions = ref<{ label: string; value: string }[]>([])
const resourceOptions = ref<{ label: string; value: string }[]>([])
const projectOptions = ref<{ label: string; value: string }[]>([])

const dialogVisible = ref(false)
const isEdit = ref(false)
const saving = ref(false)

const renewDialogVisible = ref(false)
const renewEndDate = ref<string | null>(null)
const currentContractId = ref('')

const terminateDialogVisible = ref(false)
const terminationDate = ref<string | null>(null)
const terminationReason = ref('')

const form = reactive({
  id: '',
  contractNo: '',
  contractType: 'ses',
  clientPartnerId: '',
  resourceId: '',
  projectId: '',
  startDate: null as string | null,
  endDate: null as string | null,
  workLocation: '',
  workDays: '',
  workStartTime: null as Date | null,
  workEndTime: null as Date | null,
  monthlyWorkHours: null as number | null,
  billingRate: null as number | null,
  billingRateType: 'monthly',
  overtimeRateMultiplier: 1.25,
  settlementType: 'range',
  settlementLowerHours: null as number | null,
  settlementUpperHours: null as number | null,
  costRate: null as number | null,
  costRateType: 'monthly',
  notes: ''
})

const load = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (statusFilter.value) params.status = statusFilter.value
    if (contractTypeFilter.value) params.contractType = contractTypeFilter.value
    if (clientFilter.value) params.clientId = clientFilter.value
    
    const res = await api.get('/staffing/contracts', { params })
    rows.value = res.data.data || []
    total.value = res.data.total || rows.value.length
  } catch (e: any) {
    ElMessage.error(e.message || '読み込み失敗')
  } finally {
    loading.value = false
  }
}

const searchClients = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/businesspartners', { params: { keyword: query, flag_customer: true } })
    clientOptions.value = (res.data.data || []).map((bp: any) => ({
      label: `${bp.partner_code} - ${bp.payload?.name || ''}`,
      value: bp.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const searchResources = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/staffing/resources', { params: { keyword: query } })
    resourceOptions.value = (res.data.data || []).map((r: any) => ({
      label: `${r.resourceCode} - ${r.displayName}`,
      value: r.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const searchProjects = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/staffing/projects', { params: { status: 'open' } })
    projectOptions.value = (res.data.data || []).map((p: any) => ({
      label: `${p.projectCode} - ${p.projectName}`,
      value: p.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const openNew = () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    contractNo: '',
    contractType: 'ses',
    clientPartnerId: '',
    resourceId: '',
    projectId: '',
    startDate: null,
    endDate: null,
    workLocation: '',
    workDays: '',
    workStartTime: null,
    workEndTime: null,
    monthlyWorkHours: null,
    billingRate: null,
    billingRateType: 'monthly',
    overtimeRateMultiplier: 1.25,
    settlementType: 'range',
    settlementLowerHours: null,
    settlementUpperHours: null,
    costRate: null,
    costRateType: 'monthly',
    notes: ''
  })
  dialogVisible.value = true
}

const onEdit = async (row: ContractRow) => {
  isEdit.value = true
  try {
    const res = await api.get(`/staffing/contracts/${row.id}`)
    const data = res.data
    Object.assign(form, {
      id: data.id,
      contractNo: data.contract_no,
      contractType: data.contract_type,
      clientPartnerId: data.client_partner_id,
      resourceId: data.resource_id,
      projectId: data.project_id,
      startDate: data.start_date,
      endDate: data.end_date,
      workLocation: data.work_location,
      workDays: data.work_days,
      workStartTime: data.work_start_time ? parseTime(data.work_start_time) : null,
      workEndTime: data.work_end_time ? parseTime(data.work_end_time) : null,
      monthlyWorkHours: data.monthly_work_hours ? Number(data.monthly_work_hours) : null,
      billingRate: data.billing_rate ? Number(data.billing_rate) : null,
      billingRateType: data.billing_rate_type || 'monthly',
      overtimeRateMultiplier: data.overtime_rate_multiplier ? Number(data.overtime_rate_multiplier) : 1.25,
      settlementType: data.settlement_type || 'range',
      settlementLowerHours: data.settlement_lower_hours ? Number(data.settlement_lower_hours) : null,
      settlementUpperHours: data.settlement_upper_hours ? Number(data.settlement_upper_hours) : null,
      costRate: data.cost_rate ? Number(data.cost_rate) : null,
      costRateType: data.cost_rate_type || 'monthly',
      notes: data.notes
    })
    // 選択肢セット
    if (data.client_partner_id && data.clientName) {
      clientOptions.value = [{ label: `${data.clientCode} - ${data.clientName}`, value: data.client_partner_id }]
    }
    if (data.resource_id && data.resourceName) {
      resourceOptions.value = [{ label: `${data.resourceCode} - ${data.resourceName}`, value: data.resource_id }]
    }
    if (data.project_id && data.projectName) {
      projectOptions.value = [{ label: `${data.projectCode} - ${data.projectName}`, value: data.project_id }]
    }
    dialogVisible.value = true
  } catch (e: any) {
    ElMessage.error('詳細取得失敗')
  }
}

const save = async () => {
  if (!form.clientPartnerId) {
    ElMessage.warning('顧客を選択してください')
    return
  }
  if (!form.startDate) {
    ElMessage.warning('開始日を入力してください')
    return
  }
  if (!form.billingRate) {
    ElMessage.warning('請求単価を入力してください')
    return
  }
  saving.value = true
  try {
    const payload = {
      contractType: form.contractType,
      clientPartnerId: form.clientPartnerId,
      resourceId: form.resourceId || null,
      projectId: form.projectId || null,
      startDate: form.startDate,
      endDate: form.endDate || null,
      workLocation: form.workLocation || null,
      workDays: form.workDays || null,
      workStartTime: form.workStartTime ? formatTime(form.workStartTime) : null,
      workEndTime: form.workEndTime ? formatTime(form.workEndTime) : null,
      monthlyWorkHours: form.monthlyWorkHours,
      billingRate: form.billingRate,
      billingRateType: form.billingRateType,
      overtimeRateMultiplier: form.overtimeRateMultiplier,
      settlementType: form.settlementType,
      settlementLowerHours: form.settlementLowerHours,
      settlementUpperHours: form.settlementUpperHours,
      costRate: form.costRate,
      costRateType: form.costRateType,
      notes: form.notes || null
    }
    
    if (isEdit.value) {
      await api.put(`/staffing/contracts/${form.id}`, payload)
      ElMessage.success('更新しました')
    } else {
      await api.post('/staffing/contracts', payload)
      ElMessage.success('登録しました')
    }
    dialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message || '保存失敗')
  } finally {
    saving.value = false
  }
}

const openRenew = (row: ContractRow) => {
  currentContractId.value = row.id
  renewEndDate.value = null
  renewDialogVisible.value = true
}

const renewContract = async () => {
  if (!renewEndDate.value) {
    ElMessage.warning('新終了日を入力してください')
    return
  }
  saving.value = true
  try {
    await api.post(`/staffing/contracts/${currentContractId.value}/renew`, {
      newEndDate: renewEndDate.value
    })
    ElMessage.success('契約を更新しました')
    renewDialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '更新失敗')
  } finally {
    saving.value = false
  }
}

const openTerminate = (row: ContractRow) => {
  currentContractId.value = row.id
  terminationDate.value = new Date().toISOString().split('T')[0]
  terminationReason.value = ''
  terminateDialogVisible.value = true
}

const terminateContract = async () => {
  saving.value = true
  try {
    await api.post(`/staffing/contracts/${currentContractId.value}/terminate`, {
      terminationDate: terminationDate.value,
      reason: terminationReason.value || null
    })
    ElMessage.success('契約を終了しました')
    terminateDialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '終了失敗')
  } finally {
    saving.value = false
  }
}

const parseTime = (timeStr: string): Date => {
  const [h, m] = timeStr.split(':').map(Number)
  const d = new Date()
  d.setHours(h, m, 0, 0)
  return d
}

const formatTime = (d: Date): string => {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const getContractLabel = (type: string) => {
  const map: Record<string, string> = { dispatch: '派遣', ses: 'SES', contract: '請負' }
  return map[type] || type
}

const getContractTagType = (type: string) => {
  const map: Record<string, string> = { dispatch: 'warning', ses: 'primary', contract: 'success' }
  return map[type] || 'info'
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = { active: '有効', ended: '終了', terminated: '解約', draft: '下書き' }
  return map[status] || status
}

const getStatusTagType = (status: string) => {
  const map: Record<string, string> = { active: 'success', ended: 'info', terminated: 'danger', draft: '' }
  return map[status] || 'info'
}

const getRateTypeLabel = (type: string) => {
  const map: Record<string, string> = { monthly: '月', daily: '日', hourly: '時' }
  return map[type] || type
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return ''
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

onMounted(() => {
  load()
})
</script>

<style scoped>
.contracts-list {
  padding: 20px;
}

.contracts-card {
  border-radius: 8px;
}

.contracts-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.contracts-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.contracts-header__icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.contracts-header__title {
  font-size: 18px;
  font-weight: 600;
}

.contracts-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.contracts-filters__status,
.contracts-filters__type {
  width: 120px;
}

.contracts-filters__client {
  width: 200px;
}

.contract-no {
  font-family: monospace;
  color: var(--el-color-primary);
}

.resource-name {
  font-weight: 500;
}

.resource-code,
.client-code,
.project-code {
  font-size: 12px;
  color: #999;
}

.project-name {
  font-size: 13px;
}

.rate-value {
  font-weight: 600;
}

.rate-type {
  font-size: 12px;
  color: #999;
}

.el-divider {
  margin: 16px 0;
}
</style>

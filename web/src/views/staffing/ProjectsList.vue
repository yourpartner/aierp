<template>
  <div class="projects-list">
    <el-card class="projects-card">
      <template #header>
        <div class="projects-header">
          <div class="projects-header__left">
            <el-icon class="projects-header__icon"><Briefcase /></el-icon>
            <span class="projects-header__title">案件一覧</span>
            <el-tag size="small" type="info" class="projects-header__count">{{ total }}件</el-tag>
          </div>
          <div class="projects-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規登録</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="projects-filters">
        <el-select v-model="statusFilter" clearable placeholder="ステータス" class="projects-filters__status">
          <el-option label="募集中" value="open" />
          <el-option label="マッチング中" value="matching" />
          <el-option label="決定済" value="filled" />
          <el-option label="保留中" value="on_hold" />
          <el-option label="完了" value="closed" />
        </el-select>

        <el-select v-model="contractTypeFilter" clearable placeholder="契約形態" class="projects-filters__type">
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
          class="projects-filters__client"
        >
          <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- 案件テーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="projects-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="案件番号" prop="projectCode" width="100">
          <template #default="{ row }">
            <span class="project-code">{{ row.projectCode }}</span>
          </template>
        </el-table-column>

        <el-table-column label="案件名" prop="projectName" min-width="200">
          <template #default="{ row }">
            <div class="project-name-cell">
              <div class="project-name">{{ row.projectName }}</div>
              <div class="project-category" v-if="row.jobCategory">{{ row.jobCategory }}</div>
            </div>
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

        <el-table-column label="募集/決定" width="90" align="center">
          <template #default="{ row }">
            <span class="headcount">{{ row.filledCount }}/{{ row.headcount }}</span>
          </template>
        </el-table-column>

        <el-table-column label="期間" width="180">
          <template #default="{ row }">
            <div v-if="row.expectedStartDate">
              {{ formatDate(row.expectedStartDate) }} ～ {{ row.expectedEndDate ? formatDate(row.expectedEndDate) : '' }}
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="単価" width="150" align="right">
          <template #default="{ row }">
            <div v-if="row.billingRateMin || row.billingRateMax">
              <span v-if="row.billingRateMin && row.billingRateMax">
                ¥{{ formatNumber(row.billingRateMin) }}～{{ formatNumber(row.billingRateMax) }}
              </span>
              <span v-else-if="row.billingRateMin">¥{{ formatNumber(row.billingRateMin) }}～</span>
              <span v-else>～¥{{ formatNumber(row.billingRateMax) }}</span>
              <span class="rate-type">/{{ getRateTypeLabel(row.rateType) }}</span>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="勤務地" prop="workLocation" width="120">
          <template #default="{ row }">
            {{ row.workLocation || '-' }}
          </template>
        </el-table-column>

        <el-table-column label="ステータス" prop="status" width="100">
          <template #default="{ row }">
            <el-tag :type="getStatusTagType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="優先度" prop="priority" width="80">
          <template #default="{ row }">
            <el-tag :type="getPriorityTagType(row.priority)" size="small" v-if="row.priority !== 'normal'">
              {{ getPriorityLabel(row.priority) }}
            </el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="140" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="onEdit(row)">編集</el-button>
            <el-button link type="success" @click="openMatching(row)">マッチング</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新規登録・編集ダイアログ -->
    <el-dialog 
      v-model="dialogVisible" 
      :title="isEdit ? '案件編集' : '新規案件登録'"
      width="800px"
      destroy-on-close
    >
      <el-form :model="form" label-width="120px" label-position="right">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="案件名" required>
              <el-input v-model="form.projectName" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="職種カテゴリ">
              <el-input v-model="form.jobCategory" />
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
            <el-form-item label="契約形態" required>
              <el-select v-model="form.contractType" style="width: 100%">
                <el-option label="派遣" value="dispatch" />
                <el-option label="SES（準委任）" value="ses" />
                <el-option label="請負" value="contract" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="業務内容">
          <el-input v-model="form.jobDescription" type="textarea" :rows="3" />
        </el-form-item>

        <el-divider>募集条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="募集人数">
              <el-input-number v-model="form.headcount" :min="1" :max="100" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="必要経験年数">
              <el-input-number v-model="form.experienceYearsMin" :min="0" :max="30" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="優先度">
              <el-select v-model="form.priority" style="width: 100%">
                <el-option label="緊急" value="urgent" />
                <el-option label="高" value="high" />
                <el-option label="通常" value="normal" />
                <el-option label="低" value="low" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>期間・勤務条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="開始予定日">
              <el-date-picker v-model="form.expectedStartDate" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="終了予定日">
              <el-date-picker v-model="form.expectedEndDate" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="勤務地">
              <el-input v-model="form.workLocation" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="リモート比率">
              <el-slider v-model="form.remoteWorkRatio" :min="0" :max="100" :step="10" :format-tooltip="(val: number) => `${val}%`" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="勤務曜日">
              <el-input v-model="form.workDays" placeholder="例: 月～金" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="勤務時間">
              <el-input v-model="form.workHours" placeholder="例: 9:00～18:00" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>単価条件</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="単価下限">
              <el-input-number v-model="form.billingRateMin" :min="0" :step="10000" controls-position="right" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="単価上限">
              <el-input-number v-model="form.billingRateMax" :min="0" :step="10000" controls-position="right" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="単価種別">
              <el-select v-model="form.rateType" style="width: 100%">
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

    <!-- マッチングダイアログ -->
    <el-dialog v-model="matchingDialogVisible" title="候補者マッチング" width="900px">
      <div class="matching-header" v-if="currentProject">
        <h4>{{ currentProject.projectName }}</h4>
        <el-tag>{{ currentProject.filledCount }}/{{ currentProject.headcount }}名決定</el-tag>
      </div>

      <el-divider>登録済み候補者</el-divider>
      <el-table :data="candidates" border size="small" v-loading="candidatesLoading">
        <el-table-column label="コード" prop="resourceCode" width="90" />
        <el-table-column label="氏名" prop="displayName" width="120" />
        <el-table-column label="種別" prop="resourceType" width="90">
          <template #default="{ row }">
            <el-tag size="small">{{ getResourceTypeLabel(row.resourceType) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="提案単価" prop="proposedRate" width="100" align="right">
          <template #default="{ row }">
            {{ row.proposedRate ? `¥${formatNumber(row.proposedRate)}` : '-' }}
          </template>
        </el-table-column>
        <el-table-column label="ステータス" prop="status" width="100">
          <template #default="{ row }">
            <el-tag :type="getCandidateStatusType(row.status)" size="small">
              {{ getCandidateStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="180">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="updateCandidateStatus(row, 'interview_scheduled')">面談設定</el-button>
            <el-button link type="success" size="small" @click="updateCandidateStatus(row, 'accepted')">決定</el-button>
            <el-button link type="danger" size="small" @click="updateCandidateStatus(row, 'rejected')">不採用</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-divider>候補者追加</el-divider>
      <div class="add-candidate">
        <el-select 
          v-model="newCandidateResourceId"
          filterable
          remote
          :remote-method="searchResources"
          placeholder="リソースを検索"
          style="width: 300px"
        >
          <el-option v-for="opt in resourceOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <el-input-number v-model="newCandidateRate" :min="0" :step="10000" placeholder="提案単価" style="width: 150px" />
        <el-button type="primary" @click="addCandidate">追加</el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Briefcase, Plus, Search } from '@element-plus/icons-vue'
import api from '../../api'

interface ProjectRow {
  id: string
  projectCode: string
  projectName: string
  jobCategory?: string
  contractType: string
  headcount: number
  filledCount: number
  expectedStartDate?: string
  expectedEndDate?: string
  workLocation?: string
  billingRateMin?: number
  billingRateMax?: number
  rateType: string
  status: string
  priority: string
  clientPartnerId: string
  clientCode?: string
  clientName?: string
}

interface Candidate {
  id: string
  resourceId: string
  resourceCode: string
  displayName: string
  resourceType: string
  proposedRate?: number
  status: string
}

const loading = ref(false)
const rows = ref<ProjectRow[]>([])
const total = ref(0)
const statusFilter = ref('')
const contractTypeFilter = ref('')
const clientFilter = ref('')
const clientOptions = ref<{ label: string; value: string }[]>([])

const dialogVisible = ref(false)
const isEdit = ref(false)
const saving = ref(false)

const matchingDialogVisible = ref(false)
const currentProject = ref<ProjectRow | null>(null)
const candidates = ref<Candidate[]>([])
const candidatesLoading = ref(false)
const newCandidateResourceId = ref('')
const newCandidateRate = ref<number | null>(null)
const resourceOptions = ref<{ label: string; value: string }[]>([])

const form = reactive({
  id: '',
  projectName: '',
  jobCategory: '',
  jobDescription: '',
  clientPartnerId: '',
  contractType: 'ses',
  headcount: 1,
  experienceYearsMin: null as number | null,
  expectedStartDate: null as string | null,
  expectedEndDate: null as string | null,
  workLocation: '',
  workDays: '',
  workHours: '',
  remoteWorkRatio: 0,
  billingRateMin: null as number | null,
  billingRateMax: null as number | null,
  rateType: 'monthly',
  priority: 'normal',
  notes: ''
})

const load = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (statusFilter.value) params.status = statusFilter.value
    if (contractTypeFilter.value) params.contractType = contractTypeFilter.value
    if (clientFilter.value) params.clientId = clientFilter.value
    
    const res = await api.get('/staffing/projects', { params })
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

const openNew = () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    projectName: '',
    jobCategory: '',
    jobDescription: '',
    clientPartnerId: '',
    contractType: 'ses',
    headcount: 1,
    experienceYearsMin: null,
    expectedStartDate: null,
    expectedEndDate: null,
    workLocation: '',
    workDays: '',
    workHours: '',
    remoteWorkRatio: 0,
    billingRateMin: null,
    billingRateMax: null,
    rateType: 'monthly',
    priority: 'normal',
    notes: ''
  })
  dialogVisible.value = true
}

const onEdit = async (row: ProjectRow) => {
  isEdit.value = true
  try {
    const res = await api.get(`/staffing/projects/${row.id}`)
    const data = res.data
    Object.assign(form, {
      id: data.id,
      projectName: data.project_name,
      jobCategory: data.job_category,
      jobDescription: data.job_description,
      clientPartnerId: data.client_partner_id,
      contractType: data.contract_type,
      headcount: data.headcount,
      experienceYearsMin: data.experience_years_min,
      expectedStartDate: data.expected_start_date,
      expectedEndDate: data.expected_end_date,
      workLocation: data.work_location,
      workDays: data.work_days,
      workHours: data.work_hours,
      remoteWorkRatio: data.remote_work_ratio || 0,
      billingRateMin: data.billing_rate_min ? Number(data.billing_rate_min) : null,
      billingRateMax: data.billing_rate_max ? Number(data.billing_rate_max) : null,
      rateType: data.rate_type || 'monthly',
      priority: data.priority || 'normal',
      notes: data.notes
    })
    // 顧客選択肢に追加
    if (data.client_partner_id && data.clientName) {
      clientOptions.value = [{ label: `${data.clientCode} - ${data.clientName}`, value: data.client_partner_id }]
    }
    dialogVisible.value = true
  } catch (e: any) {
    ElMessage.error('詳細取得失敗')
  }
}

const save = async () => {
  if (!form.projectName) {
    ElMessage.warning('案件名を入力してください')
    return
  }
  if (!form.clientPartnerId) {
    ElMessage.warning('顧客を選択してください')
    return
  }
  saving.value = true
  try {
    const payload = {
      projectName: form.projectName,
      jobCategory: form.jobCategory || null,
      jobDescription: form.jobDescription || null,
      clientPartnerId: form.clientPartnerId,
      contractType: form.contractType,
      headcount: form.headcount,
      experienceYearsMin: form.experienceYearsMin,
      expectedStartDate: form.expectedStartDate,
      expectedEndDate: form.expectedEndDate,
      workLocation: form.workLocation || null,
      workDays: form.workDays || null,
      workHours: form.workHours || null,
      remoteWorkRatio: form.remoteWorkRatio,
      billingRateMin: form.billingRateMin,
      billingRateMax: form.billingRateMax,
      rateType: form.rateType,
      priority: form.priority,
      notes: form.notes || null
    }
    
    if (isEdit.value) {
      await api.put(`/staffing/projects/${form.id}`, payload)
      ElMessage.success('更新しました')
    } else {
      await api.post('/staffing/projects', payload)
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

const openMatching = async (row: ProjectRow) => {
  currentProject.value = row
  matchingDialogVisible.value = true
  await loadCandidates(row.id)
}

const loadCandidates = async (projectId: string) => {
  candidatesLoading.value = true
  try {
    const res = await api.get(`/staffing/projects/${projectId}/candidates`)
    candidates.value = res.data.data || []
  } catch (e) {
    console.error(e)
  } finally {
    candidatesLoading.value = false
  }
}

const searchResources = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/staffing/resources', { params: { keyword: query, status: 'available' } })
    resourceOptions.value = (res.data.data || []).map((r: any) => ({
      label: `${r.resourceCode} - ${r.displayName}`,
      value: r.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const addCandidate = async () => {
  if (!newCandidateResourceId.value || !currentProject.value) {
    ElMessage.warning('リソースを選択してください')
    return
  }
  try {
    await api.post(`/staffing/projects/${currentProject.value.id}/candidates`, {
      resourceId: newCandidateResourceId.value,
      proposedRate: newCandidateRate.value
    })
    ElMessage.success('候補者を追加しました')
    newCandidateResourceId.value = ''
    newCandidateRate.value = null
    await loadCandidates(currentProject.value.id)
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '追加失敗')
  }
}

const updateCandidateStatus = async (candidate: Candidate, newStatus: string) => {
  try {
    await api.put(`/staffing/projects/candidates/${candidate.id}`, { status: newStatus })
    ElMessage.success('更新しました')
    if (currentProject.value) {
      await loadCandidates(currentProject.value.id)
      load()
    }
  } catch (e: any) {
    ElMessage.error('更新失敗')
  }
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
  const map: Record<string, string> = { open: '募集中', matching: 'マッチング中', filled: '決定済', on_hold: '保留中', closed: '完了', cancelled: 'キャンセル' }
  return map[status] || status
}

const getStatusTagType = (status: string) => {
  const map: Record<string, string> = { open: 'success', matching: 'warning', filled: 'primary', on_hold: 'info', closed: '', cancelled: 'danger' }
  return map[status] || 'info'
}

const getPriorityLabel = (p: string) => {
  const map: Record<string, string> = { urgent: '緊急', high: '高', normal: '通常', low: '低' }
  return map[p] || p
}

const getPriorityTagType = (p: string) => {
  const map: Record<string, string> = { urgent: 'danger', high: 'warning', normal: 'info', low: '' }
  return map[p] || 'info'
}

const getRateTypeLabel = (type: string) => {
  const map: Record<string, string> = { monthly: '月', daily: '日', hourly: '時' }
  return map[type] || type
}

const getResourceTypeLabel = (type: string) => {
  const map: Record<string, string> = { employee: '自社', freelancer: '個人', bp: 'BP', candidate: '商談中' }
  return map[type] || type
}

const getCandidateStatusLabel = (status: string) => {
  const map: Record<string, string> = { proposed: '提案中', client_review: '顧客レビュー', interview_scheduled: '面談予定', interview_done: '面談済', offered: 'オファー中', accepted: '決定', rejected: '不採用', withdrawn: '辞退' }
  return map[status] || status
}

const getCandidateStatusType = (status: string) => {
  const map: Record<string, string> = { proposed: 'info', client_review: 'warning', interview_scheduled: 'warning', interview_done: '', offered: 'primary', accepted: 'success', rejected: 'danger', withdrawn: '' }
  return map[status] || 'info'
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
.projects-list {
  padding: 20px;
}

.projects-card {
  border-radius: 8px;
}

.projects-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.projects-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.projects-header__icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.projects-header__title {
  font-size: 18px;
  font-weight: 600;
}

.projects-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.projects-filters__status,
.projects-filters__type {
  width: 140px;
}

.projects-filters__client {
  width: 200px;
}

.project-code {
  font-family: monospace;
  color: var(--el-color-primary);
}

.project-name-cell {
  line-height: 1.4;
}

.project-name {
  font-weight: 500;
}

.project-category {
  font-size: 12px;
  color: #999;
}

.client-code {
  font-size: 12px;
  color: #999;
}

.headcount {
  font-weight: 600;
}

.rate-type {
  font-size: 12px;
  color: #999;
}

.matching-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
}

.matching-header h4 {
  margin: 0;
}

.add-candidate {
  display: flex;
  gap: 12px;
  align-items: center;
}

.el-divider {
  margin: 16px 0;
}
</style>


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
      width="860px"
      :show-close="false"
      append-to-body
      destroy-on-close
      class="project-form-dialog"
    >
      <template #header></template>
      <el-card class="project-form-card">
        <template #header>
          <div class="project-dialog-header">
            <span class="project-dialog-title">{{ isEdit ? '案件編集' : '新規案件登録' }}</span>
            <div class="header-actions">
              <el-button @click="dialogVisible = false">キャンセル</el-button>
              <el-button type="primary" @click="save" :loading="saving">保存</el-button>
            </div>
          </div>
        </template>
        <el-form :model="form" label-width="100px" label-position="right" class="project-form">
          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="案件名" required>
                <el-input v-model="form.projectName" placeholder="案件名を入力" />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="職種カテゴリ">
                <el-input v-model="form.jobCategory" placeholder="例: Java開発、インフラ構築" />
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
                  allow-create
                  default-first-option
                  :remote-method="searchClients"
                  placeholder="顧客を選択または入力"
                  style="width: 100%"
                  @change="onClientChange"
                >
                  <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                </el-select>
                <div class="form-hint" v-if="!form.clientPartnerId || isManualClientName">
                  <el-icon><InfoFilled /></el-icon>
                  <span>未登録の顧客名を直接入力できます</span>
                </div>
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
            <el-input v-model="form.jobDescription" type="textarea" :rows="3" placeholder="業務内容の詳細を入力" />
          </el-form-item>

          <el-divider content-position="left">募集条件</el-divider>

          <el-row :gutter="20">
            <el-col :span="8">
              <el-form-item label="募集人数">
                <el-input-number v-model="form.headcount" :min="1" :max="100" style="width: 100%" />
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="必要経験年数">
                <el-input-number v-model="form.experienceYearsMin" :min="0" :max="30" style="width: 100%" />
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

          <el-divider content-position="left">期間・勤務条件</el-divider>

          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="開始予定日">
                <el-date-picker v-model="form.expectedStartDate" type="date" style="width: 100%" placeholder="選択" />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="終了予定日">
                <el-date-picker v-model="form.expectedEndDate" type="date" style="width: 100%" placeholder="選択" />
              </el-form-item>
            </el-col>
          </el-row>

          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="勤務地">
                <el-input v-model="form.workLocation" placeholder="例: 東京都港区" />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="リモート比率">
                <div class="slider-with-value">
                  <el-slider v-model="form.remoteWorkRatio" :min="0" :max="100" :step="10" />
                  <span class="slider-value">{{ form.remoteWorkRatio }}%</span>
                </div>
              </el-form-item>
            </el-col>
          </el-row>

          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="勤務曜日">
                <el-input v-model="form.workDays" placeholder="例: 月～金" />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="勤務時間">
                <el-input v-model="form.workHours" placeholder="例: 9:00～18:00" />
              </el-form-item>
            </el-col>
          </el-row>

          <el-divider content-position="left">単価条件</el-divider>

          <el-row :gutter="20">
            <el-col :span="8">
              <el-form-item label="単価下限">
                <el-input-number v-model="form.billingRateMin" :min="0" :step="10000" controls-position="right" style="width: 100%" placeholder="円" />
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="単価上限">
                <el-input-number v-model="form.billingRateMax" :min="0" :step="10000" controls-position="right" style="width: 100%" placeholder="円" />
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
            <el-input v-model="form.notes" type="textarea" :rows="2" placeholder="その他の備考事項" />
          </el-form-item>
        </el-form>
      </el-card>
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
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Briefcase, Plus, Search, InfoFilled } from '@element-plus/icons-vue'
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
  clientName: '', // 手工输入的客户名（潜在客户）
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

// 判断是否为手工输入的客户名（非已有客户ID）
const isManualClientName = computed(() => {
  if (!form.clientPartnerId) return false
  // 如果选择的值不在已有选项中，则为手工输入
  return !clientOptions.value.some(opt => opt.value === form.clientPartnerId)
})

// 客户选择变化时的处理
const onClientChange = (val: string) => {
  const existingOpt = clientOptions.value.find(opt => opt.value === val)
  if (existingOpt) {
    // 选择了已有客户，清空手工输入的客户名
    form.clientName = ''
  } else {
    // 手工输入的客户名
    form.clientName = val
    form.clientPartnerId = '' // 清空关联的客户ID
  }
}

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
  try {
    // 使用正确的搜索 API
    const where: any[] = [{ field: 'flag_customer', op: 'eq', value: true }]
    if (query && query.trim()) {
      where.push({ json: 'name', op: 'contains', value: query.trim() })
    }
    const res = await api.post('/objects/businesspartner/search', {
      page: 1,
      pageSize: 50,
      where,
      orderBy: [{ field: 'name', direction: 'asc' }]
    })
    clientOptions.value = (res.data?.data || []).map((bp: any) => {
      const code = bp.partner_code || bp.payload?.code || ''
      const name = bp.payload?.name || bp.name || ''
      return {
        label: name ? `${name} (${code})` : code,
        value: bp.id
      }
    })
  } catch (e) {
    console.error('Failed to search clients:', e)
    clientOptions.value = []
  }
}

// 加载默认客户列表（用于弹窗打开时）
const loadDefaultClients = async () => {
  await searchClients('')
}

const openNew = async () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    projectName: '',
    jobCategory: '',
    jobDescription: '',
    clientPartnerId: '',
    clientName: '',
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
  // 加载默认客户列表
  await loadDefaultClients()
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
      clientPartnerId: data.client_partner_id || '',
      clientName: data.client_name || '',
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
    // 顧客選択肢に追加（已有客户）
    if (data.client_partner_id && data.clientName) {
      clientOptions.value = [{ label: `${data.clientCode || ''} - ${data.clientName}`.replace(/^\s*-\s*/, ''), value: data.client_partner_id }]
    } else if (data.client_name && !data.client_partner_id) {
      // 手工输入的客户名，设置到 clientPartnerId 以便显示
      form.clientPartnerId = data.client_name
      clientOptions.value = []
    } else {
      clientOptions.value = []
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
  // 检查是否有客户（已有客户或手工输入的客户名）
  const hasClient = form.clientPartnerId || form.clientName
  if (!hasClient) {
    ElMessage.warning('顧客を選択または入力してください')
    return
  }
  saving.value = true
  try {
    // 判断是选择的已有客户还是手工输入的客户名
    const selectedClient = clientOptions.value.find(opt => opt.value === form.clientPartnerId)
    
    const payload = {
      projectName: form.projectName,
      jobCategory: form.jobCategory || null,
      jobDescription: form.jobDescription || null,
      // 如果是已有客户，使用 clientPartnerId；否则只传 clientName
      clientPartnerId: selectedClient ? form.clientPartnerId : null,
      clientName: selectedClient ? null : (form.clientName || form.clientPartnerId), // 手工输入时 clientPartnerId 存的是输入的文本
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
  try {
    const params: Record<string, any> = { status: 'available' }
    if (query && query.trim()) {
      params.keyword = query.trim()
    }
    const res = await api.get('/staffing/resources', { params })
    resourceOptions.value = (res.data.data || []).map((r: any) => ({
      label: `${r.resourceCode} - ${r.displayName}`,
      value: r.id
    }))
  } catch (e) {
    console.error('Failed to search resources:', e)
    resourceOptions.value = []
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

.project-form {
  padding: 8px 0;
}

.project-form .el-form-item {
  margin-bottom: 18px;
}

.project-form .el-divider {
  margin: 20px 0 16px;
}

.project-form .el-divider__text {
  font-size: 13px;
  font-weight: 500;
  color: #606266;
}

.form-hint {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: 4px;
  font-size: 12px;
  color: #909399;
}

.form-hint .el-icon {
  font-size: 14px;
}

.slider-with-value {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
}

.slider-with-value :deep(.el-slider) {
  flex: 1;
  min-width: 120px;
}

.slider-value {
  min-width: 45px;
  font-size: 13px;
  color: #606266;
  text-align: right;
  flex-shrink: 0;
}

/* 案件弹窗样式 - 与财务会计保持一致 */
.project-form-card {
  max-width: 100%;
  margin: 0;
  border: none;
  box-shadow: none;
}

.project-dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  border-bottom: 1px solid var(--color-divider, #ebeef5);
}

.project-dialog-title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>

<style>
/* 案件弹窗全局样式 - 与财务会计保持一致 */
.el-dialog.project-form-dialog {
  background: transparent !important;
  box-shadow: none !important;
  border: none !important;
  padding: 0 !important;
}

.el-dialog.project-form-dialog .el-dialog__header {
  display: none !important;
}

.el-dialog.project-form-dialog .el-dialog__body {
  padding: 0 !important;
  background: transparent !important;
}

/* 覆盖 el-card__header 的 padding，让分隔线延伸到边缘 */
.project-form-card.el-card .el-card__header {
  padding: 0 !important;
}

.project-form-card.el-card .el-card__body {
  padding: 20px !important;
}
</style>



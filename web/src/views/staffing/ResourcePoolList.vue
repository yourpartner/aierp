<template>
  <div class="resource-pool-list">
    <el-card class="resource-card">
      <template #header>
        <div class="resource-header">
          <div class="resource-header__left">
            <el-icon class="resource-header__icon"><User /></el-icon>
            <span class="resource-header__title">リソースプール</span>
            <el-tag size="small" type="info" class="resource-header__count">{{ total }}名</el-tag>
          </div>
          <div class="resource-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規登録</span>
            </el-button>
            <el-button plain @click="openFromEmployee">
              <el-icon><Connection /></el-icon>
              <span>社員から追加</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="resource-filters">
        <el-input
          v-model="keyword"
          placeholder="氏名／リソースコードで検索"
          clearable
          class="resource-filters__keyword"
          @keyup.enter="load"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>

        <el-select v-model="resourceType" clearable placeholder="リソース種別" class="resource-filters__type">
          <el-option label="自社社員" value="employee" />
          <el-option label="個人事業主" value="freelancer" />
          <el-option label="BP要員" value="bp" />
          <el-option label="商談中" value="candidate" />
        </el-select>

        <el-select v-model="availabilityStatus" clearable placeholder="稼働状態" class="resource-filters__status">
          <el-option label="稼働可能" value="available" />
          <el-option label="稼働中" value="assigned" />
          <el-option label="一部稼働可" value="partially_available" />
          <el-option label="稼働不可" value="unavailable" />
        </el-select>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- リソーステーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="resource-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="コード" prop="resourceCode" width="100" sortable>
          <template #default="{ row }">
            <span class="resource-code">{{ row.resourceCode }}</span>
          </template>
        </el-table-column>

        <el-table-column label="氏名" prop="displayName" min-width="140">
          <template #default="{ row }">
            <div class="resource-name-cell">
              <div class="resource-name">{{ row.displayName }}</div>
              <div class="resource-name-kana" v-if="row.displayNameKana">{{ row.displayNameKana }}</div>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="種別" prop="resourceType" width="100">
          <template #default="{ row }">
            <el-tag :type="getTypeTagType(row.resourceType)" size="small">
              {{ getTypeLabel(row.resourceType) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="主要スキル" prop="primarySkillCategory" width="120">
          <template #default="{ row }">
            <span>{{ row.primarySkillCategory || '-' }}</span>
          </template>
        </el-table-column>

        <el-table-column label="経験年数" prop="experienceYears" width="90" align="center">
          <template #default="{ row }">
            <span v-if="row.experienceYears">{{ row.experienceYears }}年</span>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="単価" width="120" align="right">
          <template #default="{ row }">
            <div v-if="row.defaultBillingRate">
              <span class="rate-value">¥{{ formatNumber(row.defaultBillingRate) }}</span>
              <span class="rate-type">/{{ getRateTypeLabel(row.rateType) }}</span>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="稼働状態" prop="availabilityStatus" width="100">
          <template #default="{ row }">
            <el-tag :type="getStatusTagType(row.availabilityStatus)" size="small">
              {{ getStatusLabel(row.availabilityStatus) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="連絡先" min-width="160">
          <template #default="{ row }">
            <div v-if="row.email" class="contact-cell">
              <el-icon><Message /></el-icon>
              <span>{{ row.email }}</span>
            </div>
            <div v-if="row.phone" class="contact-cell">
              <el-icon><Phone /></el-icon>
              <span>{{ row.phone }}</span>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="100" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="onEdit(row)">
              <el-icon><Edit /></el-icon>
              編集
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新規登録・編集ダイアログ -->
    <el-dialog 
      v-model="dialogVisible" 
      :title="isEdit ? 'リソース編集' : '新規リソース登録'"
      width="700px"
      destroy-on-close
    >
      <el-form :model="form" label-width="120px" label-position="right">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="リソース種別" required>
              <el-select v-model="form.resourceType" style="width: 100%">
                <el-option label="自社社員" value="employee" />
                <el-option label="個人事業主" value="freelancer" />
                <el-option label="BP要員" value="bp" />
                <el-option label="商談中" value="candidate" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="コード" v-if="isEdit">
              <el-input v-model="form.resourceCode" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="氏名" required>
              <el-input v-model="form.displayName" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="氏名（カナ）">
              <el-input v-model="form.displayNameKana" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20" v-if="form.resourceType === 'bp'">
          <el-col :span="24">
            <el-form-item label="所属BP会社">
              <el-select 
                v-model="form.supplierPartnerId" 
                filterable 
                remote 
                :remote-method="searchBpPartners"
                placeholder="協力会社を選択"
                style="width: 100%"
              >
                <el-option v-for="opt in bpOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="メール">
              <el-input v-model="form.email" type="email" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="電話番号">
              <el-input v-model="form.phone" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>スキル・経験</el-divider>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="主要スキル">
              <el-input v-model="form.primarySkillCategory" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="経験年数">
              <el-input-number v-model="form.experienceYears" :min="0" :max="50" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>単価情報</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="請求単価">
              <el-input-number v-model="form.defaultBillingRate" :min="0" :step="10000" controls-position="right" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="原価単価">
              <el-input-number v-model="form.defaultCostRate" :min="0" :step="10000" controls-position="right" />
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

        <el-divider>稼働状態</el-divider>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="稼働状態">
              <el-select v-model="form.availabilityStatus" style="width: 100%">
                <el-option label="稼働可能" value="available" />
                <el-option label="稼働中" value="assigned" />
                <el-option label="一部稼働可" value="partially_available" />
                <el-option label="稼働不可" value="unavailable" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="稼働可能日">
              <el-date-picker v-model="form.availableFrom" type="date" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="備考">
          <el-input v-model="form.internalNotes" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="save" :loading="saving">保存</el-button>
      </template>
    </el-dialog>

    <!-- 社員選択ダイアログ -->
    <el-dialog 
      v-model="employeeDialogVisible" 
      width="520px"
      destroy-on-close
      :show-close="false"
      class="employee-select-dialog"
    >
      <div class="employee-dialog-header">
        <div class="employee-dialog-header__left">
          <el-icon class="employee-dialog-header__icon"><Connection /></el-icon>
          <span class="employee-dialog-header__title">社員から追加</span>
        </div>
        <div class="employee-dialog-header__right">
          <el-button size="small" @click="employeeDialogVisible = false">キャンセル</el-button>
          <el-button size="small" type="primary" @click="addFromEmployee" :loading="saving">追加</el-button>
        </div>
      </div>
      <el-form label-width="60px" label-position="right" class="employee-select-form">
        <el-form-item label="社員" required>
          <el-select
            v-model="selectedEmployeeId"
            filterable
            remote
            :remote-method="searchEmployees"
            placeholder="社員を選択"
            style="width: 260px"
            :loading="empLoading"
          >
            <el-option v-for="opt in employeeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>
        </el-form-item>
        <div class="form-hint">
          <el-icon><InfoFilled /></el-icon>
          <span>選択後、氏名等が自動入力されます</span>
        </div>
      </el-form>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { User, Plus, Search, Edit, Message, Phone, Connection, InfoFilled } from '@element-plus/icons-vue'
import api from '../../api'

interface ResourceRow {
  id: string
  resourceCode: string
  displayName: string
  displayNameKana?: string
  resourceType: string
  email?: string
  phone?: string
  primarySkillCategory?: string
  experienceYears?: number
  defaultBillingRate?: number
  defaultCostRate?: number
  rateType: string
  availabilityStatus: string
  availableFrom?: string
}

const loading = ref(false)
const rows = ref<ResourceRow[]>([])
const total = ref(0)
const keyword = ref('')
const resourceType = ref('')
const availabilityStatus = ref('')

const dialogVisible = ref(false)
const isEdit = ref(false)
const saving = ref(false)

const employeeDialogVisible = ref(false)
const selectedEmployeeId = ref('')
const employeeOptions = ref<{ label: string; value: string }[]>([])
const empLoading = ref(false)

const bpOptions = ref<{ label: string; value: string }[]>([])

const form = reactive({
  id: '',
  resourceCode: '',
  displayName: '',
  displayNameKana: '',
  resourceType: 'freelancer',
  employeeId: '',
  supplierPartnerId: '',
  email: '',
  phone: '',
  primarySkillCategory: '',
  experienceYears: null as number | null,
  defaultBillingRate: null as number | null,
  defaultCostRate: null as number | null,
  rateType: 'monthly',
  availabilityStatus: 'available',
  availableFrom: null as string | null,
  internalNotes: ''
})

const load = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (keyword.value) params.keyword = keyword.value
    if (resourceType.value) params.type = resourceType.value
    if (availabilityStatus.value) params.status = availabilityStatus.value
    
    const res = await api.get('/staffing/resources', { params })
    rows.value = res.data.data || []
    total.value = res.data.total || rows.value.length
  } catch (e: any) {
    ElMessage.error(e.message || '読み込み失敗')
  } finally {
    loading.value = false
  }
}

const openNew = () => {
  isEdit.value = false
  Object.assign(form, {
    id: '',
    resourceCode: '',
    displayName: '',
    displayNameKana: '',
    resourceType: 'freelancer',
    employeeId: '',
    supplierPartnerId: '',
    email: '',
    phone: '',
    primarySkillCategory: '',
    experienceYears: null,
    defaultBillingRate: null,
    defaultCostRate: null,
    rateType: 'monthly',
    availabilityStatus: 'available',
    availableFrom: null,
    internalNotes: ''
  })
  dialogVisible.value = true
}

const onEdit = async (row: ResourceRow) => {
  isEdit.value = true
  try {
    const res = await api.get(`/staffing/resources/${row.id}`)
    const data = res.data
    Object.assign(form, {
      id: data.id,
      resourceCode: data.resource_code,
      displayName: data.display_name,
      displayNameKana: data.display_name_kana,
      resourceType: data.resource_type,
      employeeId: data.employee_id,
      supplierPartnerId: data.supplier_partner_id,
      email: data.email,
      phone: data.phone,
      primarySkillCategory: data.primary_skill_category,
      experienceYears: data.experience_years,
      defaultBillingRate: data.default_billing_rate ? Number(data.default_billing_rate) : null,
      defaultCostRate: data.default_cost_rate ? Number(data.default_cost_rate) : null,
      rateType: data.rate_type || 'monthly',
      availabilityStatus: data.availability_status,
      availableFrom: data.available_from,
      internalNotes: data.internal_notes
    })
    dialogVisible.value = true
  } catch (e: any) {
    ElMessage.error('詳細取得失敗')
  }
}

const save = async () => {
  if (!form.displayName) {
    ElMessage.warning('氏名を入力してください')
    return
  }
  saving.value = true
  try {
    const payload = {
      displayName: form.displayName,
      displayNameKana: form.displayNameKana || null,
      resourceType: form.resourceType,
      supplierPartnerId: form.supplierPartnerId || null,
      email: form.email || null,
      phone: form.phone || null,
      primarySkillCategory: form.primarySkillCategory || null,
      experienceYears: form.experienceYears,
      defaultBillingRate: form.defaultBillingRate,
      defaultCostRate: form.defaultCostRate,
      rateType: form.rateType,
      availabilityStatus: form.availabilityStatus,
      availableFrom: form.availableFrom,
      internalNotes: form.internalNotes || null
    }
    
    if (isEdit.value) {
      await api.put(`/staffing/resources/${form.id}`, payload)
      ElMessage.success('更新しました')
    } else {
      await api.post('/staffing/resources', payload)
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

const openFromEmployee = async () => {
  selectedEmployeeId.value = ''
  employeeOptions.value = []
  employeeDialogVisible.value = true
  // 打开弹窗时加载默认员工列表
  await loadDefaultEmployees()
}

const loadDefaultEmployees = async () => {
  empLoading.value = true
  try {
    const res = await api.get('/hr/employees', { params: { limit: 50 } })
    employeeOptions.value = (res.data.data || []).map((e: any) => ({
      label: `${e.employee_code} - ${e.payload?.nameKanji || e.payload?.name || ''}`,
      value: e.id
    }))
  } catch (e) {
    console.error(e)
  } finally {
    empLoading.value = false
  }
}

const searchEmployees = async (query: string) => {
  empLoading.value = true
  try {
    const params: Record<string, any> = { limit: 50 }
    if (query && query.trim()) {
      params.keyword = query.trim()
    }
    const res = await api.get('/hr/employees', { params })
    employeeOptions.value = (res.data.data || []).map((e: any) => ({
      label: `${e.employee_code} - ${e.payload?.nameKanji || e.payload?.name || ''}`,
      value: e.id
    }))
  } catch (e) {
    console.error(e)
  } finally {
    empLoading.value = false
  }
}

const addFromEmployee = async () => {
  if (!selectedEmployeeId.value) {
    ElMessage.warning('社員を選択してください')
    return
  }
  saving.value = true
  try {
    const res = await api.post(`/staffing/resources/from-employee/${selectedEmployeeId.value}`)
    if (res.data.alreadyExists) {
      ElMessage.info('この社員は既にリソースとして登録されています')
    } else {
      ElMessage.success('リソースを追加しました')
    }
    employeeDialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message || '追加失敗')
  } finally {
    saving.value = false
  }
}

const searchBpPartners = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/businesspartners', { params: { keyword: query, flag_vendor: true } })
    bpOptions.value = (res.data.data || []).map((bp: any) => ({
      label: `${bp.partner_code} - ${bp.payload?.name || ''}`,
      value: bp.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const getTypeLabel = (type: string) => {
  const map: Record<string, string> = {
    employee: '自社社員',
    freelancer: '個人事業主',
    bp: 'BP要員',
    candidate: '商談中'
  }
  return map[type] || type
}

const getTypeTagType = (type: string) => {
  const map: Record<string, string> = {
    employee: 'primary',
    freelancer: 'success',
    bp: 'warning',
    candidate: 'info'
  }
  return map[type] || 'info'
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    available: '稼働可能',
    assigned: '稼働中',
    partially_available: '一部稼働可',
    unavailable: '稼働不可'
  }
  return map[status] || status
}

const getStatusTagType = (status: string) => {
  const map: Record<string, string> = {
    available: 'success',
    assigned: 'primary',
    partially_available: 'warning',
    unavailable: 'danger'
  }
  return map[status] || 'info'
}

const getRateTypeLabel = (type: string) => {
  const map: Record<string, string> = {
    monthly: '月',
    daily: '日',
    hourly: '時'
  }
  return map[type] || type
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

onMounted(() => {
  load()
})
</script>

<style scoped>
.resource-pool-list {
  padding: 20px;
}

.resource-card {
  border-radius: 8px;
}

.resource-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.resource-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.resource-header__icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.resource-header__title {
  font-size: 18px;
  font-weight: 600;
}

.resource-header__right {
  display: flex;
  gap: 10px;
}

.resource-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.resource-filters__keyword {
  width: 240px;
}

.resource-filters__type,
.resource-filters__status {
  width: 140px;
}

.resource-table {
  width: 100%;
}

.resource-code {
  font-family: monospace;
  color: var(--el-color-primary);
}

.resource-name-cell {
  line-height: 1.4;
}

.resource-name {
  font-weight: 500;
}

.resource-name-kana {
  font-size: 12px;
  color: #999;
}

.rate-value {
  font-weight: 600;
}

.rate-type {
  font-size: 12px;
  color: #999;
}

.contact-cell {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #666;
}

.el-divider {
  margin: 16px 0;
}

/* 社员选择弹窗样式 */
:deep(.el-dialog.employee-select-dialog) {
  border-radius: 12px !important;
  overflow: hidden !important;
  background: #fff !important;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15) !important;
  width: 520px !important;
  max-width: 520px !important;
}
:deep(.el-dialog.employee-select-dialog .el-dialog__header) {
  display: none !important;
}
:deep(.el-dialog.employee-select-dialog .el-dialog__body) {
  padding: 20px !important;
  overflow: visible !important;
  width: 100% !important;
  box-sizing: border-box !important;
}

.employee-dialog-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
  padding-bottom: 16px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  width: 100%;
  box-sizing: border-box;
}

.employee-dialog-header__left {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.employee-dialog-header__icon {
  font-size: 18px;
  color: var(--el-color-primary);
}

.employee-dialog-header__title {
  font-size: 15px;
  font-weight: 600;
  white-space: nowrap;
}

.employee-dialog-header__right {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}

.employee-select-form {
  padding: 0;
  width: 100%;
  box-sizing: border-box;
}

.form-hint {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 10px 12px;
  background: var(--el-fill-color-light);
  border-radius: 6px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-top: 8px;
  width: 100%;
  box-sizing: border-box;
}

.form-hint .el-icon {
  flex-shrink: 0;
  margin-top: 1px;
  color: var(--el-color-primary);
}
</style>


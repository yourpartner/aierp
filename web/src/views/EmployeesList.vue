<template>
  <div class="employees-list">
    <el-card class="employees-card">
      <template #header>
        <div class="employees-header">
          <div class="employees-header__left">
            <el-icon class="employees-header__icon"><User /></el-icon>
            <span class="employees-header__title">社員一覧</span>
            <el-tag size="small" type="info" class="employees-header__count">{{ total }}名</el-tag>
          </div>
          <div class="employees-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規登録</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="employees-filters">
            <el-input
              v-model="keyword"
          placeholder="氏名／社員コードで検索"
          clearable
          class="employees-filters__keyword"
          @keyup.enter="load"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>

            <el-select
              v-model="deptId"
              filterable
              remote
              clearable
              reserve-keyword
              placeholder="所属部門"
          class="employees-filters__dept"
              :remote-method="searchDepartments"
              :loading="deptLoading"
            >
              <el-option v-for="opt in deptOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>

        <el-select
          v-model="employmentTypeFilter"
          clearable
          placeholder="雇用区分"
          class="employees-filters__type"
        >
          <el-option v-for="opt in employmentTypeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>

        <el-radio-group v-model="statusFilter" class="employees-filters__status">
          <el-radio-button label="active">
            <el-icon><Check /></el-icon>
            在籍
          </el-radio-button>
          <el-radio-button label="resigned">
            <el-icon><Close /></el-icon>
            退職
          </el-radio-button>
          <el-radio-button label="all">全員</el-radio-button>
            </el-radio-group>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
          </div>

      <!-- 社員テーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="employees-table"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="社員コード" prop="employee_code" width="110" sortable>
          <template #default="{ row }">
            <span class="emp-code">{{ row.employee_code }}</span>
          </template>
        </el-table-column>

        <el-table-column label="氏名" min-width="160">
          <template #default="{ row }">
            <div class="emp-name-cell">
              <div class="emp-name-kanji">{{ renderNameKanji(row) }}</div>
              <div class="emp-name-kana">{{ renderNameKana(row) }}</div>
        </div>
      </template>
        </el-table-column>

        <el-table-column label="雇用区分" width="120">
          <template #default="{ row }">
            <el-tag v-if="renderEmploymentType(row)" size="small" :type="getEmploymentTypeTagType(row)">
              {{ renderEmploymentType(row) }}
            </el-tag>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="所属部門" width="150">
          <template #default="{ row }">
            <span v-if="renderPrimaryDepartment(row)">{{ renderPrimaryDepartment(row) }}</span>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="役職" width="100">
          <template #default="{ row }">
            <span v-if="renderPosition(row)">{{ renderPosition(row) }}</span>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="入社日" width="110">
          <template #default="{ row }">
            <span v-if="renderJoinDate(row)">{{ renderJoinDate(row) }}</span>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="状態" width="80" align="center">
          <template #default="{ row }">
            <el-tag 
              size="small" 
              :type="isEmployeeActive(row) ? 'success' : 'info'"
            >
              {{ isEmployeeActive(row) ? '在籍' : '退職' }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="" width="100" align="center" fixed="right">
          <template #default="{ row }">
            <el-button type="info" text size="small" @click="openView(row.id)" title="照会">
              <el-icon><View /></el-icon>
            </el-button>
            <el-button type="primary" text size="small" @click="openEdit(row.id)" title="編集">
              <el-icon><Edit /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- ページネーション -->
      <div class="employees-pagination">
        <span class="employees-pagination__info">
          {{ (page - 1) * pageSize + 1 }} - {{ Math.min(page * pageSize, total) }} / {{ total }}件
        </span>
        <el-pagination 
          layout="prev, pager, next" 
          :page-size="pageSize" 
          :total="total" 
          :current-page="page"
          @current-change="onPageChange" 
        />
      </div>
    </el-card>

    <!-- 照会ダイアログ -->
    <el-dialog 
      v-model="showView" 
      width="auto" 
      append-to-body 
      destroy-on-close 
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="employee-dialog-wrap">
        <EmployeeFormNew 
          v-if="showView" 
          :emp-id="viewId || undefined" 
          :readonly="true"
          @switch-to-edit="switchToEdit" 
        />
      </div>
    </el-dialog>

    <!-- 編集ダイアログ -->
    <el-dialog 
      v-model="showEdit" 
      width="auto" 
      append-to-body 
      destroy-on-close 
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="employee-dialog-wrap">
        <EmployeeFormNew v-if="showEdit" :emp-id="editId || undefined" @saved="onSaved" />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, nextTick } from 'vue'
import { User, Plus, Search, Check, Close, Edit, View } from '@element-plus/icons-vue'
import EmployeeFormNew from './EmployeeFormNew.vue'
import api from '../api'

const showEdit = ref(false)
const editId = ref<string>('')
const showView = ref(false)
const viewId = ref<string>('')

const rows = ref<any[]>([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const keyword = ref('')
type EmploymentFilter = 'all' | 'active' | 'resigned'
const statusFilter = ref<EmploymentFilter>('active') // デフォルト：在籍
const employmentTypeFilter = ref<string>('')
const employmentTypeOptions = ref<{label:string, value:string}[]>([])
const deptId = ref<string | undefined>()
const deptOptions = ref<{label:string,value:string}[]>([])
const deptLoading = ref(false)
const deptMap = ref<Record<string, { name: string; code: string }>>({})
const employmentTypeMap = ref<Record<string, string>>({})

async function load(){
  const where:any[] = []
  const k = keyword.value.trim()
  if (k){
    where.push({
      anyOf: [
        { json:'nameKanji', op:'contains', value:k },
        { json:'nameKana', op:'contains', value:k },
        { json:'code', op:'contains', value:k }
      ]
    })
  }
  if (deptId.value){
    where.push({
      anyOf: [
        { json:'primaryDepartmentId', op:'eq', value: deptId.value },
        { json:'departments[*].departmentId', op:'eq', value: deptId.value }
      ]
    })
  }
  if (employmentTypeFilter.value) {
    where.push({ json:'contracts[*].employmentTypeCode', op:'eq', value: employmentTypeFilter.value })
  }
  if (statusFilter.value !== 'all'){ 
    where.push({ field:'__employment_status__', op:'eq', value: statusFilter.value }) 
  }
  const r = await api.post('/objects/employee/search', { 
    page: page.value, 
    pageSize: pageSize.value, 
    where, 
    orderBy: [{ field: 'employee_code', dir: 'ASC' }] 
  })
  rows.value = r.data?.data || []
  await preloadPrimaryDepartmentNames(rows.value)
  total.value = r.data?.total ?? (rows.value.length < pageSize.value && page.value === 1 ? rows.value.length : 0)
}

async function loadEmploymentTypes() {
  try {
    const r = await api.post('/objects/employment_type/search', { 
      page: 1, 
      pageSize: 100, 
      where: [], 
      orderBy: [{ field: 'payload->>name', dir: 'ASC' }] 
    })
    const list = r.data?.data || []
    employmentTypeOptions.value = list.map((x: any) => ({
      label: x.payload?.name || x.name || '',
      value: x.payload?.code || x.code || ''
    }))
    // Build map for quick lookup
    for (const x of list) {
      const code = x.payload?.code || x.code || ''
      const name = x.payload?.name || x.name || ''
      if (code) employmentTypeMap.value[code] = name
    }
  } catch { /* ignore */ }
}

async function searchDepartments(query:string){
  deptLoading.value = true
  try{
    const where:any[] = []
    const q = (query||'').trim()
    if (q){
      where.push({ json:'name', op:'contains', value:q })
      where.push({ field:'department_code', op:'contains', value:q })
    }
    const r = await api.post('/objects/department/search', { page:1, pageSize:20, where, orderBy:[{field:'department_code',dir:'ASC'}] })
    const list = (r.data?.data || []) as any[]
    cacheDepartmentRecords(list)
    deptOptions.value = list.map(x=>{
      const name = x.name ?? x.payload?.name ?? ''
      const code = x.department_code ?? x.code ?? ''
      const id = x.id
      return { label: name ? `${name} (${code})` : `${code}`, value: id }
    })
  } finally {
    deptLoading.value = false
  }
}

function onPageChange(p: number) {
  page.value = p
  load()
}

onMounted(async () => {
  await loadEmploymentTypes()
  await load()
})

function openEdit(id:string){ editId.value = id; showEdit.value = true }
function openNew(){ editId.value = ''; showEdit.value = true }
function openView(id:string){ viewId.value = id; showView.value = true }
function switchToEdit(){ showView.value = false; openEdit(viewId.value) }
function onSaved(){ showEdit.value = false; load() }

async function applyIntent(payload: any) {
  const code = (payload?.openEmployeeCode || payload?.employeeCode || payload?.code || '').toString().trim()
  if (!code) return
  keyword.value = code
  page.value = 1
  await load()
  await nextTick()
  const row = rows.value.find((r: any) => (r?.employee_code ?? r?.payload?.code ?? '').toString().trim() === code) || rows.value[0]
  if (row?.id) openEdit(row.id)
}

defineExpose({ applyIntent })
function onEdit(row:any){ openEdit(row.id) }

function ensurePayload(row:any){
  const payload = row?.payload
  if (payload && typeof payload === 'object') return payload as Record<string, any>
  return {}
}

function renderNameKanji(row:any){
  const payload = ensurePayload(row)
  return payload.nameKanji || payload.name || row.name || ''
}

function renderNameKana(row:any){
  const payload = ensurePayload(row)
  return payload.nameKana || ''
}

function renderEmploymentType(row: any) {
  const payload = ensurePayload(row)
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  const now = new Date().toISOString().slice(0, 10)
  const activeContract = contracts.find((c: any) => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  }) || contracts[0]
  if (!activeContract?.employmentTypeCode) return ''
  return employmentTypeMap.value[activeContract.employmentTypeCode] || activeContract.employmentTypeCode
}

function getEmploymentTypeTagType(row: any) {
  const payload = ensurePayload(row)
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  const activeContract = contracts[0]
  const code = activeContract?.employmentTypeCode || ''
  if (code === 'CONTRACTOR') return 'warning'
  if (code === 'PT') return 'info'
  return ''
}

function renderJoinDate(row: any) {
  const payload = ensurePayload(row)
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  // Find earliest contract start date
  const dates = contracts.map((c: any) => c.periodFrom).filter(Boolean).sort()
  return dates[0] || ''
}

function renderPosition(row: any) {
  const payload = ensurePayload(row)
  const departments = Array.isArray(payload.departments) ? payload.departments : []
  // 找到当前有效的部门（没有结束日期或结束日期在未来）
  const now = new Date().toISOString().slice(0, 10)
  const activeDept = departments.find((d: any) => {
    const to = d.toDate || '9999-12-31'
    return to >= now
  }) || departments[0]
  return activeDept?.position || ''
}

function isEmployeeActive(row: any) {
  const payload = ensurePayload(row)
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  if (!contracts.length) return false
  const now = new Date().toISOString().slice(0, 10)
  return contracts.some((c: any) => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  })
}

function renderPrimaryDepartment(row:any){
  const payload = ensurePayload(row)
  const directName = payload.primaryDepartmentName || payload.primaryDepartment || payload.primaryDeptName
  if (directName) return directName
  const departments = Array.isArray(payload.departments) ? payload.departments : []
  const activeDept = departments.find((d:any)=>!d?.toDate || d.toDate==='') || departments[0]
  if (activeDept){
    const name = activeDept.name || activeDept.departmentName || activeDept.department_name
    if (name) return name
  }
  const deptIdVal = extractPrimaryDeptId(row)
  if (deptIdVal && deptMap.value[deptIdVal]){
    return deptMap.value[deptIdVal].name || ''
  }
  return ''
}

function cacheDepartmentRecords(records:any[]){
  if (!Array.isArray(records)) return
  const next = { ...deptMap.value }
  for (const item of records){
    if (!item) continue
    const id = item.id || item.department_id || item.departmentId
    if (!id || next[id]) continue
    const name = item.name ?? item.payload?.name ?? item.payload?.payload?.name ?? ''
    const code = item.department_code ?? item.code ?? item.payload?.code ?? ''
    next[id] = { name, code }
  }
  deptMap.value = next
}

function extractPrimaryDeptId(row:any){
  const payload = ensurePayload(row)
  return row.primary_department_id
    || payload.primaryDepartmentId
    || payload.primaryDepartmentID
    || payload.primaryDeptId
    || payload.primaryDepartmentCode
    || ''
}

async function preloadPrimaryDepartmentNames(list:any[]){
  const ids = new Set<string>()
  for (const row of list){
    const id = extractPrimaryDeptId(row)
    if (id && !deptMap.value[id]) ids.add(id)
  }
  if (!ids.size) return
  try{
    const where = [{ field:'id', op:'in', value: Array.from(ids) }]
    const resp = await api.post('/objects/department/search', { page:1, pageSize:ids.size, where })
    cacheDepartmentRecords(resp.data?.data || [])
  }catch{}
}
</script>

<style scoped>
.employees-list {
  padding: 16px;
}

.employees-card {
  border-radius: 12px;
  overflow: hidden;
}

.employees-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.employees-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.employees-header__icon {
  font-size: 22px;
  color: #667eea;
}

.employees-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.employees-header__count {
  font-weight: 500;
}

.employees-header__right {
  display: flex;
  gap: 8px;
}

/* フィルター */
.employees-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.employees-filters__keyword {
  width: 220px;
}

.employees-filters__dept {
  width: 180px;
}

.employees-filters__type {
  width: 140px;
}

.employees-filters__status {
  flex-shrink: 0;
}

.employees-filters__status :deep(.el-radio-button__inner) {
  display: flex;
  align-items: center;
  gap: 4px;
}

/* テーブル */
.employees-table {
  border-radius: 8px;
  overflow: hidden;
}

.employees-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

.emp-code {
  font-size: 14px;
  color: #303133;
}

.emp-name-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.emp-name-kanji {
  font-size: 14px;
  font-weight: 500;
  color: #303133;
}

.emp-name-kana {
  font-size: 12px;
  color: #909399;
}

.emp-empty {
  color: #c0c4cc;
}

/* ページネーション */
.employees-pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.employees-pagination__info {
  font-size: 13px;
  color: #909399;
}

/* ダイアログ */
.employee-dialog-wrap {
  min-width: 1000px;
  max-width: 1400px;
}

@media (max-width: 1200px) {
  .employees-filters {
    flex-direction: column;
    align-items: stretch;
  }
  .employees-filters__keyword,
  .employees-filters__dept,
  .employees-filters__type {
    width: 100%;
  }
}
</style>

<style>
.voucher-detail-dialog {
  background: transparent !important;
  box-shadow: none !important;
}
.voucher-detail-dialog .el-dialog__header {
  display: none !important;
}
.voucher-detail-dialog .el-dialog__body {
  padding: 0 !important;
}
</style>

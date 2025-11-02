<template>
  <div class="page">
    <el-card v-if="!detailOnlyMode">
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ listText.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="reload">{{ buttonsText.refresh }}</el-button>
          </div>
        </div>
      </template>

      <div class="page-toolbar">
        <el-date-picker v-model="range" type="daterange" :start-placeholder="listText.date" :end-placeholder="listText.date" value-format="YYYY-MM-DD" />
        <el-input v-model="keyword" :placeholder="placeholder" style="width:280px" />
        <el-button @click="load" :loading="loading">{{ buttonsText.search }}</el-button>
        <el-button @click="reset">{{ buttonsText.reset }}</el-button>
      </div>

      <el-table :data="rows" stripe style="width:100%" v-loading="loading">
        <el-table-column prop="posting_date" :label="listText.date" width="130" />
        <el-table-column prop="voucher_type" :label="listText.type" width="140">
          <template #default="{ row }">
            <el-tag size="small">{{ row.voucher_type }}</el-tag>
            <span class="type-label">{{ voucherTypeMap[row.voucher_type] || row.payload?.header?.voucherType || row.voucher_type }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="voucher_no" :label="listText.number" width="160" />
        <el-table-column :label="listText.summary" min-width="260">
          <template #default="{ row }">{{ row.payload?.header?.summary || '' }}</template>
        </el-table-column>
        <el-table-column :label="listText.actions" width="120">
          <template #default="{ row }">
            <el-button text type="primary" @click="openDetail(row)">{{ listText.view }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="page-pagination">
        <el-pagination
          background
          layout="prev, pager, next, sizes, total"
          :total="total"
          :page-size="pageSize"
          :current-page="page"
          @update:page-size="onPageSize"
          @update:current-page="onPage" />
      </div>
    </el-card>

    <el-card v-else class="detail-card">
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ listText.view || listText.title }}</div>
          <div class="page-actions" v-if="detail?.payload?.header?.voucherNo">
            <el-tag type="info">{{ detail.payload.header.voucherNo }}</el-tag>
          </div>
        </div>
      </template>
      <template v-if="detailLoading">
        <el-skeleton :rows="6" animated />
      </template>
      <template v-else-if="detail">
        <div class="kv">
          <div><b>{{ listText.date }}</b>：{{ detail.payload?.header?.postingDate }}</div>
          <div><b>{{ listText.type }}</b>：{{ voucherTypeLabelText(detailHeader.voucherType) }}</div>
          <div><b>{{ listText.number }}</b>：{{ detail.payload?.header?.voucherNo }}</div>
          <div style="flex:1"><b>{{ listText.summary }}</b>：{{ detail.payload?.header?.summary }}</div>
          <div v-if="detailHeader.createdAt"><b>{{ listText.createdAt }}</b>：{{ formatDateTime(detailHeader.createdAt) }}</div>
          <div v-if="userDisplay(detailHeader, 'created')"><b>{{ listText.createdBy }}</b>：{{ userDisplay(detailHeader, 'created') }}</div>
          <div v-if="detailHeader.updatedAt"><b>{{ listText.updatedAt }}</b>：{{ formatDateTime(detailHeader.updatedAt) }}</div>
          <div v-if="userDisplay(detailHeader, 'updated')"><b>{{ listText.updatedBy }}</b>：{{ userDisplay(detailHeader, 'updated') }}</div>
        </div>
        <el-table :data="(detail.payload?.lines || [])" size="small" border style="width:100%; margin-top:8px">
          <el-table-column type="index" width="60" label="#" />
          <el-table-column :label="text.tables.accounts.name" min-width="220">
            <template #default="{ row }">{{ accountLabel(row.accountCode) }}</template>
          </el-table-column>
          <el-table-column prop="drcr" :label="text.columns.drcr" width="100" />
          <el-table-column prop="amount" :label="text.columns.amount" width="180" />
          <el-table-column prop="customerId" :label="listText.customer" />
          <el-table-column prop="vendorId" :label="listText.vendor" />
          <el-table-column prop="departmentId" :label="listText.department" />
          <el-table-column prop="employeeId" :label="listText.employee" />
        </el-table>
      </template>
      <el-empty v-else :description="detailError || '暂无数据'" />
    </el-card>

    <el-dialog v-if="!detailOnlyMode" v-model="show" :title="listText.title" width="900px">
      <template v-if="detail">
        <div class="kv">
          <div><b>{{ listText.date }}</b>：{{ detail.payload?.header?.postingDate }}</div>
          <div><b>{{ listText.type }}</b>：{{ voucherTypeLabelText(detailHeader.voucherType) }}</div>
          <div><b>{{ listText.number }}</b>：{{ detail.payload?.header?.voucherNo }}</div>
          <div style="flex:1"><b>{{ listText.summary }}</b>：{{ detail.payload?.header?.summary }}</div>
          <div v-if="detailHeader.createdAt"><b>{{ listText.createdAt }}</b>：{{ formatDateTime(detailHeader.createdAt) }}</div>
          <div v-if="userDisplay(detailHeader, 'created')"><b>{{ listText.createdBy }}</b>：{{ userDisplay(detailHeader, 'created') }}</div>
          <div v-if="detailHeader.updatedAt"><b>{{ listText.updatedAt }}</b>：{{ formatDateTime(detailHeader.updatedAt) }}</div>
          <div v-if="userDisplay(detailHeader, 'updated')"><b>{{ listText.updatedBy }}</b>：{{ userDisplay(detailHeader, 'updated') }}</div>
        </div>
        <el-table :data="(detail.payload?.lines || [])" size="small" border style="width:100%; margin-top:8px">
          <el-table-column type="index" width="60" label="#" />
          <el-table-column :label="text.tables.accounts.name" min-width="220">
            <template #default="{ row }">{{ accountLabel(row.accountCode) }}</template>
          </el-table-column>
          <el-table-column prop="drcr" :label="text.columns.drcr" width="100" />
          <el-table-column prop="amount" :label="text.columns.amount" width="180" />
          <el-table-column prop="customerId" :label="listText.customer" />
          <el-table-column prop="vendorId" :label="listText.vendor" />
          <el-table-column prop="departmentId" :label="listText.department" />
          <el-table-column prop="employeeId" :label="listText.employee" />
        </el-table>
      </template>
      <template v-else>
        <el-skeleton :rows="6" animated />
      </template>
      <template #footer>
        <el-button @click="show=false">{{ buttonsText.close }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed, reactive, watchEffect } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const { text } = useI18n()
const defaultVoucherListText = { title:'', date:'', type:'', number:'', summary:'', actions:'', view:'', customer:'', vendor:'', department:'', employee:'', createdAt:'', createdBy:'', updatedAt:'', updatedBy:'' }
const defaultButtonsText = { refresh:'', search:'', reset:'', close:'' }

const listText = reactive({ ...defaultVoucherListText })
const buttonsText = reactive({ ...defaultButtonsText })

watchEffect(() => {
  const tbl = text.value?.tables?.voucherList ?? {}
  const detail = text.value?.tables?.voucherDetail ?? {}
  Object.assign(listText, defaultVoucherListText, tbl, {
    customer: detail.customer ?? defaultVoucherListText.customer,
    vendor: detail.vendor ?? defaultVoucherListText.vendor,
    department: detail.department ?? defaultVoucherListText.department,
    employee: detail.employee ?? defaultVoucherListText.employee,
    createdAt: detail.createdAt ?? tbl.createdAt ?? defaultVoucherListText.createdAt,
    createdBy: detail.createdBy ?? tbl.createdBy ?? defaultVoucherListText.createdBy,
    updatedAt: detail.updatedAt ?? tbl.updatedAt ?? defaultVoucherListText.updatedAt,
    updatedBy: detail.updatedBy ?? tbl.updatedBy ?? defaultVoucherListText.updatedBy
  })
  Object.assign(buttonsText, defaultButtonsText, text.value?.buttons ?? {})
})

const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const range = ref<string[]|null>(null)
const keyword = ref('')
const placeholder = computed(() => `${listText.number} / ${listText.summary}`)

const detailOnlyMode = ref(false)
const detailLoading = ref(false)
const detailError = ref('')

const voucherTypeMap = computed<Record<string, string>>(() => {
  const options = text.value?.voucherForm?.typeOptions
  if (!options) return {}
  if (Array.isArray(options)) {
    return options.reduce((acc: Record<string, string>, cur: any) => {
      if (cur && cur.value) acc[cur.value] = cur.label || cur.value
      return acc
    }, {})
  }
  return options as Record<string, string>
})

const detailHeader = computed(() => detail.value?.payload?.header ?? {})

function formatDateTime(value: any) {
  if (!value) return ''
  const str = typeof value === 'string' ? value : String(value)
  const dt = new Date(str)
  if (Number.isNaN(dt.getTime())) return str
  return dt.toLocaleString()
}

function userDisplay(meta: any, prefix: string) {
  if (!meta) return ''
  const name = meta?.[`${prefix}ByName`] ?? meta?.[`${prefix}Byname`]
  const employee = meta?.[`${prefix}ByEmployee`]
  const base = meta?.[`${prefix}By`] ?? meta?.[prefix]
  const code = employee || base
  if (name && code) return `${name} (${code})`
  if (name) return name
  return code || ''
}

function voucherTypeLabelText(code?: string) {
  if (!code) return ''
  const label = voucherTypeMap.value[code]
  return label ? `${label} (${code})` : code
}

function buildWhere(){
  const where:any[] = []
  if (range.value && range.value.length===2){
    where.push({ field: 'posting_date', op: 'between', value: [range.value[0], range.value[1]] })
  }
  const kw = keyword.value.trim()
  if (kw){
    where.push({
      anyOf: [
        { field: 'voucher_no', op: 'contains', value: kw },
        { json: 'header.summary', op: 'contains', value: kw }
      ]
    })
  }
  return where
}

async function load(){
  loading.value = true
  try{
    const r = await api.post('/objects/voucher/search', {
      page: page.value,
      pageSize: pageSize.value,
      where: buildWhere(),
      orderBy: [{ field:'posting_date', dir:'DESC' }, { field:'voucher_no', dir:'DESC' }]
    })
    const payload = r.data || {}
    const data = Array.isArray(payload.data) ? payload.data : []
    rows.value = data
    total.value = Number(payload.total ?? 0)
    if (typeof payload.page === 'number') page.value = payload.page
    if (typeof payload.pageSize === 'number') pageSize.value = payload.pageSize
  } finally {
    loading.value = false
  }
  return rows.value
}

function reload(){ load() }

function reset(){ range.value = null; keyword.value = ''; page.value = 1; load() }
function onPage(p:number){ page.value = p; load() }
function onPageSize(s:number){ pageSize.value = s; page.value = 1; load() }

onMounted(load)

// 详情
const show = ref(false)
const detail = ref<any>(null)
const accountNameByCode = ref<Record<string,string>>({})
async function openDetail(row:any){
  if (!row) return
  detail.value = row
  detailError.value = ''
  show.value = !detailOnlyMode.value
  try{
    const codes = Array.from(new Set(((detail.value.payload?.lines||[]).map((l:any)=> l.accountCode).filter((x:string)=> !!x))))
    accountNameByCode.value = {}
    if (codes.length>0){
      const resp = await api.post('/objects/account/search', { page:1, pageSize: codes.length, where:[{ field:'account_code', op:'in', value: codes }], orderBy:[] })
      const rows = resp.data?.data || []
      rows.forEach((it:any)=>{ const code = it.account_code || it.payload?.code; const name = it.payload?.name || it.name; if (code) accountNameByCode.value[code] = name || '' })
    }
  }catch{}
}
function accountLabel(code:string){ const name = accountNameByCode.value[code] || ''; return name ? `${name} (${code})` : code }

async function fetchVoucherByNo(no: string){
  const r = await api.post('/objects/voucher/search', {
    page: 1,
    pageSize: 1,
    where: [{ field: 'voucher_no', op: 'eq', value: no }],
    orderBy: []
  })
  const data = Array.isArray(r.data?.data) ? r.data.data : []
  return data[0] || null
}

async function applyIntent(payload:any){
  try{
    if (!payload || typeof payload !== 'object') return
    const voucherNo = payload.voucherNo || payload.voucher_no || payload.number
    if (!voucherNo) return
    const voucherKey = String(voucherNo)
    const detailOnly = payload.detailOnly === true || payload.mode === 'detail'
    detailOnlyMode.value = detailOnly
    if (detailOnly){
      detailLoading.value = true
      detailError.value = ''
      detail.value = null
      try{
        const row = await fetchVoucherByNo(voucherKey)
        if (row){
          await openDetail(row)
        } else {
          detailError.value = `${listText.number || '凭证'} ${voucherKey} 未找到`
        }
      } catch (e:any) {
        detailError.value = e?.response?.data?.error || e?.message || '加载失败'
      } finally {
        detailLoading.value = false
      }
      return
    }
    keyword.value = voucherKey
    page.value = 1
    const data = await load()
    const target = (data || rows.value || []).find((row:any) => String(row?.voucher_no || row?.payload?.header?.voucherNo || '') === voucherKey)
    if (target){
      await openDetail(target)
    }
  }catch{}
}

defineExpose({ applyIntent })
</script>

<style scoped>
.kv{ display:flex; gap:16px; flex-wrap:wrap }
.detail-card{ max-width:900px; margin:0 auto; }
.detail-card .kv{ margin-bottom:12px; }
.type-label{ margin-left:6px; color:#374151 }
</style>



<template>
  <div :class="detailOnlyMode ? 'voucher-detail-only' : 'page'">
    <el-card v-if="!detailOnlyMode">
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Tickets /></el-icon>
            <span class="page-header-title">{{ listText.title }}</span>
            <el-tag v-if="total > 0" size="small" type="info">{{ total }}件</el-tag>
          </div>
          <div class="page-actions">
            <el-button type="primary" @click="reload">{{ buttonsText.refresh }}</el-button>
          </div>
        </div>
      </template>

      <div class="page-toolbar">
        <el-date-picker v-model="range" type="daterange" :start-placeholder="listText.date" :end-placeholder="listText.date" value-format="YYYY-MM-DD" style="width:240px" />
        <el-input v-model="keyword" :placeholder="placeholder" style="width:240px" />
        <el-select
          v-model="partnerCode"
          filterable
          remote
          reserve-keyword
          :remote-method="searchPartners"
          :loading="loadingPartners"
          clearable
          style="width:160px"
          placeholder="取引先"
        >
          <el-option v-for="opt in partnerOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <el-select
          v-model="employeeId"
          filterable
          remote
          reserve-keyword
          :remote-method="searchEmployees"
          :loading="loadingEmployees"
          clearable
          style="width:160px"
          placeholder="社員"
        >
          <el-option v-for="opt in employeeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <el-select v-model="voucherType" clearable placeholder="伝票種別" style="width:120px">
          <el-option v-for="opt in voucherTypeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <el-button @click="load" :loading="loading">{{ buttonsText.search }}</el-button>
      </div>

      <el-table :data="rows" stripe style="width:100%" v-loading="loading">
        <el-table-column prop="posting_date" :label="listText.date" width="130" />
        <el-table-column prop="voucher_type" :label="listText.type" width="160">
          <template #default="{ row }">
            <el-tag size="small">{{ row.voucher_type }}</el-tag>
            <span class="type-label">
              {{ voucherTypeLabelText(row.payload?.header?.voucherType || row.voucher_type) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column prop="voucher_no" :label="listText.number" width="160" />
        <el-table-column :label="listText.summary" min-width="260">
          <template #default="{ row }">{{ row.payload?.header?.summary || '' }}</template>
        </el-table-column>
        <el-table-column :label="listText.actions" width="160">
          <template #default="{ row }">
            <el-button text type="primary" @click="openDetail(row)">{{ listText.view }}</el-button>
            <el-tooltip
              v-if="getDeleteDisabledReason(row)"
              :content="getDeleteDisabledReason(row)"
              placement="top"
            >
              <el-button text type="danger" disabled>{{ buttonsText.delete || '削除' }}</el-button>
            </el-tooltip>
            <el-button
              v-else
              text
              type="danger"
              @click="confirmDelete(row)"
            >{{ buttonsText.delete || '削除' }}</el-button>
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

    <VoucherDetailCard
      v-else
      :title="detailTitle"
      :voucher-no="detailMeta.voucherNo"
      :show-edit="props.allowEdit && detail && !editMode"
      :edit-label="buttonsText.edit || '編集'"
      :body-props="voucherDetailBodyProps"
      @edit="enterEditMode"
      @save="saveVoucherChanges"
      @cancel="cancelEditMode"
      @add-line="addEditableLine"
      @remove-line="removeEditableLine"
      @update:editable-header="patchEditableHeader"
      @update:attachments="updateEditableAttachments"
      @delete-attachment-blob="deleteAttachmentBlob"
      @open-clearing-voucher="openClearingVoucher"
    >
      <template #actions>
        <template v-if="props.allowEdit">
          <template v-if="editMode">
            <el-button
              type="primary"
              size="small"
              :loading="editLoading"
              @click="saveVoucherChanges"
            >
              {{ buttonsText.save || '保存' }}
            </el-button>
            <el-button
              size="small"
              :disabled="editLoading"
              @click="cancelEditMode"
            >
              {{ buttonsText.cancel || '取消' }}
            </el-button>
          </template>
          <template v-else>
              <el-button
              v-if="detail"
                type="primary"
                size="small"
                @click="enterEditMode"
              >
                {{ buttonsText.edit || '編集' }}
              </el-button>
              <el-popconfirm
                v-if="detail && !getDeleteDisabledReason(detail)"
                :title="`伝票を削除しますか？`"
                confirm-button-text="削除"
                cancel-button-text="キャンセル"
                @confirm="confirmDeleteInDetail"
              >
                <template #reference>
                  <el-button
                    type="danger"
                    size="small"
                  >
                    {{ buttonsText.delete || '削除' }}
                  </el-button>
                </template>
              </el-popconfirm>
              <el-tooltip
                v-else-if="detail && getDeleteDisabledReason(detail)"
                :content="getDeleteDisabledReason(detail)"
                placement="top"
              >
                <el-button type="danger" size="small" disabled>
                  {{ buttonsText.delete || '削除' }}
                </el-button>
              </el-tooltip>
          </template>
        </template>
      </template>
    </VoucherDetailCard>

    <el-dialog
      v-if="!detailOnlyMode"
      v-model="show"
      width="auto"
      :show-close="false"
      append-to-body
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VoucherDetailCard
          :title="detailTitle"
          :voucher-no="detailMeta.voucherNo"
          :show-edit="props.allowEdit && detail && !editMode"
          :edit-label="buttonsText.edit || '編集'"
          :body-props="voucherDetailBodyProps"
          @edit="enterEditMode"
          @save="saveVoucherChanges"
          @cancel="cancelEditMode"
          @add-line="addEditableLine"
          @remove-line="removeEditableLine"
          @update:editable-header="patchEditableHeader"
          @update:attachments="updateEditableAttachments"
          @delete-attachment-blob="deleteAttachmentBlob"
          @open-clearing-voucher="openClearingVoucher"
        >
          <template #actions>
            <template v-if="props.allowEdit">
              <template v-if="editMode">
                <el-button
                  type="primary"
                  size="small"
                  :loading="editLoading"
                  @click="saveVoucherChanges"
                >
                  {{ buttonsText.save || '保存' }}
                </el-button>
                <el-button
                  size="small"
                  :disabled="editLoading"
                  @click="cancelEditMode"
                >
                  {{ buttonsText.cancel || '取消' }}
                </el-button>
              </template>
              <template v-else>
                  <el-button
                  v-if="detail"
                    type="primary"
                    size="small"
                    @click="enterEditMode"
                  >
                    {{ buttonsText.edit || '編集' }}
                  </el-button>
                  <el-popconfirm
                    v-if="detail && !getDeleteDisabledReason(detail)"
                    :title="`伝票を削除しますか？`"
                    confirm-button-text="削除"
                    cancel-button-text="キャンセル"
                    @confirm="confirmDeleteInDetail"
                  >
                    <template #reference>
                      <el-button
                        type="danger"
                        size="small"
                      >
                        {{ buttonsText.delete || '削除' }}
                      </el-button>
                    </template>
                  </el-popconfirm>
                  <el-tooltip
                    v-else-if="detail && getDeleteDisabledReason(detail)"
                    :content="getDeleteDisabledReason(detail)"
                    placement="top"
                  >
                    <el-button type="danger" size="small" disabled>
                      {{ buttonsText.delete || '削除' }}
                    </el-button>
                  </el-tooltip>
              </template>
            </template>
          </template>
        </VoucherDetailCard>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed, reactive, watch, watchEffect } from 'vue'
import { Tickets } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import VoucherDetailCard from '../components/VoucherDetailCard.vue'
import { ElMessage, ElMessageBox } from 'element-plus'

const { text } = useI18n()
const props = withDefaults(defineProps<{ 
  allowEdit?: boolean
  initialVoucherId?: string // 初始化时直接显示此凭证的详情（UUID）
  initialVoucherNo?: string // 初始化时直接显示此凭证的详情（凭证编号）
}>(), { allowEdit: true })

const emit = defineEmits<{
  (e: 'deleted', voucherId: string): void
  (e: 'reversed', voucherId: string, reversalVoucherId: string): void
}>()
const defaultVoucherListText = { title:'', date:'', type:'', number:'', summary:'', actions:'', view:'', customer:'得意先', vendor:'', department:'', employee:'', createdAt:'', createdBy:'', updatedAt:'', updatedBy:'', paymentDate:'', note:'', invoiceRegistrationNo:'' }
const defaultButtonsText = { refresh:'', search:'', reset:'', close:'', edit:'', save:'', cancel:'', delete:'' }

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
    updatedBy: detail.updatedBy ?? tbl.updatedBy ?? defaultVoucherListText.updatedBy,
    paymentDate: detail.paymentDate ?? defaultVoucherListText.paymentDate,
    note: detail.note ?? defaultVoucherListText.note,
    invoiceRegistrationNo: detail.invoiceRegistrationNo ?? defaultVoucherListText.invoiceRegistrationNo
  })
  Object.assign(buttonsText, defaultButtonsText, text.value?.buttons ?? {})
  listText.customer = '得意先'
})

const accountColumnLabel = '勘定科目'
const customerColumnLabel = '得意先'
const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const range = ref<string[]|null>(null)
const keyword = ref('')
const partnerCode = ref<string | null>(null)
const employeeId = ref<string | null>(null)
const voucherType = ref<string | null>(null)
const partnerOptions = ref<{ label: string; value: string }[]>([])
const loadingPartners = ref(false)
const employeeOptions = ref<{ label: string; value: string }[]>([])
const loadingEmployees = ref(false)
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
const detailLines = computed(() => {
  const lines = detail.value?.payload?.lines
  return Array.isArray(lines) ? lines : []
})

function extractString(value: any): string {
  if (typeof value === 'string') return value
  if (value === null || value === undefined) return ''
  return String(value)
}

function linePaymentDate(row: any): string {
  if (!row) return ''
  return extractString(row.paymentDate ?? row.payment_date ?? row.payment_date ?? '')
}

function lineNote(row: any): string {
  if (!row) return ''
  return extractString(row.note ?? row.remark ?? row.description ?? '')
}

function lineFieldValue(row: any, keys: string[]): string {
  if (!row) return ''
  for (const key of keys) {
    const value = row[key]
    if (value !== undefined && value !== null) {
      const text = extractString(value).trim()
      if (text) return text
    }
  }
  return ''
}

const showCustomerColumn = computed(() =>
  detailLines.value.some((line: any) =>
    !!lineFieldValue(line, ['customerId', 'customer_id', 'customerCode', 'customer_code'])
  )
)

const showVendorColumn = computed(() =>
  detailLines.value.some((line: any) =>
    !!lineFieldValue(line, ['vendorId', 'vendor_id', 'vendorCode', 'vendor_code'])
  )
)

const showDepartmentColumn = computed(() =>
  detailLines.value.some((line: any) =>
    !!lineFieldValue(line, ['departmentId', 'department_id', 'departmentCode', 'department_code'])
  )
)

const showEmployeeColumn = computed(() =>
  detailLines.value.some((line: any) =>
    !!lineFieldValue(line, ['employeeId', 'employee_id', 'employeeCode', 'employee_code'])
  )
)

const showPaymentDateColumn = computed(() => detailLines.value.some((line: any) => !!linePaymentDate(line)))
const showNoteColumn = computed(() => detailLines.value.some((line: any) => !!lineNote(line)))

function formatAmountCell(val: any) {
  const num = Number(val)
  if (!Number.isFinite(num)) return val ?? ''
  return num.toLocaleString()
}

const invoiceLabel = computed(() => text.value?.voucherForm?.header?.invoiceRegistrationNo || listText.invoiceRegistrationNo || 'Invoice Registration No.')

function formatDate(value: any) {
  if (!value) return ''
  const str = typeof value === 'string' ? value : String(value)
  const dt = new Date(str)
  if (Number.isNaN(dt.getTime())) return str
  return dt.toISOString().slice(0, 10)
}

function extractAuditInfo(source: any, prefix: string) {
  const info = { display: '', name: '', code: '', raw: '' }
  if (source === null || source === undefined) return info
  if (typeof source !== 'object') {
    const text = extractString(source).trim()
    if (text) info.raw = text
    return info
  }
  const normalizedPrefix = prefix.toLowerCase()
  for (const [key, value] of Object.entries(source)) {
    if (value === null || value === undefined) continue
    const normKey = key.toLowerCase().replace(/[_-]/g, '')
    if (!normKey.startsWith(normalizedPrefix)) continue
    if (normKey.endsWith('at')) continue
    const text = extractString(value).trim()
    if (!text) continue
    if (normKey.endsWith('display')) {
      if (!info.display) info.display = text
      continue
    }
    if (normKey.endsWith('name')) {
      if (!info.name) info.name = text
      continue
    }
    if (normKey.endsWith('employee') || normKey.endsWith('code') || normKey.endsWith('user') || normKey.endsWith('id')) {
      if (!info.code) info.code = text
      continue
    }
    if (!info.raw) info.raw = text
  }
  return info
}

function auditDisplay(prefix: string, ...sources: any[]) {
  const aggregated = { display: '', name: '', code: '', raw: '' }
  sources.forEach((source) => {
    const info = extractAuditInfo(source, prefix)
    if (!aggregated.display && info.display) aggregated.display = info.display
    if (!aggregated.name && info.name) aggregated.name = info.name
    if (!aggregated.code && info.code) aggregated.code = info.code
    if (!aggregated.raw && info.raw) aggregated.raw = info.raw
  })
  if (aggregated.display) return aggregated.display
  if (aggregated.name && aggregated.code) return `${aggregated.name}（${aggregated.code}）`
  if (aggregated.name) return aggregated.name
  if (aggregated.code) return aggregated.code
  return aggregated.raw
}

function formatVoucherTypeOption(code: string, rawLabel?: string) {
  const trimmedCode = (code || '').toString().trim()
  if (!trimmedCode) return ''
  const label = (rawLabel || '').toString().trim()
  if (!label || label.toUpperCase() === trimmedCode.toUpperCase()) return trimmedCode
  return `${trimmedCode}${label}`
}

function voucherTypeLabelText(code?: string) {
  const normalized = (code || '').toString().trim()
  if (!normalized) return ''
  const label = voucherTypeMap.value[normalized]
  return formatVoucherTypeOption(normalized, typeof label === 'string' ? label : String(label ?? ''))
}

function formatAuditUser(source: any, prefix: string) {
  if (!source) return ''
  const display = source?.[`${prefix}ByDisplay`] || source?.[`${prefix}Bydisplay`]
  if (display) {
    const text = extractString(display).trim()
    if (text) return text
  }
  const name = source?.[`${prefix}ByName`] || source?.[`${prefix}Byname`] || ''
  const code = source?.[`${prefix}ByEmployee`] || source?.[`${prefix}Byemployee`] || ''
  if (name && code) return `${name}（${code}）`
  if (name) return name
  if (code) return code
  const fallback = source?.[`${prefix}By`] ?? source?.[prefix]
  return extractString(fallback)
}

const headerLabels = computed(() => ({
  number: listText.number,
  date: listText.date,
  type: listText.type,
  currency: text.value?.voucherForm?.header?.currency || '通貨',
  summary: listText.summary,
  createdAt: listText.createdAt,
  createdBy: listText.createdBy,
  updatedAt: listText.updatedAt,
  updatedBy: listText.updatedBy
}))

const detailTitle = computed(() => text.value?.tables?.voucherDetail?.title || '会計伝票照会')

const detailMeta = computed(() => {
  const header = detailHeader.value || {}
  const auditMeta = detail.value?.payload?.meta
    ?? detail.value?.payload?.audit
    ?? detail.value?.meta
    ?? detail.value?.metadata
    ?? {}
  return {
    voucherNo: extractString(header.voucherNo ?? header.voucher_no ?? ''),
    postingDate: extractString(header.postingDate ?? header.posting_date ?? ''),
    voucherType: voucherTypeLabelText(header.voucherType ?? header.voucher_type ?? ''),
    currency: extractString(header.currency ?? header.currency_code ?? detail.value?.payload?.header?.currency ?? ''),
    invoiceRegistrationNo: extractString(header.invoiceRegistrationNo ?? header.invoice_registration_no ?? ''),
    summary: extractString(detail.value?.payload?.header?.summary ?? ''),
    createdAt: formatDate(header.createdAt ?? header.created_at ?? ''),
    createdBy: auditDisplay('created', header, detail.value?.payload?.meta, detail.value?.payload, detail.value),
    updatedAt: formatDate(header.updatedAt ?? header.updated_at ?? ''),
    updatedBy: auditDisplay('updated', header, detail.value?.payload?.meta, detail.value?.payload, detail.value)
  }
})

function buildWhere(){
  const where:any[] = []
  if (range.value && range.value.length===2){
    where.push({ field: 'posting_date', op: 'between', value: [range.value[0], range.value[1]] })
  }
  if (partnerCode.value){
    where.push({ field: 'primary_partner_code', op: 'eq', value: partnerCode.value })
  }
  if (employeeId.value){
    where.push({ field: 'primary_employee_id', op: 'eq', value: employeeId.value })
  }
  if (voucherType.value){
    where.push({ field: 'voucher_type', op: 'eq', value: voucherType.value })
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

async function searchPartners(query: string) {
  loadingPartners.value = true
  try{
    const q = (query || '').trim()
    const where: any[] = []
    if (q){
      where.push({ json: 'name', op: 'contains', value: q })
    }
    const resp = await api.post('/objects/businesspartner/search', {
      page: 1,
      pageSize: 50,
      where,
      orderBy: [{ field: 'partner_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    partnerOptions.value = rows.map((x:any) => {
      const id = x.id || ''
      const code = x.partner_code || x.payload?.code || ''
      const name = x.payload?.name || x.name || ''
      return { value: id, label: name ? `${name} (${code})` : code }
    }).filter((x:any) => !!x.value)
  }catch{
    partnerOptions.value = []
  }finally{
    loadingPartners.value = false
  }
}

async function searchEmployees(query: string) {
  loadingEmployees.value = true
  try{
    const q = (query || '').trim()
    const where: any[] = []
    if (q){
      // 支持按姓名（nameKanji/nameKana）或员工编号搜索
      where.push({
        anyOf: [
          { json: 'nameKanji', op: 'contains', value: q },
          { json: 'nameKana', op: 'contains', value: q },
          { field: 'employee_code', op: 'contains', value: q }
        ]
      })
    }
    // 获取全部员工，不限制数量
    const resp = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 10000,
      where,
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    employeeOptions.value = rows.map((x:any) => {
      const id = x.id || ''
      const code = x.employee_code || x.payload?.code || ''
      const name = x.payload?.nameKanji || x.payload?.nameKana || x.payload?.name || ''
      return { value: id, label: name ? `${name} (${code})` : code }
    }).filter((x:any) => !!x.value)
  }catch{
    employeeOptions.value = []
  }finally{
    loadingEmployees.value = false
  }
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

async function searchAccountOptions(keyword: string) {
  const trimmed = (keyword || '').trim()
  try{
    const resp = await api.post('/objects/account/search', {
      page: 1,
      pageSize: trimmed ? 20 : 200,
      where: trimmed
        ? [
            {
              anyOf: [
                { field: 'account_code', op: 'contains', value: trimmed },
                { json: 'name', op: 'contains', value: trimmed },
                { json: 'payload.code', op: 'contains', value: trimmed },
                { json: 'payload.name', op: 'contains', value: trimmed }
              ]
            }
          ]
        : [],
      orderBy: [
        { field: 'account_code', dir: 'ASC' }
      ]
    })
    const data = Array.isArray(resp.data?.data) ? resp.data.data : []
    return data.map((row: any) => {
      const code = row.account_code || row.payload?.code || ''
      const name = row.payload?.name || row.name || ''
      const label = name ? `${code} ${name}` : code
      return {
        value: code,
        label,
        rules: {
          fieldRules: row.payload?.fieldRules || {},
          taxType: row.payload?.taxType || 'NON_TAX'
        }
      }
    })
  }catch{
    return []
  }
}

function reload(){ load() }

/**
 * 判断凭证是否可删除，返回不可删除的原因（空字符串表示可删除）
 */
function getDeleteDisabledReason(row: any): string {
  const payload = row?.payload
  if (!payload) return ''
  
  // 已被冲销的凭证不能删除
  if (payload.reversal) {
    const reversalNo = payload.reversal.reversalVoucherNo || ''
    return `この伝票は反対仕訳されています（${reversalNo}）`
  }
  
  // 冲销凭证本身不能删除
  if (payload.isReversal) {
    return '反対仕訳は削除できません'
  }
  
  return ''
}

/**
 * 判断凭证的 open_items 是否已被清账（需要冲销而非删除）
 */
function needsReversal(row: any): boolean {
  const payload = row?.payload
  if (!payload) return false
  
  // 如果是清账凭证（有 clearings），可以冲销来撤回
  if (payload.clearings && Array.isArray(payload.clearings) && payload.clearings.length > 0) {
    return true
  }
  
  return false
}

async function confirmDelete(row: any) {
  const voucherNo = row?.voucher_no || row?.payload?.header?.voucherNo || ''
  
  // 先尝试直接删除，看后端返回什么错误
  try {
    // 如果是清账凭证，直接提示使用冲销
    if (needsReversal(row)) {
      await confirmReverse(row, voucherNo)
      return
    }
    
    // 尝试普通删除确认
    await ElMessageBox.confirm(
      `伝票「${voucherNo}」を削除しますか？この操作は取り消せません。`,
      '削除確認',
      {
        confirmButtonText: '削除',
        cancelButtonText: 'キャンセル',
        type: 'warning'
      }
    )
    await deleteVoucher(row)
  } catch (e: any) {
    // 用户取消或删除失败
    if (e === 'cancel' || e?.message === 'cancel') return
    
    // 如果删除失败且包含"清算"相关信息，提示用户使用冲销
    const errMsg = e?.response?.data?.error || e?.message || ''
    if (errMsg.includes('清算') || errMsg.includes('清账')) {
      try {
        await confirmReverse(row, voucherNo)
      } catch {
        // 用户取消冲销
      }
    }
  }
}

async function confirmDeleteInDetail() {
  if (!detail.value) return
  const row = detail.value
  
  // 如果是清账凭证，需要冲销而非删除
  if (needsReversal(row)) {
    const voucherNo = row?.voucher_no || row?.payload?.header?.voucherNo || ''
    try {
      await confirmReverse(row, voucherNo)
    } catch {
      // 用户取消
    }
    return
  }
  
  // 直接删除（popconfirm已经确认过了）
  await deleteVoucher(row)
  // 删除成功后返回列表
  show.value = false
  detail.value = null
}

async function confirmReverse(row: any, voucherNo: string) {
  await ElMessageBox.confirm(
    `伝票「${voucherNo}」は清算済み項目があるため直接削除できません。\n反対仕訳を作成して取り消しますか？`,
    '反対仕訳確認',
    {
      confirmButtonText: '反対仕訳を作成',
      cancelButtonText: 'キャンセル',
      type: 'warning'
    }
  )
  await reverseVoucher(row)
}

async function reverseVoucher(row: any) {
  if (!row?.id) return
  const voucherId = row.id
  try {
    const today = new Date().toISOString().split('T')[0]
    const res = await api.post(`/operations/voucher/${voucherId}/reverse`, {
      postingDate: today,
      reason: '誤記訂正'
    })
    const data = res.data as any
    ElMessage.success(`反対仕訳を作成しました（${data.reversalVoucherNo}）`)
    
    // 刷新列表
    await load()
    
    // 詳細ダイアログを閉じる
    if (detail.value?.id === voucherId) {
      show.value = false
      detail.value = null
    }
    
    // 通知父组件凭证已被冲销
    emit('reversed', voucherId, data.reversalVoucherId || data.reversalVoucherNo)
  } catch (e: any) {
    const msg = e?.response?.data?.error || e?.message || '反対仕訳の作成に失敗しました'
    ElMessage.error(msg)
  }
}

async function deleteVoucher(row: any) {
  if (!row?.id) return
  const voucherId = row.id
  try {
    await api.delete(`/objects/voucher/${voucherId}`)
    ElMessage.success('伝票を削除しました')
    // リストから削除
    const idx = rows.value.findIndex((r: any) => r?.id === voucherId)
    if (idx >= 0) {
      rows.value.splice(idx, 1)
      total.value = Math.max(0, total.value - 1)
    }
    // 詳細ダイアログを閉じる
    if (detail.value?.id === voucherId) {
      show.value = false
      detail.value = null
    }
    // 通知父组件凭证已删除
    emit('deleted', voucherId)
  } catch (e: any) {
    const msg = e?.response?.data?.error || e?.message || '削除に失敗しました'
    ElMessage.error(msg)
    throw e // 重新抛出以便 confirmDelete 可以捕获
  }
}

function reset(){
  range.value = null
  keyword.value = ''
  partnerCode.value = null
  employeeId.value = null
  voucherType.value = null
  page.value = 1
  load()
}
function onPage(p:number){ page.value = p; load() }
function onPageSize(s:number){ pageSize.value = s; page.value = 1; load() }

onMounted(async () => {
  console.log('[VouchersList] onMounted called with props:', { initialVoucherId: props.initialVoucherId, initialVoucherNo: props.initialVoucherNo })
  // 如果有 initialVoucherId 或 initialVoucherNo，直接进入详情模式
  if (props.initialVoucherId || props.initialVoucherNo) {
    console.log('[VouchersList] Entering detailOnly mode')
    detailOnlyMode.value = true
    detailLoading.value = true
    detailError.value = ''
    detail.value = null
    try {
      console.log('[VouchersList] Fetching voucher by', props.initialVoucherId ? 'ID' : 'No')
      const row = props.initialVoucherId 
        ? await fetchVoucherById(props.initialVoucherId)
        : await fetchVoucherByNo(props.initialVoucherNo!)
      console.log('[VouchersList] Fetch result:', row)
      if (row) {
        await openDetail(row)
      } else {
        const displayKey = props.initialVoucherId || props.initialVoucherNo
        detailError.value = `伝票 ${displayKey} 未找到`
        console.warn('[VouchersList] Voucher not found:', displayKey)
      }
    } catch (e: any) {
      console.error('[VouchersList] Error fetching voucher:', e)
      detailError.value = e?.response?.data?.error || e?.message || '加载失败'
    } finally {
      detailLoading.value = false
    }
  } else {
    console.log('[VouchersList] No initial voucher, loading list')
    await load()
  }
})

// 详情
const show = ref(false)
const detail = ref<any>(null)
const accountNameByCode = ref<Record<string,string>>({})

async function loadAccountNamesForRow(row: any){
  const map: Record<string, string> = {}
  try{
    const codes = Array.from(new Set(((row?.payload?.lines || []).map((l: any) => l?.accountCode).filter((x: string) => !!x))))
    if (codes.length > 0){
      const resp = await api.post('/objects/account/search', {
        page: 1,
        pageSize: codes.length,
        where: [{ field: 'account_code', op: 'in', value: codes }],
        orderBy: []
      })
      const rows = resp.data?.data || []
      rows.forEach((it: any) => {
        const code = it.account_code || it.payload?.code
        const name = it.payload?.name || it.name
        if (code) map[code] = name || ''
      })
    }
  }catch{
    // ignore account loading errors, fallback to empty map
  }
  accountNameByCode.value = map
}

async function openDetail(row:any){
  if (!row) return
  detailError.value = ''
  let targetRow = row
  const voucherNo = row?.voucher_no || row?.payload?.header?.voucherNo
  const useDialog = !detailOnlyMode.value
  if (useDialog) {
    show.value = true
    detailLoading.value = true
  } else {
    show.value = false
  }
  try {
    if (useDialog && voucherNo) {
      const fresh = await fetchVoucherByNo(String(voucherNo))
      if (fresh) {
        targetRow = fresh
      }
    }
  } catch (e: any) {
    detailError.value = e?.response?.data?.error || e?.message || '伝票を取得できませんでした'
  } finally {
    if (useDialog) {
      detailLoading.value = false
    }
  }
  detail.value = targetRow
  await loadAccountNamesForRow(targetRow)
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

async function fetchVoucherById(id: string){
  const r = await api.post('/objects/voucher/search', {
    page: 1,
    pageSize: 1,
    where: [{ field: 'id', op: 'eq', value: id }],
    orderBy: []
  })
  const data = Array.isArray(r.data?.data) ? r.data.data : []
  return data[0] || null
}

async function applyIntent(payload:any){
  try{
    if (!payload || typeof payload !== 'object') return
    const voucherId = payload.voucherId || payload.voucher_id
    const voucherNo = payload.voucherNo || payload.voucher_no || payload.number
    if (!voucherId && !voucherNo) return
    const detailOnly = payload.detailOnly === true || payload.mode === 'detail'
    detailOnlyMode.value = detailOnly
    if (detailOnly){
      detailLoading.value = true
      detailError.value = ''
      detail.value = null
      try{
        // 优先使用 voucherId 查询，否则使用 voucherNo
        const row = voucherId 
          ? await fetchVoucherById(String(voucherId))
          : await fetchVoucherByNo(String(voucherNo))
        if (row){
          await openDetail(row)
        } else {
          const displayKey = voucherId || voucherNo
          detailError.value = `${listText.number || '凭证'} ${displayKey} 未找到`
        }
      } catch (e:any) {
        detailError.value = e?.response?.data?.error || e?.message || '加载失败'
      } finally {
        detailLoading.value = false
      }
      return
    }
    const voucherKey = String(voucherNo || voucherId)
    keyword.value = voucherKey
    page.value = 1
    const data = await load()
    const target = (data || rows.value || []).find((row:any) => 
      String(row?.voucher_no || row?.payload?.header?.voucherNo || '') === voucherKey ||
      String(row?.id || '') === voucherKey
    )
    if (target){
      await openDetail(target)
    }
  }catch{}
}

defineExpose({ applyIntent })

// 打开清账凭证详情
async function openClearingVoucher(voucherNo: string) {
  if (!voucherNo) return
  try {
    const row = await fetchVoucherByNo(voucherNo)
    if (row) {
      await openDetail(row)
    } else {
      ElMessage.warning(`伝票 ${voucherNo} が見つかりません`)
    }
  } catch (e: any) {
    ElMessage.error(e?.message || '伝票の取得に失敗しました')
  }
}

const editMode = ref(false)
const editLoading = ref(false)
const editError = ref('')
const editableHeader = reactive<any>({})
const editableLines = ref<any[]>([])
const editableAttachments = ref<any[]>([])

function patchEditableHeader(payload: Record<string, any>) {
  if (!payload || typeof payload !== 'object') return
  Object.assign(editableHeader, payload)
}

function updateEditableAttachments(attachments: any[]) {
  editableAttachments.value = attachments || []
}

// 删除 Azure Storage 中的附件
async function deleteAttachmentBlob(blobName: string): Promise<boolean> {
  if (!blobName) return true
  try {
    await api.delete(`/voucher-attachments/${encodeURIComponent(blobName)}`)
    return true
  } catch (err) {
    console.error('Failed to delete blob:', blobName, err)
    return false
  }
}

// 上传单个附件文件到服务器
async function uploadAttachmentFile(file: File): Promise<any> {
  const companyCode = sessionStorage.getItem('currentCompany') || 'JP01'
  const formData = new FormData()
  formData.append('file', file, file.name)
  const response = await api.post('/voucher-attachments/upload', formData, {
    headers: {
      'x-company-code': companyCode
      // Content-Type 会自动设置为 multipart/form-data
    }
  })
  return response.data
}

function deepClone<T>(value: T): T {
  return value === undefined ? value : JSON.parse(JSON.stringify(value))
}

function resetEditState(){
  editMode.value = false
  editError.value = ''
  editLoading.value = false
  Object.keys(editableHeader).forEach((key) => delete editableHeader[key])
  editableLines.value = []
  editableAttachments.value = []
}

watch(detail, () => {
  resetEditState()
}, { immediate: true })

const allowedCurrencies = ['JPY', 'USD', 'CNY']

const currencyOptions = computed(() => {
  const voucherForm = text.value?.voucherForm as any;
  const labelMap = voucherForm?.currencyOptions ?? {};
  return allowedCurrencies.map((code) => ({
    value: code,
    label: typeof labelMap?.[code] === 'string' && labelMap[code] ? String(labelMap[code]) : code
  }))
})

const voucherTypeOptions = computed(() => {
  const map = text.value?.voucherForm?.typeOptions || {}
  const entries = Object.entries(map)
  if (entries.length === 0){
    const defaults = ['GL','AP','AR','AA','SA','IN','OT']
    return defaults.map((code) => ({ value: code, label: formatVoucherTypeOption(code, code) }))
  }
  return entries.map(([value, label]) => ({
    value,
    label: formatVoucherTypeOption(value, typeof label === 'string' ? label : String(label ?? ''))
  }))
})

const drLabel = computed(() => text.value?.voucherForm?.drLabel || '借方')
const crLabel = computed(() => text.value?.voucherForm?.crLabel || '貸方')
const drcrOptions = computed(() => [
  { value: 'DR', label: drLabel.value },
  { value: 'CR', label: crLabel.value }
])

function ensureEditableLine(line: any = {}){
  const result: any = deepClone(line)
  result.accountCode = typeof result.accountCode === 'string' ? result.accountCode : (result.account_code || '')
  result.drcr = typeof result.drcr === 'string' ? result.drcr.toUpperCase() : (typeof result.side === 'string' ? result.side.toUpperCase() : 'DR')
  result.amount = Number(result.amount ?? 0)
  if (!Number.isFinite(result.amount)) result.amount = 0
  result.note = result.note ?? ''
  result.customerId = result.customerId ?? null
  result.vendorId = result.vendorId ?? null
  result.departmentId = result.departmentId ?? null
  result.employeeId = result.employeeId ?? null
  result.paymentDate = result.paymentDate ?? null
  return result
}

const editDebitTotal = computed(() => editableLines.value.reduce((sum, line) => sum + (line.drcr === 'DR' ? Number(line.amount || 0) : 0), 0))
const editCreditTotal = computed(() => editableLines.value.reduce((sum, line) => sum + (line.drcr === 'CR' ? Number(line.amount || 0) : 0), 0))
const editBalanced = computed(() => Math.abs(editDebitTotal.value - editCreditTotal.value) < 0.0001)

function enterEditMode(){
  if (!detail.value) return
  const payload = deepClone(detail.value.payload || {})
  const header = payload.header || {}
  const lines = Array.isArray(payload.lines) ? payload.lines : []
  const attachments = Array.isArray(payload.attachments) ? payload.attachments : []
  Object.keys(editableHeader).forEach((key) => delete editableHeader[key])
  Object.assign(editableHeader, header)
  const normalizedCurrency = typeof editableHeader.currency === 'string' ? editableHeader.currency.trim().toUpperCase() : ''
  editableHeader.currency = allowedCurrencies.includes(normalizedCurrency) ? normalizedCurrency : 'JPY'
  editableLines.value = lines.map((line: any) => ensureEditableLine(line))
  if (editableLines.value.length === 0){
    editableLines.value.push(ensureEditableLine({ drcr: 'DR' }))
    editableLines.value.push(ensureEditableLine({ drcr: 'CR' }))
  }
  // 初始化附件列表，保留原有的 source 标记
  editableAttachments.value = attachments.map((att: any) => {
    if (typeof att === 'string') {
      // 旧数据只有 ID，标记为 AI 来源
      return { id: att, name: 'ファイル', contentType: '', size: 0, url: '', source: 'ai' }
    }
    // 保留原有的 source，如果没有则默认为 ai（兼容旧数据）
    return { ...att, source: att.source || 'ai' }
  })
  editMode.value = true
  editError.value = ''
}

function cancelEditMode(){
  editMode.value = false
  editError.value = ''
}

function addEditableLine(side: 'DR' | 'CR'){
  editableLines.value.push(ensureEditableLine({ drcr: side, amount: 0 }))
}

function removeEditableLine(index: number){
  if (index >= 0 && index < editableLines.value.length){
    editableLines.value.splice(index, 1)
  }
  if (editableLines.value.length === 0){
    editableLines.value.push(ensureEditableLine({ drcr: 'DR' }))
  }
}

function sanitizeLineForSave(line: any, index: number){
  const clone = ensureEditableLine(line)
  clone.lineNo = index + 1
  clone.accountCode = (clone.accountCode || '').trim()
  clone.drcr = (clone.drcr || 'DR').toUpperCase()
  clone.amount = Number(clone.amount || 0)
  if (!Number.isFinite(clone.amount)) clone.amount = 0
  const optionalKeys = ['customerId', 'vendorId', 'departmentId', 'employeeId', 'note']
  optionalKeys.forEach((key) => {
    if (clone[key] === undefined || clone[key] === null || clone[key] === '') clone[key] = null
  })
  clone.paymentDate = clone.paymentDate ? clone.paymentDate : null
  if (clone.tax && typeof clone.tax === 'object'){
    if (clone.tax.amount !== undefined) clone.tax.amount = Number(clone.tax.amount || 0)
    if (clone.tax.baseLineNo === undefined || clone.tax.baseLineNo === null) clone.tax.baseLineNo = clone.lineNo
  }
  return clone
}

async function saveVoucherChanges(){
  if (!detail.value || !detail.value.id){
    editError.value = text.value?.common?.saveFailed || '保存に失敗しました'
    return
  }
  if (!editBalanced.value){
    editError.value = text.value?.voucherForm?.totals?.imbalance || '借方と貸方が一致していません'
    return
  }
  const missingAccount = editableLines.value.find((line) => !line || !line.accountCode || !line.accountCode.toString().trim())
  if (missingAccount){
    editError.value = '勘定科目コードを入力してください'
    return
  }
  editLoading.value = true
  editError.value = ''
  try{
    const basePayload = deepClone(detail.value.payload || {})
    const headerClone = deepClone(editableHeader)
    headerClone.companyCode = detail.value.payload?.header?.companyCode || headerClone.companyCode || (detail.value.company_code ?? '')
    headerClone.voucherNo = detail.value.payload?.header?.voucherNo || detail.value.voucher_no || headerClone.voucherNo
    const normalizedCurrency = typeof headerClone.currency === 'string' ? headerClone.currency.trim().toUpperCase() : ''
    const voucherMessages = (text.value?.voucherForm as any)?.messages ?? {}
    if (!normalizedCurrency){
      editError.value = voucherMessages.currencyRequired ?? '通貨を選択してください'
      editLoading.value = false
      return
    }
    if (!allowedCurrencies.includes(normalizedCurrency)){
      editError.value = voucherMessages.currencyInvalid ?? 'サポートされていない通貨です'
      editLoading.value = false
      return
    }
    headerClone.currency = normalizedCurrency
    editableHeader.currency = normalizedCurrency
    const sanitizedLines = editableLines.value.map((line, idx) => sanitizeLineForSave(line, idx))
    basePayload.header = headerClone
    basePayload.lines = sanitizedLines
    
    // 上传本地文件并构建附件列表
    const uploadedAttachments: any[] = []
    const failedUploads: string[] = []
    for (const att of editableAttachments.value) {
      if (att._isLocal && att._file) {
        // 本地文件需要先上传
        try {
          const uploaded = await uploadAttachmentFile(att._file)
          if (uploaded) {
            // 标记为手工上传
            uploaded.source = 'manual'
            uploadedAttachments.push(uploaded)
          }
        } catch (uploadErr: any) {
          console.error('Failed to upload attachment:', att.name, uploadErr)
          failedUploads.push(att.name)
        }
      } else {
        // 已上传的文件，移除内部字段但保留 source
        const { _file, _isLocal, ...rest } = att
        uploadedAttachments.push(rest)
      }
    }
    if (failedUploads.length > 0) {
      ElMessage.warning(`添付ファイルのアップロードに失敗しました: ${failedUploads.join(', ')}`)
    }
    basePayload.attachments = uploadedAttachments
    console.log('[voucher save] attachments to save:', uploadedAttachments)
    
    const resp = await api.put(`/vouchers/${detail.value.id}`, { payload: basePayload })
    const updated = resp.data
    if (!updated){
      throw new Error('update failed')
    }
    detail.value = updated
    await loadAccountNamesForRow(updated)
    const idx = rows.value.findIndex(row => row?.id === updated.id)
    if (idx >= 0){
      rows.value.splice(idx, 1, updated)
    }
    editMode.value = false
    editError.value = ''
    ElMessage.success(text.value?.common?.saved || '保存しました')
  }catch(e:any){
    const msg = e?.response?.data?.error || e?.message || (text.value?.common?.saveFailed || '保存に失敗しました')
    editError.value = msg
    ElMessage.error(msg)
  }finally{
    editLoading.value = false
  }
}

const voucherDetailBodyProps = computed(() => ({
  detailLoading: detailLoading.value,
  detail: detail.value,
  detailError: detailError.value,
  detailMeta: detailMeta.value,
  headerLabels: headerLabels.value,
  invoiceLabel: invoiceLabel.value,
  editMode: editMode.value,
  editLoading: editLoading.value,
  editError: editError.value,
  drLabel: drLabel.value,
  crLabel: crLabel.value,
  editBalanced: editBalanced.value,
  editDebitTotal: editDebitTotal.value,
  editCreditTotal: editCreditTotal.value,
  buttonsText: { ...buttonsText },
  listText: { ...listText },
  text: text.value ?? null,
  accountColumnLabel,
  customerColumnLabel,
  showCustomerColumn: showCustomerColumn.value,
  showVendorColumn: showVendorColumn.value,
  showDepartmentColumn: showDepartmentColumn.value,
  showEmployeeColumn: showEmployeeColumn.value,
  showPaymentDateColumn: showPaymentDateColumn.value,
  showNoteColumn: showNoteColumn.value,
  editableHeader,
  editableLines: editableLines.value,
  editableAttachments: editableAttachments.value,
  voucherTypeOptions: voucherTypeOptions.value,
  currencyOptions: currencyOptions.value,
  drcrOptions: drcrOptions.value,
  accountLabel,
  formatAmountCell,
  linePaymentDate,
  lineNote,
  fetchAccountOptions: searchAccountOptions
}))
</script>

<style scoped>
.type-label{ margin-left:6px; color:#374151 }
.voucher-dialog-card-wrap{ padding:0; margin:0; }
.voucher-detail-only{ padding:0; margin:0; }

/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #409eff;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

/* 表格表头样式 - 浅灰色风格 */
:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}

/* 卡片圆角 */
:deep(.el-card) {
  border-radius: 12px;
  overflow: hidden;
}
</style>

<!-- 凭证详情弹窗样式已在全局 style.css 中定义 -->


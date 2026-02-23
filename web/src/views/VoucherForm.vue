<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ voucherText.title }}</div>
          <div class="page-actions">
            <el-tag v-if="savedVoucherNo" type="info" size="large" class="voucher-no-tag">{{ savedVoucherNo }}</el-tag>
            <el-button type="primary" :loading="saving" @click="save">
              {{ savedVoucherId ? (voucherText.actions.update || '更新') : voucherText.actions.save }}
            </el-button>
            <el-button @click="reset">{{ voucherText.actions.reset }}</el-button>
          </div>
        </div>
      </template>

      <el-skeleton :loading="loading" animated>
        <template #default>
          <!-- 抬头区 -->
          <el-form label-width="96px" :model="model.header" class="voucher-header-form">
            <el-row :gutter="12" class="header-row align-grid">
              <el-col :span="spanOf('header.companyCode')">
                <el-form-item :label="voucherText.header.companyCode">
                  <el-input v-model="model.header.companyCode" style="width:100%" disabled />
                </el-form-item>
              </el-col>
              <el-col :span="spanOf('header.postingDate')">
                <el-form-item :label="voucherText.header.postingDate">
                  <el-date-picker v-model="model.header.postingDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
                  <div v-if="!periodState.isOpen" class="period-warning">{{ periodState.message }}</div>
                </el-form-item>
              </el-col>
              <el-col :span="spanOf('header.voucherType')">
                <el-form-item :label="voucherText.header.voucherType" required>
                  <el-select v-model="model.header.voucherType" filterable style="width:100%">
                    <el-option v-for="opt in voucherTypeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :span="spanOf('header.currency')">
                <el-form-item :label="voucherText.header.currency" required>
                  <el-select v-model="model.header.currency" style="width:100%" :clearable="false">
                    <el-option v-for="opt in currencyOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :span="spanOf('header.invoiceRegistrationNo')">
                <el-form-item :label="voucherText.header.invoiceRegistrationNo" class="nowrap-label align-offset">
                  <div class="invoice-field">
                    <el-input
                      v-model="model.header.invoiceRegistrationNo"
                      maxlength="14"
                      placeholder="T1234567890123"
                      style="width:100%"
                      @input="onInvoiceInput"
                      @blur="onInvoiceBlur"
                    />
                    <el-button
                      size="small"
                      type="primary"
                      :loading="invoiceChecking"
                      @click="verifyInvoice"
                    >
                      {{ voucherText.actions.verifyInvoice || '照会' }}
                    </el-button>
                  </div>
                  <div v-if="invoiceMessage" :class="['invoice-status', invoiceStatusClass(invoiceStatus)]">{{ invoiceMessage }}</div>
                </el-form-item>
              </el-col>
              <el-col :span="spanOf('header.summary')">
                <el-form-item :label="voucherText.header.summary">
                  <el-input v-model="model.header.summary" :maxlength="summaryMax" show-word-limit style="width:100%" />
                </el-form-item>
              </el-col>
            </el-row>
          </el-form>

          <!-- 明细行表格 -->
          <el-table :data="model.lines" border style="width: 100%; margin-top: 12px;">
            <el-table-column label="#" width="32" type="index" />
            <el-table-column :label="voucherText.lines.account" min-width="180">
              <template #default="{ row }">
                <el-select v-model="row.accountCode" filterable remote reserve-keyword :remote-method="searchAccounts" :loading="loadingAccounts" style="width:100%" :placeholder="voucherText.placeholders.account">
                  <el-option v-for="a in accountOptions" :key="a.value" :label="a.label" :value="a.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.drcr" width="120">
              <template #default="{ row }">
                <el-select v-model="row.drcr" style="width:100%">
                  <el-option v-for="opt in drcrOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.amount" min-width="140">
              <template #default="{ row }">
                <div class="amount-cell">
                  <el-input
                    type="text"
                    inputmode="decimal"
                    :model-value="formatAmountInput(row.grossAmount)"
                    style="width: 100%"
                    @input="onGrossAmountInput(row, $event as string)"
                    class="gross-input"
                  />
                  <div v-if="shouldShowTax(row)" class="tax-box" style="margin-left: 8px;">
                    <div class="tax-item">
                      <span class="tax-label">{{ taxRateLabel }}</span>
                      <div class="tax-rate-input">
                        <el-input-number
                          :model-value="row.taxRate"
                          :min="0"
                          :max="100"
                          :step-strictly="true"
                          :step="1"
                          :precision="0"
                          size="small"
                          @change="onTaxRateChange(row, $event as number)"
                        />
                        <span class="tax-rate-unit">%</span>
                      </div>
                    </div>
                    <div class="tax-item">
                      <span class="tax-label">{{ taxAmountLabel }}</span>
                      <el-input
                        size="small"
                        type="text"
                        inputmode="decimal"
                        :model-value="formatAmountInput(row.taxAmount)"
                        @input="onTaxAmountInput(row, $event as string)"
                        class="tax-amount-input"
                      />
                    </div>
                  </div>
                </div>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.department" min-width="160">
              <template #default="{ row }">
                <el-select
                  v-if="isVisible(row,'departmentId')"
                  v-model="row.departmentId"
                  :class="{ req: isRequired(row,'departmentId') }"
                  filterable
                  remote
                  reserve-keyword
                  :remote-method="searchDepartments"
                  :loading="loadingDepartments"
                  style="width:100%"
                  :placeholder="voucherText.placeholders.department"
                  clearable
                >
                  <el-option v-for="d in departmentOptions" :key="d.value" :label="d.label" :value="d.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.employee" min-width="160">
              <template #default="{ row }">
                <el-select
                  v-if="isVisible(row,'employeeId')"
                  v-model="row.employeeId"
                  :class="{ req: isRequired(row,'employeeId') }"
                  filterable
                  remote
                  reserve-keyword
                  :remote-method="searchEmployees"
                  :loading="loadingEmployees"
                  style="width:100%"
                  :placeholder="voucherText.placeholders.employee"
                  clearable
                >
                  <el-option v-for="e in employeeOptions" :key="e.value" :label="e.label" :value="e.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.customer" min-width="180">
              <template #default="{ row }">
                <el-select v-if="isVisible(row,'customerId')" v-model="row.customerId" :class="{ req: isRequired(row,'customerId') }" filterable remote reserve-keyword :remote-method="searchCustomers" :loading="loadingCustomers" style="width:100%" :placeholder="voucherText.placeholders.customer">
                  <el-option v-for="c in customerOptions" :key="c.value" :label="c.label" :value="c.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.vendor" min-width="180">
              <template #default="{ row }">
                <el-select v-if="isVisible(row,'vendorId')" v-model="row.vendorId" :class="{ req: isRequired(row,'vendorId') }" filterable remote reserve-keyword :remote-method="searchVendors" :loading="loadingVendors" style="width:100%" :placeholder="voucherText.placeholders.vendor">
                  <el-option v-for="v in vendorOptions" :key="v.value" :label="v.label" :value="v.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="固定資産" min-width="180">
              <template #default="{ row }">
                <el-select v-if="isAssetAccountSelected(row)" v-model="row.assetId" filterable remote reserve-keyword :remote-method="searchAssets" :loading="loadingAssets" style="width:100%" placeholder="資産を選択" clearable>
                  <el-option v-for="a in assetOptions" :key="a.value" :label="a.label" :value="a.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.paymentDate" width="140">
              <template #default="{ row }">
                <el-date-picker v-if="isVisible(row,'paymentDate')" v-model="row.paymentDate" type="date" value-format="YYYY-MM-DD" style="width: 100%;" />
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.note" min-width="200">
              <template #default="{ row }">
                <el-input v-model="row.note" />
              </template>
            </el-table-column>
            <el-table-column :label="voucherText.lines.actions" width="120">
              <template #default="{ $index }">
                <el-button size="small" type="danger" @click="removeLine($index)">{{ voucherText.actions.deleteLine }}</el-button>
              </template>
            </el-table-column>
          </el-table>

          <div class="line-actions">
            <el-button size="small" type="primary" @click="addLine">{{ voucherText.actions.addLine }}</el-button>
          </div>

          <!-- 附件上传区域（凭证保存成功后才显示） -->
          <div v-if="savedVoucherId" class="attachments-section">
            <div class="attachments-header">
              <span class="attachments-title">添付ファイル</span>
              <el-upload
                ref="uploadRef"
                :action="uploadUrl"
                :headers="uploadHeaders"
                :before-upload="beforeUpload"
                :on-success="onUploadSuccess"
                :on-error="onUploadError"
                :show-file-list="false"
                :multiple="true"
                :accept="acceptTypes"
              >
                <el-button size="small" type="primary" :icon="Upload">ファイル追加</el-button>
              </el-upload>
            </div>
            <div v-if="attachments.length > 0" class="attachments-grid">
              <div 
                v-for="(att, idx) in attachments" 
                :key="att.id || idx" 
                class="attachment-card"
                :class="{ clickable: !!att.url }"
              >
                <!-- 图片类型显示缩略图 -->
                <template v-if="isImageAttachment(att)">
                  <div class="attachment-thumb" @click="handleAttachmentClick(att)">
                    <img :src="att.url" :alt="att.name" @error="onImageError($event)" />
                  </div>
                </template>
                <!-- 非图片类型显示图标 -->
                <template v-else>
                  <div class="attachment-thumb file-icon" @click="handleAttachmentClick(att)">
                    <el-icon :size="32"><Document /></el-icon>
                    <span class="file-ext">{{ getFileExtension(att.name) }}</span>
                  </div>
                </template>
                <div class="attachment-info">
                  <div class="attachment-name" :title="att.name">{{ att.name }}</div>
                  <div class="attachment-meta">
                    <span class="attachment-size">{{ formatFileSize(att.size) }}</span>
                    <el-button size="small" type="danger" :icon="Delete" circle @click.stop="removeAttachment(idx)" />
                  </div>
                </div>
              </div>
            </div>
            <div v-else class="attachments-empty">添付ファイルはありません</div>
          </div>

          <!-- 图片预览弹窗 -->
          <el-dialog 
            v-model="imagePreviewVisible" 
            :title="imagePreviewName || '画像プレビュー'"
            width="auto"
            append-to-body
            destroy-on-close
            class="voucher-image-preview-dialog"
          >
            <img v-if="imagePreviewUrl" :src="imagePreviewUrl" :alt="imagePreviewName" class="preview-image" />
          </el-dialog>

          <!-- 文件预览弹窗（PDF、Office、其他） -->
          <el-dialog 
            v-model="filePreviewVisible" 
            :title="filePreviewName || 'ファイル プレビュー'"
            width="min(1200px, 96vw)"
            top="2vh"
            append-to-body
            destroy-on-close
            class="voucher-file-preview-dialog"
          >
            <!-- PDF 和 Office 文件：用 iframe 预览 -->
            <div v-if="filePreviewType === 'iframe' || filePreviewType === 'office'" class="file-preview-container">
              <iframe 
                :src="filePreviewUrl" 
                class="file-preview-iframe"
                frameborder="0"
              />
            </div>
            <!-- 其他文件：显示下载提示 -->
            <div v-else-if="filePreviewType === 'download'" class="file-download-prompt">
              <el-icon :size="64" color="#909399"><Document /></el-icon>
              <p class="file-name">{{ filePreviewName }}</p>
              <p class="file-hint">このファイル形式はプレビューできません</p>
              <el-button type="primary" @click="downloadFile">
                <el-icon><Download /></el-icon>
                ダウンロード
              </el-button>
            </div>
          </el-dialog>

          <div class="totals" :class="{ warn: sumDebit !== sumCredit }">
            {{ totalsText }}
            <span v-if="sumDebit !== sumCredit">{{ voucherText.totals.imbalance }}</span>
          </div>

          <div class="msgs">
            <span v-if="message" class="ok">{{ message }}</span>
            <span v-if="error" class="err">{{ error }}</span>
          </div>
        </template>
      </el-skeleton>
    </el-card>
  </div>
  
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch, onBeforeUnmount } from 'vue'
import { Upload, Delete, Document, Download } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import type { Messages } from '../i18n/messages'
const emit = defineEmits<{ (e:'done', payload:any): void; (e:'close'): void }>()

const { section } = useI18n()

const defaultVoucherForm: Messages['voucherForm'] = {
  title: '',
  actions: { save: '', update: '', reset: '', addLine: '', deleteLine: '', verifyInvoice: '' },
  header: { companyCode: '', postingDate: '', voucherType: '', currency: '', summary: '', invoiceRegistrationNo: '' },
  lines: { account: '', drcr: '', amount: '', taxRate: '', taxAmount: '', netAmount: '', department: '', employee: '', customer: '', vendor: '', paymentDate: '', note: '', actions: '' },
  totals: { prefix: '', imbalance: '' },
  placeholders: { account: '', customer: '', vendor: '', department: '', employee: '' },
  messages: {
    saved: '',
    error: '',
    posted: '',
    missingInputTaxAccount: '',
    missingOutputTaxAccount: '',
    periodClosed: '',
    invoiceInvalid: '',
    invoiceNotFound: '',
    invoiceInactive: '',
    invoiceExpired: '',
    invoiceMatched: '',
    invoiceCheckFailed: '',
    invoiceRequired: '',
    invoiceUnchecked: '',
    voucherTypeRequired: '',
    voucherNoRequired: ''
  },
  typeOptions: { GL: 'GL', AP: 'AP', AR: 'AR', AA: 'AA', SA: '給与', IN: '入金', OT: '出金' },
  drLabel: 'DR',
  crLabel: 'CR'
}

const voucherText = section(defaultVoucherForm, (msg) => msg.voucherForm)

const loading = ref(true)
const saving = ref(false)
// 首次保存后，记住该凭证 ID/番号；后续保存走更新而不是再次新建（避免重复创建）
const savedVoucherId = ref('')
const savedVoucherNo = ref('')
const message = ref('')
const error = ref('')
const saveClicked = ref(false)
const invoiceChecking = ref(false)
const invoiceStatus = ref('')
const invoiceMessage = ref('')
const lastVerifiedInvoiceNo = ref('')

const invoiceNoPattern = /^T\d{13}$/

const types = ['GL','AP','AR','AA','SA','IN','OT']
const voucherTypeMap = computed<Record<string, string>>(() => {
  const opts = voucherText.value.typeOptions || {}
  return Object.fromEntries(Object.entries(opts).map(([code, label]) => [code, String(label)]))
})
function formatVoucherTypeOption(code: string, rawLabel?: string) {
  const label = (rawLabel || '').trim()
  if (!label || label.toUpperCase() === code.toUpperCase()) return code
  return `${code}${label}`
}

const voucherTypeOptions = computed(() => {
  const mapEntries = Object.entries(voucherTypeMap.value || {})
  if (mapEntries.length > 0) {
    return mapEntries.map(([code, label]) => ({
      value: code,
      label: formatVoucherTypeOption(code, typeof label === 'string' ? label : String(label ?? ''))
    }))
  }
  return types.map((code) => ({
    value: code,
    label: formatVoucherTypeOption(code, code)
  }))
})
const drcrOptions = computed(() => [
  { value: 'DR', label: voucherText.value.drLabel || 'DR' },
  { value: 'CR', label: voucherText.value.crLabel || 'CR' }
])

const taxRateLabel = computed(() => '税率')

const taxAmountLabel = computed(() => '税額')

function normalizeInvoiceNo(raw: string) {
  return (raw || '').replace(/\s+/g, '').toUpperCase()
}

function invoiceStatusClass(status: string) {
  if (status === 'matched') return 'status-success'
  if (status === 'inactive') return 'status-warning'
  if (status === 'expired' || status === 'not_found' || status === 'invalid' || status === 'error') return 'status-error'
  return ''
}

function buildInvoiceMessage(status: string, context: any) {
  const msgs = voucherText.value.messages || {}
  const no = context?.invoiceRegistrationNo || ''
  const name = context?.invoiceRegistrationName || context?.name || ''
  const effectiveFrom = context?.invoiceRegistrationEffectiveFrom || context?.effectiveFrom || ''
  const effectiveTo = context?.invoiceRegistrationEffectiveTo || context?.effectiveTo || ''

  switch (status) {
    case 'matched':
      return no ? (msgs.invoiceMatched || '').replace('{no}', no).replace('{name}', name || '') : ''
    case 'inactive':
      return no ? (msgs.invoiceInactive || '').replace('{no}', no).replace('{date}', effectiveFrom || '') : ''
    case 'expired':
      return no ? (msgs.invoiceExpired || '').replace('{no}', no).replace('{date}', effectiveTo || '') : ''
    case 'not_found':
      return no ? (msgs.invoiceNotFound || '').replace('{no}', no) : (msgs.invoiceNotFound || '')
    case 'invalid':
      return msgs.invoiceInvalid || ''
    case 'error':
      return msgs.invoiceCheckFailed || ''
    default:
      return no ? (msgs.invoiceUnchecked || '') : ''
  }
}

function clearInvoiceStatus() {
  model.header.invoiceRegistrationStatus = ''
  model.header.invoiceRegistrationName = ''
  model.header.invoiceRegistrationNameKana = ''
  model.header.invoiceRegistrationCheckedAt = ''
  model.header.invoiceRegistrationEffectiveFrom = ''
  model.header.invoiceRegistrationEffectiveTo = ''
  invoiceStatus.value = ''
  invoiceMessage.value = ''
  lastVerifiedInvoiceNo.value = ''
  updateInvoiceStatusFromHeader()
}

function updateInvoiceStatusFromHeader(header: any = model.header, extra?: any) {
  const status = (header?.invoiceRegistrationStatus || extra?.status || '').toString()
  invoiceStatus.value = status
  const ctx = {
    invoiceRegistrationNo: header?.invoiceRegistrationNo || extra?.registrationNo || '',
    invoiceRegistrationName: header?.invoiceRegistrationName || extra?.name || '',
    invoiceRegistrationEffectiveFrom: header?.invoiceRegistrationEffectiveFrom || extra?.effectiveFrom || '',
    invoiceRegistrationEffectiveTo: header?.invoiceRegistrationEffectiveTo || extra?.effectiveTo || ''
  }
  invoiceMessage.value = buildInvoiceMessage(status, ctx)
}

function onInvoiceInput(value: string) {
  const normalized = normalizeInvoiceNo(value)
  if (normalized !== model.header.invoiceRegistrationNo) {
    model.header.invoiceRegistrationNo = normalized
  }
  if (normalized !== lastVerifiedInvoiceNo.value) {
    clearInvoiceStatus()
  }
}

function onInvoiceBlur() {
  const normalized = normalizeInvoiceNo(model.header.invoiceRegistrationNo || '')
  if (normalized !== model.header.invoiceRegistrationNo) {
    model.header.invoiceRegistrationNo = normalized
  }
  if (!normalized) {
    clearInvoiceStatus()
  }
}

async function verifyInvoice() {
  const normalized = normalizeInvoiceNo(model.header.invoiceRegistrationNo || '')
  model.header.invoiceRegistrationNo = normalized
  if (!normalized) {
    invoiceStatus.value = 'invalid'
    invoiceMessage.value = voucherText.value.messages.invoiceRequired || ''
    return
  }
  if (!invoiceNoPattern.test(normalized)) {
    invoiceStatus.value = 'invalid'
    invoiceMessage.value = voucherText.value.messages.invoiceInvalid || ''
    return
  }
  invoiceChecking.value = true
  try {
    const resp = await api.get(`/references/invoice/verify/${encodeURIComponent(normalized)}`)
    const data = resp.data || {}
    model.header.invoiceRegistrationStatus = data.status || ''
    model.header.invoiceRegistrationName = data.name || ''
    model.header.invoiceRegistrationNameKana = data.nameKana || ''
    model.header.invoiceRegistrationCheckedAt = data.checkedAt || ''
    model.header.invoiceRegistrationEffectiveFrom = data.effectiveFrom || ''
    model.header.invoiceRegistrationEffectiveTo = data.effectiveTo || ''
    lastVerifiedInvoiceNo.value = normalized
    updateInvoiceStatusFromHeader(model.header, data)
  } catch (err: any) {
    console.error('invoice verify failed', err?.response?.data || err)
    clearInvoiceStatus()
    invoiceStatus.value = 'error'
    invoiceMessage.value = voucherText.value.messages.invoiceCheckFailed || ''
  } finally {
    invoiceChecking.value = false
  }
}

function voucherTypeLabel(code: string) {
  const label = voucherTypeMap.value[code]
  return formatVoucherTypeOption(code, typeof label === 'string' ? label : String(label ?? ''))
}

const accountOptions = ref<{label:string,value:string}[]>([])
const loadingAccounts = ref(false)
const customerOptions = ref<{label:string,value:string}[]>([])
const vendorOptions = ref<{label:string,value:string}[]>([])
const assetOptions = ref<{label:string,value:string}[]>([])
const loadingCustomers = ref(false)
const loadingVendors = ref(false)
const loadingAssets = ref(false)
const assetAccountCodes = ref<Set<string>>(new Set())
const departmentOptions = ref<{label:string,value:string}[]>([])
const loadingDepartments = ref(false)
const employeeOptions = ref<{label:string,value:string}[]>([])
const loadingEmployees = ref(false)

// 附件相关
interface Attachment {
  id: string
  name: string
  contentType: string
  size: number
  blobName?: string
  url?: string
  uploadedAt?: string
  sha256?: string
}
const attachments = ref<Attachment[]>([])
const uploadRef = ref<any>(null)
function getStored(key: string) {
  try { return localStorage.getItem(key) } catch {}
  try { return sessionStorage.getItem(key) } catch {}
  return null
}
const uploadUrl = computed(() => {
  const base = String((api as any)?.defaults?.baseURL || '').trim()
  if (!base || base === '/') return '/voucher-attachments/upload'
  return `${base.replace(/\/$/, '')}/voucher-attachments/upload`
})
const uploadHeaders = computed(() => {
  const headers: Record<string, string> = {
    'x-company-code': getStored('company_code') || sessionStorage.getItem('currentCompany') || 'JP01'
  }
  const token = getStored('auth_token')
  if (token) headers.Authorization = `Bearer ${token}`
  return headers
})
const acceptTypes = '.pdf,.jpg,.jpeg,.png,.gif,.xlsx,.xls,.doc,.docx,.csv'
const maxFileSize = 10 * 1024 * 1024 // 10MB

function beforeUpload(file: File) {
  if (file.size > maxFileSize) {
    error.value = `ファイルサイズが10MBを超えています: ${file.name}`
    return false
  }
  return true
}

function onUploadSuccess(response: any, file: any) {
  if (response && response.id) {
    attachments.value.push({
      id: response.id,
      name: response.name || file.name,
      contentType: response.contentType || file.type,
      size: response.size || file.size,
      blobName: response.blobName,
      url: response.url,
      uploadedAt: response.uploadedAt || new Date().toISOString(),
      sha256: response.sha256
    })
  }
}

function onUploadError(err: any) {
  console.error('Upload failed:', err)
  error.value = 'ファイルのアップロードに失敗しました'
}

function removeAttachment(index: number) {
  attachments.value.splice(index, 1)
}

function formatFileSize(bytes: number) {
  if (!bytes || bytes < 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

// 附件预览相关
const imagePreviewVisible = ref(false)
const imagePreviewUrl = ref('')
const imagePreviewName = ref('')
const filePreviewVisible = ref(false)
const filePreviewUrl = ref('')
const filePreviewName = ref('')
const filePreviewType = ref<'iframe' | 'office' | 'download'>('iframe')

const imageTypes = ['image/jpeg', 'image/jpg', 'image/png', 'image/gif', 'image/webp', 'image/bmp']

function isImageAttachment(att: any) {
  if (!att) return false
  if (att.contentType && imageTypes.includes(att.contentType.toLowerCase())) return true
  const name = (att.name || '').toLowerCase()
  return /\.(jpg|jpeg|png|gif|webp|bmp)$/i.test(name)
}

function isPdfAttachment(att: any) {
  if (!att) return false
  if (att.contentType && att.contentType.toLowerCase() === 'application/pdf') return true
  const name = (att.name || '').toLowerCase()
  return /\.pdf$/i.test(name)
}

function isOfficeAttachment(att: any) {
  if (!att) return false
  const name = (att.name || '').toLowerCase()
  return /\.(doc|docx|xls|xlsx|ppt|pptx)$/i.test(name)
}

function getFileExtension(filename: string) {
  if (!filename) return ''
  const ext = filename.split('.').pop()?.toUpperCase() || ''
  return ext.length > 4 ? ext.substring(0, 4) : ext
}

function handleAttachmentClick(att: any) {
  if (!att.url) return
  
  if (isImageAttachment(att)) {
    imagePreviewUrl.value = att.url
    imagePreviewName.value = att.name || 'プレビュー'
    imagePreviewVisible.value = true
  } else if (isPdfAttachment(att)) {
    filePreviewUrl.value = att.url
    filePreviewName.value = att.name || 'PDF プレビュー'
    filePreviewType.value = 'iframe'
    filePreviewVisible.value = true
  } else if (isOfficeAttachment(att)) {
    const encodedUrl = encodeURIComponent(att.url)
    filePreviewUrl.value = `https://view.officeapps.live.com/op/embed.aspx?src=${encodedUrl}`
    filePreviewName.value = att.name || 'ファイル プレビュー'
    filePreviewType.value = 'office'
    filePreviewVisible.value = true
  } else {
    filePreviewUrl.value = att.url
    filePreviewName.value = att.name || 'ファイル'
    filePreviewType.value = 'download'
    filePreviewVisible.value = true
  }
}

function downloadFile() {
  if (filePreviewUrl.value) {
    window.open(filePreviewUrl.value, '_blank')
  }
}

function onImageError(event: Event) {
  const img = event.target as HTMLImageElement
  if (img) img.style.display = 'none'
}

// 抬头各字段布局（span=24 栅格）
const headerSpans: Record<string, number> = {
  'header.companyCode': 6,
  'header.postingDate': 6,
  'header.voucherType': 6,
  'header.currency': 6,
  'header.summary': 24,
  'header.invoiceRegistrationNo': 12
}
function spanOf(field: string) { return headerSpans[field] ?? 6 }
const summaryMax = 500

const allowedCurrencies = ['JPY', 'USD', 'CNY']
const currencyOptions = allowedCurrencies.map((code) => ({ label: code, value: code }))

const defaultCompanyCode = sessionStorage.getItem('currentCompany') || 'JP01'
const initialHeader = {
  companyCode: defaultCompanyCode,
  postingDate: '',
  voucherType: 'GL',
  currency: 'JPY',
  summary: '',
  invoiceRegistrationNo: '',
  invoiceRegistrationStatus: '',
  invoiceRegistrationName: '',
  invoiceRegistrationNameKana: '',
  invoiceRegistrationCheckedAt: '',
  invoiceRegistrationEffectiveFrom: '',
  invoiceRegistrationEffectiveTo: ''
}

const defaultTaxRate = 10
const isMounted = ref(true)
let pendingRequests = 0
let latestPeriodRequestId = 0

function createLine() {
  return {
    accountCode: '',
    drcr: 'DR',
    amount: 0,
    grossAmount: 0,
    taxAmount: 0,
    taxRate: defaultTaxRate,
    taxType: 'NON_TAX',
    departmentId: null,
    employeeId: null,
    customerId: null,
    vendorId: null,
    paymentDate: '',
    note: ''
  }
}

function ensureLineShape(line: any) {
  if (!line) return line
  if (typeof line.amount !== 'number') line.amount = Number(line.amount || 0)
  if (typeof line.grossAmount !== 'number') line.grossAmount = Number(line.grossAmount ?? line.amount ?? 0)
  if (typeof line.taxAmount !== 'number') line.taxAmount = Number(line.taxAmount || 0)
  if (typeof line.taxRate !== 'number') line.taxRate = defaultTaxRate
  if (typeof line.taxType !== 'string') line.taxType = 'NON_TAX'
  return line
}

function roundCurrency(val: number) {
  if (!Number.isFinite(val)) return 0
  if (amountPrecision.value === 0) return Math.round(val)
  const factor = Math.pow(10, amountPrecision.value)
  return Math.round(val * factor) / factor
}

function sanitizeRate(rate: number) {
  if (!Number.isFinite(rate)) return defaultTaxRate
  if (rate < 0) return 0
  if (rate > 100) return 100
  return rate
}

function calcTaxFromGross(gross: number, ratePercent: number) {
  const rate = sanitizeRate(ratePercent) / 100
  if (!Number.isFinite(gross) || rate <= 0) return 0
  const absGross = Math.abs(gross)
  const tax = absGross * rate / (1 + rate)
  const rounded = roundCurrency(tax)
  return gross >= 0 ? rounded : -rounded
}

function clampTaxAmount(gross: number, taxAmount: number) {
  if (!Number.isFinite(taxAmount)) return 0
  const max = Math.abs(gross)
  const val = Math.abs(taxAmount)
  const clamped = val > max ? max : val
  const rounded = roundCurrency(clamped)
  return gross >= 0 ? rounded : -rounded
}

function netAmountOf(line: any) {
  const gross = Number(line?.grossAmount ?? line?.amount ?? 0)
  const tax = needsTax(line) ? Number(line?.taxAmount || 0) : 0
  return roundCurrency(gross - tax)
}
function taxAmountOf(line: any) {
  if (!needsTax(line)) return 0
  return Math.abs(roundCurrency(Number(line?.taxAmount || 0)))
}
function taxSideOf(line: any) {
  if (!needsTax(line)) return null
  if (line?.taxType === 'INPUT_TAX') return 'DR'
  if (line?.taxType === 'OUTPUT_TAX') return 'CR'
  return null
}

const companySettings = reactive<{ inputTaxAccountCode: string; outputTaxAccountCode: string }>({
  inputTaxAccountCode: '',
  outputTaxAccountCode: ''
})
const periodState = reactive<{ isOpen: boolean; message: string }>({ isOpen: true, message: '' })
const model = reactive<any>({
  header: { ...initialHeader },
  lines: [
    createLine(),
    Object.assign(createLine(), { drcr: 'CR' })
  ]
})

model.lines.forEach(ensureLineShape)

const amountPrecision = computed(() => model.header.currency === 'JPY' ? 0 : 2)

model.lines.forEach(syncNetAmount)

const sumDebit = computed(() => model.lines.reduce((total: number, line: any) => {
  const net = line.drcr === 'DR' ? netAmountOf(line) : 0
  const tax = taxSideOf(line) === 'DR' ? taxAmountOf(line) : 0
  return total + net + tax
}, 0))
const sumCredit = computed(() => model.lines.reduce((total: number, line: any) => {
  const net = line.drcr === 'CR' ? netAmountOf(line) : 0
  const tax = taxSideOf(line) === 'CR' ? taxAmountOf(line) : 0
  return total + net + tax
}, 0))
function formatAmountDisplay(val: number) {
  return Number(val || 0).toLocaleString(undefined, {
    minimumFractionDigits: amountPrecision.value,
    maximumFractionDigits: amountPrecision.value
  })
}
function formatAmountInput(val: number | string) {
  const num = Number(val || 0)
  if (!Number.isFinite(num)) return ''
  return formatAmountDisplay(num)
}
function parseAmountInput(val: string) {
  if (typeof val !== 'string') return Number(val || 0)
  const cleaned = val.replace(/[^0-9.-]/g, '')
  const num = Number(cleaned)
  return Number.isFinite(num) ? num : 0
}
function syncNetAmount(row: any) {
  if (!row) return
  if (!needsTax(row)) {
    row.taxAmount = 0
    row.amount = roundCurrency(row.grossAmount)
    return
  }
  const tax = clampTaxAmount(row.grossAmount, row.taxAmount)
  row.taxAmount = tax
  row.amount = roundCurrency(row.grossAmount - tax)
}
function onGrossAmountInput(row: any, val: string) {
  ensureLineShape(row)
  const num = parseAmountInput(val)
  row.grossAmount = roundCurrency(num)
  if (needsTax(row)) {
    const tax = calcTaxFromGross(row.grossAmount, row.taxRate)
    row.taxAmount = clampTaxAmount(row.grossAmount, tax)
  }
  syncNetAmount(row)
}
function onTaxRateChange(row: any, val: number) {
  saveClicked.value = false
  ensureLineShape(row)
  row.taxRate = sanitizeRate(typeof val === 'number' ? val : Number(val))
  if (!needsTax(row)) {
    row.taxAmount = 0
  } else {
    const tax = calcTaxFromGross(row.grossAmount, row.taxRate)
    row.taxAmount = clampTaxAmount(row.grossAmount, tax)
  }
  syncNetAmount(row)
}
function onTaxAmountInput(row: any, val: string) {
  saveClicked.value = false
  ensureLineShape(row)
  if (!needsTax(row)) return
  const num = parseAmountInput(val)
  row.taxAmount = clampTaxAmount(row.grossAmount, num)
  syncNetAmount(row)
}

function taxAccountCodeFor(line: any) {
  const type = line?.taxType
  if (type === 'INPUT_TAX') return companySettings.inputTaxAccountCode || ''
  if (type === 'OUTPUT_TAX') return companySettings.outputTaxAccountCode || ''
  return ''
}
const totalsText = computed(() => {
  const tpl = voucherText.value.totals.prefix
  const debitText = formatAmountDisplay(sumDebit.value)
  const creditText = formatAmountDisplay(sumCredit.value)
  if (saveClicked.value) return tpl ? tpl.replace('{debit}', debitText).replace('{credit}', creditText) : `Debit: ${debitText} / Credit: ${creditText}`
  if (tpl) {
    return tpl.replace('{debit}', debitText).replace('{credit}', creditText)
  }
  return `Debit: ${debitText} / Credit: ${creditText}`
})

// 科目规则缓存与计算
const codeToRules = new Map<string, any>()
function getRules(code: string) { return codeToRules.get(code) || null }
function getTaxType(code: string) {
  const rules = getRules(code)
  return rules?.taxType || 'NON_TAX'
}
function needsTax(row: any) {
  const type = row?.taxType || getTaxType(row?.accountCode || '')
  return type === 'INPUT_TAX' || type === 'OUTPUT_TAX'
}
function shouldShowTax(row: any) {
  return needsTax(row)
}
function isVisible(row: any, field: string) {
  const r = getRules(row.accountCode)
  if (!r || !r.fieldRules) return true
  const state = r.fieldRules[field]
  return state !== 'hidden'
}
function isRequired(row: any, field: string) {
  const r = getRules(row.accountCode)
  if (!r || !r.fieldRules) return false
  return r.fieldRules[field] === 'required'
}

watch(() => model.header.currency, () => {
  model.lines.forEach((l:any) => {
    ensureLineShape(l)
    l.grossAmount = roundCurrency(l.grossAmount)
    l.taxAmount = roundCurrency(l.taxAmount)
    syncNetAmount(l)
  })
})

watch(() => model.lines.map((l: any) => l.accountCode), (codes) => {
  codes.forEach((code: string, idx: number) => {
    const line = ensureLineShape(model.lines[idx])
    const type = getTaxType(code || '')
    if (line.taxType !== type) line.taxType = type
    if (!needsTax(line)) {
      line.taxAmount = 0
      syncNetAmount(line)
      return
    }
    if (!Number.isFinite(line.taxRate)) line.taxRate = defaultTaxRate
    line.taxRate = sanitizeRate(line.taxRate)
    const tax = calcTaxFromGross(line.grossAmount, line.taxRate)
    line.taxAmount = clampTaxAmount(line.grossAmount, tax)
    syncNetAmount(line)
  })
})

watch(() => model.header.postingDate, (val: string) => {
  if (!isMounted.value) return
  if (!val) {
    periodState.isOpen = true
    periodState.message = ''
    return
  }
  checkPostingPeriod(val)
}, { immediate: true })

watch(() => [
  model.header.invoiceRegistrationStatus,
  model.header.invoiceRegistrationEffectiveFrom,
  model.header.invoiceRegistrationEffectiveTo
], () => {
  updateInvoiceStatusFromHeader()
})

watch(() => voucherText.value.messages, () => {
  updateInvoiceStatusFromHeader()
})

function addLine() {
  const line = createLine()
  ensureLineShape(line)
  model.lines.push(line)
  saveClicked.value = false
}
function removeLine(i: number) {
  if (model.lines.length <= 1) {
    Object.assign(model.lines[0], createLine())
    ensureLineShape(model.lines[0])
    return
  }
  model.lines.splice(i, 1)
  if (!model.lines.some((l:any) => l.drcr === 'DR')) {
    model.lines[0].drcr = 'DR'
  }
  if (!model.lines.some((l:any) => l.drcr === 'CR')) {
    model.lines[model.lines.length - 1].drcr = 'CR'
  }
  saveClicked.value = false
}
function reset() {
  Object.assign(model.header, initialHeader)
  clearInvoiceStatus()
  const first = createLine()
  const second = Object.assign(createLine(), { drcr: 'CR' })
  ensureLineShape(first)
  ensureLineShape(second)
  model.lines.splice(0, model.lines.length, first, second)
  model.lines.forEach(syncNetAmount)
  message.value = ''
  error.value = ''
  saveClicked.value = false
  periodState.isOpen = true
  periodState.message = ''
  attachments.value = [] // 清空附件
  savedVoucherId.value = ''
  savedVoucherNo.value = ''
}

async function loadUi() {
  try {
    await api.get('/schemas/voucher')
    // 这里可根据 r.data.ui 渲染控件/布局，当前先做最小表单可用
  } catch {}
}

async function loadCompanySettings() {
  try {
    pendingRequests++
    const resp = await api.post('/objects/company_setting/search', {
      page: 1,
      pageSize: 1,
      where: [],
      orderBy: [{ field: 'created_at', dir: 'DESC' }]
    })
    if (!isMounted.value) return
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    if (rows.length > 0) {
      const payload = rows[0].payload || {}
      companySettings.inputTaxAccountCode = payload.inputTaxAccountCode || ''
      companySettings.outputTaxAccountCode = payload.outputTaxAccountCode || ''
    }
  } catch (err) {
    console.error('load company settings failed', err)
    if (!isMounted.value) return
    companySettings.inputTaxAccountCode = ''
    companySettings.outputTaxAccountCode = ''
  } finally {
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}

async function searchAccounts(query: string) {
  loadingAccounts.value = true
  try {
    pendingRequests++
    const where: any[] = []
    const q = query?.trim()
    if (q) {
      // 使用 anyOf 实现 OR 逻辑：名称包含 OR 科目代码包含
      where.push({
        anyOf: [
          { json: 'name', op: 'contains', value: q },
          { field: 'account_code', op: 'contains', value: q }
        ]
      })
    }
    // pageSize: 0 表示不限制，全量返回
    const dsl = { where, page: 1, pageSize: 0 }
    const r = await api.post('/objects/account/search', dsl)
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    accountOptions.value = rows.map((x:any) => ({
      label: `${x.payload?.name || ''} (${x.account_code})`,
      value: x.account_code
    }))
    // 缓存规则: 从返回数据的 payload 读取 openItem/openItemBaseline/fieldRules
    rows.forEach((x:any) => {
      codeToRules.set(x.account_code, {
        openItem: !!x.payload?.openItem,
        openItemBaseline: x.payload?.openItemBaseline || 'NONE',
        fieldRules: x.payload?.fieldRules || {},
        taxType: x.payload?.taxType || 'NON_TAX'
      })
    })
  } catch {
    accountOptions.value = []
  } finally {
    loadingAccounts.value = false
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}

async function searchCustomers(query: string) {
  loadingCustomers.value = true
  try {
    pendingRequests++
    const base = [{ field: 'flag_customer', op: 'eq', value: true }]
    const where = query?.trim() ? [ ...base, { json: 'name', op: 'contains', value: query } ] : base
    const r = await api.post('/objects/businesspartner/search', { where, page:1, pageSize: query?.trim() ? 50 : 0 })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    customerOptions.value = rows.map((x:any) => ({ label: `${x.payload?.name || ''} (${x.partner_code})`, value: x.partner_code }))
  } catch { customerOptions.value = [] } finally { loadingCustomers.value = false; pendingRequests = Math.max(0, pendingRequests - 1) }
}

async function searchVendors(query: string) {
  loadingVendors.value = true
  try {
    pendingRequests++
    const base = [{ field: 'flag_vendor', op: 'eq', value: true }]
    const where = query?.trim() ? [ ...base, { json: 'name', op: 'contains', value: query } ] : base
    const r = await api.post('/objects/businesspartner/search', { where, page:1, pageSize: query?.trim() ? 50 : 0 })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    vendorOptions.value = rows.map((x:any) => ({ label: `${x.payload?.name || ''} (${x.partner_code})`, value: x.partner_code }))
  } catch { vendorOptions.value = [] } finally { loadingVendors.value = false; pendingRequests = Math.max(0, pendingRequests - 1) }
}

async function searchAssets(query: string) {
  loadingAssets.value = true
  try {
    pendingRequests++
    let url = '/fixed-assets/assets'
    if (query?.trim()) {
      url += `?assetName=${encodeURIComponent(query)}`
    }
    const r = await api.get(url)
    const rows = Array.isArray(r.data) ? r.data : []
    assetOptions.value = rows.map((x:any) => ({ 
      label: `${x.asset_no} ${x.asset_name || ''}`, 
      value: x.id 
    }))
  } catch { assetOptions.value = [] } finally { loadingAssets.value = false; pendingRequests = Math.max(0, pendingRequests - 1) }
}

function isAssetAccountSelected(row: any): boolean {
  // 使用和其他字段（vendorId、customerId等）相同的逻辑
  return isVisible(row, 'assetId')
}

async function searchDepartments(query: string) {
  loadingDepartments.value = true
  try {
    pendingRequests++
    const q = (query || '').trim()
    const where: any[] = []
    if (q) {
      where.push({ anyOf: [{ json: 'name', op: 'contains', value: q }, { field: 'department_code', op: 'contains', value: q }] })
    }
    const r = await api.post('/objects/department/search', { where, page: 1, pageSize: q ? 50 : 0, orderBy: [{ field: 'department_code', dir: 'ASC' }] })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    departmentOptions.value = rows.map((x:any) => {
      const name = x.payload?.name || x.name || ''
      const code = x.department_code || x.payload?.code || ''
      return { label: `${name} (${code})`, value: x.id || code }
    })
    return departmentOptions.value
  } catch {
    departmentOptions.value = []
  } finally {
    loadingDepartments.value = false
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}

async function searchEmployees(query: string) {
  loadingEmployees.value = true
  try {
    pendingRequests++
    const q = (query || '').trim()
    const where: any[] = []
    if (q) {
      where.push({ anyOf: [{ json: 'nameKanji', op: 'contains', value: q }, { json: 'nameKana', op: 'contains', value: q }, { field: 'employee_code', op: 'contains', value: q }] })
    }
    const r = await api.post('/objects/employee/search', { where, page: 1, pageSize: q ? 50 : 0, orderBy: [{ field: 'employee_code', dir: 'ASC' }] })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    employeeOptions.value = rows.map((x:any) => {
      const name = x.payload?.nameKanji || x.payload?.name || x.name || ''
      const code = x.employee_code || x.payload?.code || ''
      return { label: `${name} (${code})`, value: x.id || code }
    })
    return employeeOptions.value
  } catch {
    employeeOptions.value = []
  } finally {
    loadingEmployees.value = false
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}

async function save() {
  if (saving.value) return
  error.value = ''
  message.value = ''
  saving.value = true
  saveClicked.value = true
  try {
    const normalizedVoucherType = (model.header.voucherType || '').toString().trim()
    const messages = voucherText.value.messages || {}
    const messagesAny = messages as Record<string, string | undefined>
    if (!normalizedVoucherType) {
      throw new Error(messages.voucherTypeRequired || '伝票種別を選択してください。')
    }

    const normalizedCurrency = (model.header.currency || '').toString().trim().toUpperCase()
    if (!normalizedCurrency) {
      throw new Error(messagesAny.currencyRequired ?? '通貨を選択してください。')
    }
    if (!allowedCurrencies.includes(normalizedCurrency)) {
      throw new Error(messagesAny.currencyInvalid ?? 'サポートされていない通貨です。')
    }
    model.header.currency = normalizedCurrency

    const header: {
      companyCode: string
      postingDate: string
      voucherType: string
      currency: string
      summary: string
      invoiceRegistrationNo?: string
    } = {
      companyCode: model.header.companyCode,
      postingDate: model.header.postingDate,
      voucherType: normalizedVoucherType,
      currency: normalizedCurrency,
      summary: model.header.summary
    }

    const invoiceNo = normalizeInvoiceNo(model.header.invoiceRegistrationNo || '')
    if (invoiceNo) {
      if (!invoiceNoPattern.test(invoiceNo)) {
        throw new Error(voucherText.value.messages.invoiceInvalid || 'インボイス登録番号の形式が正しくありません')
      }
      header.invoiceRegistrationNo = invoiceNo
    } else {
      delete header.invoiceRegistrationNo
    }

    let missingTaxAccountType: string | null = null
    const sanitizedLines = model.lines.map((line: any, index: number) => {
      const current = ensureLineShape(line)
      const gross = roundCurrency(Number(current.grossAmount ?? current.amount ?? 0))
      const taxNeeded = needsTax(current)
      const taxAmount = taxNeeded ? clampTaxAmount(gross, Number(current.taxAmount || 0)) : 0
      const net = taxNeeded ? roundCurrency(gross - taxAmount) : gross

      const next: any = {
        lineNo: index + 1,
        accountCode: current.accountCode,
        drcr: current.drcr,
        amount: Number(net),
        customerId: current.customerId,
        vendorId: current.vendorId,
        departmentId: current.departmentId,
        employeeId: current.employeeId,
        paymentDate: current.paymentDate,
        note: current.note
      }

      ;['customerId','vendorId','departmentId','employeeId','paymentDate','note'].forEach((key) => {
        if (next[key] === '' || typeof next[key] === 'undefined') next[key] = null
      })

      if (taxNeeded) {
        const rateDecimal = sanitizeRate(current.taxRate) / 100
        const normalizedTaxAmount = roundCurrency(taxAmount)
        const taxSide = (taxSideOf(current) || current.drcr || 'DR') as string
        const taxAccountCode = taxAccountCodeFor(current)
        if (!taxAccountCode && !missingTaxAccountType) {
          missingTaxAccountType = current.taxType || taxSide
        }
        if (rateDecimal > 0 || normalizedTaxAmount !== 0) {
          next.tax = {
            rate: rateDecimal,
            amount: Number(normalizedTaxAmount),
            side: taxSide,
            accountCode: taxAccountCode,
            taxType: current.taxType || 'NON_TAX',
            baseLineNo: index + 1
          }
        }
      }

      return next
    })

    if (missingTaxAccountType) {
      const errKey = missingTaxAccountType === 'OUTPUT_TAX' ? voucherText.value.messages.missingOutputTaxAccount : voucherText.value.messages.missingInputTaxAccount
      throw new Error(errKey || 'missing tax account')
    }

    const payload: any = {
      header,
      lines: sanitizedLines
    }

    // 添加附件信息
    if (attachments.value.length > 0) {
      payload.attachments = attachments.value.map(att => ({
        id: att.id,
        name: att.name,
        contentType: att.contentType,
        size: att.size,
        blobName: att.blobName,
        url: att.url,
        uploadedAt: att.uploadedAt,
        sha256: att.sha256
      }))
    }

    // 重要：如果本页面已创建过一次凭证，则后续保存应更新同一凭证，避免产生第二张“几乎相同”的凭证
    const resp = savedVoucherId.value
      ? await api.put(`/vouchers/${savedVoucherId.value}`, { payload })
      : await api.post('/objects/voucher', { payload })

    const data = resp.data || {}
    const no = data?.payload?.header?.voucherNo || savedVoucherNo.value || ''
    const pd = data?.payload?.header?.postingDate || model.header.postingDate
    const vt = data?.payload?.header?.voucherType || model.header.voucherType
    const savedHeader = data?.payload?.header || {}
    const returnedId = (data?.id || data?.voucherId || data?.voucher_id || '').toString()
    if (!savedVoucherId.value && returnedId) savedVoucherId.value = returnedId
    if (no) savedVoucherNo.value = no
    if (savedHeader) {
      model.header.invoiceRegistrationNo = savedHeader.invoiceRegistrationNo || header.invoiceRegistrationNo || ''
      model.header.invoiceRegistrationStatus = savedHeader.invoiceRegistrationStatus || ''
      model.header.invoiceRegistrationName = savedHeader.invoiceRegistrationName || ''
      model.header.invoiceRegistrationNameKana = savedHeader.invoiceRegistrationNameKana || ''
      model.header.invoiceRegistrationCheckedAt = savedHeader.invoiceRegistrationCheckedAt || ''
      model.header.invoiceRegistrationEffectiveFrom = savedHeader.invoiceRegistrationEffectiveFrom || ''
      model.header.invoiceRegistrationEffectiveTo = savedHeader.invoiceRegistrationEffectiveTo || ''
      lastVerifiedInvoiceNo.value = model.header.invoiceRegistrationNo || ''
      updateInvoiceStatusFromHeader(model.header, savedHeader)
    }

    // 保存成功後、フォームをリセットして閉じる
    // 後続の編集は凭証一覧の詳細編集画面（VoucherDetailBody）で行う
    reset()

    const savedTpl = voucherText.value.messages.saved || ''
    message.value = savedTpl ? savedTpl.replace('{no}', no) : no
    try {
      const postedTpl = voucherText.value.messages.posted || ''
      const text = postedTpl
        .replace('{date}', pd || '')
        .replace('{type}', voucherTypeLabel(vt))
        .replace('{no}', no)
      emit('done', { kind:'voucher.created', voucherNo:no, postingDate:pd, voucherType:vt, message:text, status:'success' })
    } catch {}
  } catch (e: any) {
    const details = e?.response?.data?.details
    const baseErr = e?.response?.data?.error || e?.message || voucherText.value.messages.error
    const fullError = details ? `${baseErr}\n${JSON.stringify(details)}` : baseErr
    error.value = fullError
    emit('done', { kind:'voucher.failed', message: fullError, status: 'error' })
    try { console.error('save voucher failed:', e?.response?.data || e) } catch {}
  } finally {
    saving.value = false
  }
}

async function checkPostingPeriod(date: string) {
  latestPeriodRequestId += 1
  const token = latestPeriodRequestId
  periodState.isOpen = true
  periodState.message = ''
  if (!date) return
  pendingRequests++
  try {
    const resp = await api.post('/objects/accounting_period/search', {
      page: 1,
      pageSize: 1,
      where: [
        { field: 'period_start', op: 'le', value: date },
        { field: 'period_end', op: 'ge', value: date }
      ],
      orderBy: [{ field: 'period_start', dir: 'DESC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    if (!isMounted.value || token !== latestPeriodRequestId) return
    if (rows.length === 0) {
      // 期間が存在しない = 閉鎖（後端 FinanceService.EnsureVoucherCreateAllowed と同じ口径）
      periodState.isOpen = false
      periodState.message = voucherText.value.messages.periodClosed || '会計期間が開いていません。会計期間を開いてから再試行してください。'
    } else {
      const row = rows[0]
      const payload = row.payload || {}
      const isOpen = payload.isOpen !== false && row.is_open !== false
      periodState.isOpen = !!isOpen
      periodState.message = isOpen ? '' : (voucherText.value.messages.periodClosed || '会計期間が閉じています。摘要などのテキストのみ変更できます。')
    }
  } catch (err) {
    console.error('check posting period failed', err)
    if (token === latestPeriodRequestId) {
      periodState.isOpen = true
      periodState.message = ''
    }
  } finally {
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}

onMounted(async () => {
  await Promise.all([loadUi(), loadCompanySettings()])
  await Promise.all([searchDepartments(''), searchEmployees('')])
  // 默认日期今天
  const d = new Date()
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  model.header.postingDate = `${yyyy}-${mm}-${dd}`
  updateInvoiceStatusFromHeader()
  loading.value = false
})

onBeforeUnmount(() => {
  isMounted.value = false
})

// 供 ChatKit 预填意图调用：支持 header.currency/postingDate/summary 与第一行 amount/drcr
function applyIntent(payload:any){
  try{
    pendingRequests++
    if (!payload || typeof payload!=='object') return
    if (payload.header){
      const h = payload.header
      if (typeof h.currency==='string') model.header.currency = h.currency
      if (typeof h.postingDate==='string') model.header.postingDate = h.postingDate
      if (typeof h.summary==='string') model.header.summary = h.summary
      if (typeof h.invoiceRegistrationNo === 'string') {
        model.header.invoiceRegistrationNo = normalizeInvoiceNo(h.invoiceRegistrationNo)
        lastVerifiedInvoiceNo.value = model.header.invoiceRegistrationNo
      }
      if (typeof h.invoiceRegistrationStatus === 'string') model.header.invoiceRegistrationStatus = h.invoiceRegistrationStatus
      if (typeof h.invoiceRegistrationName === 'string') model.header.invoiceRegistrationName = h.invoiceRegistrationName
      if (typeof h.invoiceRegistrationNameKana === 'string') model.header.invoiceRegistrationNameKana = h.invoiceRegistrationNameKana
      if (typeof h.invoiceRegistrationCheckedAt === 'string') model.header.invoiceRegistrationCheckedAt = h.invoiceRegistrationCheckedAt
      if (typeof h.invoiceRegistrationEffectiveFrom === 'string') model.header.invoiceRegistrationEffectiveFrom = h.invoiceRegistrationEffectiveFrom
      if (typeof h.invoiceRegistrationEffectiveTo === 'string') model.header.invoiceRegistrationEffectiveTo = h.invoiceRegistrationEffectiveTo
      updateInvoiceStatusFromHeader(model.header, h)
    }
    if (Array.isArray(payload.lines) && payload.lines.length>0){
      const l0 = payload.lines[0]||{}
      if (typeof l0.amount==='number') {
        model.lines[0].grossAmount = roundCurrency(l0.amount)
        model.lines[0].taxAmount = 0
        syncNetAmount(model.lines[0])
      }
      if (typeof l0.drcr==='string') model.lines[0].drcr = l0.drcr
      if (typeof l0.accountCode==='string') model.lines[0].accountCode = l0.accountCode
    }
  }catch{}
  finally {
    pendingRequests = Math.max(0, pendingRequests - 1)
  }
}
defineExpose({ applyIntent })
</script>

<style scoped>
.page.page-wide { max-width: 1200px; }
.form-row{ display:flex; gap:16px; margin-bottom:16px }
.form-row .el-form-item{ flex:1 }
.lines{ margin-top:24px }
.line-actions { margin: 12px 0; display: flex; justify-content: flex-end; }
.totals { margin: 12px 0; font-weight: 600; display:flex; gap:8px; align-items:center; flex-wrap:wrap }
.totals.warn { color: #d93025; }
.totals .tag { font-size:12px; padding:2px 6px; border-radius:4px; background:#e0f2fe; color:#0369a1 }
.totals.warn .tag { background:#fee2e2; color:#b91c1c }
.msgs { margin-top: 8px; }
.ok { color: #1a73e8; }
.err { color: #d93025; }
.amount-cell { display: flex; flex-direction: column; gap: 6px; }
.gross-input { max-width: 160px; }
.tax-box { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; }
.tax-item { display: flex; align-items: center; gap: 6px; width: 160px; }
.tax-label { font-size: 12px; color: #4b5563; min-width: 48px; white-space: nowrap; }
.tax-rate-input { display: flex; align-items: center; gap: 4px; flex: 1; }
.tax-rate-input :deep(.el-input-number) { width: 100%; }
.tax-rate-unit { font-size: 12px; color: #4b5563; }
.tax-amount-input { width: 100%; max-width: none; }
.period-warning { margin-top: 6px; font-size: 12px; color: #d93025; }
.invoice-field {
  display: flex;
  align-items: center;
  gap: 8px;
}
.invoice-status { margin-top: 6px; font-size: 12px; }
.invoice-status.status-success { color: #15803d; }
.invoice-status.status-warning { color: #b45309; }
.invoice-status.status-error { color: #d93025; }
.voucher-header-form .el-form-item { margin-bottom: 12px; }
.voucher-header-form .el-form-item.nowrap-label .el-form-item__label { white-space: nowrap; }
.voucher-header-form :deep(.header-row) {
  padding-left: 12px;
}
.voucher-header-form :deep(.align-grid) {
  padding-left: 20px;
}

.voucher-header-form :deep(.align-offset .el-form-item__label) {
  padding-left: 8px;
}

.voucher-header-form :deep(.nowrap-label .el-form-item__label) {
  white-space: nowrap;
}

.voucher-no-tag{
  margin-right: 8px;
}

/* 附件区域样式 */
.attachments-section {
  margin-top: 24px;
  padding: 16px;
  background: #fafbfc;
  border-radius: 8px;
  border: 1px solid #e8e8e8;
}

.attachments-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.attachments-title {
  font-weight: 600;
  font-size: 14px;
  color: #333;
}

.attachments-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.attachment-card {
  width: 120px;
  background: #fff;
  border-radius: 8px;
  border: 1px solid #e0e0e0;
  overflow: hidden;
  transition: all 0.2s ease;
}

.attachment-card.clickable {
  cursor: pointer;
}

.attachment-card.clickable:hover {
  border-color: #409eff;
  box-shadow: 0 2px 8px rgba(64, 158, 255, 0.15);
  transform: translateY(-2px);
}

.attachment-thumb {
  width: 100%;
  height: 80px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #f5f7fa;
  overflow: hidden;
}

.attachment-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.attachment-thumb.file-icon {
  flex-direction: column;
  gap: 4px;
  color: #909399;
}

.attachment-thumb .file-ext {
  font-size: 10px;
  font-weight: 600;
  color: #606266;
  text-transform: uppercase;
}

.attachment-info {
  padding: 8px;
}

.attachment-name {
  font-size: 12px;
  color: #333;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin-bottom: 4px;
}

.attachment-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.attachment-size {
  font-size: 11px;
  color: #999;
}

.attachments-empty {
  text-align: center;
  color: #999;
  font-size: 13px;
  padding: 12px 0;
}

/* 图片预览弹窗样式 */
.voucher-image-preview-dialog {
  text-align: center;
}

.voucher-image-preview-dialog .preview-image {
  max-width: 80vw;
  max-height: 80vh;
  border-radius: 12px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

/* 文件预览弹窗样式 */
:deep(.voucher-file-preview-dialog) {
  border-radius: 12px;
  overflow: hidden;
}

:deep(.voucher-file-preview-dialog .el-dialog) {
  max-height: 96vh;
}

:deep(.voucher-file-preview-dialog .el-dialog__body) {
  padding: 0 !important;
}

.file-preview-container {
  height: 88vh;
  width: 100%;
  background: #111827;
}

.file-preview-iframe {
  width: 100%;
  height: 100%;
  border: none;
  display: block;
}

.file-download-prompt {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 300px;
  padding: 40px;
  text-align: center;
}

.file-download-prompt .file-name {
  margin-top: 16px;
  font-size: 16px;
  font-weight: 600;
  color: #333;
  word-break: break-all;
}

.file-download-prompt .file-hint {
  margin: 8px 0 24px;
  font-size: 14px;
  color: #909399;
}
</style>



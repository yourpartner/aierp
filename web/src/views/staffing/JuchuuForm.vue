<template>
  <div class="juchuu-form" v-loading="loading">

    <!-- Step 1: 注文書アップロード（新規登録時のみ） -->
    <div v-if="step === 'upload'" class="step-upload">
      <div class="step-header">
        <el-icon class="step-header__icon" color="#409eff"><Document /></el-icon>
        <div>
          <div class="step-header__title">注文書をアップロード</div>
          <div class="step-header__desc">顧客から受け取った注文書（PDF・画像）をアップロードすると、AIが自動で内容を読み取ります</div>
        </div>
      </div>

      <el-upload
        drag
        :auto-upload="false"
        accept=".pdf,.png,.jpg,.jpeg"
        :on-change="onFileChange"
        :show-file-list="false"
        class="upload-area"
      >
        <el-icon class="el-icon--upload" style="font-size:48px;color:#c0c4cc"><UploadFilled /></el-icon>
        <div class="el-upload__text">
          ここにドラッグ、または<em>クリックしてファイルを選択</em>
        </div>
        <template #tip>
          <div class="el-upload__tip">PDF・PNG・JPG / 10MB以内</div>
        </template>
      </el-upload>

      <div v-if="selectedFile" class="selected-file">
        <el-icon><Paperclip /></el-icon>
        <span>{{ selectedFile.name }}</span>
        <el-button link type="danger" @click="selectedFile = null"><el-icon><Delete /></el-icon></el-button>
      </div>

      <el-divider>または</el-divider>

      <div class="skip-hint">
        <el-text type="info">注文書がない場合、または後でアップロードする場合はスキップできます</el-text>
      </div>
    </div>

    <!-- Step 2: 受注内容入力（新規・編集共通） -->
    <div v-if="step === 'form'">
      <!-- OCR解析結果バナー（ある場合） -->
      <el-alert
        v-if="ocrApplied"
        type="success"
        :closable="true"
        show-icon
        style="margin-bottom:16px"
      >
        <template #title>OCRで読み取った情報を反映しました。内容を確認して保存してください。</template>
      </el-alert>

      <el-form :model="form" :rules="rules" ref="formRef" label-width="96px" label-position="right">

        <!-- 基本情報 -->
        <el-divider content-position="left">基本情報</el-divider>
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="顧客" prop="clientPartnerId">
              <el-select
                v-model="form.clientPartnerId"
                filterable remote clearable
                :remote-method="searchClients"
                @focus="loadClientsDefault"
                placeholder="顧客を選択"
                style="width:100%"
              >
                <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="契約形態" prop="contractType">
              <el-select v-model="form.contractType" style="width:100%">
                <el-option label="SES（準委任）" value="ses" />
                <el-option label="派遣" value="dispatch" />
                <el-option label="請負" value="contract" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="開始日">
              <el-date-picker v-model="form.startDate" type="date" value-format="YYYY-MM-DD" placeholder="開始日" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="終了日">
              <el-date-picker v-model="form.endDate" type="date" value-format="YYYY-MM-DD" placeholder="終了日" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="ステータス">
              <el-select v-model="form.status" style="width:100%">
                <el-option label="下書き" value="draft" />
                <el-option label="有効" value="active" />
                <el-option label="終了" value="ended" />
                <el-option label="解約" value="terminated" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <!-- 勤務条件 -->
        <el-divider content-position="left">勤務条件</el-divider>
        <el-row :gutter="20">
          <el-col :span="16">
            <el-form-item label="勤務地">
              <el-input v-model="form.workLocation" placeholder="例: 東京都渋谷区..." style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="勤務曜日">
              <el-input v-model="form.workDays" placeholder="月〜金" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="始業">
              <el-time-picker v-model="form.workStartTime" format="HH:mm" value-format="HH:mm" placeholder="09:00" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="終業">
              <el-time-picker v-model="form.workEndTime" format="HH:mm" value-format="HH:mm" placeholder="18:00" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="月間基準時間">
              <el-input-number v-model="form.monthlyWorkHours" :min="0" :max="300" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <!-- 要員明細 -->
        <el-divider content-position="left">
          <span>要員明細</span>
          <el-button
            type="primary" plain size="small" style="margin-left:12px"
            @click="addDetail"
          ><el-icon><Plus /></el-icon>行追加</el-button>
        </el-divider>

        <div class="detail-table-wrap">
          <table class="detail-table" v-if="form.details.length > 0">
            <thead>
              <tr>
                <th style="width:180px">要員</th>
                <th style="width:110px">請求単価</th>
                <th style="width:70px">種別</th>
                <th style="width:80px">精算方式</th>
                <th style="width:140px">精算時間（H）</th>
                <th style="width:100px">精算単価</th>
                <th style="width:100px">控除単価</th>
                <th style="width:36px"></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(detail, idx) in form.details" :key="idx">
                <td>
                  <el-select
                    :model-value="detail.resourceId || detail.resourceName || ''"
                    filterable remote clearable allow-create default-first-option
                    :remote-method="loadResources"
                    placeholder="要員を選択 / 氏名入力"
                    style="width:100%"
                    size="small"
                    @change="(val: string) => onResourceChange(val, detail)"
                    @focus="!resourceLoaded && loadResources()"
                  >
                    <el-option
                      v-for="opt in allResourceOptions"
                      :key="opt.value" :label="opt.label" :value="opt.value"
                    />
                  </el-select>
                </td>
                <td>
                  <el-input-number
                    v-model="detail.billingRate"
                    :precision="0" :step="10000" :min="0"
                    :controls="false"
                    style="width:100%"
                    size="small"
                    placeholder="単価"
                  />
                </td>
                <td>
                  <el-select v-model="detail.billingRateType" style="width:100%" size="small">
                    <el-option label="月額" value="monthly" />
                    <el-option label="日額" value="daily" />
                    <el-option label="時給" value="hourly" />
                  </el-select>
                </td>
                <td>
                  <el-select v-model="detail.settlementType" style="width:100%" size="small">
                    <el-option label="幅精算" value="range" />
                    <el-option label="固定" value="fixed" />
                  </el-select>
                </td>
                <td>
                  <template v-if="detail.settlementType === 'range'">
                    <el-input-number
                      v-model="detail.settlementLowerH"
                      :min="0" :max="300" :controls="false"
                      style="width:65px" size="small"
                    />
                    <span style="margin:0 4px;color:#999">〜</span>
                    <el-input-number
                      v-model="detail.settlementUpperH"
                      :min="0" :max="300" :controls="false"
                      style="width:65px" size="small"
                    />
                  </template>
                  <el-text v-else type="info" size="small">—</el-text>
                </td>
                <td>
                  <el-input-number
                    v-model="detail.settlementRate"
                    :precision="0" :step="100" :min="0"
                    :controls="false"
                    style="width:100%"
                    size="small"
                    placeholder="時間単価"
                  />
                </td>
                <td>
                  <el-input-number
                    v-model="detail.deductionRate"
                    :precision="0" :step="100" :min="0"
                    :controls="false"
                    style="width:100%"
                    size="small"
                    placeholder="控除単価"
                  />
                </td>
                <td>
                  <el-button link type="danger" size="small" @click="removeDetail(idx)">
                    <el-icon><Delete /></el-icon>
                  </el-button>
                </td>
              </tr>
            </tbody>
          </table>
          <div v-else class="detail-empty">
            <el-text type="info">要員が未登録です。「行追加」で要員を追加してください。</el-text>
          </div>
        </div>

        <!-- 添付・備考 -->
        <el-divider content-position="left">添付・備考</el-divider>
        <el-row :gutter="20">
          <el-col :span="24">
            <el-form-item label="特記事項">
              <el-input v-model="form.notes" type="textarea" :rows="3" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <!-- 既存の注文書添付がある場合 -->
        <el-row v-if="form.attachedDocBlobName" :gutter="20">
          <el-col :span="24">
            <el-form-item label="注文書">
              <el-tag type="success">
                <el-icon><Paperclip /></el-icon>
                注文書登録済み
              </el-tag>
              <el-button link size="small" style="margin-left:8px" @click="viewDoc">表示</el-button>
            </el-form-item>
          </el-col>
        </el-row>

      </el-form>
    </div>

    <!-- Step 1 OCR処理中 -->
    <div v-if="step === 'ocr-loading'" class="step-ocr-loading">
      <el-icon class="loading-icon is-loading" style="font-size:48px;color:#409eff"><Loading /></el-icon>
      <div class="loading-text">注文書を解析中です...</div>
      <div class="loading-subtext">AIが注文書の内容を読み取っています。しばらくお待ちください。</div>
    </div>

  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import {
  Document, UploadFilled, Paperclip, Delete, Loading, Plus
} from '@element-plus/icons-vue'
import api from '../../api'

interface DetailRow {
  resourceId: string | null
  resourceName: string | null
  billingRate: number | null
  billingRateType: string
  settlementType: string
  settlementLowerH: number
  settlementUpperH: number
  settlementRate: number | null
  deductionRate: number | null
  notes: string | null
}

const props = defineProps({
  juchuuId: { type: String, default: null }
})
const emit = defineEmits(['saved', 'cancel'])

const step = ref<'upload' | 'ocr-loading' | 'form'>(props.juchuuId ? 'form' : 'upload')

const loading = ref(false)
const saving = ref(false)
const formRef = ref<any>(null)
const selectedFile = ref<any>(null)
const ocrApplied = ref(false)

const clientOptions = ref<any[]>([])
const allResourceOptions = ref<any[]>([])
const resourceLoaded = ref(false)

const form = reactive({
  clientPartnerId: null as string | null,
  contractType: 'ses',
  status: 'active',
  startDate: null as string | null,
  endDate: null as string | null,
  workLocation: null as string | null,
  workDays: '月〜金',
  workStartTime: '09:00',
  workEndTime: '18:00',
  monthlyWorkHours: 160,
  notes: null as string | null,
  attachedDocBlobName: null as string | null,
  details: [] as DetailRow[],
})

const rules = {
  contractType: [{ required: true, message: '契約形態を選択してください', trigger: 'change' }],
}

function newDetailRow(): DetailRow {
  return {
    resourceId: null,
    resourceName: null,
    billingRate: null,
    billingRateType: 'monthly',
    settlementType: 'range',
    settlementLowerH: 140,
    settlementUpperH: 180,
    settlementRate: null,
    deductionRate: null,
    notes: null,
  }
}

function addDetail() {
  form.details.push(newDetailRow())
}

function removeDetail(idx: number) {
  form.details.splice(idx, 1)
}

// ---- ファイル選択 ----
function onFileChange(uploadFile: any) {
  selectedFile.value = uploadFile.raw || uploadFile
}

// ---- 顧客検索 ----
async function searchClients(q: string) {
  const params: any = { limit: 20 }
  if (q) params.keyword = q
  const res = await api.get('/businesspartners', { params })
  clientOptions.value = (res.data.data || []).map((bp: any) => ({ value: bp.id, label: bp.name || bp.partnerCode }))
}

async function loadClientsDefault() {
  if (clientOptions.value.length > 0) return
  await searchClients('')
}

function normalizeName(input?: string | null) {
  if (!input) return ''
  return input
    .replace(/\s+/g, '')
    .replace(/[（）()]/g, '')
    .replace(/^株式会社|株式会社$/g, '')
    .toLowerCase()
}

async function matchClientFromOcr(parsed: any) {
  const candidateName = (parsed?.clientName || '').trim()
  if (!candidateName) return

  const res = await api.get('/businesspartners', { params: { keyword: candidateName, limit: 20 } })
  const options = (res.data.data || []).map((bp: any) => ({
    value: bp.id,
    label: bp.name || bp.partnerCode
  }))
  clientOptions.value = options

  if (options.length === 0) return

  const target = normalizeName(candidateName)
  const best =
    options.find((o: any) => normalizeName(o.label) === target) ||
    options.find((o: any) => normalizeName(o.label).includes(target) || target.includes(normalizeName(o.label)))

  if (best) form.clientPartnerId = best.value
}

async function loadResources(q?: string) {
  const params: any = { limit: 100 }
  if (q) params.keyword = q
  const res = await api.get('/staffing/resources', { params })
  allResourceOptions.value = (res.data.data || []).map((r: any) => ({
    value: r.id,
    label: `${r.displayName || r.resourceCode} (${r.resourceCode})`
  }))
  resourceLoaded.value = true
}

function isUuid(s: string) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s)
}

function onResourceChange(val: string | null, detail: DetailRow) {
  if (!val) {
    detail.resourceId = null
    detail.resourceName = null
  } else if (isUuid(val)) {
    detail.resourceId = val
    const opt = allResourceOptions.value.find(o => o.value === val)
    detail.resourceName = opt ? opt.label.replace(/\s*\([^)]*\)\s*$/, '') : null
  } else {
    detail.resourceId = null
    detail.resourceName = val
  }
}

// ---- 注文書表示 ----
function viewDoc() {
  if (!form.attachedDocBlobName || !props.juchuuId) return
  window.open(`/api/staffing/juchuu/${props.juchuuId}/doc-view`, '_blank')
}

// ---- Step 1: アップロード ＋ OCR ----
async function uploadAndOcr() {
  if (!selectedFile.value) {
    step.value = 'form'
    return
  }
  step.value = 'ocr-loading'

  let newId = props.juchuuId

  try {
    if (!newId) {
      const res = await api.post('/staffing/juchuu', {
        contractType: form.contractType,
        status: 'draft',
      })
      newId = res.data.id
    }

    const formData = new FormData()
    formData.append('file', selectedFile.value)
    const openaiKey = localStorage.getItem('openai_key') || ''
    const headers: Record<string, string> = { 'Content-Type': 'multipart/form-data' }
    if (openaiKey) headers['x-openai-key'] = openaiKey

    const uploadRes = await api.post(`/staffing/juchuu/${newId}/upload-doc`, formData, { headers })
    const parsed = uploadRes.data?.parsed

    if (parsed) {
      applyOcrData(parsed)
      await matchClientFromOcr(parsed)
      ocrApplied.value = true
    } else if (uploadRes.data?.message) {
      ElMessage.warning(uploadRes.data.message)
    }

    form.attachedDocBlobName = uploadRes.data?.blobName || null
  } catch (e: any) {
    ElMessage.warning(`OCR解析に失敗しました。手動で入力してください。（${e.message}）`)
  }

  step.value = 'form'
}

// ---- OCRデータ適用 ----
function applyOcrData(parsed: any) {
  if (parsed.contractType) form.contractType = parsed.contractType
  if (parsed.startDate) form.startDate = parsed.startDate
  if (parsed.endDate) form.endDate = parsed.endDate
  if (parsed.workLocation) form.workLocation = parsed.workLocation
  if (parsed.workDays) form.workDays = parsed.workDays
  if (parsed.workStartTime) form.workStartTime = parsed.workStartTime
  if (parsed.workEndTime) form.workEndTime = parsed.workEndTime
  if (parsed.monthlyWorkHours) form.monthlyWorkHours = parsed.monthlyWorkHours
  if (parsed.notes) form.notes = parsed.notes

  // リソース明細の適用
  if (Array.isArray(parsed.resources) && parsed.resources.length > 0) {
    form.details = parsed.resources.map((r: any) => ({
      resourceId: null,
      resourceName: r.resourceName ?? null,
      billingRate: r.billingRate ?? null,
      billingRateType: r.billingRateType ?? 'monthly',
      settlementType: r.settlementType ?? 'range',
      settlementLowerH: r.settlementLowerH ?? 140,
      settlementUpperH: r.settlementUpperH ?? 180,
      settlementRate: r.settlementRate ?? null,
      deductionRate: r.deductionRate ?? null,
      notes: null,
    }))
  }
}

// ---- Step 2: 保存 ----
async function save() {
  await formRef.value?.validate()
  saving.value = true
  try {
    const payload = {
      clientPartnerId: form.clientPartnerId,
      contractType: form.contractType,
      status: form.status,
      startDate: form.startDate,
      endDate: form.endDate,
      workLocation: form.workLocation,
      workDays: form.workDays,
      workStartTime: form.workStartTime,
      workEndTime: form.workEndTime,
      monthlyWorkHours: form.monthlyWorkHours,
      notes: form.notes,
      details: form.details.map(d => ({
        resourceId: d.resourceId,
        resourceName: d.resourceName,
        billingRate: d.billingRate,
        billingRateType: d.billingRateType,
        settlementType: d.settlementType,
        settlementLowerH: d.settlementLowerH,
        settlementUpperH: d.settlementUpperH,
        settlementRate: d.settlementRate,
        deductionRate: d.deductionRate,
        notes: d.notes,
      })),
    }
    if (props.juchuuId) {
      await api.put(`/staffing/juchuu/${props.juchuuId}`, payload)
    } else {
      await api.post('/staffing/juchuu', payload)
    }
    emit('saved')
  } catch (e: any) {
    ElMessage.error(`保存エラー: ${e.message}`)
  } finally {
    saving.value = false
  }
}

// ---- 読み込み（編集時） ----
async function load() {
  if (!props.juchuuId) return
  loading.value = true
  try {
    const res = await api.get(`/staffing/juchuu/${props.juchuuId}`)
    const data = res.data
    Object.assign(form, {
      clientPartnerId: data.clientPartnerId,
      contractType: data.contractType || 'ses',
      status: data.status || 'active',
      startDate: data.startDate,
      endDate: data.endDate,
      workLocation: data.workLocation,
      workDays: data.workDays || '月〜金',
      workStartTime: data.workStartTime || '09:00',
      workEndTime: data.workEndTime || '18:00',
      monthlyWorkHours: data.monthlyWorkHours ?? 160,
      notes: data.notes,
      attachedDocBlobName: data.attachedDocBlobName,
      details: (data.details || []).map((d: any): DetailRow => ({
        resourceId: d.resourceId ?? null,
        resourceName: d.resourceName ?? null,
        billingRate: d.billingRate ?? null,
        billingRateType: d.billingRateType ?? 'monthly',
        settlementType: d.settlementType ?? 'range',
        settlementLowerH: d.settlementLowerH ?? 140,
        settlementUpperH: d.settlementUpperH ?? 180,
        settlementRate: d.settlementRate ?? null,
        deductionRate: d.deductionRate ?? null,
        notes: d.notes ?? null,
      })),
    })
    if (data.clientPartnerId && data.clientName) {
      clientOptions.value = [{ value: data.clientPartnerId, label: data.clientName }]
    }
    // 既存の要員をオプションに追加（resourceIdがある行）
    const existingOpts: any[] = []
    ;(data.details || []).forEach((d: any) => {
      if (d.resourceId && d.resourceName) {
        existingOpts.push({
          value: d.resourceId,
          label: `${d.resourceName}${d.resourceCode ? ' (' + d.resourceCode + ')' : ''}`
        })
      }
    })
    if (existingOpts.length > 0) {
      allResourceOptions.value = existingOpts
    }
  } catch (e: any) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

function skipToForm() {
  step.value = 'form'
}

defineExpose({ reload: load, step, selectedFile, uploadAndOcr, save, saving, skipToForm })

onMounted(load)
</script>

<style scoped>
.juchuu-form {
  min-height: 200px;
}


/* Step 1: Upload */
.step-upload {
  padding: 8px 0;
}
.step-header {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 20px;
  padding: 14px 16px;
  background: #f0f7ff;
  border-radius: 6px;
  border-left: 4px solid #409eff;
}
.step-header__icon {
  font-size: 24px;
  margin-top: 2px;
  flex-shrink: 0;
}
.step-header__title {
  font-size: 15px;
  font-weight: 600;
  color: #303133;
  margin-bottom: 4px;
}
.step-header__desc {
  font-size: 13px;
  color: #909399;
  line-height: 1.5;
}
.upload-area {
  width: 100%;
}
.upload-area :deep(.el-upload-dragger) {
  padding: 30px;
  width: 100%;
  box-sizing: border-box;
}
.selected-file {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 10px;
  padding: 8px 12px;
  background: #f5f7fa;
  border-radius: 4px;
  color: #303133;
  font-size: 13px;
}
.skip-hint {
  text-align: center;
  padding: 4px 0;
}

/* Step: OCR Loading */
.step-ocr-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 60px 0;
  gap: 16px;
}
.loading-text {
  font-size: 15px;
  font-weight: 600;
  color: #303133;
}
.loading-subtext {
  font-size: 13px;
  color: #909399;
}

/* 要員明細テーブル */
.detail-table-wrap {
  margin: 0 0 8px 0;
  overflow-x: auto;
}
.detail-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}
.detail-table th {
  background: #f5f7fa;
  padding: 8px 6px;
  border: 1px solid #e4e7ed;
  font-weight: 600;
  color: #606266;
  text-align: center;
  white-space: nowrap;
}
.detail-table td {
  padding: 6px 4px;
  border: 1px solid #e4e7ed;
  vertical-align: middle;
}
.detail-table :deep(.el-input-number__decrease),
.detail-table :deep(.el-input-number__increase) {
  display: none;
}
.detail-empty {
  padding: 20px;
  text-align: center;
  border: 1px dashed #d9d9d9;
  border-radius: 4px;
  background: #fafafa;
}

</style>

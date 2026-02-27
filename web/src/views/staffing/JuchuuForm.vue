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

      <el-form :model="form" :rules="rules" ref="formRef" label-width="130px" label-position="right" size="default">

        <!-- 基本情報 -->
        <el-divider content-position="left">基本情報</el-divider>
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="顧客" prop="clientPartnerId">
              <el-select
                v-model="form.clientPartnerId"
                filterable remote clearable
                :remote-method="searchClients"
                placeholder="顧客を選択"
                style="width:100%"
              >
                <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="リソース">
              <el-select
                v-model="form.resourceId"
                filterable remote clearable
                :remote-method="searchResources"
                placeholder="リソースを選択"
                style="width:100%"
              >
                <el-option v-for="opt in resourceOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="契約形態" prop="contractType">
              <el-select v-model="form.contractType" style="width:100%">
                <el-option label="SES（準委任）" value="ses" />
                <el-option label="派遣" value="dispatch" />
                <el-option label="請負" value="contract" />
              </el-select>
            </el-form-item>
          </el-col>
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

        <!-- 請求条件 -->
        <el-divider content-position="left">請求条件</el-divider>
        <el-row :gutter="20">
          <el-col :span="10">
            <el-form-item label="請求単価">
              <el-input-number v-model="form.billingRate" :precision="0" :step="10000" :min="0" style="width:150px" />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="種別">
              <el-select v-model="form.billingRateType" style="width:100%">
                <el-option label="月額" value="monthly" />
                <el-option label="日額" value="daily" />
                <el-option label="時給" value="hourly" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業割増率">
              <el-input-number v-model="form.overtimeRateMultiplier" :precision="2" :step="0.05" :min="1" :max="3" style="width:110px" />
              <el-text type="info" style="margin-left:6px;font-size:12px">倍</el-text>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="10">
            <el-form-item label="精算方式">
              <el-radio-group v-model="form.settlementType">
                <el-radio value="range">幅精算</el-radio>
                <el-radio value="fixed">固定</el-radio>
              </el-radio-group>
            </el-form-item>
          </el-col>
          <el-col :span="14" v-if="form.settlementType === 'range'">
            <el-form-item label="精算時間">
              <el-input-number v-model="form.settlementLowerH" :min="0" :max="300" style="width:100px" />
              <span style="margin:0 8px;color:#999">H 〜</span>
              <el-input-number v-model="form.settlementUpperH" :min="0" :max="300" style="width:100px" />
              <span style="margin-left:6px;color:#999">H</span>
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

        <!-- 注文書・備考 -->
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
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import {
  Document, UploadFilled, Paperclip, Delete, Loading
} from '@element-plus/icons-vue'
import api from '../../api'

const props = defineProps({
  juchuuId: { type: String, default: null }
})
const emit = defineEmits(['saved', 'cancel'])

// ステップ管理: 'upload' | 'ocr-loading' | 'form'
const step = ref<'upload' | 'ocr-loading' | 'form'>(props.juchuuId ? 'form' : 'upload')

const loading = ref(false)
const saving = ref(false)
const uploading = ref(false)
const formRef = ref<any>(null)
const selectedFile = ref<any>(null)
const ocrApplied = ref(false)

const clientOptions = ref<any[]>([])
const resourceOptions = ref<any[]>([])

const form = reactive({
  clientPartnerId: null as string | null,
  resourceId: null as string | null,
  contractType: 'ses',
  status: 'active',
  startDate: null as string | null,
  endDate: null as string | null,
  billingRate: null as number | null,
  billingRateType: 'monthly',
  overtimeRateMultiplier: 1.25,
  settlementType: 'range',
  settlementLowerH: 140,
  settlementUpperH: 180,
  workLocation: null as string | null,
  workDays: '月〜金',
  workStartTime: '09:00',
  workEndTime: '18:00',
  monthlyWorkHours: 160,
  notes: null as string | null,
  attachedDocBlobName: null as string | null,
})

const rules = {
  contractType: [{ required: true, message: '契約形態を選択してください', trigger: 'change' }],
}

// ---- ファイル選択 ----
function onFileChange(uploadFile: any) {
  selectedFile.value = uploadFile.raw || uploadFile
}

// ---- 顧客・リソース検索 ----
async function searchClients(q: string) {
  if (!q) return
  const res = await api.get('/businesspartners', { params: { keyword: q, limit: 20 } })
  clientOptions.value = (res.data.data || []).map((bp: any) => ({ value: bp.id, label: bp.name || bp.partnerCode }))
}

async function searchResources(q: string) {
  if (!q) return
  const res = await api.get('/staffing/resources', { params: { keyword: q, limit: 20 } })
  resourceOptions.value = (res.data.data || []).map((r: any) => ({
    value: r.id,
    label: `${r.displayName || r.resourceCode} (${r.resourceCode})`
  }))
}

// ---- 注文書表示 ----
function viewDoc() {
  if (!form.attachedDocBlobName || !props.juchuuId) return
  window.open(`/api/staffing/juchuu/${props.juchuuId}/doc-view`, '_blank')
}

// ---- Step 1: アップロード ＋ OCR ----
async function uploadAndOcr() {
  if (!selectedFile.value) {
    // ファイルなしでスキップ（手入力）
    step.value = 'form'
    return
  }
  step.value = 'ocr-loading'

  let newId = props.juchuuId

  try {
    // 1. 先にレコードを作成（IDが必要）
    if (!newId) {
      const res = await api.post('/staffing/juchuu', {
        contractType: form.contractType,
        status: 'draft',
      })
      newId = res.data.id
    }

    // 2. PDFアップロード + OCR解析
    const formData = new FormData()
    formData.append('file', selectedFile.value)
    const openaiKey = localStorage.getItem('openai_key') || ''
    const headers: Record<string, string> = { 'Content-Type': 'multipart/form-data' }
    if (openaiKey) headers['x-openai-key'] = openaiKey

    const uploadRes = await api.post(`/staffing/juchuu/${newId}/upload-doc`, formData, { headers })
    const parsed = uploadRes.data?.parsed

    if (parsed) {
      // OCR結果をフォームに反映
      applyOcrData(parsed)
      ocrApplied.value = true
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
  if (parsed.billingRate) form.billingRate = parsed.billingRate
  if (parsed.billingRateType) form.billingRateType = parsed.billingRateType
  if (parsed.overtimeRateMultiplier) form.overtimeRateMultiplier = parsed.overtimeRateMultiplier
  if (parsed.settlementType) form.settlementType = parsed.settlementType
  if (parsed.settlementLowerH) form.settlementLowerH = parsed.settlementLowerH
  if (parsed.settlementUpperH) form.settlementUpperH = parsed.settlementUpperH
  if (parsed.workLocation) form.workLocation = parsed.workLocation
  if (parsed.workDays) form.workDays = parsed.workDays
  if (parsed.workStartTime) form.workStartTime = parsed.workStartTime
  if (parsed.workEndTime) form.workEndTime = parsed.workEndTime
  if (parsed.monthlyWorkHours) form.monthlyWorkHours = parsed.monthlyWorkHours
  if (parsed.notes) form.notes = parsed.notes
}

// ---- Step 2: 保存 ----
async function save() {
  await formRef.value?.validate()
  saving.value = true
  try {
    const payload = {
      clientPartnerId: form.clientPartnerId,
      resourceId: form.resourceId,
      contractType: form.contractType,
      status: form.status,
      startDate: form.startDate,
      endDate: form.endDate,
      billingRate: form.billingRate,
      billingRateType: form.billingRateType,
      overtimeRateMultiplier: form.overtimeRateMultiplier,
      settlementType: form.settlementType,
      settlementLowerH: form.settlementLowerH,
      settlementUpperH: form.settlementUpperH,
      workLocation: form.workLocation,
      workDays: form.workDays,
      workStartTime: form.workStartTime,
      workEndTime: form.workEndTime,
      monthlyWorkHours: form.monthlyWorkHours,
      notes: form.notes,
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
      resourceId: data.resourceId,
      contractType: data.contractType || 'ses',
      status: data.status || 'active',
      startDate: data.startDate,
      endDate: data.endDate,
      billingRate: data.billingRate,
      billingRateType: data.billingRateType || 'monthly',
      overtimeRateMultiplier: data.overtimeRateMultiplier ?? 1.25,
      settlementType: data.settlementType || 'range',
      settlementLowerH: data.settlementLowerH ?? 140,
      settlementUpperH: data.settlementUpperH ?? 180,
      workLocation: data.workLocation,
      workDays: data.workDays || '月〜金',
      workStartTime: data.workStartTime || '09:00',
      workEndTime: data.workEndTime || '18:00',
      monthlyWorkHours: data.monthlyWorkHours ?? 160,
      notes: data.notes,
      attachedDocBlobName: data.attachedDocBlobName,
    })
    if (data.clientPartnerId && data.clientName) {
      clientOptions.value = [{ value: data.clientPartnerId, label: data.clientName }]
    }
    if (data.resourceId && data.resourceName) {
      resourceOptions.value = [{ value: data.resourceId, label: `${data.resourceName} (${data.resourceCode})` }]
    }
  } catch (e: any) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

// 外部から step 制御・save 呼び出せるように expose
defineExpose({ step, uploadAndOcr, save, isUploading: () => step.value === 'ocr-loading' })

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
</style>

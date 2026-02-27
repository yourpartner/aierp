<template>
  <div class="juchuu-form" v-loading="loading">
    <!-- タブ切り替え -->
    <el-tabs v-model="activeTab">
      <el-tab-pane label="基本情報" name="basic">
        <el-form :model="form" :rules="rules" ref="formRef" label-width="140px" size="default">
          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="顧客" prop="clientPartnerId">
                <el-select
                  v-model="form.clientPartnerId"
                  filterable
                  remote
                  clearable
                  :remote-method="searchClients"
                  placeholder="顧客を選択"
                  style="width:100%"
                >
                  <el-option
                    v-for="opt in clientOptions"
                    :key="opt.value"
                    :label="opt.label"
                    :value="opt.value"
                  />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="リソース" prop="resourceId">
                <el-select
                  v-model="form.resourceId"
                  filterable
                  remote
                  clearable
                  :remote-method="searchResources"
                  placeholder="リソースを選択"
                  style="width:100%"
                >
                  <el-option
                    v-for="opt in resourceOptions"
                    :key="opt.value"
                    :label="opt.label"
                    :value="opt.value"
                  />
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
              <el-form-item label="ステータス" prop="status">
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
              <el-form-item label="開始日" prop="startDate">
                <el-date-picker
                  v-model="form.startDate"
                  type="date"
                  placeholder="開始日"
                  value-format="YYYY-MM-DD"
                  style="width:100%"
                />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="終了日" prop="endDate">
                <el-date-picker
                  v-model="form.endDate"
                  type="date"
                  placeholder="終了日"
                  value-format="YYYY-MM-DD"
                  style="width:100%"
                />
              </el-form-item>
            </el-col>
          </el-row>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="請求条件" name="billing">
        <el-form :model="form" label-width="160px" size="default">
          <el-row :gutter="20">
            <el-col :span="14">
              <el-form-item label="請求単価">
                <el-input-number
                  v-model="form.billingRate"
                  :precision="0"
                  :step="10000"
                  :min="0"
                  style="width:160px"
                />
                <span style="margin-left:8px">円</span>
              </el-form-item>
            </el-col>
            <el-col :span="10">
              <el-form-item label="単価種別">
                <el-select v-model="form.billingRateType" style="width:110px">
                  <el-option label="月額" value="monthly" />
                  <el-option label="日額" value="daily" />
                  <el-option label="時給" value="hourly" />
                </el-select>
              </el-form-item>
            </el-col>
          </el-row>

          <el-form-item label="残業割増率">
            <el-input-number
              v-model="form.overtimeRateMultiplier"
              :precision="2"
              :step="0.05"
              :min="1.0"
              :max="3.0"
              style="width:130px"
            />
            <span style="margin-left:8px;color:#999">倍（例: 1.25）</span>
          </el-form-item>

          <el-form-item label="精算方式">
            <el-radio-group v-model="form.settlementType">
              <el-radio value="range">幅精算</el-radio>
              <el-radio value="fixed">固定</el-radio>
            </el-radio-group>
          </el-form-item>

          <div v-if="form.settlementType === 'range'">
            <el-form-item label="精算下限 (H)">
              <el-input-number v-model="form.settlementLowerH" :min="0" :max="300" style="width:120px" />
              <span style="margin:0 8px">H 〜</span>
              <el-input-number v-model="form.settlementUpperH" :min="0" :max="300" style="width:120px" />
              <span style="margin-left:8px">H</span>
            </el-form-item>
          </div>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="勤務条件" name="work">
        <el-form :model="form" label-width="160px" size="default">
          <el-form-item label="勤務地">
            <el-input v-model="form.workLocation" placeholder="例: 東京都渋谷区..." style="width:360px" />
          </el-form-item>
          <el-form-item label="勤務曜日">
            <el-input v-model="form.workDays" placeholder="例: 月〜金" style="width:200px" />
          </el-form-item>
          <el-row :gutter="20">
            <el-col :span="12">
              <el-form-item label="始業時間">
                <el-time-picker v-model="form.workStartTime" format="HH:mm" value-format="HH:mm" placeholder="09:00" style="width:130px" />
              </el-form-item>
            </el-col>
            <el-col :span="12">
              <el-form-item label="終業時間">
                <el-time-picker v-model="form.workEndTime" format="HH:mm" value-format="HH:mm" placeholder="18:00" style="width:130px" />
              </el-form-item>
            </el-col>
          </el-row>
          <el-form-item label="月間基準時間">
            <el-input-number v-model="form.monthlyWorkHours" :min="0" :max="300" style="width:120px" />
            <span style="margin-left:8px">H</span>
          </el-form-item>
          <el-form-item label="特記事項">
            <el-input v-model="form.notes" type="textarea" :rows="3" style="width:100%" />
          </el-form-item>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="注文書PDF" name="doc">
        <div class="doc-section">
          <div class="doc-upload-area">
            <el-upload
              ref="uploadRef"
              :action="uploadUrl"
              :headers="uploadHeaders"
              :on-success="onUploadSuccess"
              :on-error="onUploadError"
              :before-upload="beforeUpload"
              :show-file-list="false"
              accept=".pdf,.png,.jpg,.jpeg"
              :auto-upload="false"
              drag
            >
              <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
              <div class="el-upload__text">
                注文書PDF/画像をドラッグ<em>またはクリックして選択</em>
              </div>
              <template #tip>
                <div class="el-upload__tip">PDF・PNG・JPG / 10MB以内 / OCRで自動解析します</div>
              </template>
            </el-upload>
            <el-button
              v-if="selectedFile"
              type="primary"
              :loading="uploading"
              @click="doUpload"
              style="margin-top:12px"
            >
              <el-icon><Upload /></el-icon>
              アップロード & OCR解析
            </el-button>
            <div v-if="selectedFile && !uploading" style="margin-top:8px;color:#666;font-size:13px">
              選択中: {{ selectedFile.name }}
            </div>
          </div>

          <!-- 既存添付書類 -->
          <div v-if="form.attachedDocBlobName" class="doc-existing">
            <el-alert type="success" :closable="false">
              <template #title>
                <el-icon><Document /></el-icon>
                注文書が登録済みです
                <el-button size="small" link @click="viewDoc" style="margin-left:8px">表示</el-button>
              </template>
            </el-alert>
          </div>

          <!-- OCR結果プレビュー -->
          <div v-if="ocrResult" class="ocr-result">
            <el-divider>OCR解析結果（プレビュー）</el-divider>
            <el-alert type="info" :closable="false" style="margin-bottom:12px">
              OCR結果を確認し、「フォームに適用」ボタンで基本情報・請求条件を自動入力できます
            </el-alert>
            <el-descriptions :column="2" border size="small">
              <el-descriptions-item label="契約形態">{{ ocrResult.contractType || '-' }}</el-descriptions-item>
              <el-descriptions-item label="顧客名">{{ ocrResult.clientName || '-' }}</el-descriptions-item>
              <el-descriptions-item label="開始日">{{ ocrResult.startDate || '-' }}</el-descriptions-item>
              <el-descriptions-item label="終了日">{{ ocrResult.endDate || '-' }}</el-descriptions-item>
              <el-descriptions-item label="請求単価">{{ ocrResult.billingRate ? `¥${Number(ocrResult.billingRate).toLocaleString()}` : '-' }}</el-descriptions-item>
              <el-descriptions-item label="単価種別">{{ ocrResult.billingRateType || '-' }}</el-descriptions-item>
              <el-descriptions-item label="精算方式">{{ ocrResult.settlementType || '-' }}</el-descriptions-item>
              <el-descriptions-item label="精算時間">{{ ocrResult.settlementLowerH || '-' }}H 〜 {{ ocrResult.settlementUpperH || '-' }}H</el-descriptions-item>
              <el-descriptions-item label="勤務地">{{ ocrResult.workLocation || '-' }}</el-descriptions-item>
              <el-descriptions-item label="勤務時間">{{ ocrResult.workStartTime || '-' }} 〜 {{ ocrResult.workEndTime || '-' }}</el-descriptions-item>
            </el-descriptions>
            <div style="margin-top:12px;text-align:right">
              <el-button type="primary" @click="applyOcrResult">
                <el-icon><Check /></el-icon>
                フォームに適用
              </el-button>
            </div>
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>

    <!-- フッター -->
    <div class="juchuu-form-footer">
      <el-button @click="emit('cancel')">キャンセル</el-button>
      <el-button type="primary" :loading="saving" @click="save">
        <el-icon><Check /></el-icon>
        保存
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import {
  UploadFilled, Upload, Check, Document
} from '@element-plus/icons-vue'
import api from '../../api'

const props = defineProps({
  juchuuId: { type: String, default: null }
})
const emit = defineEmits(['saved', 'cancel'])

const loading = ref(false)
const saving = ref(false)
const uploading = ref(false)
const activeTab = ref('basic')
const formRef = ref<any>(null)
const selectedFile = ref<any>(null)
const ocrResult = ref<any>(null)

const clientOptions = ref<any[]>([])
const resourceOptions = ref<any[]>([])

const form = reactive({
  clientPartnerId: null,
  resourceId: null,
  contractType: 'ses',
  status: 'active',
  startDate: null,
  endDate: null,
  billingRate: null,
  billingRateType: 'monthly',
  overtimeRateMultiplier: 1.25,
  settlementType: 'range',
  settlementLowerH: 140,
  settlementUpperH: 180,
  workLocation: null,
  workDays: '月〜金',
  workStartTime: '09:00',
  workEndTime: '18:00',
  monthlyWorkHours: 160,
  notes: null,
  attachedDocBlobName: null,
})

const rules = {
  contractType: [{ required: true, message: '契約形態を選択してください', trigger: 'change' }],
}

// api モジュールの baseURL（/api）を使ってアップロードURLを構築
const uploadUrl = computed(() => {
  if (!props.juchuuId) return ''
  return `/api/staffing/juchuu/${props.juchuuId}/upload-doc`
})

const uploadHeaders = computed(() => {
  const token = (localStorage.getItem('auth_token') || sessionStorage.getItem('auth_token') || '').trim()
  const cc = localStorage.getItem('company_code') || 'JP01'
  const key = localStorage.getItem('openai_key') || ''
  const headers: Record<string, string> = { 'x-company-code': cc }
  if (token) headers['Authorization'] = `Bearer ${token}`
  if (key) headers['x-openai-key'] = key
  return headers
})

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

function beforeUpload(file: any) {
  selectedFile.value = file
  return false // 手動アップロード
}

async function doUpload() {
  if (!selectedFile.value) return
  if (!props.juchuuId) {
    ElMessage.warning('先に保存してください（IDが必要です）')
    return
  }

  uploading.value = true
  try {
    const formData = new FormData()
    formData.append('file', selectedFile.value)
    const res = await api.post(`/staffing/juchuu/${props.juchuuId}/upload-doc`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' }
    })
    const data = res.data
    form.attachedDocBlobName = data.blobName
    if (data.parsed) {
      ocrResult.value = data.parsed
      ElMessage.success('OCR解析完了！「フォームに適用」で自動入力できます')
    } else {
      ElMessage.success('アップロード完了（OCR解析なし）')
    }
    selectedFile.value = null
  } catch (e: any) {
    ElMessage.error(`アップロードエラー: ${e.message}`)
  } finally {
    uploading.value = false
  }
}

function onUploadSuccess(res: any) {
  form.attachedDocBlobName = res.blobName
  if (res.parsed) {
    ocrResult.value = res.parsed
    ElMessage.success('OCR解析完了！')
  }
}

function onUploadError() {
  ElMessage.error('アップロードに失敗しました')
}

function applyOcrResult() {
  const r = ocrResult.value
  if (!r) return
  if (r.contractType) form.contractType = r.contractType
  if (r.startDate) form.startDate = r.startDate
  if (r.endDate) form.endDate = r.endDate
  if (r.billingRate) form.billingRate = r.billingRate
  if (r.billingRateType) form.billingRateType = r.billingRateType
  if (r.overtimeRateMultiplier) form.overtimeRateMultiplier = r.overtimeRateMultiplier
  if (r.settlementType) form.settlementType = r.settlementType
  if (r.settlementLowerH) form.settlementLowerH = r.settlementLowerH
  if (r.settlementUpperH) form.settlementUpperH = r.settlementUpperH
  if (r.workLocation) form.workLocation = r.workLocation
  if (r.workDays) form.workDays = r.workDays
  if (r.workStartTime) form.workStartTime = r.workStartTime
  if (r.workEndTime) form.workEndTime = r.workEndTime
  if (r.monthlyWorkHours) form.monthlyWorkHours = r.monthlyWorkHours
  if (r.notes) form.notes = r.notes
  activeTab.value = 'basic'
  ElMessage.success('フォームに適用しました。内容を確認して保存してください。')
}

function viewDoc() {
  if (!form.attachedDocBlobName || !props.juchuuId) return
  window.open(`/api/staffing/juchuu/${props.juchuuId}`, '_blank')
}

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
    if (data.ocrRawText) {
      try { ocrResult.value = JSON.parse(data.ocrRawText) } catch { }
    }
  } catch (e: any) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

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

onMounted(load)
</script>

<style scoped>
.juchuu-form {
  padding: 0 4px;
}
.doc-section {
  padding: 8px 0;
}
.doc-upload-area {
  margin-bottom: 16px;
}
.doc-existing {
  margin-bottom: 16px;
}
.ocr-result {
  background: #f8fbff;
  border-radius: 6px;
  padding: 12px;
}
.juchuu-form-footer {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid #eee;
}
</style>

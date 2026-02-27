<template>
  <div class="hatchuu-form" v-loading="loading">
    <el-form :model="form" :rules="rules" ref="formRef" label-width="160px" size="default">
      <!-- 受注紐付け -->
      <el-divider content-position="left">受注との紐付け</el-divider>
      <el-form-item label="受注番号">
        <el-select
          v-model="form.juchuuId"
          filterable
          remote
          clearable
          :remote-method="searchJuchuu"
          placeholder="受注番号で検索（任意）"
          style="width:100%"
        >
          <el-option
            v-for="opt in juchuuOptions"
            :key="opt.value"
            :label="opt.label"
            :value="opt.value"
          />
        </el-select>
        <div v-if="linkedJuchuuInfo" class="linked-info">
          <el-icon><Link /></el-icon>
          受注: {{ linkedJuchuuInfo.clientName }} 請求: ¥{{ Number(linkedJuchuuInfo.billingRate || 0).toLocaleString() }}/月
        </div>
      </el-form-item>

      <!-- リソース -->
      <el-divider content-position="left">リソース・発注先</el-divider>
      <el-row :gutter="20">
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
        <el-col :span="12">
          <el-form-item label="発注先（BP会社）">
            <el-select
              v-model="form.supplierPartnerId"
              filterable
              remote
              clearable
              :remote-method="searchSuppliers"
              placeholder="発注先を選択"
              style="width:100%"
            >
              <el-option
                v-for="opt in supplierOptions"
                :key="opt.value"
                :label="opt.label"
                :value="opt.value"
              />
            </el-select>
          </el-form-item>
        </el-col>
      </el-row>

      <!-- 契約情報 -->
      <el-divider content-position="left">契約情報</el-divider>
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

      <!-- 原価条件 -->
      <el-divider content-position="left">原価条件</el-divider>
      <el-row :gutter="20">
        <el-col :span="14">
          <el-form-item label="原価単価">
            <el-input-number v-model="form.costRate" :precision="0" :step="10000" :min="0" style="width:160px" />
            <span style="margin-left:8px">円</span>
          </el-form-item>
        </el-col>
        <el-col :span="10">
          <el-form-item label="単価種別">
            <el-select v-model="form.costRateType" style="width:110px">
              <el-option label="月額" value="monthly" />
              <el-option label="日額" value="daily" />
              <el-option label="時給" value="hourly" />
            </el-select>
          </el-form-item>
        </el-col>
      </el-row>

      <el-form-item label="精算方式">
        <el-radio-group v-model="form.settlementType">
          <el-radio value="range">幅精算</el-radio>
          <el-radio value="fixed">固定</el-radio>
        </el-radio-group>
      </el-form-item>

      <div v-if="form.settlementType === 'range'">
        <el-form-item label="精算時間幅">
          <el-input-number v-model="form.settlementLowerH" :min="0" :max="300" style="width:120px" />
          <span style="margin:0 8px">H 〜</span>
          <el-input-number v-model="form.settlementUpperH" :min="0" :max="300" style="width:120px" />
          <span style="margin-left:8px">H</span>
        </el-form-item>
      </div>

      <!-- 勤務条件 -->
      <el-divider content-position="left">勤務条件（発注書記載）</el-divider>
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

    <!-- フッター -->
    <div class="hatchuu-form-footer">
      <el-button @click="emit('cancel')">キャンセル</el-button>
      <el-button type="primary" :loading="saving" @click="save">
        <el-icon><Check /></el-icon>
        保存
      </el-button>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, watch, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Check, Link } from '@element-plus/icons-vue'

const props = defineProps({
  hatchuuId: { type: String, default: null },
  initialJuchuuId: { type: String, default: null },
  initialJuchuuNo: { type: String, default: null },
})
const emit = defineEmits(['saved', 'cancel'])

const loading = ref(false)
const saving = ref(false)
const formRef = ref(null)

const juchuuOptions = ref([])
const resourceOptions = ref([])
const supplierOptions = ref([])
const linkedJuchuuInfo = ref(null)

const form = reactive({
  juchuuId: props.initialJuchuuId || null,
  resourceId: null,
  supplierPartnerId: null,
  contractType: 'ses',
  status: 'active',
  startDate: null,
  endDate: null,
  costRate: null,
  costRateType: 'monthly',
  settlementType: 'range',
  settlementLowerH: 140,
  settlementUpperH: 180,
  workLocation: null,
  workDays: '月〜金',
  workStartTime: '09:00',
  workEndTime: '18:00',
  monthlyWorkHours: 160,
  notes: null,
})

const rules = {
  contractType: [{ required: true, message: '契約形態を選択してください', trigger: 'change' }],
}

function apiBase() {
  const host = window.location.hostname
  if (host === 'localhost' || host === '127.0.0.1') return 'http://localhost:5181'
  return ''
}

function getHeaders() {
  const token = localStorage.getItem('token') || sessionStorage.getItem('token')
  const cc = localStorage.getItem('companyCode') || 'JP01'
  return {
    'Authorization': `Bearer ${token}`,
    'x-company-code': cc,
    'Content-Type': 'application/json'
  }
}

// 受注紐付けオプション設定（受注一覧から渡された場合）
onMounted(() => {
  if (props.initialJuchuuId && props.initialJuchuuNo) {
    juchuuOptions.value = [{ value: props.initialJuchuuId, label: props.initialJuchuuNo }]
    loadJuchuuInfo(props.initialJuchuuId)
  }
})

async function searchJuchuu(q) {
  if (!q) return
  const res = await fetch(`${apiBase()}/staffing/juchuu?keyword=${encodeURIComponent(q)}&limit=20`, { headers: getHeaders() })
  if (!res.ok) return
  const data = await res.json()
  juchuuOptions.value = (data.data || []).map(j => ({
    value: j.id,
    label: `${j.juchuuNo} ${j.clientName ? `(${j.clientName})` : ''}`
  }))
}

async function loadJuchuuInfo(juchuuId) {
  if (!juchuuId) { linkedJuchuuInfo.value = null; return }
  try {
    const res = await fetch(`${apiBase()}/staffing/juchuu/${juchuuId}`, { headers: getHeaders() })
    if (!res.ok) return
    const data = await res.json()
    linkedJuchuuInfo.value = { clientName: data.clientName, billingRate: data.billingRate }
    // 受注情報から勤務条件を自動コピー（未設定の場合）
    if (!form.workLocation && data.workLocation) form.workLocation = data.workLocation
    if (!form.startDate && data.startDate) form.startDate = data.startDate
    if (!form.endDate && data.endDate) form.endDate = data.endDate
  } catch { }
}

watch(() => form.juchuuId, (val) => loadJuchuuInfo(val))

async function searchResources(q) {
  if (!q) return
  const res = await fetch(`${apiBase()}/staffing/resources?keyword=${encodeURIComponent(q)}&limit=20`, { headers: getHeaders() })
  if (!res.ok) return
  const data = await res.json()
  resourceOptions.value = (data.data || []).map(r => ({
    value: r.id,
    label: `${r.displayName || r.resourceCode} (${r.resourceCode})`
  }))
}

async function searchSuppliers(q) {
  if (!q) return
  const res = await fetch(`${apiBase()}/businesspartners?keyword=${encodeURIComponent(q)}&limit=20`, { headers: getHeaders() })
  if (!res.ok) return
  const data = await res.json()
  supplierOptions.value = (data.data || []).map(bp => ({ value: bp.id, label: bp.name || bp.partnerCode }))
}

async function load() {
  if (!props.hatchuuId) return
  loading.value = true
  try {
    const res = await fetch(`${apiBase()}/staffing/hatchuu/${props.hatchuuId}`, { headers: getHeaders() })
    if (!res.ok) throw new Error(await res.text())
    const data = await res.json()
    Object.assign(form, {
      juchuuId: data.juchuuId,
      resourceId: data.resourceId,
      supplierPartnerId: data.supplierPartnerId,
      contractType: data.contractType || 'ses',
      status: data.status || 'active',
      startDate: data.startDate,
      endDate: data.endDate,
      costRate: data.costRate,
      costRateType: data.costRateType || 'monthly',
      settlementType: data.settlementType || 'range',
      settlementLowerH: data.settlementLowerH ?? 140,
      settlementUpperH: data.settlementUpperH ?? 180,
      workLocation: data.workLocation,
      workDays: data.workDays || '月〜金',
      workStartTime: data.workStartTime || '09:00',
      workEndTime: data.workEndTime || '18:00',
      monthlyWorkHours: data.monthlyWorkHours ?? 160,
      notes: data.notes,
    })
    // option初期値
    if (data.juchuuId && data.juchuuNo) {
      juchuuOptions.value = [{ value: data.juchuuId, label: `${data.juchuuNo}${data.clientName ? ` (${data.clientName})` : ''}` }]
      linkedJuchuuInfo.value = { clientName: data.clientName, billingRate: data.billingRate }
    }
    if (data.resourceId && data.resourceName) {
      resourceOptions.value = [{ value: data.resourceId, label: `${data.resourceName} (${data.resourceCode})` }]
    }
    if (data.supplierPartnerId && data.supplierName) {
      supplierOptions.value = [{ value: data.supplierPartnerId, label: data.supplierName }]
    }
  } catch (e) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

async function save() {
  await formRef.value?.validate()
  saving.value = true
  try {
    const url = props.hatchuuId
      ? `${apiBase()}/staffing/hatchuu/${props.hatchuuId}`
      : `${apiBase()}/staffing/hatchuu`
    const method = props.hatchuuId ? 'PUT' : 'POST'
    const res = await fetch(url, {
      method,
      headers: getHeaders(),
      body: JSON.stringify({
        juchuuId: form.juchuuId,
        resourceId: form.resourceId,
        supplierPartnerId: form.supplierPartnerId,
        contractType: form.contractType,
        status: form.status,
        startDate: form.startDate,
        endDate: form.endDate,
        costRate: form.costRate,
        costRateType: form.costRateType,
        settlementType: form.settlementType,
        settlementLowerH: form.settlementLowerH,
        settlementUpperH: form.settlementUpperH,
        workLocation: form.workLocation,
        workDays: form.workDays,
        workStartTime: form.workStartTime,
        workEndTime: form.workEndTime,
        monthlyWorkHours: form.monthlyWorkHours,
        notes: form.notes,
      })
    })
    if (!res.ok) throw new Error(await res.text())
    emit('saved')
  } catch (e) {
    ElMessage.error(`保存エラー: ${e.message}`)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.hatchuu-form {
  padding: 0 4px;
}
.linked-info {
  margin-top: 4px;
  color: #409eff;
  font-size: 12px;
  display: flex;
  align-items: center;
  gap: 4px;
}
.hatchuu-form-footer {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid #eee;
}
</style>

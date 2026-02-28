<template>
  <div class="hatchuu-form" v-loading="loading">
    <el-form :model="form" :rules="rules" ref="formRef" label-width="150px" size="default">

      <!-- 受注紐付け -->
      <el-divider content-position="left">受注との紐付け</el-divider>
      <el-form-item label="受注番号">
        <el-select
          v-model="form.juchuuId"
          filterable remote clearable
          :remote-method="searchJuchuu"
          placeholder="受注番号で検索（任意）"
          style="width:100%"
        >
          <el-option v-for="opt in juchuuOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <div v-if="linkedJuchuuInfo" class="linked-info">
          <el-icon><Link /></el-icon>
          受注: {{ linkedJuchuuInfo.clientName }}
        </div>
      </el-form-item>

      <!-- 発注先 -->
      <el-divider content-position="left">発注先</el-divider>
      <el-form-item label="発注先（BP会社）">
        <el-select
          v-model="form.supplierPartnerId"
          filterable remote clearable
          :remote-method="searchSuppliers"
          placeholder="発注先を選択"
          style="width:100%"
        >
          <el-option v-for="opt in supplierOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
      </el-form-item>

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

      <!-- 勤務条件 -->
      <el-divider content-position="left">勤務条件（発注書記載）</el-divider>
      <el-row :gutter="20">
        <el-col :span="16">
          <el-form-item label="勤務地">
            <el-input v-model="form.workLocation" placeholder="例: 東京都渋谷区..." style="width:100%" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="勤務曜日">
            <el-input v-model="form.workDays" placeholder="例: 月〜金" style="width:100%" />
          </el-form-item>
        </el-col>
      </el-row>
      <el-row :gutter="20">
        <el-col :span="8">
          <el-form-item label="始業時間">
            <el-time-picker v-model="form.workStartTime" format="HH:mm" value-format="HH:mm" placeholder="09:00" style="width:100%" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="終業時間">
            <el-time-picker v-model="form.workEndTime" format="HH:mm" value-format="HH:mm" placeholder="18:00" style="width:100%" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="月間基準時間">
            <el-input-number v-model="form.monthlyWorkHours" :min="0" :max="300" style="width:100%" />
          </el-form-item>
        </el-col>
      </el-row>

      <!-- 要員明細（原価条件） -->
      <el-divider content-position="left">
        <span>要員明細（原価条件）</span>
        <el-button
          type="primary" plain size="small" style="margin-left:12px"
          @click="addDetail"
        ><el-icon><Plus /></el-icon>行追加</el-button>
      </el-divider>

      <div class="detail-table-wrap">
        <table class="detail-table" v-if="form.details.length > 0">
          <thead>
            <tr>
              <th style="width:200px">要員</th>
              <th style="width:120px">原価単価</th>
              <th style="width:80px">種別</th>
              <th style="width:90px">精算方式</th>
              <th style="width:160px">精算時間（H）</th>
              <th style="width:40px"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(detail, idx) in form.details" :key="idx">
              <td>
                <el-select
                  v-model="detail.resourceId"
                  filterable remote clearable
                  :remote-method="(q: string) => searchResources(q, idx)"
                  placeholder="要員を選択"
                  style="width:100%"
                  size="small"
                >
                  <el-option
                    v-for="opt in getResourceOptions(idx)"
                    :key="opt.value" :label="opt.label" :value="opt.value"
                  />
                </el-select>
              </td>
              <td>
                <el-input-number
                  v-model="detail.costRate"
                  :precision="0" :step="10000" :min="0"
                  :controls="false"
                  style="width:100%"
                  size="small"
                  placeholder="単価"
                />
              </td>
              <td>
                <el-select v-model="detail.costRateType" style="width:100%" size="small">
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

      <!-- 備考 -->
      <el-divider content-position="left">備考</el-divider>
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

<script setup lang="ts">
import { ref, reactive, watch, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Check, Link, Plus, Delete } from '@element-plus/icons-vue'
import api from '../../api'

interface DetailRow {
  resourceId: string | null
  costRate: number | null
  costRateType: string
  settlementType: string
  settlementLowerH: number
  settlementUpperH: number
  notes: string | null
}

const props = defineProps({
  hatchuuId: { type: String, default: null },
  initialJuchuuId: { type: String, default: null },
  initialJuchuuNo: { type: String, default: null },
})
const emit = defineEmits(['saved', 'cancel'])

const loading = ref(false)
const saving = ref(false)
const formRef = ref<any>(null)

const juchuuOptions = ref<any[]>([])
const supplierOptions = ref<any[]>([])
const resourceOptionsMap = ref<Record<number, any[]>>({})
const linkedJuchuuInfo = ref<any>(null)

function getResourceOptions(idx: number): any[] {
  return resourceOptionsMap.value[idx] || []
}

const form = reactive({
  juchuuId: props.initialJuchuuId || null as string | null,
  supplierPartnerId: null as string | null,
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
  details: [] as DetailRow[],
})

const rules = {
  contractType: [{ required: true, message: '契約形態を選択してください', trigger: 'change' }],
}

function newDetailRow(): DetailRow {
  return {
    resourceId: null,
    costRate: null,
    costRateType: 'monthly',
    settlementType: 'range',
    settlementLowerH: 140,
    settlementUpperH: 180,
    notes: null,
  }
}

function addDetail() {
  form.details.push(newDetailRow())
}

function removeDetail(idx: number) {
  form.details.splice(idx, 1)
  const newMap: Record<number, any[]> = {}
  Object.keys(resourceOptionsMap.value).forEach(k => {
    const n = parseInt(k)
    if (n < idx) newMap[n] = resourceOptionsMap.value[n]
    else if (n > idx) newMap[n - 1] = resourceOptionsMap.value[n]
  })
  resourceOptionsMap.value = newMap
}

onMounted(() => {
  if (props.initialJuchuuId && props.initialJuchuuNo) {
    juchuuOptions.value = [{ value: props.initialJuchuuId, label: props.initialJuchuuNo }]
    loadJuchuuInfo(props.initialJuchuuId)
  }
  load()
})

async function searchJuchuu(q: string) {
  if (!q) return
  const res = await api.get('/staffing/juchuu', { params: { keyword: q, limit: 20 } })
  juchuuOptions.value = (res.data.data || []).map((j: any) => ({
    value: j.id,
    label: `${j.juchuuNo} ${j.clientName ? `(${j.clientName})` : ''}`
  }))
}

async function loadJuchuuInfo(juchuuId: string | null) {
  if (!juchuuId) { linkedJuchuuInfo.value = null; return }
  try {
    const res = await api.get(`/staffing/juchuu/${juchuuId}`)
    const data = res.data
    linkedJuchuuInfo.value = { clientName: data.clientName }
    if (!form.workLocation && data.workLocation) form.workLocation = data.workLocation
    if (!form.startDate && data.startDate) form.startDate = data.startDate
    if (!form.endDate && data.endDate) form.endDate = data.endDate
  } catch { }
}

watch(() => form.juchuuId, (val) => loadJuchuuInfo(val))

async function searchResources(q: string, rowIdx: number) {
  if (!q) return
  const res = await api.get('/staffing/resources', { params: { keyword: q, limit: 20 } })
  resourceOptionsMap.value[rowIdx] = (res.data.data || []).map((r: any) => ({
    value: r.id,
    label: `${r.displayName || r.resourceCode} (${r.resourceCode})`
  }))
}

async function searchSuppliers(q: string) {
  if (!q) return
  const res = await api.get('/businesspartners', { params: { keyword: q, limit: 20 } })
  supplierOptions.value = (res.data.data || []).map((bp: any) => ({ value: bp.id, label: bp.name || bp.partnerCode }))
}

async function load() {
  if (!props.hatchuuId) return
  loading.value = true
  try {
    const res = await api.get(`/staffing/hatchuu/${props.hatchuuId}`)
    const data = res.data
    Object.assign(form, {
      juchuuId: data.juchuuId,
      supplierPartnerId: data.supplierPartnerId,
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
      details: (data.details || []).map((d: any): DetailRow => ({
        resourceId: d.resourceId ?? null,
        costRate: d.costRate ?? null,
        costRateType: d.costRateType ?? 'monthly',
        settlementType: d.settlementType ?? 'range',
        settlementLowerH: d.settlementLowerH ?? 140,
        settlementUpperH: d.settlementUpperH ?? 180,
        notes: d.notes ?? null,
      })),
    })
    if (data.juchuuId && data.juchuuNo) {
      juchuuOptions.value = [{ value: data.juchuuId, label: `${data.juchuuNo}${data.clientName ? ` (${data.clientName})` : ''}` }]
      linkedJuchuuInfo.value = { clientName: data.clientName }
    }
    if (data.supplierPartnerId && data.supplierName) {
      supplierOptions.value = [{ value: data.supplierPartnerId, label: data.supplierName }]
    }
    // 各明細行の要員オプションを設定
    ;(data.details || []).forEach((d: any, i: number) => {
      if (d.resourceId && d.resourceName) {
        resourceOptionsMap.value[i] = [{
          value: d.resourceId,
          label: `${d.resourceName}${d.resourceCode ? ' (' + d.resourceCode + ')' : ''}`
        }]
      }
    })
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
      juchuuId: form.juchuuId,
      supplierPartnerId: form.supplierPartnerId,
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
        costRate: d.costRate,
        costRateType: d.costRateType,
        settlementType: d.settlementType,
        settlementLowerH: d.settlementLowerH,
        settlementUpperH: d.settlementUpperH,
        notes: d.notes,
      })),
    }
    if (props.hatchuuId) {
      await api.put(`/staffing/hatchuu/${props.hatchuuId}`, payload)
    } else {
      await api.post('/staffing/hatchuu', payload)
    }
    emit('saved')
  } catch (e: any) {
    ElMessage.error(`保存エラー: ${e.message}`)
  } finally {
    saving.value = false
  }
}
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

.hatchuu-form-footer {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid #eee;
}
</style>

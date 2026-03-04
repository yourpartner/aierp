<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBatchNew }}</div>
          <div class="page-actions">
            <el-button type="primary" :loading="saving" :disabled="!model.materialCode" @click="save">{{ commonText.save }}</el-button>
            <el-button @click="$router.push('/batches')">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <el-form :model="model" label-width="100px" class="batch-form">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="品目コード" required>
              <el-select
                v-model="model.materialCode"
                filterable
                placeholder="品目を選択"
                style="width: 100%"
                @change="onMaterialChange"
              >
                <el-option
                  v-for="m in materials"
                  :key="m.code"
                  :label="`${m.code} - ${m.name}`"
                  :value="m.code"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="ロット番号">
              <el-input v-model="batchNoPreview" disabled placeholder="自動採番" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="製造日">
              <el-date-picker
                v-model="model.mfgDate"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="製造日を選択"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="有効期限">
              <el-date-picker
                v-model="model.expDate"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="有効期限を選択"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import { useI18n } from '../i18n'
import { useRouter } from 'vue-router'

const router = useRouter()
const { section } = useI18n()
const navText = section({ inventoryBatchNew: '' }, (msg) => msg.nav)
const commonText = section({ backList: '', save: '', saved: '' }, (msg) => msg.common)

interface MaterialOption { code: string; name: string }
const materials = ref<MaterialOption[]>([])
const model = reactive<{ materialCode: string; mfgDate: string; expDate: string }>({
  materialCode: '',
  mfgDate: '',
  expDate: ''
})
const saving = ref(false)

const batchNoPreview = computed(() => {
  if (!model.materialCode) return ''
  const today = new Date()
  const yyyymmdd = `${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}`
  return `${model.materialCode}-${yyyymmdd}-***`
})

function onMaterialChange() {
  // placeholder for future logic
}

async function loadMaterials() {
  try {
    const r = await api.post('/objects/material/search', {
      page: 1,
      pageSize: 500,
      where: [],
      orderBy: [{ field: 'payload.code', direction: 'asc' }]
    })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    materials.value = rows.map((row: any) => ({
      code: row.payload?.code || '',
      name: row.payload?.name || ''
    })).filter((m: MaterialOption) => m.code)
  } catch (e: any) {
    console.error('Failed to load materials', e)
  }
}

async function generateBatchNo(materialCode: string): Promise<string> {
  const today = new Date()
  const yyyymmdd = `${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}`
  const prefix = `${materialCode}-${yyyymmdd}-`

  try {
    const r = await api.post('/objects/batch/search', {
      page: 1,
      pageSize: 1000,
      where: [{ field: 'payload.materialCode', op: 'eq', value: materialCode }],
      orderBy: [{ field: 'payload.batchNo', direction: 'desc' }]
    })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    let maxSeq = 0
    for (const row of rows) {
      const no: string = row.payload?.batchNo || ''
      if (no.startsWith(prefix)) {
        const seqStr = no.substring(prefix.length)
        const seq = parseInt(seqStr, 10)
        if (!isNaN(seq) && seq > maxSeq) maxSeq = seq
      }
    }
    return `${prefix}${String(maxSeq + 1).padStart(3, '0')}`
  } catch {
    return `${prefix}001`
  }
}

async function save() {
  if (!model.materialCode) {
    ElMessage.warning('品目コードを選択してください')
    return
  }
  saving.value = true
  try {
    const batchNo = await generateBatchNo(model.materialCode)
    const payload: any = {
      materialCode: model.materialCode,
      batchNo
    }
    if (model.mfgDate) payload.mfgDate = model.mfgDate
    if (model.expDate) payload.expDate = model.expDate

    await api.post('/objects/batch', { payload })
    ElMessage.success(commonText.value.saved || '保存しました')
    router.push('/batches')
  } catch (e: any) {
    const msg = e?.response?.data?.error || e?.message || '保存に失敗しました'
    ElMessage.error(msg)
  } finally {
    saving.value = false
  }
}

loadMaterials()
</script>

<style scoped>
.page.page-medium {
  max-width: 900px;
}
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.page-header-title {
  font-weight: 600;
  font-size: 15px;
  color: #303133;
}
.page-actions {
  display: flex;
  gap: 8px;
}
.batch-form {
  max-width: 760px;
  padding-top: 4px;
}
</style>

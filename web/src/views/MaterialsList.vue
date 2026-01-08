<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryMaterials }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="createNew" size="small">{{ navText.inventoryMaterialNew }}</el-button>
            <el-button @click="load" :loading="loading" size="small">{{ schemaText.refresh }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" style="width:100%" stripe v-loading="loading" size="small">
        <el-table-column v-for="col in columns" :key="col" :label="columnLabel(col)" :min-width="minWidth(col)">
          <template #default="{ row }">
            {{ cellValue(row, col) }}
          </template>
        </el-table-column>
        <el-table-column :label="commonText.view" width="120">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click.stop="openDetail(row)">{{ commonText.view }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
    <el-dialog
      v-model="showDetail"
      :title="detailTitle"
      width="800px"
      destroy-on-close
      append-to-body
      class="material-detail-dialog"
      top="5vh"
    >
      <MaterialForm
        v-if="showDetail"
        :material-id="detailId"
        embed
        @saved="handleSaved"
        @cancel="closeDetail"
      />
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted, computed, reactive, watchEffect } from 'vue'
import { useRouter } from 'vue-router'
import api from '../api'
import MaterialForm from './MaterialForm.vue'
import { useI18n } from '../i18n'
import { ElMessage } from 'element-plus'

const router = useRouter()
const { section, common, text, lang } = useI18n()
const navText = section({ inventoryMaterials:'', inventoryMaterialNew:'' }, (msg) => msg.nav)
const commonText = reactive({ view: '' })
const schemaText = reactive({ refresh: '' })

watchEffect(() => {
  const commonSource = common.value || {}
  const schemaSource = text.value?.schemaList || {}
  commonText.view = commonSource.view || '照会'
  schemaText.refresh = schemaSource.refresh || '再読込'
})

const schemaLabels = computed(() => text.value?.schemaLabels ?? {})

function syncTexts() {
  const commonSource = common.value || {}
  const schemaSource = text.value?.schemaList || {}
  commonText.view = commonSource.view || '照会'
  schemaText.refresh = schemaSource.refresh || '再読込'
}

syncTexts()

const rows = ref<any[]>([])
const loading = ref(false)
const schemaDoc = ref<any>(null)
const showDetail = ref(false)
const detailId = ref<string>('')

const detailTitle = computed(() => `${navText.value.inventoryMaterials || '品目'} - ${commonText.view || '照会'}`)

const columns = computed<string[]>(() => {
  const uiCols = schemaDoc.value?.ui?.list?.columns
  if (Array.isArray(uiCols) && uiCols.length > 0) return uiCols
  const first = rows.value[0]
  if (first) {
    const keys = Object.keys(first)
    return keys.filter((k) => !['payload'].includes(k))
  }
  return []
})

function minWidth(col: string) {
  return /name|summary|description/i.test(col) ? 220 : 120
}

function columnLabel(col: string) {
  const map = schemaLabels.value || {}
  const candidates = [col, normalizeKey(col)]
  if (col.includes('.')) {
    const last = col.split('.').pop() as string
    candidates.push(last)
    candidates.push(normalizeKey(last))
  }
  for (const key of candidates) {
    if (map[key] !== undefined) return map[key]
  }
  return col
}

function normalizeKey(input: string) {
  return input.toLowerCase().replace(/[^a-z0-9]/g, '')
}

function resolvePath(row: any, path: string) {
  if (!row) return ''
  const walk = (target: any, segments: string[]) => {
    let cur = target
    for (const seg of segments) {
      if (cur == null) return undefined
      cur = cur[seg]
    }
    return cur
  }
  const segments = path.split('.')
  let value = walk(row, segments)
  if (value === undefined) value = walk(row.payload, segments)
  if (value === undefined) value = row.payload?.[path]
  if (value === undefined) value = row[path]
  if (Array.isArray(value)) return value.map((v) => (v == null ? '' : String(v))).join(', ')
  if (value && typeof value === 'object') return JSON.stringify(value)
  return value ?? ''
}

function cellValue(row: any, col: string) {
  const val = resolvePath(row, col)
  return typeof val === 'string' ? val : String(val ?? '')
}

async function load() {
  loading.value = true
  try {
    try {
      const schemaResp = await api.get('/schemas/material', { params: { lang: lang.value } })
      schemaDoc.value = schemaResp.data || {}
    } catch {}
    const orderBy = (Array.isArray(schemaDoc.value?.query?.sorts) && schemaDoc.value.query.sorts.length > 0)
      ? [{ field: schemaDoc.value.query.sorts[0], dir: 'ASC' }]
      : []
    const resp = await api.post('/objects/material/search', { where: [], page: 1, pageSize: 200, orderBy })
    rows.value = Array.isArray(resp.data?.data) ? resp.data.data : []
  } finally {
    loading.value = false
  }
}

function createNew() {
  router.push('/material/new')
}

function openDetail(row: any) {
  const id = row?.id ?? row?.payload?.id ?? row?.payload?.code
  if (!id) {
    ElMessage.warning('品目のIDが取得できません')
    return
  }
  detailId.value = id
  showDetail.value = true
}

function handleSaved() {
  showDetail.value = false
  load()
}

function closeDetail() {
  showDetail.value = false
}

onMounted(load)
</script>
<style scoped>
.material-detail-dialog :deep(.el-dialog) {
  max-height: 90vh;
  display: flex;
  flex-direction: column;
}

.material-detail-dialog :deep(.el-dialog__header) {
  padding: 14px 20px;
  margin: 0;
  border-bottom: 1px solid #ebeef5;
  flex-shrink: 0;
}

.material-detail-dialog :deep(.el-dialog__title) {
  font-size: 15px;
  font-weight: 600;
  color: #303133;
}

.material-detail-dialog :deep(.el-dialog__body) {
  padding: 0;
  flex: 1;
  overflow-y: auto;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.page-header-title {
  font-weight: 600;
  font-size: 16px;
}

.page-actions {
  display: flex;
  gap: 8px;
}
</style>



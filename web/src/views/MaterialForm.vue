<template>
  <div class="material-card">
    <!-- 非嵌入模式下显示标题栏 -->
    <div v-if="!isEmbedded" class="page-header">
      <div class="page-header-title">{{ headerTitle }}</div>
      <div class="page-actions">
        <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
        <el-button v-if="!isEdit" @click="goList">{{ commonText.backList }}</el-button>
      </div>
    </div>

    <div class="material-body">
      <template v-if="uiError">
        <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
      </template>
      <template v-else>
        <el-skeleton v-if="uiLoading" :rows="6" animated />
        <template v-else>
          <!-- 主表单区域 -->
          <el-form label-width="100px" label-position="left" class="material-form">
            <!-- 第一行：图片和基本信息 -->
            <div class="form-row-with-image">
              <!-- 左侧：商品图片 -->
              <div class="image-block">
                <div class="image-preview" :class="{ empty: !model.primaryImageUrl }">
                  <template v-if="model.primaryImageUrl">
                    <img :src="model.primaryImageUrl" :alt="labels.productImage" />
                    <div class="image-overlay">
                      <el-button size="small" circle @click="previewImage">
                        <el-icon><View /></el-icon>
                      </el-button>
                      <el-button size="small" circle type="danger" @click="removeImage">
                        <el-icon><Delete /></el-icon>
                      </el-button>
                    </div>
                  </template>
                  <template v-else>
                    <el-icon class="empty-icon"><Picture /></el-icon>
                  </template>
                </div>
                  <el-upload
                    ref="imageUploadRef"
                    :show-file-list="false"
                    :auto-upload="false"
                    accept="image/*"
                    :on-change="handleImageChange"
                  >
                  <el-button size="small" :loading="imageUploading">
                    <el-icon v-if="!imageUploading"><Upload /></el-icon>
                    {{ imageUploading ? labels.uploading : labels.uploadImage }}
                  </el-button>
                </el-upload>
                  </div>

              <!-- 右侧：基本信息 -->
              <div class="basic-fields">
                <el-form-item :label="labels.materialName">
                  <el-input v-model="model.name" :placeholder="labels.enterMaterialName" />
                </el-form-item>
                <el-form-item :label="labels.materialCode">
                  <el-input v-model="model.code" :placeholder="labels.autoGenerate" disabled />
                </el-form-item>
                <el-row :gutter="16">
                  <el-col :span="12">
                    <el-form-item :label="labels.baseUnit">
                      <el-select v-model="model.baseUom" :placeholder="labels.select" style="width: 100%">
                        <el-option v-for="opt in baseUomOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                      </el-select>
                    </el-form-item>
                  </el-col>
                  <el-col :span="12">
                    <el-form-item :label="labels.batchMgmt">
                      <el-switch v-model="model.batchManagement" />
                    </el-form-item>
                  </el-col>
                </el-row>
              </div>
            </div>

            <!-- 规格和价格信息 -->
            <el-divider content-position="left">{{ labels.specInfo }}</el-divider>
            <el-row :gutter="16">
              <el-col :xs="24" :sm="8">
                <el-form-item :label="labels.weight">
                  <el-input-number
                    v-model="model.weight"
                    :min="0"
                    :step="0.1"
                    :precision="3"
                    controls-position="right"
                    style="width: 100%"
                  />
                </el-form-item>
              </el-col>
              <el-col :xs="24" :sm="8">
                <el-form-item :label="labels.weightUnit">
                  <el-select v-model="model.weightUom" :placeholder="labels.select" style="width: 100%">
                    <el-option v-for="opt in weightUomOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :xs="24" :sm="8">
                <el-form-item :label="labels.price">
                  <el-input-number
                    v-model="model.price"
                    :min="0"
                    :step="1"
                    :precision="0"
                    controls-position="right"
                    style="width: 100%"
                  />
                </el-form-item>
              </el-col>
            </el-row>
            <el-row :gutter="16">
              <el-col :span="12">
                <el-form-item :label="labels.dimensions">
                  <el-input v-model="model.dimensions" :placeholder="labels.dimensionsPlaceholder" />
                </el-form-item>
              </el-col>
              <el-col :span="12">
                <el-form-item :label="labels.description">
                  <el-input v-model="model.description" :placeholder="labels.descriptionPlaceholder" />
                </el-form-item>
              </el-col>
            </el-row>
          </el-form>

          <!-- 动态表单（如果有） -->
          <template v-if="hasLayout">
            <el-divider content-position="left">{{ labels.additionalInfo }}</el-divider>
            <DynamicForm :ui="ui" :schema="schema" :model="model" class="dynamic-form" />
          </template>
        </template>
      </template>
    </div>

    <!-- 消息提示 -->
    <div v-if="msg || err" class="form-messages">
      <span v-if="msg" class="text-success">{{ msg }}</span>
      <span v-if="err" class="text-error">{{ err }}</span>
    </div>

    <!-- 嵌入模式下的操作按钮 -->
    <div v-if="isEmbedded" class="embedded-actions">
      <el-button @click="emit('cancel')">{{ commonText.close || '閉じる' }}</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted, watch } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import { View, Delete, Upload, Picture } from '@element-plus/icons-vue'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'
import { useRouter } from 'vue-router'
import type { UploadInstance, UploadProps, UploadFile } from 'element-plus'

const props = defineProps<{ materialId?: string; embed?: boolean }>()
const emit = defineEmits<{ (e: 'saved'): void; (e: 'cancel'): void }>()

const { section, lang } = useI18n()
const router = useRouter()
const navText = section({ inventoryMaterialNew:'', inventoryMaterials:'' }, (msg) => msg.nav)
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'', close:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

// 日语标签
const labels = computed(() => ({
  productImage: '商品画像',
  uploadImage: '画像変更',
  uploading: 'アップロード中...',
  materialName: '品目名',
  enterMaterialName: '品目名を入力',
  materialCode: '品目コード',
  autoGenerate: '自動採番',
  baseUnit: '基準単位',
  batchMgmt: 'ロット管理',
  select: '選択',
  specInfo: '規格・価格情報',
  weight: '重量',
  weightUnit: '重量単位',
  price: '標準単価',
  dimensions: '寸法',
  dimensionsPlaceholder: '例：100×50×30mm',
  description: '説明',
  descriptionPlaceholder: '品目の説明を入力',
  additionalInfo: 'その他',
  uploadSuccess: '画像をアップロードしました',
  uploadFailed: '画像のアップロードに失敗しました',
  selectImageFile: '画像ファイルを選択してください',
  imageSizeLimit: '画像サイズは15MB以内にしてください',
  imageFormatError: 'JPG/PNG/WEBP/GIF形式のみ対応'
}))

const schema = ref<any>({})
const ui = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})

function createEmptyModel() {
  return {
  name: '',
    code: '',
  baseUom: 'EA',
  batchManagement: false,
  primaryImageUrl: '',
  primaryImageBlobName: '',
  primaryImageContentType: '',
  primaryImageFileName: '',
    weight: 0,
    weightUom: 'kg',
    price: 0,
    dimensions: '',
    description: ''
  }
}

const model = reactive<any>(createEmptyModel())

function resetModel() {
  const empty = createEmptyModel()
  Object.keys(empty).forEach((key) => {
    ;(model as any)[key] = (empty as any)[key]
  })
}

const baseUomOptions = [
  { label: '個 (EA)', value: 'EA' },
  { label: '箱 (BOX)', value: 'BOX' },
  { label: 'セット (SET)', value: 'SET' },
  { label: 'kg', value: 'KG' },
  { label: 'g', value: 'G' },
  { label: 'L', value: 'L' },
  { label: 'mL', value: 'ML' },
  { label: 'm', value: 'M' },
  { label: 'cm', value: 'CM' }
]

const weightUomOptions = [
  { label: 'kg', value: 'kg' },
  { label: 'g', value: 'g' },
  { label: 'lb', value: 'lb' },
  { label: 'oz', value: 'oz' }
]

const imageUploading = ref(false)
const imageUploadRef = ref<UploadInstance>()
const IMAGE_MAX_SIZE = 15 * 1024 * 1024
const imageExtensions = new Set(['.jpg', '.jpeg', '.png', '.webp', '.gif'])

function stripLayoutFields(layout: any[], fields: string[]) {
  if (!Array.isArray(layout)) return
  for (let i = layout.length - 1; i >= 0; i -= 1) {
    const block = layout[i]
    if (!block) continue
    if (block.type === 'grid' && Array.isArray(block.cols)) {
      block.cols = block.cols.filter((col: any) => col && !fields.includes(col.field))
      if (block.cols.length === 0) layout.splice(i, 1)
    } else if (block.type === 'section' && Array.isArray(block.layout)) {
      stripLayoutFields(block.layout, fields)
      if (block.layout.length === 0) layout.splice(i, 1)
    } else if (block.type === 'tabs' && Array.isArray(block.items)) {
      block.items.forEach((tab: any) => {
        if (Array.isArray(tab.children)) {
          tab.children = tab.children.filter((col: any) => col && !fields.includes(col.field))
        }
      })
    }
  }
}

const saving = ref(false)
const msg = ref('')
const err = ref('')

const isEmbedded = computed(() => Boolean(props.embed))
const isEdit = computed(() => !!props.materialId)
const headerTitle = computed(() => {
  const base = navText.value.inventoryMaterials || '品目'
  if (!isEdit.value) return navText.value.inventoryMaterialNew || '新規品目'
  return `${base} - ${model.name || ''}`
})

function validateImage(file: File): string | null {
  if (!file.type.startsWith('image/')) return labels.value.selectImageFile
  if (file.size > IMAGE_MAX_SIZE) return labels.value.imageSizeLimit
  const ext = file.name?.slice(file.name.lastIndexOf('.')) || ''
  if (ext && !imageExtensions.has(ext.toLowerCase())) return labels.value.imageFormatError
  return null
}

async function uploadMaterialMedia(file: File) {
  const formData = new FormData()
  formData.append('file', file)
  const resp = await api.post('/inventory/materials/upload', formData, {
    headers: { 'Content-Type': 'multipart/form-data' }
  })
  return resp.data || {}
}

const handleImageChange: UploadProps['onChange'] = async (uploadFile: UploadFile) => {
  const raw = uploadFile?.raw
  if (!raw) return
  const errMsg = validateImage(raw)
  if (errMsg) {
    ElMessage.error(errMsg)
    imageUploadRef.value?.clearFiles()
    return
  }
  imageUploading.value = true
  try {
    const result = await uploadMaterialMedia(raw)
    model.primaryImageUrl = result?.url || result?.previewUrl || ''
    model.primaryImageBlobName = result?.blobName || ''
    model.primaryImageContentType = result?.contentType || raw.type
    model.primaryImageFileName = raw.name
    ElMessage.success(labels.value.uploadSuccess)
  } catch (error: any) {
    const message = error?.response?.data?.error || error?.message || labels.value.uploadFailed
    ElMessage.error(message)
  } finally {
    imageUploading.value = false
    imageUploadRef.value?.clearFiles()
  }
}

function removeImage() {
  model.primaryImageUrl = ''
  model.primaryImageBlobName = ''
  model.primaryImageContentType = ''
  model.primaryImageFileName = ''
}

function previewImage() {
  if (!model.primaryImageUrl) return
  window.open(model.primaryImageUrl, '_blank')
}

function goList() {
  router.push('/materials')
}

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const payload = JSON.parse(JSON.stringify(model))
    if (isEdit.value && props.materialId) {
      await api.put(`/inventory/material/${props.materialId}`, payload)
    } else {
      await api.post('/inventory/material', payload)
    }
    msg.value = commonText.value.saved
    ElMessage.success(commonText.value.saved)
    emit('saved')
    if (!isEdit.value) resetModel()
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed
  } finally {
    saving.value = false
  }
}

async function loadSchema() {
  if (uiLoading.value) return
  uiLoading.value = true
  uiError.value = ''
  try {
    const r = await api.get('/schemas/material', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || null
    if (ui.value?.form?.layout) {
      stripLayoutFields(ui.value.form.layout, ['name', 'code', 'primaryImageUrl', 'weight', 'weightUom', 'price', 'baseUom', 'batchManagement', 'dimensions', 'description'])
    }
  } catch (e: any) {
    uiError.value = e?.response?.data?.error || e?.message || schemaText.value.loadFailed
  } finally {
    uiLoading.value = false
  }
}

async function loadMaterial(id: string) {
  try {
    const resp = await api.get(`/inventory/materials/${id}`)
    const row = resp.data || {}
    let payload: any = row?.payload ?? row ?? {}
    if (
      payload &&
      typeof payload === 'object' &&
      !Array.isArray(payload) &&
      typeof payload.payload === 'object' &&
      payload.payload !== null &&
      !Array.isArray(payload.payload)
    ) {
      payload = payload.payload
    }
    resetModel()
    Object.assign(model, createEmptyModel(), payload)
    model.weight = Number(model.weight ?? 0)
    model.price = Number(model.price ?? 0)
    if (typeof model.batchManagement !== 'boolean') {
      const raw = model.batchManagement
      model.batchManagement = typeof raw === 'string'
        ? raw.toLowerCase() === 'true'
        : Boolean(raw)
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || '品目データを取得できませんでした')
  }
}

onMounted(async () => {
  await loadSchema()
  if (props.materialId) await loadMaterial(props.materialId)
})

watch(() => props.materialId, async (id) => {
  if (id) {
    await loadMaterial(id)
  } else {
    resetModel()
  }
})
</script>

<style scoped>
.material-card {
  display: flex;
  flex-direction: column;
  padding: 16px 20px;
  background: #fff;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
  padding-bottom: 12px;
  border-bottom: 1px solid #ebeef5;
}

.page-header-title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.page-actions {
  display: flex;
  gap: 8px;
}

.material-body {
  flex: 1;
}

/* 图片和基本信息并排布局 */
.form-row-with-image {
  display: flex;
  gap: 24px;
  margin-bottom: 8px;
}

.image-block {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
}

.image-preview {
  width: 120px;
  height: 120px;
  border: 1px dashed #dcdfe6;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  overflow: hidden;
  background: #fafafa;
  position: relative;
}

.image-preview:hover {
  border-color: #409eff;
}

.image-preview img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.image-preview .image-overlay {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  opacity: 0;
  transition: opacity 0.2s;
}

.image-preview:hover .image-overlay {
  opacity: 1;
}

.empty-icon {
  font-size: 32px;
  color: #c0c4cc;
}

.basic-fields {
  flex: 1;
  min-width: 0;
}

/* 表单样式 */
.material-form {
  width: 100%;
}

.material-form :deep(.el-form-item) {
  margin-bottom: 14px;
}

.material-form :deep(.el-form-item__label) {
  font-weight: 500;
  color: #606266;
}

.material-form :deep(.el-divider__text) {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
}

.material-form :deep(.el-divider) {
  margin: 16px 0 12px;
}

/* 动态表单 */
.dynamic-form {
  margin-top: 8px;
}

/* 消息提示 */
.form-messages {
  margin-top: 12px;
  font-size: 13px;
}

.text-success {
  color: #67c23a;
}

.text-error {
  color: #f56c6c;
}

/* 嵌入模式操作按钮 */
.embedded-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  padding-top: 16px;
  margin-top: 16px;
  border-top: 1px solid #ebeef5;
}
</style>

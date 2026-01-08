<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ isEdit ? labels.editTitle : labels.newTitle }}</div>
          <div class="page-actions">
            <el-button @click="goBack">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <el-form ref="formRef" :model="model" :rules="rules" label-width="120px" style="max-width:720px">
        <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon style="margin-bottom:12px" />
        <el-form-item :label="labels.code" prop="code">
          <el-input v-model="model.code" :placeholder="labels.codePlaceholder" :disabled="isEdit" maxlength="4" style="width:200px" />
          <span class="input-hint">{{ labels.codeHint }}</span>
        </el-form-item>
        <el-form-item :label="labels.name" prop="name">
          <el-input v-model="model.name" :placeholder="labels.namePlaceholder" style="width:300px" />
        </el-form-item>
        <el-form-item :label="labels.inactive">
          <el-switch v-model="model.inactive" :active-text="labels.yes" :inactive-text="labels.no" />
        </el-form-item>
        <DynamicForm v-if="!errorMsg && hasLayout" :ui="filteredUi" :schema="schema" :model="model" />
        <el-form-item>
          <el-button type="primary" :loading="saving" :disabled="!!errorMsg" @click="save">{{ commonText.save }}</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import api from '../api'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'

const route = useRoute()
const router = useRouter()
const { section, lang } = useI18n()
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

// 标签国际化
const labels = computed(() => ({
  newTitle: lang.value === 'ja' ? '倉庫新規' : lang.value === 'en' ? 'New Warehouse' : '新建仓库',
  editTitle: lang.value === 'ja' ? '倉庫編集' : lang.value === 'en' ? 'Edit Warehouse' : '编辑仓库',
  code: lang.value === 'ja' ? '倉庫コード' : lang.value === 'en' ? 'Warehouse Code' : '仓库编码',
  codePlaceholder: lang.value === 'ja' ? '2-4桁の英数字' : lang.value === 'en' ? '2-4 alphanumeric' : '2-4位字母数字',
  codeHint: lang.value === 'ja' ? '※2-4桁の英数字を入力してください' : lang.value === 'en' ? '※Enter 2-4 alphanumeric characters' : '※请输入2-4位字母或数字',
  name: lang.value === 'ja' ? '倉庫名' : lang.value === 'en' ? 'Warehouse Name' : '仓库名称',
  namePlaceholder: lang.value === 'ja' ? '倉庫名を入力' : lang.value === 'en' ? 'Enter warehouse name' : '请输入仓库名称',
  codeRequired: lang.value === 'ja' ? '倉庫コードを入力してください' : lang.value === 'en' ? 'Warehouse code is required' : '请输入仓库编码',
  codeFormat: lang.value === 'ja' ? '2-4桁の英数字を入力してください' : lang.value === 'en' ? 'Enter 2-4 alphanumeric characters' : '请输入2-4位字母或数字',
  codeDuplicate: lang.value === 'ja' ? 'この倉庫コードは既に使用されています' : lang.value === 'en' ? 'This warehouse code already exists' : '此仓库编码已存在',
  nameRequired: lang.value === 'ja' ? '倉庫名を入力してください' : lang.value === 'en' ? 'Warehouse name is required' : '请输入仓库名称',
  inactive: lang.value === 'ja' ? '無効' : lang.value === 'en' ? 'Inactive' : '停用',
  yes: lang.value === 'ja' ? 'はい' : lang.value === 'en' ? 'Yes' : '是',
  no: lang.value === 'ja' ? 'いいえ' : lang.value === 'en' ? 'No' : '否'
}))

const formRef = ref<FormInstance>()
const schema = ref<any>({})
const ui = ref<any>({ form: { layout: [] } })
const errorMsg = ref('')
const isEdit = ref(false)
const editId = ref<string>('')

const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})

// 过滤掉 code 和 name 字段（因为已经单独处理）
const filteredUi = computed(() => {
  if (!ui.value?.form?.layout) return ui.value
  const newUi = JSON.parse(JSON.stringify(ui.value))
  if (Array.isArray(newUi.form?.layout)) {
    newUi.form.layout = newUi.form.layout.map((block: any) => {
      if (block.type === 'grid' && Array.isArray(block.cols)) {
        block.cols = block.cols.filter((col: any) => !['code', 'name'].includes(col.field))
      }
      return block
    }).filter((block: any) => {
      if (block.type === 'grid' && Array.isArray(block.cols)) return block.cols.length > 0
      return true
    })
  }
  return newUi
})

const model = reactive<any>({ code: '', name: '', inactive: false })
const saving = ref(false)

// 验证编码格式：2-4位字母或数字
const validateCode = (_rule: any, value: string, callback: any) => {
  if (!value) {
    callback(new Error(labels.value.codeRequired))
  } else if (!/^[A-Za-z0-9]{2,4}$/.test(value)) {
    callback(new Error(labels.value.codeFormat))
  } else {
    callback()
  }
}

const rules = reactive<FormRules>({
  code: [{ required: true, validator: validateCode, trigger: 'blur' }],
  name: [{ required: true, message: labels.value.nameRequired, trigger: 'blur' }]
})

// 检查编码是否重复
async function checkCodeDuplicate(code: string): Promise<boolean> {
  try {
    const r = await api.post('/objects/warehouse/search', {
      page: 1, pageSize: 1,
      where: [{ field: 'warehouse_code', op: 'eq', value: code.toUpperCase() }]
    })
    const existing = r.data?.data || []
    // 编辑模式下，排除自己
    if (isEdit.value && existing.length === 1 && existing[0].id === editId.value) {
      return false
    }
    return existing.length > 0
  } catch {
    return false
  }
}

async function save() {
  // 表单验证
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  // 检查编码重复
  const isDuplicate = await checkCodeDuplicate(model.code)
  if (isDuplicate) {
    ElMessage.warning(labels.value.codeDuplicate)
    return
  }

  saving.value = true
  try {
    // 统一转大写
    model.code = model.code.toUpperCase()
    
    if (isEdit.value) {
      await api.put(`/objects/warehouse/${editId.value}`, { payload: model })
    } else {
    await api.post('/objects/warehouse', { payload: model })
    }
    ElMessage.success(commonText.value.saved)
    router.push('/warehouses')
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || commonText.value.saveFailed)
  } finally {
    saving.value = false
  }
}

function goBack() {
  router.push('/warehouses')
}

async function loadSchema() {
  try {
    const r = await api.get('/schemas/warehouse', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || { form: { layout: [] } }
  } catch (e: any) {
    errorMsg.value = `${schemaText.value.loadFailed}: ${e?.message || ''}`
  }
}

async function loadDetail(id: string) {
  try {
    const r = await api.get(`/inventory/warehouses/${id}`)
    const data = r.data?.payload || r.data || {}
    Object.assign(model, data)
  } catch (e: any) {
    errorMsg.value = `${commonText.value.loadFailed}: ${e?.message || ''}`
  }
}

onMounted(async () => {
  await loadSchema()
  // 检查是否编辑模式
  const id = route.params.id as string
  if (id) {
    isEdit.value = true
    editId.value = id
    await loadDetail(id)
  }
})
</script>

<style scoped>
.page.page-medium {
  max-width: 900px;
}
.input-hint {
  margin-left: 12px;
  color: #909399;
  font-size: 12px;
}
</style>



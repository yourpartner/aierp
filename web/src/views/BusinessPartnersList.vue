<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><OfficeBuilding /></el-icon>
            <span class="page-header-title">{{ tableLabels.title }}</span>
          </div>
          <div class="page-header-search">
            <el-input
              v-model="searchKeyword"
              :placeholder="tableLabels.searchPlaceholder"
              clearable
              style="width: 280px"
              @keyup.enter="onSearch"
              @clear="onSearch"
            >
              <template #prefix>
                <el-icon><Search /></el-icon>
              </template>
            </el-input>
            <el-button @click="onSearch">{{ tableLabels.search }}</el-button>
          </div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/businesspartner/new')">{{ tableLabels.new }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="partner_code" :label="tableLabels.code" width="160" />
        <el-table-column prop="name" :label="tableLabels.name" min-width="220" />
        <el-table-column :label="tableLabels.customerVendor" width="200">
          <template #default="{ row }">
            <el-tag size="small" v-if="row.flag_customer">{{ tableLabels.customerTag }}</el-tag>
            <el-tag size="small" type="warning" v-if="row.flag_vendor">{{ tableLabels.vendorTag }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="status" :label="tableLabels.status" width="140" />
        <el-table-column :label="tableLabels.actions" width="260" fixed="right">
          <template #default="{ row }">
            <el-button size="small" @click="openPartnerDetail(row)">{{ tableLabels.view }}</el-button>
            <el-button size="small" type="danger" @click="confirmDelete(row)">{{ commonText.delete }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div class="page-pagination">
        <el-pagination
          background
          layout="prev, pager, next, sizes, total"
          :total="total"
          :page-size="pageSize"
          :current-page="page"
          @update:page-size="onPageSize"
          @update:current-page="onPage" />
      </div>
    </el-card>

    <!-- 详情弹窗 -->
    <el-dialog
      v-model="detailDialog.visible"
      width="auto"
      destroy-on-close
      append-to-body
      class="voucher-detail-dialog"
      @closed="onDetailClosed"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <el-card class="detail-card">
          <template #header>
            <div class="page-header detail-header">
              <div class="page-header-title">{{ detailDialogTitle }}</div>
              <div class="detail-header-actions">
                <el-tag v-if="detailDialog.partnerCode" type="info">{{ detailDialog.partnerCode }}</el-tag>
                <!-- 显示模式：編集按钮 -->
                <template v-if="detailDialog.readonly">
                  <el-button type="primary" size="small" @click="switchToEditMode">{{ tableLabels.edit }}</el-button>
                </template>
                <!-- 编辑模式：キャンセル + 保存 -->
                <template v-else>
                  <el-button size="small" @click="cancelEditMode">{{ commonText.cancel }}</el-button>
                  <el-button type="primary" size="small" :loading="detailDialog.saving" @click="savePartnerFromDialog">{{ commonText.save }}</el-button>
                </template>
              </div>
        </div>
      </template>
      <BusinessPartnerForm
        ref="partnerFormRef"
        :partner-id="detailDialog.id || undefined"
        mode="edit"
            :readonly="detailDialog.readonly"
        @saved="onPartnerSaved"
        @cancel="onPartnerEditCancel"
        @action="handleFormAction"
      />
        </el-card>
        </div>
    </el-dialog>
  </div>
  <!-- 银行/支店选择弹窗 -->
  <el-dialog v-model="showBank" title="銀行選択" width="720px" append-to-body destroy-on-close>
    <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank = false" />
  </el-dialog>
  <el-dialog v-model="showBranch" title="支店選択" width="720px" append-to-body destroy-on-close>
    <BankBranchPicker mode="branch" :bank-code="currentBankCode" @select="onPickBranch" @cancel="showBranch = false" />
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, nextTick } from 'vue'
import { Search, OfficeBuilding } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'
import BusinessPartnerForm from './BusinessPartnerForm.vue'
import BankBranchPicker from '../components/BankBranchPicker.vue'

const { section } = useI18n()
const tableLabels = section(
  {
    title: '',
    new: '',
    code: '',
    name: '',
    email: '',
    phone: '',
    type: '',
    status: '',
    actions: '',
    view: '',
    edit: '',
    detail: '',
    customerTag: '',
    vendorTag: '',
    customerVendor: '',
    search: '',
    searchPlaceholder: ''
  },
  (msg) => ({
    ...msg.tables.partners,
    actions: msg.common?.actions || '操作',
    view: msg.common?.detail || '詳細',
    edit: msg.common?.edit || '編集',
    detail: msg.common?.detail || '詳細',
    search: msg.common?.search || '検索',
    searchPlaceholder: msg.tables?.partners?.searchPlaceholder || '取引先名・コードで検索'
  })
)
const commonText = section({ close: '', edit: '', save: '', delete: '', cancel: '', confirmDelete: '', deleteSuccess: '', deleteFailed: '', cannotDelete: '', saveSuccess: '' }, (msg) => ({
  close: msg.common?.close || '閉じる',
  edit: msg.common?.edit || '編集',
  save: msg.common?.save || '保存',
  delete: msg.common?.delete || '削除',
  cancel: msg.common?.cancel || 'キャンセル',
  confirmDelete: msg.common?.confirmDelete || 'この取引先を削除しますか？',
  deleteSuccess: msg.common?.deleteSuccess || '削除しました',
  deleteFailed: msg.common?.deleteFailed || '削除に失敗しました',
  cannotDelete: msg.common?.cannotDelete || 'この取引先は使用中のため削除できません',
  saveSuccess: msg.common?.saveSuccess || '保存しました'
}))

const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const searchKeyword = ref('')
const deleting = ref(false)
const detailDialog = reactive<{
  visible: boolean
  id: string | null
  readonly: boolean
  saving: boolean
  partnerCode: string
}>({
  visible: false,
  id: null,
  readonly: true,
  saving: false,
  partnerCode: ''
})
const partnerFormRef = ref<any>(null)

// 银行选择弹窗状态
const showBank = ref(false)
const showBranch = ref(false)
const currentBankCode = ref('')

const detailDialogTitle = computed(() => '取引先詳細')

async function load() {
  loading.value = true
  try {
    const where: any[] = []
    if (searchKeyword.value.trim()) {
      // 搜索取引先名或编码
      where.push({
        or: [
          { field: 'name', op: 'contains', value: searchKeyword.value.trim() },
          { field: 'partner_code', op: 'contains', value: searchKeyword.value.trim() }
        ]
      })
    }
    const r = await api.post('/objects/businesspartner/search', {
      page: page.value,
      pageSize: pageSize.value,
      where,
      orderBy: [{ field: 'name', dir: 'ASC' }]
    })
    const data = Array.isArray(r.data?.data) ? r.data.data : []
    rows.value = data
    total.value = Number(r.data?.total) || data.length
  } finally {
    loading.value = false
  }
}

function onSearch() {
  page.value = 1
  load()
}

async function confirmDelete(row: any) {
  const id = row?.id || row?.partner_id
  if (!id) return

  // 先检查是否被引用
  try {
    const refResp = await api.get(`/objects/businesspartner/${id}/references`)
    if (!refResp.data?.canDelete) {
      const refs = refResp.data?.references?.map((r: any) => r.description).join('、') || ''
      ElMessage.error(`${commonText.value.cannotDelete}：${refs}`)
      return
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || commonText.value.deleteFailed)
    return
  }

  // 确认删除
  try {
    await ElMessageBox.confirm(
      commonText.value.confirmDelete,
      commonText.value.delete,
      { type: 'warning', confirmButtonText: commonText.value.delete, cancelButtonText: commonText.value.close }
    )
  } catch {
    return // 用户取消
  }

  // 执行删除
  deleting.value = true
  try {
    await api.delete(`/objects/businesspartner/${id}`)
    ElMessage.success(commonText.value.deleteSuccess)
    await load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || commonText.value.deleteFailed)
  } finally {
    deleting.value = false
  }
}

function openPartnerDetail(row: any) {
  const id = row?.id || row?.partner_id || null
  detailDialog.visible = true
  detailDialog.readonly = true
  detailDialog.id = id
  const parsed = parsePayload(row?.payload) || {}
  detailDialog.partnerCode = (row?.partner_code ?? parsed?.partnerCode ?? parsed?.code ?? '').toString().trim()
}

async function switchToEditMode() {
  // Ensure we have an id; if not, try resolving from partner code
  if (!detailDialog.id) {
    const code = (detailDialog.partnerCode || '').trim()
    if (code) {
      try {
        const r = await api.post('/objects/businesspartner/search', {
          page: 1,
          pageSize: 5,
          where: [{ field: 'partner_code', op: 'eq', value: code }],
          orderBy: []
        })
        const found = Array.isArray(r.data?.data) ? r.data.data[0] : null
        const resolvedId = found?.id || found?.partner_id || null
        if (resolvedId) {
          detailDialog.id = resolvedId
        }
      } catch {}
    }
  }
  if (!detailDialog.id) {
    ElMessage.error('取引先IDを取得できませんでした')
    return
  }
  detailDialog.readonly = false
}

function closeDetail() {
  detailDialog.visible = false
}

function onDetailClosed() {
  detailDialog.id = null
  detailDialog.readonly = true
  detailDialog.partnerCode = ''
}

function onPartnerEditCancel() {
  detailDialog.readonly = true
}

function cancelEditMode() {
  // 切换回显示模式（不关闭弹窗）
  detailDialog.readonly = true
}

async function onPartnerSaved() {
  detailDialog.saving = false
  detailDialog.visible = false  // 直接关闭弹窗
  ElMessage.success(commonText.value.saveSuccess)
  await load()  // 刷新列表
}

async function savePartnerFromDialog() {
  if (partnerFormRef.value?.save) {
    detailDialog.saving = true
    try {
      await partnerFormRef.value.save()
    } finally {
      detailDialog.saving = false
    }
  }
}

function parsePayload(payload: any): any {
  if (!payload) return null
  if (typeof payload === 'string') {
    try {
      return JSON.parse(payload)
    } catch {
      return null
    }
  }
  return payload
}

function formatAddress(addr: any): string {
  if (!addr) return '-'
  if (typeof addr === 'string') return addr || '-'
  // addr 是对象 { postalCode, prefecture, address }
  const parts: string[] = []
  if (addr.postalCode) parts.push(`〒${addr.postalCode}`)
  if (addr.prefecture) parts.push(addr.prefecture)
  if (addr.address) parts.push(addr.address)
  return parts.length > 0 ? parts.join(' ') : '-'
}

function onPage(p: number) {
  page.value = p
  load()
}

function onPageSize(s: number) {
  pageSize.value = s
  page.value = 1
  load()
}

// 银行选择弹窗处理
function openBankPicker() {
  showBank.value = true
}

function openBranchPicker() {
  const form = partnerFormRef.value?.form
  if (form?.bankInfo?.bankCode) {
    currentBankCode.value = form.bankInfo.bankCode
    showBranch.value = true
  }
}

function onPickBank(row: any) {
  const form = partnerFormRef.value?.form
  if (form) {
    form.bankInfo = form.bankInfo || {}
    form.bankInfo.bankCode = row.payload.bankCode
    form.bankInfo.bankName = `${row.payload.bankCode} ${row.payload.name}`
    delete form.bankInfo.branchCode
    delete form.bankInfo.branchName
  }
  showBank.value = false
}

function onPickBranch(row: any) {
  const form = partnerFormRef.value?.form
  if (form) {
    form.bankInfo = form.bankInfo || {}
    form.bankInfo.branchCode = row.payload.branchCode
    form.bankInfo.branchName = `${row.payload.branchCode} ${row.payload.branchName}`
  }
  showBranch.value = false
}

// 处理 BusinessPartnerForm 发出的 action
function handleFormAction(name: string) {
  if (name === 'openBankPicker') openBankPicker()
  else if (name === 'openBranchPicker') openBranchPicker()
}

// 接收从 ChatKit 传入的 payload（用于列表页面内的操作）
async function applyIntent(payload: any) {
  // Prefer direct id if provided
  if (payload?.partnerId) {
    detailDialog.visible = true
    detailDialog.readonly = true
    detailDialog.id = payload.partnerId
    detailDialog.partnerCode = (payload?.openPartnerCode || payload?.partnerCode || payload?.code || '').toString().trim()
    return
  }
  // Otherwise search by partner code
  const code = (payload?.openPartnerCode || payload?.partnerCode || payload?.code || '').toString().trim()
  if (!code) return
  searchKeyword.value = code
  page.value = 1
  await load()
  await nextTick()
  const row = rows.value.find((r: any) => {
    const c = (r?.partner_code ?? r?.partnerCode ?? r?.payload?.partnerCode ?? r?.payload?.code ?? '').toString().trim()
    return c === code
  }) || rows.value[0]
  if (row) {
    openPartnerDetail(row)
  }
}

// 暴露方法给父组件（ChatKit）
defineExpose({ applyIntent })

onMounted(load)
</script>

<style scoped>
.page-header {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
}

/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #409eff;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-header-search {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
}

.page-actions {
  margin-left: auto;
}

/* 弹窗内 Card 样式（和会计凭证详情一致） */
.detail-card {
  max-width: 900px;
  margin: 0 auto;
}

.detail-header {
  align-items: center;
}

.detail-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>

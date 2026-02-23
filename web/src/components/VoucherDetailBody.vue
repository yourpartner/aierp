<template>
  <div class="voucher-detail-body">
    <template v-if="detailLoading">
      <el-skeleton :rows="6" animated />
    </template>
    <template v-else-if="detail">
      <VoucherDetailHeader
        :meta="headerMetaCombined"
        :labels="headerLabels"
        :invoice-label="invoiceLabel"
        :editable="editMode"
        :editable-header="editableHeader"
        :voucher-type-options="voucherTypeOptions"
        :currency-options="currencyOptions"
        @update:header="updateEditableHeader"
      />
      <div v-if="editError" class="voucher-edit-error">{{ editError }}</div>

      <template v-if="editMode">
        <el-table :data="editableLines" size="small" border style="width: 100%; margin-top: 8px">
          <el-table-column type="index" width="30" label="#" />
          <el-table-column :label="accountColumnLabel" min-width="160">
            <template #default="{ row }">
              <el-select
                v-model="row.accountCode"
                filterable
                remote
                reserve-keyword
                placeholder="勘定科目を検索"
                style="width: 100%"
                :remote-method="searchAccounts"
                :loading="accountsLoading"
                @focus="handleAccountFocus(row.accountCode)"
              >
                <el-option
                  v-for="item in accountOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column :label="text?.columns?.drcr || '借方/貸方'" width="120">
            <template #default="{ row }">
              <el-select v-model="row.drcr" style="width: 100%">
                <el-option v-for="opt in drcrOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column :label="text?.columns?.amount || '金額'" width="140">
            <template #default="{ row }">
              <div class="amount-cell">
                <el-input
                  class="amount-input"
                  size="small"
                  :model-value="formatEditableAmount(row.amount)"
                  style="width: 140px"
                  :input-style="amountInputStyle"
                  @input="onAmountInput(row, $event)"
                />
              </div>
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('customerId')" :label="customerColumnLabel" min-width="150">
            <template #default="{ row }">
              <el-select
                v-if="isVisible(row, 'customerId')"
                v-model="row.customerId"
                filterable
                remote
                reserve-keyword
                clearable
                style="width: 100%"
                :placeholder="customerColumnLabel"
                :remote-method="searchCustomers"
                :loading="loadingCustomers"
                @focus="handleCustomerFocus(row)"
              >
                <el-option
                  v-for="item in customerOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
              <span v-else-if="row.customerId" class="field-hidden-value" />
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('vendorId')" :label="listText.vendor" min-width="150">
            <template #default="{ row }">
              <el-select
                v-if="isVisible(row, 'vendorId')"
                v-model="row.vendorId"
                filterable
                remote
                reserve-keyword
                clearable
                style="width: 100%"
                :placeholder="listText.vendor"
                :remote-method="searchVendors"
                :loading="loadingVendors"
                @focus="handleVendorFocus(row)"
              >
                <el-option
                  v-for="item in vendorOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
              <span v-else-if="row.vendorId" class="field-hidden-value" />
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('departmentId')" :label="listText.department" min-width="150">
            <template #default="{ row }">
              <el-select
                v-if="isVisible(row, 'departmentId')"
                v-model="row.departmentId"
                filterable
                remote
                reserve-keyword
                clearable
                style="width: 100%"
                :placeholder="listText.department"
                :remote-method="searchDepartments"
                :loading="loadingDepartments"
                @focus="handleDepartmentFocus(row)"
              >
                <el-option
                  v-for="item in departmentOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
              <span v-else-if="row.departmentId" class="field-hidden-value" />
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('employeeId')" :label="listText.employee" min-width="150">
            <template #default="{ row }">
              <el-select
                v-if="isVisible(row, 'employeeId')"
                v-model="row.employeeId"
                filterable
                remote
                reserve-keyword
                clearable
                style="width: 100%"
                :placeholder="listText.employee"
                :remote-method="searchEmployees"
                :loading="loadingEmployees"
                @focus="handleEmployeeFocus(row)"
              >
                <el-option
                  v-for="item in employeeOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
              <span v-else-if="row.employeeId" class="field-hidden-value" />
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('paymentDate')" :label="listText.paymentDate" width="160">
            <template #default="{ row }">
              <el-date-picker
                v-if="isVisible(row, 'paymentDate')"
                v-model="row.paymentDate"
                type="date"
                value-format="YYYY-MM-DD"
                style="width: 140px"
                :class="{ req: isRequired(row, 'paymentDate') }"
              />
              <span v-else-if="row.paymentDate" class="field-hidden-value" />
            </template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('note')" :label="listText.note" min-width="180">
            <template #default="{ row }">
              <el-input v-model="row.note" />
            </template>
          </el-table-column>
          <el-table-column :label="deleteLabel" width="120">
            <template #default="{ $index }">
              <el-button size="small" type="danger" @click="$emit('remove-line', $index)">
                {{ deleteLabel }}
              </el-button>
            </template>
          </el-table-column>
        </el-table>
        <div class="voucher-edit-footer">
          <div class="voucher-edit-totals" :class="{ warn: !editBalanced }">
            <span>{{ drLabel }}: {{ formatAmount(editDebitTotal) }}</span>
            <span>{{ crLabel }}: {{ formatAmount(editCreditTotal) }}</span>
            <el-tag v-if="!editBalanced" type="danger" size="small">{{ imbalanceLabel }}</el-tag>
          </div>
          <el-button
            size="small"
            type="primary"
            @click="$emit('add-line', 'DR')"
          >
            明細を追加
          </el-button>
        </div>

        <!-- 编辑模式下的附件区域 -->
        <div class="voucher-attachments-section edit-mode">
          <div class="attachments-header">
            <span class="attachments-title">添付ファイル</span>
            <el-upload
              ref="uploadRef"
              action="#"
              :auto-upload="false"
              :on-change="onFileChange"
              :show-file-list="false"
              :multiple="true"
              accept=".pdf,.jpg,.jpeg,.png,.gif,.xlsx,.xls,.doc,.docx,.csv"
            >
              <el-button size="small" type="primary" :icon="Upload">ファイル追加</el-button>
            </el-upload>
          </div>
          <div v-if="editableAttachments.length > 0" class="attachments-grid">
            <div 
              v-for="(att, idx) in editableAttachments" 
              :key="att.id" 
              class="attachment-card"
              :class="{ clickable: !!att.url }"
            >
              <!-- 图片类型显示缩略图 -->
              <template v-if="isImageAttachment(att)">
                <div class="attachment-thumb" @click="handleAttachmentClick(att)">
                  <img :src="att.url" :alt="att.name" @error="onImageError($event)" />
                </div>
              </template>
              <!-- 非图片类型显示图标 -->
              <template v-else>
                <div class="attachment-thumb file-icon" @click="handleAttachmentClick(att)">
                  <el-icon :size="32"><Document /></el-icon>
                  <span class="file-ext">{{ getFileExtension(att.name) }}</span>
                </div>
              </template>
              <div class="attachment-info">
                <div class="attachment-name" :title="att.name">{{ att.name }}</div>
                <div class="attachment-meta">
                  <span class="attachment-size">{{ formatFileSize(att.size) }}</span>
                  <!-- 只有手工上传的附件可以删除（source !== 'ai'） -->
                  <el-button 
                    v-if="att.source !== 'ai'" 
                    size="small" 
                    type="danger" 
                    :icon="Delete" 
                    circle 
                    @click.stop="removeAttachment(idx)"
                  />
                  <el-tag v-else size="small" type="info">AI</el-tag>
                </div>
              </div>
            </div>
          </div>
          <div v-else class="attachments-empty">添付ファイルはありません</div>
        </div>
      </template>
      <template v-else>
        <el-table
          :data="detail.payload?.lines || []"
          size="small"
          border
          style="width: 100%; margin-top: 8px"
          v-loading="openItemsLoading"
        >
          <el-table-column type="index" width="60" label="#" />
          <el-table-column :label="accountColumnLabel" min-width="220">
            <template #default="{ row }">{{ accountLabel(row.accountCode) }}</template>
          </el-table-column>
          <el-table-column prop="drcr" :label="text?.columns?.drcr || '借方/貸方'" width="100" />
          <el-table-column :label="text?.columns?.amount || '金額'" width="160">
            <template #default="{ row }">{{ formatAmountCell(row.amount) }}</template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('departmentId')" :label="listText.department" min-width="120">
            <template #default="{ row }">{{ formatDepartment(row) }}</template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('employeeId')" :label="listText.employee" min-width="140">
            <template #default="{ row }">{{ formatEmployee(row) }}</template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('customerId')" :label="customerColumnLabel" min-width="140">
            <template #default="{ row }">{{ formatCustomer(row) }}</template>
          </el-table-column>
          <el-table-column v-if="shouldRenderField('vendorId')" :label="listText.vendor" min-width="140">
            <template #default="{ row }">{{ formatVendor(row) }}</template>
          </el-table-column>
          <el-table-column v-if="showPaymentDateColumn" :label="listText.paymentDate" width="140">
            <template #default="{ row }">{{ linePaymentDate(row) }}</template>
          </el-table-column>
          <!-- 清账相关列（低优先级）：放在備考左边 -->
          <el-table-column
            v-if="showClearingColumns"
            :label="clearingLabels.clearingStatus"
            width="110"
            align="center"
          >
            <template #default="{ row, $index }">
              <!-- 有open_item的情况 -->
              <template v-if="openItemForLine(row, $index)">
                <!-- 消込済/一部消込：可点击查看消込履历 -->
                <el-popover
                  v-if="['cleared','partial'].includes(clearingStatus(openItemForLine(row, $index))) || clearedItemsForLine(openItemForLine(row, $index)).length > 0"
                  trigger="click"
                  placement="top"
                  :width="420"
                >
                  <div class="clearing-popover">
                    <!-- 被清账履历（被谁清账了） -->
                    <template v-if="clearingHistory(openItemForLine(row, $index)).length > 0">
                      <div class="clearing-popover-title">消込履歴（消込元）</div>
                      <div class="clearing-history">
                        <div class="clearing-history-head">
                          <span class="h at">日付</span>
                          <span class="h amt">金額</span>
                          <span class="h no">伝票</span>
                          <span class="h ln">明細</span>
                        </div>
                        <div class="clearing-history-body">
                          <div
                            v-for="(h, i) in clearingHistory(openItemForLine(row, $index))"
                            :key="`${h.clearingVoucherNo || ''}-${h.clearingVoucherLineNo || ''}-${i}`"
                            class="clearing-history-row"
                          >
                            <span class="c at">{{ h.at || '-' }}</span>
                            <span class="c amt">{{ formatAmountCell(h.amount) }}</span>
                            <span 
                              class="c no clearing-voucher-link" 
                              :class="{ clickable: h.clearingVoucherNo }"
                              @click.stop="h.clearingVoucherNo && emit('open-clearing-voucher', h.clearingVoucherNo)"
                            >{{ h.clearingVoucherNo || '-' }}</span>
                            <span class="c ln">{{ h.clearingVoucherLineNo || '-' }}</span>
                          </div>
                        </div>
                      </div>
                    </template>
                    <!-- 清账了哪些凭证（消し込んだ伝票） -->
                    <template v-if="clearedItemsForLine(openItemForLine(row, $index)).length > 0">
                      <div class="clearing-popover-title cleared-items-title">消込先（本明細が消し込んだ伝票）</div>
                      <div class="clearing-history">
                        <div class="clearing-history-head">
                          <span class="h at">日付</span>
                          <span class="h amt">金額</span>
                          <span class="h no">伝票</span>
                          <span class="h ln">明細</span>
                        </div>
                        <div class="clearing-history-body">
                          <div
                            v-for="(c, i) in clearedItemsForLine(openItemForLine(row, $index))"
                            :key="`cleared-${c.voucherNo || ''}-${c.lineNo || ''}-${i}`"
                            class="clearing-history-row"
                          >
                            <span class="c at">{{ c.clearedAt || '-' }}</span>
                            <span class="c amt">{{ formatAmountCell(c.amount) }}</span>
                            <span 
                              class="c no clearing-voucher-link" 
                              :class="{ clickable: c.voucherNo }"
                              @click.stop="c.voucherNo && emit('open-clearing-voucher', c.voucherNo)"
                            >{{ c.voucherNo || '-' }}</span>
                            <span class="c ln">{{ c.lineNo || '-' }}</span>
                          </div>
                        </div>
                      </div>
                    </template>
                    <div v-if="clearingHistory(openItemForLine(row, $index)).length === 0 && clearedItemsForLine(openItemForLine(row, $index)).length === 0" class="clearing-popover-muted">※消込履歴が未登録です</div>
                  </div>
                  <template #reference>
                    <el-tag
                      class="clearing-tag clickable"
                      :type="clearingStatusTag(openItemForLine(row, $index))"
                      size="small"
                    >
                      {{ clearingStatusLabel(openItemForLine(row, $index)) }}
                    </el-tag>
                  </template>
                </el-popover>
                <el-tag v-else type="info" size="small">
                  {{ clearingLabels.open }}
                </el-tag>
              </template>
              <!-- 没有open_item但有clearedItems的情况（清账凭证） -->
              <template v-else-if="lineClearedItems(row).length > 0">
                <el-popover trigger="click" placement="top" :width="420">
                  <div class="clearing-popover">
                    <div class="clearing-popover-title">消込先（本明細が消し込んだ伝票）</div>
                    <div class="clearing-history">
                      <div class="clearing-history-head">
                        <span class="h at">日付</span>
                        <span class="h amt">金額</span>
                        <span class="h no">伝票</span>
                        <span class="h ln">明細</span>
                      </div>
                      <div class="clearing-history-body">
                        <div
                          v-for="(c, i) in lineClearedItems(row)"
                          :key="`line-cleared-${c.voucherNo || ''}-${c.lineNo || ''}-${i}`"
                          class="clearing-history-row"
                        >
                          <span class="c at">{{ c.clearedAt || '-' }}</span>
                          <span class="c amt">{{ formatAmountCell(c.amount) }}</span>
                          <span 
                            class="c no clearing-voucher-link" 
                            :class="{ clickable: c.voucherNo }"
                            @click.stop="c.voucherNo && emit('open-clearing-voucher', c.voucherNo)"
                          >{{ c.voucherNo || '-' }}</span>
                          <span class="c ln">{{ c.lineNo || '-' }}</span>
                        </div>
                      </div>
                    </div>
                  </div>
                  <template #reference>
                    <el-tag class="clearing-tag clickable" type="primary" size="small">消込元</el-tag>
                  </template>
                </el-popover>
              </template>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column
            v-if="showClearingResidualColumn"
            :label="clearingLabels.residualAmount"
            width="140"
            align="right"
          >
            <template #default="{ row, $index }">
              <template v-if="openItemForLine(row, $index)">
                <template v-if="clearingStatus(openItemForLine(row, $index)) === 'partial'">
                  {{ formatAmountCell(openItemResidual(openItemForLine(row, $index))) }}
                </template>
                <span v-else>-</span>
              </template>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column
            v-if="showClearingDateColumn"
            :label="clearingLabels.clearingDate"
            width="120"
            align="center"
          >
            <template #default="{ row, $index }">
              <template v-if="openItemForLine(row, $index)">
                {{ openItemClearingDate(openItemForLine(row, $index)) || '-' }}
              </template>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column v-if="showNoteColumn" :label="listText.note" min-width="220">
            <template #default="{ row }">{{ lineNote(row) }}</template>
          </el-table-column>
        </el-table>

        <!-- 附件显示区域 -->
        <div v-if="hasAttachments" class="voucher-attachments-section">
          <div class="attachments-title">添付ファイル</div>
          <div class="attachments-grid">
            <div 
              v-for="att in attachmentsList" 
              :key="att.id" 
              class="attachment-card"
              :class="{ clickable: !!att.url }"
              @click="handleAttachmentClick(att)"
            >
              <!-- 图片类型显示缩略图 -->
              <template v-if="isImageAttachment(att)">
                <div class="attachment-thumb">
                  <img :src="att.url" :alt="att.name" @error="onImageError($event)" />
                </div>
              </template>
              <!-- 非图片类型显示图标 -->
              <template v-else>
                <div class="attachment-thumb file-icon">
                  <el-icon :size="32"><Document /></el-icon>
                  <span class="file-ext">{{ getFileExtension(att.name) }}</span>
                </div>
              </template>
              <div class="attachment-info">
                <div class="attachment-name" :title="att.name">{{ att.name }}</div>
                <div class="attachment-size">{{ formatFileSize(att.size) }}</div>
              </div>
            </div>
          </div>
        </div>

        <!-- 图片预览弹窗 -->
        <el-dialog 
          v-model="imagePreviewVisible" 
          :title="imagePreviewName || '画像プレビュー'"
          width="auto"
          append-to-body
          destroy-on-close
          class="voucher-image-preview-dialog"
        >
          <img v-if="imagePreviewUrl" :src="imagePreviewUrl" :alt="imagePreviewName" class="preview-image" />
        </el-dialog>

        <!-- 文件预览弹窗（PDF、Office、其他） -->
        <el-dialog 
          v-model="filePreviewVisible" 
          :title="filePreviewName || 'ファイル プレビュー'"
          width="min(1200px, 96vw)"
          top="2vh"
          append-to-body
          destroy-on-close
          class="voucher-file-preview-dialog"
        >
          <!-- PDF 和 Office 文件：用 iframe 预览 -->
          <div v-if="filePreviewType === 'iframe' || filePreviewType === 'office'" class="file-preview-container">
            <iframe 
              :src="filePreviewUrl" 
              class="file-preview-iframe"
              frameborder="0"
            />
          </div>
          <!-- 其他文件：显示下载提示 -->
          <div v-else-if="filePreviewType === 'download'" class="file-download-prompt">
            <el-icon :size="64" color="#909399"><Document /></el-icon>
            <p class="file-name">{{ filePreviewName }}</p>
            <p class="file-hint">このファイル形式はプレビューできません</p>
            <el-button type="primary" @click="downloadFile">
              <el-icon><Download /></el-icon>
              ダウンロード
            </el-button>
          </div>
        </el-dialog>
      </template>
    </template>
    <el-empty v-else :description="detailError || '暂无数据'" />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Document, Upload, Delete, Download } from '@element-plus/icons-vue'
import VoucherDetailHeader from './VoucherDetailHeader.vue'
import api from '../api'

const props = defineProps<{
  detailLoading: boolean
  detail: any
  detailError: string
  detailMeta: any
  headerLabels: VoucherDetailHeaderLabels
  invoiceLabel: string
  editMode: boolean
  editLoading: boolean
  editError: string
  drLabel: string
  crLabel: string
  editBalanced: boolean
  editDebitTotal: number
  editCreditTotal: number
  buttonsText: Record<string, string>
  listText: Record<string, any>
  text: Record<string, any> | null
  accountColumnLabel: string
  customerColumnLabel: string
  showCustomerColumn: boolean
  showVendorColumn: boolean
  showDepartmentColumn: boolean
  showEmployeeColumn: boolean
  showPaymentDateColumn: boolean
  showNoteColumn: boolean
  editableHeader: Record<string, any>
  editableLines: any[]
  editableAttachments?: any[]
  voucherTypeOptions: Array<{ value: string; label: string }>
  currencyOptions: Array<{ value: string; label: string }>
  drcrOptions: Array<{ value: string; label: string }>
  accountLabel: (code: string) => string
  formatAmountCell: (val: any) => any
  linePaymentDate: (row: any) => string
  lineNote: (row: any) => string
  fetchAccountOptions?: (keyword: string) => Promise<Array<{ value: string; label: string; rules?: AccountRule }>>
}>()

interface VoucherDetailHeaderLabels {
  number: string
  date: string
  type: string
  currency: string
  summary: string
  createdAt: string
  createdBy: string
  updatedAt: string
  updatedBy: string
}

interface AccountRule {
  fieldRules: Record<string, string>
  taxType: string
}

interface AccountOption {
  value: string
  label: string
  rules?: AccountRule
}

const emit = defineEmits<{
  (e: 'save'): void
  (e: 'cancel'): void
  (e: 'add-line', side: 'DR' | 'CR'): void
  (e: 'remove-line', index: number): void
  (e: 'update:editable-header', payload: Record<string, any>): void
  (e: 'update:attachments', attachments: any[]): void
  (e: 'delete-attachment-blob', blobName: string): void
  (e: 'open-clearing-voucher', voucherNo: string): void
}>()

const accountOptions = ref<AccountOption[]>([])
const accountsLoading = ref(false)
const rulesVersion = ref(0)
const codeToRules = new Map<string, AccountRule>()
type OptionItem = { value: string; label: string }

const customerOptions = ref<OptionItem[]>([])
const vendorOptions = ref<OptionItem[]>([])
const departmentOptions = ref<OptionItem[]>([])
const employeeOptions = ref<OptionItem[]>([])
const loadingCustomers = ref(false)
const loadingVendors = ref(false)
const loadingDepartments = ref(false)
const loadingEmployees = ref(false)
const employeeOptionsPreloaded = ref(false)
const departmentOptionsPreloaded = ref(false)
const customerOptionsPreloaded = ref(false)
const vendorOptionsPreloaded = ref(false)

function normalizeCode(input: unknown) {
  return (input ?? '').toString().trim()
}

function setAccountRule(code: string, rule?: AccountRule) {
  const normalized = normalizeCode(code)
  if (!normalized) return
  const nextRule: AccountRule = {
    fieldRules: rule?.fieldRules ? { ...rule.fieldRules } : {},
    taxType: rule?.taxType || 'NON_TAX'
  }
  const existing = codeToRules.get(normalized)
  if (existing && JSON.stringify(existing) === JSON.stringify(nextRule)) return
  codeToRules.set(normalized, nextRule)
  rulesVersion.value += 1
}

function getRules(code: string | undefined) {
  const normalized = normalizeCode(code)
  if (!normalized) return null
  return codeToRules.get(normalized) || null
}

function isVisible(row: any, field: string) {
  // 依赖 rulesVersion 以便规则更新后触发重新渲染
  rulesVersion.value
  const rule = getRules(row?.accountCode)
  if (!rule || !rule.fieldRules) return true
  const state = rule.fieldRules[field]
  return state !== 'hidden'
}

function isRequired(row: any, field: string) {
  rulesVersion.value
  const rule = getRules(row?.accountCode)
  if (!rule || !rule.fieldRules) return false
  return rule.fieldRules[field] === 'required'
}

function applyFieldRulesToLine(line: any) {
  if (!line || typeof line !== 'object') return
  const rules = getRules(line.accountCode)
  // 记录规则版本，保证视图在更新后重新渲染
  line.__rulesVersion = rulesVersion.value
  if (!rules || !rules.fieldRules) return
  Object.entries(rules.fieldRules).forEach(([field, state]) => {
    if (state === 'hidden' && field in line) {
      line[field] = null
    }
  })
}

function applyFieldRulesToLines(lines: any[]) {
  if (!Array.isArray(lines)) return
  lines.forEach((line) => applyFieldRulesToLine(line))
}

function shouldRenderField(field: string) {
  if (props.editMode) {
    const lines = Array.isArray(props.editableLines) ? props.editableLines : []
    return lines.some((line: any) => isVisible(line, field))
  }
  switch (field) {
    case 'customerId':
      return props.showCustomerColumn
    case 'vendorId':
      return props.showVendorColumn
    case 'departmentId':
      return props.showDepartmentColumn
    case 'employeeId':
      return props.showEmployeeColumn
    case 'paymentDate':
      return props.showPaymentDateColumn
    case 'note':
      return props.showNoteColumn
    default:
      return true
  }
}

const imbalanceLabel = computed(() => props.text?.voucherForm?.totals?.imbalance || '不均衡')

const deleteLabel = computed(() => props.buttonsText.delete || '削除')

async function loadAccountRules(codes: string[]) {
  const unique = Array.from(new Set((codes || []).map(normalizeCode))).filter((code) => code && !codeToRules.has(code))
  if (unique.length === 0) return
  try {
    const resp = await api.post('/objects/account/search', {
      page: 1,
      pageSize: unique.length,
      where: [{ field: 'account_code', op: 'in', value: unique }],
      orderBy: []
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    rows.forEach((row: any) => {
      const code = normalizeCode(row.account_code || row.payload?.code)
      if (!code) return
      setAccountRule(code, {
        fieldRules: row.payload?.fieldRules || {},
        taxType: row.payload?.taxType || 'NON_TAX'
      })
    })
  } catch {
    // ignore rule loading errors
  }
}

async function searchAccounts(keyword: string) {
  if (!props.fetchAccountOptions) return
  const trimmed = (keyword || '').trim()
  accountsLoading.value = true
  try {
    const result = await props.fetchAccountOptions(trimmed)
    if (Array.isArray(result)) {
      result.forEach((item: AccountOption) => {
        if (item?.value) {
          setAccountRule(item.value, item.rules)
        }
      })
      accountOptions.value = result.map((item) => ({ value: item.value, label: item.label }))
      applyFieldRulesToLines(props.editableLines as any[])
    }
  } catch {
    // ignore
  } finally {
    accountsLoading.value = false
  }
}

async function ensureAccountOptions(code?: string) {
  const normalized = normalizeCode(code)
  if (!normalized || !props.fetchAccountOptions) return
  if (accountOptions.value.some(opt => opt.value === normalized)) {
    if (!codeToRules.has(normalized)) {
      await loadAccountRules([normalized])
      applyFieldRulesToLines(props.editableLines as any[])
    }
    return
  }
  await searchAccounts(normalized)
  await loadAccountRules([normalized])
  applyFieldRulesToLines(props.editableLines as any[])
}

async function handleAccountFocus(code?: string) {
  if (accountOptions.value.length === 0) {
    await searchAccounts('')
  }
  if (code) {
    await ensureAccountOptions(code)
  }
}

async function searchCustomers(keyword: string) {
  loadingCustomers.value = true
  try {
    const base = [{ field: 'flag_customer', op: 'eq', value: true }]
    const where = keyword?.trim()
      ? [...base, { json: 'name', op: 'contains', value: keyword }]
      : base
    const resp = await api.post('/objects/businesspartner/search', { where, page: 1, pageSize: keyword?.trim() ? 50 : 0 })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const mapped: OptionItem[] = rows.map((item: any) => ({
      label: `${item.payload?.name || item.payload?.displayName || ''} (${item.partner_code || item.payload?.code || ''})`,
      value: item.partner_code || item.payload?.code || item.id || ''
    }))
    customerOptions.value = mapped.filter((item) => !!item.value)
  } catch {
    customerOptions.value = []
  } finally {
    loadingCustomers.value = false
  }
}

async function searchVendors(keyword: string) {
  loadingVendors.value = true
  try {
    const base = [{ field: 'flag_vendor', op: 'eq', value: true }]
    const where = keyword?.trim()
      ? [...base, { json: 'name', op: 'contains', value: keyword }]
      : base
    const resp = await api.post('/objects/businesspartner/search', { where, page: 1, pageSize: keyword?.trim() ? 50 : 0 })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const mapped: OptionItem[] = rows.map((item: any) => ({
      label: `${item.payload?.name || item.payload?.displayName || ''} (${item.partner_code || item.payload?.code || ''})`,
      value: item.partner_code || item.payload?.code || item.id || ''
    }))
    vendorOptions.value = mapped.filter((item) => !!item.value)
  } catch {
    vendorOptions.value = []
  } finally {
    loadingVendors.value = false
  }
}

async function searchDepartments(keyword: string) {
  loadingDepartments.value = true
  try {
    const q = (keyword || '').trim()
    const where: any[] = []
    if (q) {
      where.push({ json: 'name', op: 'contains', value: q })
      where.push({ field: 'department_code', op: 'contains', value: q })
    }
    const resp = await api.post('/objects/department/search', {
      where,
      page: 1,
      pageSize: q ? 50 : 0,
      orderBy: [{ field: 'department_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const mapped: OptionItem[] = rows.map((item: any) => ({
      label: `${item.payload?.name || item.name || ''} (${item.department_code || item.payload?.code || ''})`,
      value: item.id || item.department_code || item.payload?.code || ''
    }))
    departmentOptions.value = mapped.filter((item) => !!item.value)
  } catch {
    departmentOptions.value = []
  } finally {
    loadingDepartments.value = false
  }
}

async function searchEmployees(keyword: string) {
  loadingEmployees.value = true
  try {
    const q = (keyword || '').trim()
    const where: any[] = []
    if (q) {
      where.push({ json: 'nameKanji', op: 'contains', value: q })
      where.push({ json: 'nameKana', op: 'contains', value: q })
      where.push({ field: 'employee_code', op: 'contains', value: q })
    }
    const resp = await api.post('/objects/employee/search', {
      where,
      page: 1,
      pageSize: q ? 50 : 0,
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const mapped: OptionItem[] = rows.map((item: any) => ({
      label: `${item.payload?.nameKanji || item.payload?.name || item.name || ''} (${item.employee_code || item.payload?.code || ''})`,
      value: item.id || item.employee_code || item.payload?.code || ''
    }))
    employeeOptions.value = mapped.filter((item) => !!item.value)
  } catch {
    employeeOptions.value = []
  } finally {
    loadingEmployees.value = false
  }
}

function ensureOption(list: typeof customerOptions, value: unknown, cachePrefix?: string) {
  const normalized = (value ?? '').toString().trim()
  if (!normalized) return
  if (!list.value.some(item => item.value === normalized)) {
    // 尝试从 nameCache 获取完整的 label（已包含代码）
    let label = normalized
    if (cachePrefix) {
      const cachedLabel = nameCache.value[`${cachePrefix}:${normalized}`]
      if (cachedLabel) {
        label = cachedLabel
      }
    }
    list.value.push({ value: normalized, label })
  }
}

// 更新选项的 label（如果当前是 GUID 显示，但缓存中有了正确的标签）
function updateOptionLabelFromCache(list: typeof customerOptions, value: unknown, cachePrefix: string) {
  const normalized = (value ?? '').toString().trim()
  if (!normalized) return
  const cachedLabel = nameCache.value[`${cachePrefix}:${normalized}`]
  if (!cachedLabel) return
  const opt = list.value.find(item => item.value === normalized)
  // 如果当前 label 是 GUID 或与缓存不同，则更新
  if (opt && (opt.label === normalized || opt.label !== cachedLabel)) {
    opt.label = cachedLabel
  }
}

function handleCustomerFocus(row: any) {
  ensureOption(customerOptions, row.customerId, 'cust')
  updateOptionLabelFromCache(customerOptions, row.customerId, 'cust')
  if (!customerOptionsPreloaded.value) {
    searchCustomers('').then(() => { customerOptionsPreloaded.value = true })
  }
}

function handleVendorFocus(row: any) {
  ensureOption(vendorOptions, row.vendorId, 'vend')
  updateOptionLabelFromCache(vendorOptions, row.vendorId, 'vend')
  if (!vendorOptionsPreloaded.value) {
    searchVendors('').then(() => { vendorOptionsPreloaded.value = true })
  }
}

function handleDepartmentFocus(row: any) {
  ensureOption(departmentOptions, row.departmentId, 'dept')
  updateOptionLabelFromCache(departmentOptions, row.departmentId, 'dept')
  if (!departmentOptionsPreloaded.value) {
    searchDepartments('').then(() => { departmentOptionsPreloaded.value = true })
  }
}

function handleEmployeeFocus(row: any) {
  ensureOption(employeeOptions, row.employeeId, 'emp')
  updateOptionLabelFromCache(employeeOptions, row.employeeId, 'emp')
  if (!employeeOptionsPreloaded.value) {
    searchEmployees('').then(() => { employeeOptionsPreloaded.value = true })
  }
}

function formatEditableAmount(value: string | number | null | undefined) {
  const num = Number(value)
  if (!Number.isFinite(num)) return ''
  return num.toLocaleString()
}

function parseEditableAmount(value: string) {
  const normalized = (value || '').replace(/,/g, '')
  const num = Number(normalized)
  return Number.isFinite(num) ? num : 0
}

function onAmountInput(row: any, value: string | number) {
  const normalized = typeof value === 'number' ? String(value) : String(value ?? '')
  const parsed = parseEditableAmount(normalized)
  row.amount = parsed
}

const amountInputStyle = { textAlign: 'left' } as const

watch(() => props.detail, (newDetail) => {
  accountOptions.value = []
  // Clear name cache and reload when detail changes
  nameCache.value = {}
  if (newDetail?.payload?.lines) {
    loadAllNames(newDetail.payload.lines)
  }
})

watch(
  () => props.editableLines,
  async (lines) => {
    if (!Array.isArray(lines)) {
      return
    }
    // 首先批量加载所有名称到缓存
    await loadAllNames(lines)
    await loadAccountRules(lines.map((line: any) => line?.accountCode))
    applyFieldRulesToLines(lines as any[])
    const map = new Map<string, string>()
    accountOptions.value.forEach(opt => {
      map.set(opt.value, opt.label)
    })
    lines.forEach((line: any) => {
      const code = typeof line?.accountCode === 'string' ? line.accountCode.trim() : ''
      if (!code) return
      if (!map.has(code)) {
        let label = props.accountLabel ? props.accountLabel(code) : code
        if (!label || label === code) {
          label = code
        } else if (!label.includes(code)) {
          label = `${code} ${label}`
        }
        map.set(code, label)
      }
      ensureOption(customerOptions, line.customerId, 'cust')
      ensureOption(vendorOptions, line.vendorId, 'vend')
      ensureOption(departmentOptions, line.departmentId, 'dept')
      ensureOption(employeeOptions, line.employeeId, 'emp')
    })
    accountOptions.value = Array.from(map.entries()).map(([value, label]) => ({
      value,
      label
    }))

    if (props.editMode && !employeeOptionsPreloaded.value) {
      await Promise.allSettled([
        searchEmployees('').then(() => { employeeOptionsPreloaded.value = true }),
        searchDepartments('').then(() => { departmentOptionsPreloaded.value = true }),
        searchCustomers('').then(() => { customerOptionsPreloaded.value = true }),
        searchVendors('').then(() => { vendorOptionsPreloaded.value = true })
      ])
    }
  },
  { immediate: true, deep: true }
)

watch(
  () => rulesVersion.value,
  () => {
    applyFieldRulesToLines(props.editableLines as any[])
  }
)

const headerMetaCombined = computed(() => {
  const base = props.detailMeta ? { ...props.detailMeta } : {}
  const header = props.editableHeader || {}
  const updatableKeys: Array<keyof typeof header> = ['voucherNo', 'postingDate', 'voucherType', 'currency', 'invoiceRegistrationNo', 'summary']
  updatableKeys.forEach((key) => {
    const value = header[key]
    if (value !== undefined && value !== null) {
      ;(base as any)[key] = value
    }
  })
  return base
})

function updateEditableHeader(payload: Record<string, any>) {
  if (!payload || typeof payload !== 'object') return
  emit('update:editable-header', payload)
}

function formatAmount(amount: number) {
  return Number(amount || 0).toLocaleString()
}

// 附件相关
const hasAttachments = computed(() => {
  const attachments = props.detail?.payload?.attachments
  return Array.isArray(attachments) && attachments.length > 0
})

const attachmentsList = computed(() => {
  const attachments = props.detail?.payload?.attachments
  if (!Array.isArray(attachments)) return []
  return attachments.map((att: any) => {
    // 处理两种情况：完整对象 或 只有 ID 字符串
    if (typeof att === 'string') {
      // 旧数据：只有 ID
      return {
        id: att,
        name: 'ファイル',
        contentType: 'application/octet-stream',
        size: 0,
        url: '',
        uploadedAt: ''
      }
    }
    // 新数据：完整对象
    return {
      id: att.id || att.blobName || Math.random().toString(),
      name: att.name || att.fileName || 'ファイル',
      contentType: att.contentType || 'application/octet-stream',
      size: att.size || 0,
      url: att.url || '',
      uploadedAt: att.uploadedAt
    }
  })
})

function formatFileSize(bytes: number) {
  if (!bytes || bytes < 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

// 编辑模式下的附件管理
const uploadRef = ref<any>(null)

// 使用 props 传入的附件列表
const editableAttachments = computed(() => props.editableAttachments || [])

// 文件选择后本地预览（不立即上传）
function onFileChange(uploadFile: any) {
  const file = uploadFile.raw as File
  if (!file) return
  
  const maxSize = 10 * 1024 * 1024 // 10MB
  if (file.size > maxSize) {
    console.warn('File too large:', file.name)
    return
  }
  
  // 生成本地预览 URL
  const previewUrl = URL.createObjectURL(file)
  const tempId = `temp_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`
  
  const newAtt = {
    id: tempId,
    name: file.name,
    contentType: file.type,
    size: file.size,
    url: previewUrl,
    source: 'manual', // 标记为手工上传
    _file: file, // 保存原始文件对象，保存时上传
    _isLocal: true // 标记为本地文件，尚未上传
  }
  
  const newList = [...editableAttachments.value, newAtt]
  emit('update:attachments', newList)
}

function removeAttachment(index: number) {
  const att = editableAttachments.value[index]
  // 只能删除手工上传的附件
  if (att && att.source !== 'ai') {
    // 释放本地预览 URL
    if (att._isLocal && att.url) {
      URL.revokeObjectURL(att.url)
    }
    // 如果不是本地文件（已上传到 Azure），通知父组件删除 blob
    if (!att._isLocal && att.blobName) {
      emit('delete-attachment-blob', att.blobName)
    }
    const newList = editableAttachments.value.filter((_: any, i: number) => i !== index)
    emit('update:attachments', newList)
  }
}

// 图片预览相关
const imagePreviewVisible = ref(false)
const imagePreviewUrl = ref('')
const imagePreviewName = ref('')

// 文件预览相关（PDF、Office 等）
const filePreviewVisible = ref(false)
const filePreviewUrl = ref('')
const filePreviewName = ref('')
const filePreviewType = ref<'iframe' | 'office' | 'download'>('iframe')

const imageTypes = ['image/jpeg', 'image/jpg', 'image/png', 'image/gif', 'image/webp', 'image/bmp']

function isImageAttachment(att: any) {
  if (!att) return false
  // 通过 contentType 判断
  if (att.contentType && imageTypes.includes(att.contentType.toLowerCase())) {
    return true
  }
  // 通过文件扩展名判断
  const name = (att.name || '').toLowerCase()
  return /\.(jpg|jpeg|png|gif|webp|bmp)$/i.test(name)
}

function isPdfAttachment(att: any) {
  if (!att) return false
  if (att.contentType && att.contentType.toLowerCase() === 'application/pdf') {
    return true
  }
  const name = (att.name || '').toLowerCase()
  return /\.pdf$/i.test(name)
}

function isOfficeAttachment(att: any) {
  if (!att) return false
  const name = (att.name || '').toLowerCase()
  // Word, Excel, PowerPoint
  return /\.(doc|docx|xls|xlsx|ppt|pptx)$/i.test(name)
}

function getFileExtension(filename: string) {
  if (!filename) return ''
  const ext = filename.split('.').pop()?.toUpperCase() || ''
  return ext.length > 4 ? ext.substring(0, 4) : ext
}

function handleAttachmentClick(att: any) {
  if (!att.url) return
  
  if (isImageAttachment(att)) {
    // 图片：打开图片预览弹窗
    imagePreviewUrl.value = att.url
    imagePreviewName.value = att.name || 'プレビュー'
    imagePreviewVisible.value = true
  } else if (isPdfAttachment(att)) {
    // PDF：用 iframe 预览
    filePreviewUrl.value = att.url
    filePreviewName.value = att.name || 'PDF プレビュー'
    filePreviewType.value = 'iframe'
    filePreviewVisible.value = true
  } else if (isOfficeAttachment(att)) {
    // Office 文件：用 Microsoft Office Online Viewer
    const encodedUrl = encodeURIComponent(att.url)
    filePreviewUrl.value = `https://view.officeapps.live.com/op/embed.aspx?src=${encodedUrl}`
    filePreviewName.value = att.name || 'ファイル プレビュー'
    filePreviewType.value = 'office'
    filePreviewVisible.value = true
  } else {
    // 其他文件：显示下载弹窗
    filePreviewUrl.value = att.url
    filePreviewName.value = att.name || 'ファイル'
    filePreviewType.value = 'download'
    filePreviewVisible.value = true
  }
}

function downloadFile() {
  if (filePreviewUrl.value) {
    window.open(filePreviewUrl.value, '_blank')
  }
}

function onImageError(event: Event) {
  const img = event.target as HTMLImageElement
  if (img) {
    img.style.display = 'none'
  }
}

// Reactive cache for resolved names (code -> name mapping)
const nameCache = ref<Record<string, string>>({})

// ===== 清账(open item) 信息：用于凭证详情弹窗显示 =====
const openItemsLoading = ref(false)
const openItemByLineNo = ref<Record<number, any>>({})

function parseJsonSafe(value: any): any {
  try {
    if (!value) return null
    if (typeof value === 'object') return value
    const text = String(value).trim()
    if (!text) return null
    return JSON.parse(text)
  } catch {
    return null
  }
}

function lineNoOf(row: any, index: number): number {
  const raw = row?.lineNo ?? row?.line_no ?? row?.lineNO
  const n = Number(raw)
  if (Number.isFinite(n) && n > 0) return n
  return index + 1
}

async function loadOpenItemsForVoucher(voucherId: string) {
  if (!voucherId) {
    openItemByLineNo.value = {}
    return
  }
  openItemsLoading.value = true
  try {
    // 注意：openitem 默认会自动追加 residual_amount > 0（只返回未清项）
    // 为了同时拿到已清/部分清：加一个 residual_amount >= 0 的 where 条件以关闭该默认过滤
    const resp = await api.post('/objects/openitem/search', {
      page: 1,
      pageSize: 500,
      where: [
        { field: 'voucher_id', op: 'eq', value: voucherId },
        { field: 'residual_amount', op: 'gte', value: 0 }
      ],
      orderBy: []
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const map: Record<number, any> = {}
    rows.forEach((it: any) => {
      const refs = parseJsonSafe(it?.refs)
      const lineNo = Number(it?.voucher_line_no ?? it?.voucherLineNo ?? refs?.lineNo ?? refs?.line_no ?? 0)
      if (Number.isFinite(lineNo) && lineNo > 0) {
        map[lineNo] = it
      }
    })
    openItemByLineNo.value = map
  } catch {
    openItemByLineNo.value = {}
  } finally {
    openItemsLoading.value = false
  }
}

watch(
  () => String(props.detail?.id || ''),
  (id) => {
    if (!id) {
      openItemByLineNo.value = {}
      return
    }
    // 只在查看模式加载；编辑模式时用不到这些字段
    if (!props.editMode) {
      loadOpenItemsForVoucher(id)
    }
  },
  { immediate: true }
)

const showClearingColumns = computed(() => {
  const items = Object.values(openItemByLineNo.value || {})
  // 如果存在open_item（表示该科目需要清账管理），显示清账状态列
  if (items.length > 0) return true
  // 如果凭证行有clearedItems（表示这是清账凭证），也显示清账状态列
  const lines = props.detail?.payload?.lines || []
  return lines.some((line: any) => Array.isArray(line.clearedItems) && line.clearedItems.length > 0)
})

const clearingLabels = computed(() => ({
  // 需求：固定日语显示（不随 UI 语言变化）
  clearingStatus: '消込ステータス',
  clearingDate: '消込日付',
  residualAmount: '未消込残高',
  cleared: '消込済',
  partial: '一部消込',
  open: '未消込'
}))

const showClearingResidualColumn = computed(() => {
  if (!showClearingColumns.value) return false
  // 仅当存在“一部消込”行时才显示“未消込残高”
  return Object.values(openItemByLineNo.value || {}).some((it: any) => clearingStatus(it) === 'partial')
})

const showClearingDateColumn = computed(() => {
  if (!showClearingColumns.value) return false
  return Object.values(openItemByLineNo.value || {}).some((it: any) => !!openItemClearingDate(it))
})

function openItemForLine(row: any, index: number) {
  const lineNo = lineNoOf(row, index)
  return openItemByLineNo.value?.[lineNo] || null
}

function openItemResidual(item: any) {
  const n = Number(item?.residual_amount ?? item?.residualAmount ?? 0)
  return Number.isFinite(n) ? n : 0
}

function openItemOriginal(item: any) {
  const n = Number(item?.original_amount ?? item?.originalAmount ?? 0)
  return Number.isFinite(n) ? n : 0
}

function clearingStatus(item: any): 'open' | 'partial' | 'cleared' {
  if (!item) return 'open'
  const clearedFlag = item?.cleared_flag ?? item?.clearedFlag
  const residual = openItemResidual(item)
  const original = openItemOriginal(item)
  if (clearedFlag === true || residual <= 0.00001) return 'cleared'
  if (original > 0.00001 && residual < original - 0.00001) return 'partial'
  return 'open'
}

// 获取该行清掉了哪些凭证（从 open_item的refs.clearedItems）
function clearedItemsForLine(item: any): Array<{ voucherNo: string; lineNo: string | number; amount: number; clearedAt: string }> {
  if (!item) return []
  const refs = parseJsonSafe(item?.refs)
  const clearedItems = Array.isArray(refs?.clearedItems) ? refs.clearedItems : []
  return clearedItems.map((c: any) => ({
    voucherNo: c.voucherNo || '',
    lineNo: c.lineNo || '',
    amount: Number(c.amount) || 0,
    clearedAt: (c.clearedAt || '').toString().slice(0, 10)
  }))
}

// 获取凭证行本身的clearedItems（用于清账凭证，行上直接记录了清账了哪些凭证）
function lineClearedItems(row: any): Array<{ voucherNo: string; lineNo: string | number; amount: number; clearedAt: string }> {
  const clearedItems = Array.isArray(row?.clearedItems) ? row.clearedItems : []
  return clearedItems.map((c: any) => ({
    voucherNo: c.voucherNo || '',
    lineNo: c.lineNo || '',
    amount: Number(c.amount) || 0,
    clearedAt: (c.clearedAt || '').toString().slice(0, 10)
  }))
}

// 清账状态标签类型
function clearingStatusTag(item: any): 'success' | 'warning' | 'info' | 'primary' {
  const status = clearingStatus(item)
  const hasClearedItems = clearedItemsForLine(item).length > 0
  if (status === 'cleared') return 'success'
  if (status === 'partial') return 'warning'
  if (hasClearedItems) return 'primary' // 本行清掉了其他凭证
  return 'info'
}

// 清账状态标签文本
function clearingStatusLabel(item: any): string {
  const status = clearingStatus(item)
  const hasClearedItems = clearedItemsForLine(item).length > 0
  if (status === 'cleared') return clearingLabels.value.cleared
  if (status === 'partial') return clearingLabels.value.partial
  if (hasClearedItems) return '消込元' // 本行是清账方
  return clearingLabels.value.open
}

function openItemClearingDate(item: any): string {
  if (!item) return ''
  const raw = item?.cleared_at ?? item?.clearedAt
  if (!raw) return ''
  const str = typeof raw === 'string' ? raw : String(raw)
  // 兼容 "2025-01-02T..." 或 "2025-01-02"
  return str.length >= 10 ? str.slice(0, 10) : str
}

function openItemClearingVoucherNo(item: any): string {
  if (!item) return ''
  const refs = parseJsonSafe(item?.refs)
  const hist = Array.isArray(refs?.clearingHistory) ? refs.clearingHistory : []
  const last = hist.length > 0 ? hist[hist.length - 1] : null
  const val =
    item?.clearingVoucherNo ??
    item?.clearing_voucher_no ??
    refs?.clearingVoucherNo ??
    refs?.clearing_voucher_no ??
    last?.clearingVoucherNo ??
    refs?.clearingVoucher ??
    refs?.clearing_voucher ??
    ''
  return (val ?? '').toString().trim()
}

function openItemClearingVoucherLineNo(item: any): string {
  if (!item) return ''
  const refs = parseJsonSafe(item?.refs)
  const hist = Array.isArray(refs?.clearingHistory) ? refs.clearingHistory : []
  const last = hist.length > 0 ? hist[hist.length - 1] : null
  const val =
    item?.clearingVoucherLineNo ??
    item?.clearing_voucher_line_no ??
    refs?.clearingVoucherLineNo ??
    refs?.clearing_voucher_line_no ??
    last?.clearingVoucherLineNo ??
    refs?.clearingLineNo ??
    refs?.clearing_line_no ??
    ''
  const text = (val ?? '').toString().trim()
  return text
}

function clearingHistory(item: any): Array<{ at: string; amount: number; clearingVoucherNo: string; clearingVoucherLineNo: string }> {
  if (!item) return []
  const refs = parseJsonSafe(item?.refs)
  const raw = Array.isArray(refs?.clearingHistory) ? refs.clearingHistory : []
  const mapped = raw
    .map((x: any) => {
      const atRaw = x?.at
      const atStr = atRaw ? String(atRaw) : ''
      const at = atStr.length >= 10 ? atStr.slice(0, 10) : atStr
      const amount = Number(x?.amount ?? 0)
      const clearingVoucherNo = (x?.clearingVoucherNo ?? '').toString()
      const clearingVoucherLineNo = (x?.clearingVoucherLineNo ?? '').toString()
      return { at, amount: Number.isFinite(amount) ? amount : 0, clearingVoucherNo, clearingVoucherLineNo }
    })
    .filter((x: any) => !!(x.at || x.clearingVoucherNo || x.clearingVoucherLineNo) || (Number(x.amount) || 0) !== 0)
  // 按日期正序（没日期的放后面）
  mapped.sort((a, b) => (a.at || '9999-12-31').localeCompare(b.at || '9999-12-31'))
  return mapped
}


// Format display functions for voucher line fields - use reactive cache
function formatDepartment(row: any) {
  const code = row.departmentCode || row.departmentId
  if (!code) return ''
  const cacheKey = `dept:${code}`
  return nameCache.value[cacheKey] || code
}

function formatEmployee(row: any) {
  const code = row.employeeCode || row.employeeId
  if (!code) return ''
  const cacheKey = `emp:${code}`
  return nameCache.value[cacheKey] || code
}

function formatCustomer(row: any) {
  const code = row.customerCode || row.customerId
  if (!code) return ''
  const cacheKey = `cust:${code}`
  return nameCache.value[cacheKey] || code
}

function formatVendor(row: any) {
  const code = row.vendorCode || row.vendorId
  if (!code) return ''
  const cacheKey = `vend:${code}`
  return nameCache.value[cacheKey] || code
}

// Batch load all names when detail changes
async function loadAllNames(lines: any[]) {
  if (!Array.isArray(lines) || lines.length === 0) return

  // Collect unique codes
  const deptCodes = new Set<string>()
  const empCodes = new Set<string>()
  const custCodes = new Set<string>()
  const vendCodes = new Set<string>()

  lines.forEach((line: any) => {
    if (line.departmentCode || line.departmentId) deptCodes.add(line.departmentCode || line.departmentId)
    if (line.employeeCode || line.employeeId) empCodes.add(line.employeeCode || line.employeeId)
    if (line.customerCode || line.customerId) custCodes.add(line.customerCode || line.customerId)
    if (line.vendorCode || line.vendorId) vendCodes.add(line.vendorCode || line.vendorId)
  })

  // Load in parallel
  const promises: Promise<void>[] = []

  if (deptCodes.size > 0) {
    promises.push(loadDepartmentNames(Array.from(deptCodes)))
  }
  if (empCodes.size > 0) {
    promises.push(loadEmployeeNames(Array.from(empCodes)))
  }
  if (custCodes.size > 0) {
    promises.push(loadCustomerNames(Array.from(custCodes)))
  }
  if (vendCodes.size > 0) {
    promises.push(loadVendorNames(Array.from(vendCodes)))
  }

  await Promise.all(promises)
}

async function loadDepartmentNames(codes: string[]) {
  try {
    // 分离 UUID 和 department_code
    const uuids = codes.filter(c => c.includes('-') && c.length > 30)
    const deptCodes = codes.filter(c => !c.includes('-') || c.length <= 30)
    
    const allRows: any[] = []
    
    // 通过 department_code 查询
    if (deptCodes.length > 0) {
    const resp = await api.post('/objects/department/search', {
        where: [{ field: 'department_code', op: 'in', value: deptCodes }],
      page: 1,
        pageSize: deptCodes.length
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    // 通过 UUID 查询
    if (uuids.length > 0) {
      const resp = await api.post('/objects/department/search', {
        where: [{ field: 'id', op: 'in', value: uuids }],
        page: 1,
        pageSize: uuids.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    allRows.forEach((row: any) => {
      const code = row.department_code || row.payload?.code
      const id = row.id
      const name = row.payload?.name || row.name || code
      const displayLabel = code ? `${name} (${code})` : name
      // 同时缓存 department_code 和 id 的映射（缓存完整的显示标签）
      if (code) {
        nameCache.value[`dept:${code}`] = displayLabel
      }
      if (id) {
        nameCache.value[`dept:${id}`] = displayLabel
      }
    })
  } catch {
    // Ignore errors
  }
}

async function loadEmployeeNames(codes: string[]) {
  try {
    // 分离 UUID 和 employee_code
    const uuids = codes.filter(c => c.includes('-') && c.length > 30)
    const empCodes = codes.filter(c => !c.includes('-') || c.length <= 30)
    
    const allRows: any[] = []
    
    // 通过 employee_code 查询
    if (empCodes.length > 0) {
    const resp = await api.post('/objects/employee/search', {
        where: [{ field: 'employee_code', op: 'in', value: empCodes }],
      page: 1,
        pageSize: empCodes.length
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    // 通过 UUID 查询
    if (uuids.length > 0) {
      const resp = await api.post('/objects/employee/search', {
        where: [{ field: 'id', op: 'in', value: uuids }],
        page: 1,
        pageSize: uuids.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    allRows.forEach((row: any) => {
      const code = row.employee_code || row.payload?.code
      const id = row.id
      const name = row.payload?.nameKanji || row.payload?.name || row.name || code
      const displayLabel = code ? `${name} (${code})` : name
      // 同时缓存 employee_code 和 id 的映射（缓存完整的显示标签）
      if (code) {
        nameCache.value[`emp:${code}`] = displayLabel
      }
      if (id) {
        nameCache.value[`emp:${id}`] = displayLabel
      }
    })
  } catch {
    // Ignore errors
  }
}

async function loadCustomerNames(codes: string[]) {
  try {
    // 分离 UUID 和 partner_code
    const uuids = codes.filter(c => c.includes('-') && c.length > 30)
    const partnerCodes = codes.filter(c => !c.includes('-') || c.length <= 30)
    
    const allRows: any[] = []
    
    // 通过 partner_code 查询
    if (partnerCodes.length > 0) {
      const resp = await api.post('/objects/businesspartner/search', {
        where: [
          { field: 'partner_code', op: 'in', value: partnerCodes },
          { field: 'flag_customer', op: 'eq', value: true }
        ],
        page: 1,
        pageSize: partnerCodes.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    // 通过 id (UUID) 查询
    if (uuids.length > 0) {
      const resp = await api.post('/objects/businesspartner/search', {
        where: [
          { field: 'id', op: 'in', value: uuids },
          { field: 'flag_customer', op: 'eq', value: true }
        ],
        page: 1,
        pageSize: uuids.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    allRows.forEach((row: any) => {
      const code = row.partner_code || row.payload?.code
      const id = row.id
      const name = row.payload?.name || row.payload?.displayName || code
      const displayLabel = code ? `${name} (${code})` : name
      // 同时缓存 partner_code 和 id 的映射（缓存完整的显示标签）
      if (code) {
        nameCache.value[`cust:${code}`] = displayLabel
      }
      if (id) {
        nameCache.value[`cust:${id}`] = displayLabel
      }
    })
  } catch {
    // Ignore errors
  }
}

async function loadVendorNames(codes: string[]) {
  try {
    // 分离 UUID 和 partner_code
    const uuids = codes.filter(c => c.includes('-') && c.length > 30)
    const partnerCodes = codes.filter(c => !c.includes('-') || c.length <= 30)
    
    const allRows: any[] = []
    
    // 通过 partner_code 查询
    if (partnerCodes.length > 0) {
      const resp = await api.post('/objects/businesspartner/search', {
        where: [
          { field: 'partner_code', op: 'in', value: partnerCodes },
          { field: 'flag_vendor', op: 'eq', value: true }
        ],
        page: 1,
        pageSize: partnerCodes.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    // 通过 id (UUID) 查询
    if (uuids.length > 0) {
      const resp = await api.post('/objects/businesspartner/search', {
        where: [
          { field: 'id', op: 'in', value: uuids },
          { field: 'flag_vendor', op: 'eq', value: true }
        ],
        page: 1,
        pageSize: uuids.length
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      allRows.push(...rows)
    }
    
    allRows.forEach((row: any) => {
      const code = row.partner_code || row.payload?.code
      const id = row.id
      const name = row.payload?.name || row.payload?.displayName || code
      const displayLabel = code ? `${name} (${code})` : name
      // 同时缓存 partner_code 和 id 的映射（缓存完整的显示标签）
      if (code) {
        nameCache.value[`vend:${code}`] = displayLabel
      }
      if (id) {
        nameCache.value[`vend:${id}`] = displayLabel
      }
    })
  } catch {
    // Ignore errors
  }
}
</script>

<style scoped>
.voucher-detail-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.voucher-edit-error {
  color: #d03050;
  font-size: 12px;
}

.voucher-edit-footer {
  margin-top: 8px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.voucher-edit-totals {
  display: flex;
  align-items: center;
  gap: 16px;
  font-weight: 500;
  color: inherit;
}

.voucher-edit-totals.warn {
  color: #d03050;
}

.voucher-edit-totals > span {
  white-space: nowrap;
}

.amount-cell {
  display: flex;
  width: 100%;
}

.amount-cell :deep(.el-input .el-input__inner),
.amount-cell :deep(.el-input-number .el-input__inner) {
  text-align: left;
}

.amount-cell :deep(.amount-input .el-input__wrapper) {
  border-color: var(--el-border-color);
  background-color: var(--el-color-white);
  box-shadow: none;
}

.amount-cell :deep(.amount-input.is-focus .el-input__wrapper),
.amount-cell :deep(.amount-input .el-input__wrapper.is-focus) {
  border-color: var(--el-color-primary);
  box-shadow: 0 0 0 1px var(--el-color-primary-light-3, rgba(64, 158, 255, 0.2));
}

.field-hidden-value {
  display: none;
}

/* 附件显示区域样式 */
.voucher-attachments-section {
  margin-top: 16px;
  padding: 12px 16px;
  background: #fafbfc;
  border-radius: 6px;
  border: 1px solid #e8e8e8;
}

.voucher-attachments-section .attachments-title {
  font-weight: 600;
  font-size: 13px;
  color: #333;
  margin-bottom: 12px;
}

.voucher-attachments-section .attachments-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.voucher-attachments-section .attachment-card {
  width: 120px;
  background: #fff;
  border-radius: 8px;
  border: 1px solid #e0e0e0;
  overflow: hidden;
  transition: all 0.2s ease;
}

.voucher-attachments-section .attachment-card.clickable {
  cursor: pointer;
}

.voucher-attachments-section .attachment-card.clickable:hover {
  border-color: #409eff;
  box-shadow: 0 2px 8px rgba(64, 158, 255, 0.15);
  transform: translateY(-2px);
}

.voucher-attachments-section .attachment-thumb {
  width: 100%;
  height: 80px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #f5f7fa;
  overflow: hidden;
}

.voucher-attachments-section .attachment-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.voucher-attachments-section .attachment-thumb.file-icon {
  flex-direction: column;
  gap: 4px;
  color: #909399;
}

.voucher-attachments-section .attachment-thumb .file-ext {
  font-size: 10px;
  font-weight: 600;
  color: #606266;
  text-transform: uppercase;
}

.voucher-attachments-section .attachment-info {
  padding: 8px;
}

.voucher-attachments-section .attachment-name {
  font-size: 12px;
  color: #333;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  margin-bottom: 2px;
}

.voucher-attachments-section .attachment-size {
  font-size: 11px;
  color: #999;
}

.voucher-attachments-section .attachment-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.voucher-attachments-section.edit-mode {
  margin-top: 16px;
}

.voucher-attachments-section .attachments-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

/* 图片预览弹窗样式 - 与 ChatKit 保持一致 */
.voucher-image-preview-dialog {
  text-align: center;
}

.voucher-image-preview-dialog .preview-image {
  max-width: 80vw;
  max-height: 80vh;
  border-radius: 12px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

/* 注意：本文件是 scoped，需要 :deep 才能作用到 Element Plus 内部结构 */
:deep(.voucher-file-preview-dialog) {
  border-radius: 12px;
  overflow: hidden;
}

:deep(.voucher-file-preview-dialog .el-dialog) {
  max-height: 96vh;
}

:deep(.voucher-file-preview-dialog .el-dialog__body) {
  padding: 0 !important;
}

.file-preview-container {
  /* 关键：不依赖 el-dialog__body 的高度，直接给内容区一个稳定高度 */
  height: 88vh;
  width: 100%;
  background: #111827; /* 深色底避免大白块刺眼 */
}

.file-preview-iframe {
  width: 100%;
  height: 100%;
  border: none;
  display: block;
}

.file-download-prompt {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  padding: 40px;
  text-align: center;
}

.file-download-prompt .file-name {
  margin-top: 16px;
  font-size: 16px;
  font-weight: 600;
  color: #333;
  word-break: break-all;
}

.file-download-prompt .file-hint {
  margin: 8px 0 24px;
  font-size: 14px;
  color: #909399;
}

.clearing-tag.clickable {
  cursor: pointer;
}

.clearing-popover {
  display: flex;
  flex-direction: column;
  gap: 8px;
  font-size: 12px;
}

.clearing-popover-title {
  font-weight: 700;
  color: #111827;
}

.clearing-popover-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
}

.clearing-popover-row .k {
  color: #6b7280;
  white-space: nowrap;
}

.clearing-popover-row .v {
  color: #111827;
  font-weight: 600;
  word-break: break-all;
  text-align: right;
}

.clearing-popover-muted {
  color: #9ca3af;
  line-height: 1.4;
}

.clearing-history {
  margin-top: 6px;
  border: 1px solid #e5e7eb;
  border-radius: 8px;
  overflow: hidden;
}

.clearing-history-head,
.clearing-history-row {
  display: grid;
  grid-template-columns: 90px 90px 1fr 70px;
  gap: 8px;
  align-items: center;
}

.clearing-history-head {
  background: #f9fafb;
  padding: 8px 10px;
  font-weight: 700;
  color: #374151;
  font-size: 12px;
}

.clearing-history-body {
  max-height: 180px;
  overflow: auto;
}

.clearing-history-row {
  padding: 8px 10px;
  border-top: 1px solid #f3f4f6;
  font-size: 12px;
  color: #111827;
}

.clearing-history-row .c.amt {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.clearing-history-row .c.no {
  word-break: break-all;
}

.clearing-voucher-link.clickable {
  color: #409eff;
  cursor: pointer;
  text-decoration: underline;
}

.clearing-voucher-link.clickable:hover {
  color: #66b1ff;
}

/* 清账履历中的"消込先"区域样式 */
.cleared-items-title {
  margin-top: 8px;
}
</style>


<template>
  <div class="voucher-detail-header" :class="{ editable }">
    <div class="row four-col">
      <div class="field">
        <span class="field-label">{{ labels.number }}：</span>
        <div class="field-value" :class="{ view: !editable }">
          <template v-if="editable">
            <div class="field-control short">
              <el-input :model-value="editableHeaderRef.voucherNo" disabled />
            </div>
          </template>
          <template v-else>{{ display(metaRef.voucherNo) }}</template>
        </div>
      </div>
      <div class="field">
        <span class="field-label">{{ labels.date }}：</span>
        <div class="field-value" :class="{ view: !editable }">
          <template v-if="editable">
            <div class="field-control tiny">
              <el-date-picker
                v-model="postingDateModel"
                type="date"
                value-format="YYYY-MM-DD"
              />
            </div>
          </template>
          <template v-else>{{ display(metaRef.postingDate) }}</template>
        </div>
      </div>
      <div class="field">
        <span class="field-label">{{ labels.type }}：</span>
        <div class="field-value" :class="{ view: !editable }">
          <template v-if="editable">
            <div class="field-control medium">
              <el-select
                v-model="voucherTypeModel"
                filterable
              >
                <el-option v-for="opt in voucherTypeOptionList" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </div>
          </template>
          <template v-else>{{ display(metaRef.voucherType) }}</template>
        </div>
      </div>
      <div class="field">
        <span class="field-label">{{ labels.currency }}：</span>
        <div class="field-value" :class="{ view: !editable }">
          <template v-if="editable">
            <div class="field-control tiny">
              <el-select
                v-model="currencyModel"
                placeholder="JPY"
                :clearable="false"
              >
                <el-option v-for="opt in currencyOptionList" :key="opt.value" :label="opt.label" :value="opt.value" />
              </el-select>
            </div>
          </template>
          <template v-else>{{ display(metaRef.currency) }}</template>
        </div>
      </div>
    </div>

    <div class="row single" v-if="editable || metaRef.invoiceRegistrationNo">
      <div class="field">
        <span class="field-label">{{ invoiceLabel }}：</span>
        <div class="field-value" :class="{ view: !editable }">
          <template v-if="editable">
            <div class="field-control short">
              <el-input v-model="invoiceNoModel" />
            </div>
          </template>
          <template v-else>{{ display(metaRef.invoiceRegistrationNo) }}</template>
        </div>
      </div>
    </div>

    <div class="row summary" v-if="editable || metaRef.summary">
      <div class="field summary-field">
        <span class="field-label">{{ labels.summary }}：</span>
        <div class="field-value" :class="[{ view: !editable }, { multiline: true }]">
          <template v-if="editable">
            <div class="field-control full">
              <el-input v-model="summaryModel" />
            </div>
          </template>
          <template v-else>{{ display(metaRef.summary) }}</template>
        </div>
      </div>
    </div>

    <div class="row four-col">
      <div class="field" v-if="metaRef.createdAt">
        <span class="field-label">{{ labels.createdAt }}：</span>
        <span class="field-value">{{ display(metaRef.createdAt) }}</span>
      </div>
      <div class="field" v-if="metaRef.createdBy">
        <span class="field-label">{{ labels.createdBy }}：</span>
        <span class="field-value">{{ display(metaRef.createdBy) }}</span>
      </div>
      <div class="field" v-if="metaRef.updatedAt">
        <span class="field-label">{{ labels.updatedAt }}：</span>
        <span class="field-value">{{ display(metaRef.updatedAt) }}</span>
      </div>
      <div class="field" v-if="metaRef.updatedBy">
        <span class="field-label">{{ labels.updatedBy }}：</span>
        <span class="field-value">{{ display(metaRef.updatedBy) }}</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

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

interface VoucherDetailHeaderMeta {
  voucherNo?: string
  postingDate?: string
  voucherType?: string
  currency?: string
  invoiceRegistrationNo?: string
  summary?: string
  createdAt?: string
  createdBy?: string
  updatedAt?: string
  updatedBy?: string
}

interface OptionItem {
  value: string
  label: string
}

const props = withDefaults(defineProps<{
  meta: VoucherDetailHeaderMeta
  labels: VoucherDetailHeaderLabels
  invoiceLabel: string
  editable?: boolean
  editableHeader?: VoucherDetailHeaderMeta
  voucherTypeOptions?: OptionItem[]
  currencyOptions?: OptionItem[]
}>(), {
  editable: false
})

const editableHeaderRef = computed(() => props.editableHeader ?? {})
const voucherTypeOptionList = computed(() => props.voucherTypeOptions ?? [])
const currencyOptionList = computed(() => props.currencyOptions ?? [])
const metaRef = computed(() => props.meta ?? {})
const editable = computed(() => props.editable === true)

function display(value?: string | number | null): string {
  if (value === null || value === undefined) return ''
  return String(value).trim()
}

const emit = defineEmits<{
  (e: 'update:header', payload: Record<string, any>): void
}>()

function emitUpdate(payload: Record<string, any>) {
  emit('update:header', payload)
}

type HeaderField = keyof VoucherDetailHeaderMeta
type HeaderValue = string | number | null | undefined

function binding(field: HeaderField) {
  return computed({
    get: () => editableHeaderRef.value[field] ?? '',
    set: (value: HeaderValue) => {
      const normalized = value === undefined || value === null ? '' : value
      emitUpdate({ [field]: normalized })
    }
  })
}

const postingDateModel = binding('postingDate')
const voucherTypeModel = binding('voucherType')
const currencyModel = binding('currency')
const invoiceNoModel = binding('invoiceRegistrationNo')
const summaryModel = binding('summary')
</script>

<style scoped>
.voucher-detail-header {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-left: 12px;
}

.row {
  display: grid;
  grid-template-columns: repeat(1, 1fr);
  gap: 6px 24px;
  align-items: center;
}

.row.four-col {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.row.single {
  grid-template-columns: 35%;
  justify-items: start;
}

.row.summary{
  grid-template-columns: 100%;
}

.row.summary .field,
.row.single .field {
  grid-column: span 1;
}

.field {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  width: 100%;
}

.field-label {
  font-size: 14px;
  font-weight: 600;
  color: #334155;
  white-space: nowrap;
}

.field-value {
  flex: 1;
  min-height: 32px;
  display: flex;
  align-items: flex-start;
  color: #111827;
  font-size: 14px;
  line-height: 1.4;
}

.field-value.view {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.field-value.multiline.view {
  white-space: normal;
}

.field-control {
  width: 100%;
  display: inline-flex;
  flex: 1;
}

.field-control.tiny,
.field-control.short,
.field-control.medium,
.field-control.wide {
  width: 100%;
}

.field-control.full {
  width: 100%;
}

.field-control.bordered :deep(.el-textarea .el-textarea__inner) {
  border: 1px solid #d1d5db;
  border-radius: 6px;
}

.field-control :deep(.el-input),
.field-control :deep(.el-select),
.field-control :deep(.el-date-editor) {
  width: 100%;
  min-width: 0;
}

.field-control :deep(.el-input__wrapper),
.field-control :deep(.el-select__wrapper),
.field-control :deep(.el-date-editor .el-input__wrapper) {
  width: 100%;
  min-width: 0;
}

.row.meta {
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 6px 24px;
  align-items: center;
}

.meta-field {
  display: flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
  color: #111827;
  font-size: 14px;
}

.meta-value.future-editable {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.meta-label {
  color: #6b7280;
  font-size: 14px;
}
</style>


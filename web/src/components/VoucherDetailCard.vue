<template>
  <el-card class="detail-card">
    <template #header>
      <div class="page-header detail-header">
        <div class="page-header-title">{{ titleText }}</div>
        <div class="detail-header-actions">
          <el-tag v-if="voucherNoText" type="info">{{ voucherNoText }}</el-tag>
          <slot name="actions">
            <el-button
              v-if="showEditButton"
              type="primary"
              size="small"
              @click="$emit('edit')"
            >
              {{ editButtonLabel }}
            </el-button>
          </slot>
        </div>
      </div>
    </template>
    <VoucherDetailBody
      v-bind="cardBodyProps"
      @save="$emit('save')"
      @cancel="$emit('cancel')"
      @add-line="(side) => $emit('add-line', side)"
      @remove-line="(index) => $emit('remove-line', index)"
      @update:editable-header="(payload) => $emit('update:editable-header', payload)"
      @update:attachments="(attachments) => $emit('update:attachments', attachments)"
      @delete-attachment-blob="(blobName) => $emit('delete-attachment-blob', blobName)"
      @open-clearing-voucher="(voucherNo) => $emit('open-clearing-voucher', voucherNo)"
    />
  </el-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import VoucherDetailBody from './VoucherDetailBody.vue'

const props = defineProps<{
  title: string
  voucherNo?: string
  showEdit?: boolean
  editLabel?: string
  bodyProps?: Record<string, any>
}>()

defineEmits<{
  (e: 'edit'): void
  (e: 'save'): void
  (e: 'cancel'): void
  (e: 'add-line', side: 'DR' | 'CR'): void
  (e: 'remove-line', index: number): void
  (e: 'update:editable-header', payload: Record<string, any>): void
  (e: 'update:attachments', attachments: any[]): void
  (e: 'delete-attachment-blob', blobName: string): void
  (e: 'open-clearing-voucher', voucherNo: string): void
}>()

const titleText = computed(() => props.title || '会計伝票照会')
const voucherNoText = computed(() => props.voucherNo || '')
const showEditButton = computed(() => props.showEdit === true)
const editButtonLabel = computed(() => props.editLabel || '編集')
const cardBodyProps = computed(() => props.bodyProps ?? {})
</script>

<style scoped>
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



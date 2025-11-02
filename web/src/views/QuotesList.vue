<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ tableLabels.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/crm/quote/new')">{{ tableLabels.new }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="quote_code" :label="tableLabels.number" width="160" />
        <el-table-column prop="partner_code" :label="tableLabels.customer" width="200" />
        <el-table-column prop="amount_total" :label="tableLabels.amount" width="160" />
        <el-table-column prop="status" :label="tableLabels.status" width="140" />
        <el-table-column prop="issue_date" :label="tableLabels.issueDate" width="160" />
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
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const { section } = useI18n()
const tableLabels = section({ title:'', new:'', number:'', customer:'', amount:'', status:'', issueDate:'' }, (msg) => msg.tables.quotes)

const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

async function load() {
  loading.value = true
  try {
    const r = await api.post('/objects/quote/search', {
      page: page.value,
      pageSize: pageSize.value,
      where: [],
      orderBy: [{ field: 'quote_no', dir: 'DESC' }]
    })
    const data = Array.isArray(r.data?.data) ? r.data.data : []
    rows.value = data
    total.value = Number(r.data?.total) || data.length
  } finally {
    loading.value = false
  }
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

onMounted(load)
</script>

<style scoped>
</style>



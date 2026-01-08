<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ tableLabels.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/crm/deal/new')">{{ tableLabels.new }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="deal_code" :label="tableLabels.code" width="160" />
        <el-table-column prop="partner_code" :label="tableLabels.partner" width="180" />
        <el-table-column prop="stage" :label="tableLabels.stage" width="140" />
        <el-table-column prop="expected_amount" :label="tableLabels.amount" width="140" />
        <el-table-column prop="expected_close_date" :label="tableLabels.closeDate" width="160" />
        <el-table-column prop="source" :label="tableLabels.source" width="160" />
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
const tableLabels = section({ title:'', new:'', subject:'', amount:'', status:'', stage:'' }, (msg) => msg.tables.deals)

const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

async function load() {
  loading.value = true
  try {
    const r = await api.post('/objects/deal/search', {
      page: page.value,
      pageSize: pageSize.value,
      where: [],
      orderBy: [{ field: 'expected_close_date', dir: 'DESC' }]
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



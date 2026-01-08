<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><TrendCharts /></el-icon>
            <span class="page-header-title">{{ text.title }}</span>
          </div>
          <div class="page-actions">
            <el-button type="primary" :loading="loading" @click="fetchData">{{ text.query }}</el-button>
            <el-button :disabled="tableRows.length === 0" @click="exportExcel">{{ text.exportExcel }}</el-button>
            <el-button :disabled="tableRows.length === 0" @click="exportPdf">{{ text.exportPdf }}</el-button>
          </div>
        </div>
      </template>

      <el-form :inline="true" class="filters">
        <el-form-item :label="text.statement">
          <el-select v-model="statement" style="width: 160px">
            <el-option value="BS" :label="text.balanceSheet" />
            <el-option value="PL" :label="text.incomeStatement" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="statement === 'BS'" :label="text.period">
          <el-date-picker v-model="bsPeriod" type="month" value-format="YYYY-MM" format="YYYY-MM" placeholder="YYYY-MM" />
        </el-form-item>
        <el-form-item v-else :label="text.periodRange">
          <el-date-picker v-model="plRange" type="monthrange" range-separator="～" start-placeholder="Start" end-placeholder="End" value-format="YYYY-MM" format="YYYY-MM" />
        </el-form-item>
        <el-form-item :label="text.currency">
          <el-select v-model="currency" style="width: 120px">
            <el-option value="JPY" label="JPY" />
            <el-option value="USD" label="USD" />
            <el-option value="CNY" label="CNY" />
          </el-select>
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="refresh">{{ text.refreshBefore }}</el-checkbox>
        </el-form-item>
      </el-form>

      <el-table
        :data="tableRows"
        style="width: 100%"
        v-loading="loading"
        row-key="code"
        :tree-props="{ children: 'children' }"
        default-expand-all
        border
      >
        <el-table-column :label="text.name" min-width="320">
          <template #default="{ row }">
            <span :style="{ paddingLeft: `${row.level * 16}px` }">{{ row.displayName }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="text.amount" width="160" align="right">
          <template #default="{ row }">{{ formatAmount(row.amount) }}</template>
        </el-table-column>
      </el-table>

      <el-empty v-if="!loading && tableRows.length === 0" :description="text.noData" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { TrendCharts } from '@element-plus/icons-vue'
import dayjs from 'dayjs'
import jsPDF from 'jspdf'
import autoTable from 'jspdf-autotable'
import * as XLSX from 'xlsx'
import api from '../api'
import { useI18n } from '../i18n'

type StatementType = 'BS' | 'PL'

interface FinancialNode {
  code: string
  nameJa: string
  nameEn?: string
  amount: number
  selfAmount: number
  isSubtotal: boolean
  note?: string
  level: number
  children: FinancialNode[]
}

const { section, lang } = useI18n()

const text = section({
  title: '',
  statement: '',
  balanceSheet: '',
  incomeStatement: '',
  period: '',
  periodRange: '',
  currency: '',
  refreshBefore: '',
  query: '',
  exportPdf: '',
  exportExcel: '',
  noData: '',
  name: '',
  amount: '',
  loadFailed: ''
}, (msg) => msg.financialReports)

const statement = ref<StatementType>('BS')
const bsPeriod = ref(dayjs().format('YYYY-MM'))
const plRange = ref<string[]>([])
const currency = ref('JPY')
const refresh = ref(false)
const loading = ref(false)
const data = ref<{ nodes: FinancialNode[]; periodStart: string; periodEnd: string; statement: StatementType } | null>(null)

const tableRows = computed(() => {
  if (!data.value) return [] as any[]
  const locale = lang.value
  const mapNode = (node: FinancialNode): any => ({
    code: node.code,
    amount: node.amount,
    level: node.level,
    displayName: locale === 'en'
      ? (node.nameEn || node.nameJa || node.code)
      : (node.nameJa || node.nameEn || node.code),
    children: node.children?.map(mapNode) ?? []
  })
  return data.value.nodes.map(mapNode)
})

function flatten(nodes: FinancialNode[], list: Array<{ level: number; name: string; amount: number }>, locale: string) {
  nodes.forEach((node) => {
    list.push({
      level: node.level,
      name: locale === 'en' ? (node.nameEn || node.nameJa || node.code) : (node.nameJa || node.nameEn || node.code),
      amount: node.amount
    })
    if (node.children && node.children.length > 0) flatten(node.children, list, locale)
  })
}

function formatAmount(value: number) {
  return new Intl.NumberFormat(lang.value === 'en' ? 'en-US' : 'ja-JP', {
    minimumFractionDigits: 0,
    maximumFractionDigits: 0
  }).format(value)
}

async function fetchData() {
  if (statement.value === 'BS' && !bsPeriod.value) {
    ElMessage.error(text.value.periodRequired || text.value.loadFailed)
    return
  }
  if (statement.value === 'PL' && (!plRange.value || plRange.value.length !== 2)) {
    ElMessage.error(text.value.periodRequired || text.value.loadFailed)
    return
  }
  loading.value = true
  try {
    let response
    if (statement.value === 'BS') {
      response = await api.get('/reports/financial/balance-sheet', {
        params: {
          period: bsPeriod.value,
          currency: currency.value,
          refresh: refresh.value
        }
      })
    } else {
      response = await api.get('/reports/financial/income-statement', {
        params: {
          from: plRange.value?.[0],
          to: plRange.value?.[1],
          currency: currency.value,
          refresh: refresh.value
        }
      })
    }
    const payload = response.data || {}
    data.value = {
      nodes: adaptNodes(payload.nodes || []),
      periodStart: payload.periodStart,
      periodEnd: payload.periodEnd,
      statement: payload.statement || statement.value
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.loadFailed)
    data.value = null
  } finally {
    loading.value = false
  }
}

function adaptNodes(nodes: any[], level = 0): FinancialNode[] {
  return nodes.map((node: any) => ({
    code: node.code,
    nameJa: node.nameJa || '',
    nameEn: node.nameEn || '',
    amount: Number(node.amount || 0),
    selfAmount: Number(node.selfAmount || 0),
    isSubtotal: !!node.isSubtotal,
    note: node.note || '',
    level,
    children: adaptNodes(node.children || [], level + 1)
  }))
}

function exportExcel() {
  if (!data.value) return
  const locale = lang.value
  const items: Array<{ level: number; name: string; amount: number }> = []
  flatten(data.value.nodes, items, locale)
  const sheet = [
    [text.value.name, text.value.amount]
  ]
  items.forEach((item) => {
    sheet.push([
      `${' '.repeat(item.level * 2)}${item.name}`,
      formatAmount(item.amount)
    ])
  })
  const worksheet = XLSX.utils.aoa_to_sheet(sheet)
  const workbook = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, worksheet, data.value.statement)
  const fileName = `${text.value.title}-${statement.value}-${dayjs().format('YYYYMMDDHHmmss')}.xlsx`
  XLSX.writeFile(workbook, fileName)
}

function exportPdf() {
  if (!data.value) return
  const doc = new jsPDF({ orientation: 'portrait', unit: 'pt', format: 'a4' })
  doc.setFontSize(14)
  const title = `${text.value.title} - ${(statement.value === 'BS' ? text.value.balanceSheet : text.value.incomeStatement)}`
  doc.text(title, 40, 40)
  const locale = lang.value
  const rows: Array<{ level: number; name: string; amount: number }> = []
  flatten(data.value.nodes, rows, locale)
  const body = rows.map((row) => [
    `${' '.repeat(row.level * 2)}${row.name}`,
    formatAmount(row.amount)
  ])
  autoTable(doc, {
    startY: 60,
    head: [[text.value.name, text.value.amount]],
    body,
    styles: { fontSize: 10, halign: 'right' },
    columnStyles: {
      0: { halign: 'left' }
    }
  })
  const fileName = `${text.value.title}-${statement.value}-${dayjs().format('YYYYMMDDHHmmss')}.pdf`
  doc.save(fileName)
}

watch(statement, async () => {
  if (statement.value === 'BS') {
    if (!bsPeriod.value) bsPeriod.value = dayjs().format('YYYY-MM')
  } else {
    if (!plRange.value || plRange.value.length !== 2) {
      const current = dayjs().format('YYYY-MM')
      plRange.value = [current, current]
    }
  }
  await fetchData()
})

onMounted(fetchData)

</script>

<style scoped>
.page {
  padding: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

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

.page-actions {
  display: flex;
  gap: 8px;
}

.filters {
  margin-bottom: 12px;
}
</style>



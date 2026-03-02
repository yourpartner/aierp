<template>
  <div class="timesheets-list">
    <el-card class="timesheets-card">
      <template #header>
        <div class="timesheets-header">
          <div class="timesheets-header__left">
            <el-icon class="timesheets-header__icon"><Clock /></el-icon>
            <span class="timesheets-header__title">勤怠一覧</span>
            <el-tag size="small" type="info" class="timesheets-header__count">{{ total }}件</el-tag>
          </div>
          <div class="timesheets-header__right">
            <el-button type="primary" @click="openNewForm">
              <el-icon><Plus /></el-icon>
              <span>新規入力</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="timesheets-filters">
        <el-date-picker v-model="filterMonth" type="month" placeholder="月を選択" format="YYYY-MM" value-format="YYYY-MM" clearable class="timesheets-filters__month" />
        <el-select v-model="filterStatus" placeholder="ステータス" clearable class="timesheets-filters__status">
          <el-option label="すべて" value="" />
          <el-option label="提出済" value="submitted" />
          <el-option label="承認済" value="approved" />
          <el-option label="却下" value="rejected" />
        </el-select>
        <el-input v-if="canViewAll" v-model="filterEmployee" placeholder="従業員名で検索" clearable class="timesheets-filters__employee">
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>
        <el-button type="primary" plain @click="search">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <el-table :data="rows" border stripe highlight-current-row class="timesheets-table" v-loading="loading">
        <el-table-column v-if="canViewAll" label="従業員" min-width="140">
          <template #default="{ row }">
            <div class="employee-cell">
              <span class="employee-name">{{ row.employeeName || '-' }}</span>
              <span class="employee-code">{{ row.employeeCode }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="月度" width="120">
          <template #default="{ row }">{{ row.month }}</template>
        </el-table-column>
        <el-table-column label="勤務日数" width="100" align="center">
          <template #default="{ row }">{{ row.workDays }}日</template>
        </el-table-column>
        <el-table-column label="総勤務時間" width="120" align="right">
          <template #default="{ row }">{{ formatHours(row.totalHours) }}</template>
        </el-table-column>
        <el-table-column label="総残業時間" width="120" align="right">
          <template #default="{ row }">
            <span :class="{ 'text-orange': row.totalOvertime > 45 }">{{ formatHours(row.totalOvertime) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="statusType(row.status)" size="small">{{ statusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="240" align="center">
          <template #default="{ row }">
            <el-button size="small" type="primary" link @click="viewDetail(row)">詳細</el-button>
            <el-button size="small" type="success" link @click="downloadExcel(row)">
              <el-icon><Download /></el-icon>Excel
            </el-button>
            <el-button size="small" type="warning" link @click="downloadPdf(row)">
              <el-icon><Download /></el-icon>PDF
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- ページネーション -->
      <div class="timesheets-pagination">
        <span class="timesheets-pagination__info">全 {{ total }} 件</span>
        <el-pagination 
          layout="prev, pager, next" 
          :total="total" 
          :page-size="pageSize" 
          v-model:current-page="page" 
          @current-change="search" 
        />
      </div>
    </el-card>

    <!-- 新規工数入力ダイアログ -->
    <el-dialog v-model="newFormVisible" title="工数入力" width="1100px" destroy-on-close>
      <TimesheetForm dialog-mode @saved="onFormSaved" />
    </el-dialog>

    <!-- 详细弹窗 -->
    <el-dialog v-model="detailVisible" :title="detailTitle" width="900px" destroy-on-close>
      <div v-loading="detailLoading">
        <div class="detail-summary">
          <div class="summary-item">
            <span class="label">勤務日数</span>
            <span class="value">{{ detailData.workDays }}日</span>
          </div>
          <div class="summary-item">
            <span class="label">総勤務時間</span>
            <span class="value">{{ formatHours(detailData.totalHours) }}</span>
          </div>
          <div class="summary-item">
            <span class="label">総残業時間</span>
            <span class="value" :class="{ 'text-orange': detailData.totalOvertime > 45 }">{{ formatHours(detailData.totalOvertime) }}</span>
          </div>
        </div>
        <el-table :data="detailRows" size="small" :row-class-name="rowClass" max-height="400">
          <el-table-column label="日付" width="100">
            <template #default="{ row }">
              <span>{{ row.date }}</span>
            </template>
          </el-table-column>
          <el-table-column label="曜日" width="60" align="center">
            <template #default="{ row }">
              <span :class="{ 'text-red': row.isWeekend || row.isHoliday }">{{ row.weekday }}</span>
            </template>
          </el-table-column>
          <el-table-column label="開始" width="80" align="center">
            <template #default="{ row }">{{ row.startTime || '-' }}</template>
          </el-table-column>
          <el-table-column label="終了" width="80" align="center">
            <template #default="{ row }">{{ row.endTime || '-' }}</template>
          </el-table-column>
          <el-table-column label="休憩" width="70" align="center">
            <template #default="{ row }">{{ row.lunchMinutes ? row.lunchMinutes + '分' : '-' }}</template>
          </el-table-column>
          <el-table-column label="勤務時間" width="90" align="right">
            <template #default="{ row }">{{ row.hours ? formatHours(row.hours) : '-' }}</template>
          </el-table-column>
          <el-table-column label="残業" width="80" align="right">
            <template #default="{ row }">
              <span v-if="row.overtime" class="text-orange">{{ formatHours(row.overtime) }}</span>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column label="作業内容" min-width="150">
            <template #default="{ row }">{{ row.task || '-' }}</template>
          </el-table-column>
        </el-table>
      </div>
      <template #footer>
        <div class="detail-footer">
          <div class="detail-footer__downloads">
            <el-button type="success" @click="downloadExcel(currentDetailRow)">
              <el-icon><Download /></el-icon>Excel
            </el-button>
            <el-button type="warning" @click="downloadPdf(currentDetailRow)">
              <el-icon><Download /></el-icon>PDF
            </el-button>
          </div>
          <el-button @click="detailVisible = false">閉じる</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import api from '../api'
import { Clock, Plus, Search, Download } from '@element-plus/icons-vue'
import * as XLSX from 'xlsx'
import { jsPDF } from 'jspdf'
import autoTable from 'jspdf-autotable'
import TimesheetForm from './TimesheetForm.vue'

function decodeJwt(token: string): any | null {
  try {
    if (!token) return null
    const parts = token.split('.')
    if (parts.length < 2) return null
    const raw = parts[1]
    const base64 = raw.replace(/-/g, '+').replace(/_/g, '/')
    const pad = '='.repeat((4 - (base64.length % 4)) % 4)
    const json = atob(base64 + pad)
    return JSON.parse(json)
  } catch {
    return null
  }
}

function currentUserId(): string {
  try {
    const token =
      (localStorage.getItem('auth_token') || sessionStorage.getItem('auth_token') || '').trim()
    const claims = decodeJwt(token) || {}
    return String(claims.uid || claims.userId || claims.user_id || claims.sub || '').trim()
  } catch {
    return ''
  }
}

// 权限检查：是否可以查看所有人的工时
const canViewAll = computed(() => {
  const caps = (sessionStorage.getItem('userCaps') || '').split(',').filter(Boolean)
  return caps.includes('timesheet:manage') || caps.includes('admin') || caps.includes('hr:admin')
})

const newFormVisible = ref(false)

function openNewForm() {
  newFormVisible.value = true
}

function onFormSaved() {
  newFormVisible.value = false
  search()
}

const loading = ref(false)
const rows = ref<any[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)

const filterMonth = ref<string>('')
const filterStatus = ref<string>('')
const filterEmployee = ref<string>('')

// 详细弹窗
const detailVisible = ref(false)
const detailTitle = ref('')
const detailLoading = ref(false)
const detailData = ref<any>({})
const detailRows = ref<any[]>([])
const currentDetailRow = ref<any>({})

const weekdayNames = ['日', '月', '火', '水', '木', '金', '土']

function formatHours(h: number | undefined) {
  if (!h) return '0H'
  const hours = Math.floor(h)
  const mins = Math.round((h - hours) * 60)
  if (mins === 0) return `${hours}H`
  return `${hours}H${mins}M`
}

function statusType(s: string) {
  switch (s) {
    case 'approved': return 'success'
    case 'submitted': return 'warning'
    case 'rejected': return 'danger'
    default: return 'info'
  }
}

function statusLabel(s: string) {
  switch (s) {
    case 'approved': return '承認済'
    case 'rejected': return '却下'
    case 'submitted':
    default: return '提出済'
  }
}

function rowClass({ row }: any) {
  if (row.isHoliday && !row.isWeekend) return 'row-holiday'
  if (row.isWeekend) return 'row-weekend'
  return ''
}

async function search() {
  loading.value = true
  try {
    // 获取月度提交数据（用于审批与汇总展示）
    const where: any[] = []
    
    // 按月份筛选
    if (filterMonth.value) {
      // 用 in 避免后端把 'YYYY-MM' 误判为 Date 导致类型不匹配
      where.push({ field: 'month', op: 'in', value: [filterMonth.value] })
    }
    
    // 按状态筛选
    if (filterStatus.value) {
      where.push({ field: 'status', op: 'eq', value: filterStatus.value })
    }
    
    // 非管理员只能看自己的
    if (!canViewAll.value) {
      const uid = currentUserId()
      if (uid) where.push({ json: 'creatorUserId', op: 'in', value: [uid] })
    }
    
    const r = await api.post('/objects/timesheet_submission/search', { 
      where, 
      page: 1,
      pageSize: 500,
      orderBy: [{ field: 'updated_at', dir: 'DESC' }]
    })
    
    const rawData = Array.isArray(r.data?.data) ? r.data.data : []

    // 将 submission 直接映射为列表行（无需前端聚合）
    let result = rawData.map((item: any) => {
      const payload = item.payload || {}
      return {
        id: item.id,
        createdBy: payload.creatorUserId || item.created_by || '',
        month: payload.month || item.month || '',
        employeeName: payload.employeeName || '',
        employeeCode: payload.employeeCode || item.employee_code || '',
        workDays: Number(payload.workDays || 0),
        totalHours: Number(payload.totalHours || 0),
        totalOvertime: Number(payload.totalOvertime || 0),
        status: payload.status || item.status || 'submitted',
        _submissionId: item.id
      }
    })
    
    // 按员工名筛选
    if (filterEmployee.value && canViewAll.value) {
      const keyword = filterEmployee.value.toLowerCase()
      result = result.filter(r => 
        r.employeeName.toLowerCase().includes(keyword) || 
        r.employeeCode.toLowerCase().includes(keyword)
      )
    }
    
    // 按月份降序排列（同月按员工）
    result.sort((a, b) => {
      const m = (b.month || '').localeCompare(a.month || '')
      if (m !== 0) return m
      return (a.employeeCode || '').localeCompare(b.employeeCode || '')
    })
    
    // 分页
    total.value = result.length
    const start = (page.value - 1) * pageSize.value
    rows.value = result.slice(start, start + pageSize.value)
    
  } catch (e) {
    console.error('Search failed:', e)
    rows.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

async function viewDetail(row: any) {
  detailVisible.value = true
  detailLoading.value = true
  currentDetailRow.value = row
  detailTitle.value = `${row.employeeName || '自分'} - ${row.month} 勤怠詳細`
  detailData.value = {
    workDays: row.workDays,
    totalHours: row.totalHours,
    totalOvertime: row.totalOvertime
  }
  
  try {
    // 拉取该员工该月的明细
    const where: any[] = []
    if (row.month) where.push({ field: 'month', op: 'in', value: [row.month] })
    if (!canViewAll.value) {
      const uid = currentUserId()
      if (uid) where.push({ json: 'creatorUserId', op: 'in', value: [uid] })
    } else if (row.createdBy) {
      where.push({ json: 'creatorUserId', op: 'in', value: [row.createdBy] })
    }
    const resp = await api.post('/objects/timesheet/search', { page: 1, pageSize: 200, where, orderBy: [{ field: 'timesheet_date', dir: 'ASC' }] })
    const records = Array.isArray(resp.data?.data) ? resp.data.data : []
    detailRows.value = records.map((item: any) => {
      const payload = item.payload || {}
      const dateStr = payload.date || ''
      const d = new Date(dateStr)
      const dayOfWeek = d.getDay()
      return {
        date: dateStr,
        weekday: weekdayNames[dayOfWeek] || '',
        isWeekend: dayOfWeek === 0 || dayOfWeek === 6,
        isHoliday: payload.isHoliday || false,
        startTime: payload.startTime || '',
        endTime: payload.endTime || '',
        lunchMinutes: payload.lunchMinutes || 0,
        hours: payload.hours || 0,
        overtime: payload.overtime || 0,
        task: payload.task || ''
      }
    })
    
  } catch (e) {
    console.error('Load detail failed:', e)
    detailRows.value = []
  } finally {
    detailLoading.value = false
  }
}

async function loadTimesheetDetail(row: any): Promise<any[]> {
  const where: any[] = []
  if (row.month) where.push({ field: 'month', op: 'in', value: [row.month] })
  if (!canViewAll.value) {
    const uid = currentUserId()
    if (uid) where.push({ json: 'creatorUserId', op: 'in', value: [uid] })
  } else if (row.createdBy) {
    where.push({ json: 'creatorUserId', op: 'in', value: [row.createdBy] })
  }
  const resp = await api.post('/objects/timesheet/search', { page: 1, pageSize: 200, where, orderBy: [{ field: 'timesheet_date', dir: 'ASC' }] })
  const records = Array.isArray(resp.data?.data) ? resp.data.data : []
  return records.map((item: any) => {
    const payload = item.payload || {}
    const dateStr = payload.date || ''
    const d = new Date(dateStr)
    const dayOfWeek = d.getDay()
    return {
      date: dateStr,
      weekday: weekdayNames[dayOfWeek] || '',
      isWeekend: dayOfWeek === 0 || dayOfWeek === 6,
      isHoliday: payload.isHoliday || false,
      startTime: payload.startTime || '',
      endTime: payload.endTime || '',
      lunchMinutes: payload.lunchMinutes || 0,
      hours: payload.hours || 0,
      overtime: payload.overtime || 0,
      task: payload.task || ''
    }
  })
}

function buildExportRows(details: any[]) {
  return details.map(r => ({
    '日付': r.date,
    '曜日': r.weekday,
    '開始': r.startTime || '',
    '終了': r.endTime || '',
    '休憩(分)': r.lunchMinutes || '',
    '勤務時間(H)': r.hours ? Number(r.hours.toFixed(2)) : '',
    '残業時間(H)': r.overtime ? Number(r.overtime.toFixed(2)) : '',
    '作業内容': r.task || ''
  }))
}

async function downloadExcel(row: any) {
  try {
    const month = row.month || ''
    const resourceId = row.employeeCode || row.resourceId || ''
    if (!month || !resourceId) {
      ElMessage.error('月または従業員コードが見つかりません')
      return
    }

    const resp = await api.get('/staffing/timesheets/export', { 
      params: { month, resourceId },
      responseType: 'blob' 
    })
    
    const blob = new Blob([resp.data], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
    const url = window.URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    
    // 尝试从 header 中获取文件名，如果没有则使用默认名
    let fileName = `作業報告書_${row.employeeName || '自分'}_${month}.xlsx`
    const disposition = resp.headers['content-disposition']
    if (disposition && disposition.indexOf('attachment') !== -1) {
      const filenameRegex = /filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/
      const matches = filenameRegex.exec(disposition)
      if (matches != null && matches[1]) {
        fileName = decodeURIComponent(matches[1].replace(/['"]/g, ''))
      }
    }
    
    a.download = fileName
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    window.URL.revokeObjectURL(url)
  } catch (e) {
    console.error('Excel download failed:', e)
    ElMessage.error('Excelのダウンロードに失敗しました')
  }
}

async function downloadPdf(row: any) {
  try {
    const details = detailVisible.value && detailRows.value.length ? detailRows.value : await loadTimesheetDetail(row)
    const empName = row.employeeName || '自分'
    const month = row.month || ''
    const monthStr = month.replace('-', '年') + '月'

    let companyName = sessionStorage.getItem('currentCompany') || ''
    try {
      const resp = await api.post('/objects/company_setting/search', {
        page: 1, pageSize: 1, where: [], orderBy: [{ field: 'created_at', dir: 'DESC' }]
      })
      if (resp.data?.data?.[0]?.payload?.companyName) {
        companyName = resp.data.data[0].payload.companyName
      }
    } catch {}

    const doc = new jsPDF({ orientation: 'portrait', unit: 'mm', format: 'a4' })

    // Title (Centered)
    doc.setFontSize(18)
    const title = `${monthStr} 作業報告書`
    const pageWidth = doc.internal.pageSize.getWidth()
    const titleWidth = doc.getTextWidth(title)
    doc.text(title, (pageWidth - titleWidth) / 2, 20)

    // Company and Employee (Right aligned)
    doc.setFontSize(11)
    const companyText = `会社名: ${companyName}`
    const empText = `担当者: ${empName}`
    const rightMargin = 14
    
    doc.text(companyText, pageWidth - rightMargin - doc.getTextWidth(companyText), 30)
    doc.text(empText, pageWidth - rightMargin - doc.getTextWidth(empText), 36)

    const head = [['期日', '曜日', '開始時間', '終了時間', '休憩時間', '実績時間', '作業内容']]
    const body = details.map(r => [
      r.date,
      r.weekday,
      r.startTime || '',
      r.endTime || '',
      r.lunchMinutes ? `${r.lunchMinutes}分` : '',
      r.hours ? r.hours.toFixed(2) : '',
      r.task || ''
    ])

    const totalHours = details.reduce((s, d) => s + (d.hours || 0), 0)
    body.push(['合計', '', '', '', '', totalHours.toFixed(2), ''])

    autoTable(doc, {
      startY: 42,
      head,
      body,
      theme: 'grid',
      styles: { fontSize: 9, cellPadding: 3, lineColor: [220, 220, 220] },
      headStyles: { fillColor: [235, 245, 235], textColor: [51, 51, 51], fontStyle: 'bold', halign: 'center' },
      columnStyles: {
        0: { cellWidth: 22, halign: 'center' },
        1: { cellWidth: 14, halign: 'center' },
        2: { cellWidth: 20, halign: 'center' },
        3: { cellWidth: 20, halign: 'center' },
        4: { cellWidth: 20, halign: 'center' },
        5: { cellWidth: 20, halign: 'center' },
        6: { cellWidth: 'auto' }
      },
      didParseCell(data: any) {
        // Highlight weekends
        if (data.section === 'body' && data.column.index === 1) {
          const weekday = data.cell.raw
          if (weekday === '土' || weekday === '土曜日') data.cell.styles.textColor = [0, 0, 255]
          if (weekday === '日' || weekday === '日曜日') data.cell.styles.textColor = [255, 0, 0]
        }
        // Style total row
        if (data.section === 'body' && data.row.index === body.length - 1) {
          data.cell.styles.fontStyle = 'bold'
          data.cell.styles.fillColor = [245, 247, 250]
          if (data.column.index === 0) data.cell.styles.halign = 'center'
        }
      }
    })

    doc.save(`作業報告書_${empName}_${month}.pdf`)
  } catch (e) {
    console.error('PDF download failed:', e)
  }
}

onMounted(() => {
  search()
})
</script>

<style scoped>
.timesheets-list {
  padding: 16px;
}

.timesheets-card {
  border-radius: 12px;
  overflow: hidden;
}

.timesheets-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.timesheets-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.timesheets-header__icon {
  font-size: 22px;
  color: #409eff;
}

.timesheets-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.timesheets-header__count {
  font-weight: 500;
}

.timesheets-header__right {
  display: flex;
  gap: 8px;
}

/* フィルター */
.timesheets-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.timesheets-filters__month {
  width: 140px;
}

.timesheets-filters__status {
  width: 140px;
}

.timesheets-filters__employee {
  width: 180px;
}

/* テーブル */
.timesheets-table {
  border-radius: 8px;
  overflow: hidden;
}

.timesheets-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

/* ページネーション */
.timesheets-pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.timesheets-pagination__info {
  font-size: 13px;
  color: #909399;
}

/* 従業員セル */
.employee-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.employee-name {
  font-weight: 500;
  color: #303133;
}

.employee-code {
  font-size: 12px;
  color: #909399;
}

.text-orange {
  color: #e6a23c;
  font-weight: 500;
}

.text-red {
  color: #f56c6c;
}

/* 詳細サマリー */
.detail-summary {
  display: flex;
  gap: 32px;
  margin-bottom: 16px;
  padding: 12px 16px;
  background: #f5f7fa;
  border-radius: 6px;
}

.summary-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.summary-item .label {
  font-size: 12px;
  color: #909399;
}

.summary-item .value {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

/* 詳細フッター */
.detail-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
}

.detail-footer__downloads {
  display: flex;
  gap: 8px;
}

@media (max-width: 768px) {
  .timesheets-filters {
    flex-direction: column;
    align-items: stretch;
  }
  .timesheets-filters__month,
  .timesheets-filters__status,
  .timesheets-filters__employee {
    width: 100%;
  }
}
</style>

<style>
.row-weekend > td { background-color: #f5f5f5 !important; }
.row-holiday > td { background-color: #fff5f5 !important; }
</style>

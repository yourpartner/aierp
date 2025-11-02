<template>
  <div style="padding:12px">
    <el-card>
      <template #header>工时清单</template>
      <div style="margin-bottom:8px;display:flex;gap:8px;align-items:center">
        <el-date-picker v-model="month" type="month" placeholder="选择月份" format="YYYY-MM" value-format="YYYY-MM" />
        <el-select v-model="status" placeholder="状态" clearable style="width:160px">
          <el-option label="全部" value="" />
          <el-option label="草稿" value="draft" />
          <el-option label="已提交" value="submitted" />
          <el-option label="已通过" value="approved" />
          <el-option label="已退回" value="rejected" />
        </el-select>
        <el-button type="primary" @click="search">查询</el-button>
        <el-button @click="$router.push('/timesheet/new')">新建</el-button>
      </div>
      <el-table :data="rows" size="small" :row-class-name="rowClass">
        <el-table-column prop="payload.date" label="日期" width="120" />
        <el-table-column prop="payload.projectCode" label="项目" width="140" />
        <el-table-column prop="payload.task" label="任务" />
        <el-table-column prop="payload.hours" label="工时" width="100" />
        <el-table-column prop="payload.overtime" label="加班" width="100" />
        <el-table-column prop="payload.status" label="状态" width="120" />
        <el-table-column label="操作" width="120">
          <template #default="scope">
            <el-button size="small" @click="edit(scope.row.id)">编辑</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div style="margin-top:8px;text-align:right">
        <el-pagination layout="prev, pager, next" :total="total" :page-size="pageSize" v-model:current-page="page" @current-change="search" />
      </div>
    </el-card>
  </div>
  
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import api from '../api'
import { useRouter } from 'vue-router'

const router = useRouter()
const month = ref<string>('')
const status = ref<string>('')
const rows = ref<any[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)

async function search(){
  const where:any[] = []
  if (month.value) where.push({ field: 'month', op: 'eq', value: month.value })
  if (status.value) where.push({ field: 'status', op: 'eq', value: status.value })
  const r = await api.post('/objects/timesheet/search', { where, page: page.value, pageSize: pageSize.value })
  rows.value = Array.isArray(r.data?.data) ? r.data.data : []
  total.value = r.data?.total || 0
}

function edit(id:string){ router.push(`/timesheet/${id}`) }

onMounted(()=>{ search() })

function rowClass({ row }:any){
  try{
    const d = new Date(row?.payload?.date)
    const day = d.getDay() // 0 Sun, 6 Sat
    if (day===0 || day===6) return 'row-weekend'
  }catch{}
  return ''
}
</script>
<style>
.row-weekend > td{ background-color:#f5f5f5 !important }
</style>



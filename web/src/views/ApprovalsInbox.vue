<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">我的待办</div>
          <div class="page-actions">
            <el-radio-group v-model="status" size="small" @change="onStatusChange">
              <el-radio-button label="pending">待审批</el-radio-button>
              <el-radio-button label="approved">已同意</el-radio-button>
              <el-radio-button label="rejected">已驳回</el-radio-button>
              <el-radio-button label="all">全部</el-radio-button>
            </el-radio-group>
          </div>
        </div>
      </template>
      <el-table :data="rows" border size="small" style="width:100%">
        <el-table-column type="index" width="60" />
        <el-table-column prop="entity" label="实体" width="160" />
        <el-table-column prop="step_name" label="步骤" width="200" />
        <el-table-column prop="created_at" label="创建日期" width="140">
          <template #default="{ row }">{{ formatDate(row.created_at) }}</template>
        </el-table-column>
        <el-table-column v-if="status==='pending'" label="操作" width="220">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click="approve(row)" :loading="loadingId===row.id">同意</el-button>
            <el-button size="small" type="danger" @click="reject(row)" :loading="loadingId===row.id">驳回</el-button>
          </template>
        </el-table-column>
        <el-table-column v-else label="操作" width="160">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click="downloadPdf(row)" :disabled="row.entity!=='certificate_request'">下载PDF</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div v-if="status!=='pending'" style="display:flex; justify-content:flex-end; margin-top:8px">
        <el-pagination
          background
          layout="prev, pager, next, sizes, total"
          :current-page="page"
          :page-sizes="[10,20,50,100]"
          :page-size="pageSize"
          :total="total"
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </el-card>
  </div>
  
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import api from '../api'

const rows = reactive<any[]>([])
const loadingId = ref<string>('')
const status = ref<'pending'|'approved'|'rejected'|'all'>('pending')
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

async function load(){
  rows.splice(0, rows.length)
  const where:any[] = []
  if (status.value !== 'all') where.push({ field:'status', op:'eq', value: status.value })
  const r = await api.post('/objects/approval_task/search', { page: page.value, pageSize: pageSize.value, where, orderBy: [{ field:'created_at', dir:'DESC' }] })
  const data = (r.data?.data||[]) as any[]
  for (const x of data){ rows.push(x) }
  total.value = Number(r.data?.total || rows.length || 0)
}

async function approve(row:any){ await act(row, 'approve') }
async function reject(row:any){ await act(row, 'reject') }
async function act(row:any, action:'approve'|'reject'){
  loadingId.value = row.id
  try{
    const resp = await api.post('/operations/approvals/next', { entity: row.entity, objectId: row.object_id, action })
    const mail = resp.data?.mail
    if (mail && typeof mail.ok !== 'undefined') {
      if (mail.ok) {
        // 成功提示
        // @ts-ignore
        window?.ElMessage?.success?.(`邮件已发送。${mail.body || ''}`) || console.log('邮件已发送', mail)
      } else {
        // 失败提示
        // @ts-ignore
        window?.ElMessage?.error?.(`邮件发送失败：${mail.body || ''}`) || console.error('邮件发送失败', mail)
      }
    }
    await load()
  }catch(e:any){ console.error(e?.response?.data||e) }
  finally{ loadingId.value = '' }
}

onMounted(load)

function formatDate(v:any){ try{ const d=new Date(v); if(!isNaN(d.getTime())) return d.toISOString().slice(0,10) }catch{} return v||'' }

async function downloadPdf(row:any){
  try{
    if (row?.entity !== 'certificate_request') return
    const id = row?.object_id; if (!id) return
    const resp = await api.get(`/operations/certificate_request/${id}/pdf`, { responseType: 'blob' })
    const blob = new Blob([resp.data], { type: 'application/pdf' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = 'certificate.pdf'
    document.body.appendChild(a); a.click(); URL.revokeObjectURL(a.href); document.body.removeChild(a)
  }catch{}
}

function onStatusChange(){ page.value = 1; load() }
function onPageChange(p:number){ page.value = p; load() }
function onSizeChange(ps:number){ pageSize.value = ps; page.value = 1; load() }
</script>

<style scoped>
.page.page-medium { max-width: 1000px; }
</style>



<template>
  <div class="page page-medium">
    <el-card>
      <template #header>手机拍照解析</template>
      <div class="capture-container">
        <div class="capture-actions">
          <el-upload
            class="upload"
            accept="image/png,image/jpeg"
            :show-file-list="false"
            :http-request="onUpload"
            :disabled="uploading"
          >
          <el-button type="primary">上传照片</el-button>
          </el-upload>
          <el-button @click="openCamera">打开相机</el-button>
        </div>
        <div class="preview" v-if="imageUrl">
          <img :src="imageUrl" alt="预览" />
        </div>
        <div class="no-preview" v-else>
          <span>请上传或拍摄图片</span>
        </div>
      </div>
    </el-card>
    <el-card style="margin-top:16px">
      <template #header>解析结果</template>
      <div v-if="loading" class="result-placeholder">解析中...</div>
      <template v-else>
        <pre v-if="result" class="result-json">{{ formattedResult }}</pre>
        <div v-else class="result-placeholder">暂无解析结果</div>
      </template>
    </el-card>
  </div>
</template>
<script setup lang="ts">
import { computed, ref, onMounted, onBeforeUnmount } from 'vue'
import api from '../api'

const images = ref<string[]>([])
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const result = ref<any>(null)
const pickIdx = ref<number | null>(null)
const currentCandidate = computed(()=>{
  const arr = result.value?.candidates || []
  if (pickIdx.value==null) return null
  return arr[pickIdx.value] || null
})

function onPick(e: Event){
  const input = e.target as HTMLInputElement
  const files = input.files
  if (!files) return
  const urls: string[] = []
  for (let i=0;i<files.length;i++){
    const f = files.item(i)!
    urls.push(URL.createObjectURL(f))
  }
  images.value = urls
}

async function runParse(){
  error.value = ''
  result.value = null
  loading.value = true
  try{
    // 二进制优先：multipart/form-data
    const form = new FormData()
    // 将之前的 blob URL 重新取回 File 需要原始 File，这里直接重新取 input.files 更简单
    // 但为兼容从原生注入 dataURL 的情况（仍支持），当无法获得 File 时回退为 JSON
    const input = document.querySelector('input[type="file"]') as HTMLInputElement | null
    const files = input?.files
    if (files && files.length>0){
      for (let i=0;i<files.length;i++) form.append('files', files.item(i)!)
      const r = await api.post('/ai/documents/parse', form, { headers: { 'Content-Type': 'multipart/form-data' } })
      result.value = r.data
    } else {
      const r = await api.post('/ai/documents/parse', { images: images.value })
      result.value = r.data
    }
    pickIdx.value = (result.value?.candidates && result.value.candidates.length>0) ? 0 : null
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '解析失败' }
  finally{ loading.value = false }
}

async function saveCandidate(){
  if (!currentCandidate.value) return
  saving.value = true
  error.value = ''
  try{
    const entity = String(currentCandidate.value.entity || '').toLowerCase()
    const payload = currentCandidate.value.payload
    if (!entity || !payload) { error.value = '候选无效'; return }
    const resp = await api.post(`/objects/${entity}`, { payload })
    // 简单反馈
    images.value = []
    result.value = null
    pickIdx.value = null
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '保存失败' }
  finally{ saving.value = false }
}

// 原生桥接：接收已扫描的预览图（dataURL）与解析结果（JSON）
function handleNativeImages(e: any){ try{ const arr = Array.isArray(e?.detail) ? e.detail : []; if (arr.length>0) { images.value = arr } }catch{} }
function handleNativeParseResult(e: any){ try{ const obj = e?.detail; if (obj) { result.value = obj; pickIdx.value = (obj?.candidates && obj.candidates.length>0) ? 0 : null } }catch{} }
onMounted(()=>{ window.addEventListener('nativeImages', handleNativeImages as any); window.addEventListener('nativeParseResult', handleNativeParseResult as any) })
onBeforeUnmount(()=>{ window.removeEventListener('nativeImages', handleNativeImages as any); window.removeEventListener('nativeParseResult', handleNativeParseResult as any) })
</script>
<style scoped>
.page.page-medium { max-width: 980px; }
.capture-container{ display:flex; gap:16px; align-items:flex-start; flex-wrap:wrap }
.capture-actions{ display:flex; gap:12px }
.preview{ width:320px; border:1px dashed #d1d5db; border-radius:12px; overflow:hidden }
.thumbs{ display:flex; gap:8px; flex-wrap:wrap }
.thumbs img{ width:120px; height:120px; object-fit:cover; border:1px solid #eee }
</style>

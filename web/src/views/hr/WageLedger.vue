<template>
  <div class="wage-ledger-container">
    <div class="page-header">
      <div class="title-area">
        <h2>賃金台帳</h2>
        <el-tooltip content="従業員の賃金台帳を確認・ダウンロードします" placement="right">
          <el-icon class="info-icon"><InfoFilled /></el-icon>
        </el-tooltip>
      </div>
      <div class="action-area">
        <el-date-picker
          v-model="year"
          type="year"
          placeholder="対象年"
          size="small"
          class="year-picker"
        />
        <el-select v-model="department" placeholder="部門" size="small" class="filter-select" clearable>
          <el-option label="開発部" value="dev" />
          <el-option label="営業部" value="sales" />
          <el-option label="総務部" value="admin" />
        </el-select>
        <el-select v-model="position" placeholder="職位" size="small" class="filter-select" clearable>
          <el-option label="部長" value="manager" />
          <el-option label="リーダー" value="leader" />
          <el-option label="社員" value="staff" />
        </el-select>
        <el-button type="primary" size="small" class="download-btn">一括ダウンロード</el-button>
      </div>
    </div>

    <div class="employee-grid">
      <div v-for="emp in employees" :key="emp.code" class="employee-card">
        <div class="emp-code">{{ emp.code }}</div>
        <div class="emp-name">{{ emp.name }}</div>
        <div class="emp-position">「{{ emp.position }}」</div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { InfoFilled } from '@element-plus/icons-vue'

const year = ref(new Date('2023-01-01'))
const department = ref('')
const position = ref('')

// 假数据生成
const generateEmployees = () => {
  const list = []
  const names = ['山田 太郎', '鈴木 一郎', '佐藤 花子', '田中 次郎', '伊藤 三郎', '渡辺 四郎', '小林 五郎', '加藤 六郎', '吉田 七郎', '山田 八郎', '佐々木 九郎', '山口 十郎', '松本 十一郎', '井上 十二郎', '木村 十三郎', '林 十四郎', '斎藤 十五郎', '清水 十六郎', '山崎 十七郎', '森 十八郎', '池田 十九郎', '橋本 二十郎', '阿部 二十一郎', '石川 二十二郎', '山下 二十三郎', '中島 二十四郎', '石井 二十五郎', '小川 二十六郎', '前田 二十七郎', '岡田 二十八郎', '長谷川 二十九郎', '藤田 三十郎', '後藤 三十一郎', '近藤 三十二郎', '村上 三十三郎', '遠藤 三十四郎', '青木 三十五郎', '坂本 三十六郎', '斉藤 三十七郎', '福田 三十八郎', '太田 三十九郎', '西村 四十郎', '藤井 四十一郎', '金子 四十二郎', '岡本 四十三郎', '藤原 四十四郎', '中野 四十五郎', '中田 四十六郎', '原田 四十七郎', '松田 四十八郎']
  
  for (let i = 0; i < 35; i++) {
    const codeNum = Math.floor(Math.random() * 200) + 1
    const code = `YP${codeNum.toString().padStart(3, '0')}`
    list.push({
      code,
      name: names[i % names.length],
      position: '社員'
    })
  }
  
  // 按 code 排序
  return list.sort((a, b) => a.code.localeCompare(b.code))
}

const employees = ref(generateEmployees())
</script>

<style scoped>
.wage-ledger-container {
  padding: 20px;
  background-color: #fff;
  min-height: 100%;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 24px;
  padding-bottom: 12px;
  border-bottom: 1px solid #ebeef5;
  background-color: #f8f9fa;
  padding: 12px 16px;
  border-radius: 4px 4px 0 0;
}

.title-area {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title-area h2 {
  margin: 0;
  font-size: 16px;
  color: #303133;
  font-weight: 500;
}

.info-icon {
  color: #409eff;
  cursor: pointer;
}

.action-area {
  display: flex;
  gap: 12px;
  align-items: center;
}

.year-picker {
  width: 100px !important;
}

.filter-select {
  width: 120px;
}

.download-btn {
  background-color: #1d4ed8;
  border-color: #1d4ed8;
}

.download-btn:hover {
  background-color: #1e40af;
  border-color: #1e40af;
}

.employee-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 16px;
  padding: 8px 0;
}

.employee-card {
  background-color: #86efac; /* 浅绿色背景 */
  border-radius: 4px;
  padding: 16px 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  color: #fff;
  font-size: 13px;
  cursor: pointer;
  transition: transform 0.2s, box-shadow 0.2s;
  box-shadow: 0 1px 3px rgba(0,0,0,0.1);
}

.employee-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 6px rgba(0,0,0,0.1);
  background-color: #4ade80;
}

.emp-code {
  font-weight: 500;
}

.emp-name {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 80px;
}

.emp-position {
  white-space: nowrap;
}
</style>

<template>
  <div class="staffing-contract-form-page">
    <div class="page-header">
      <el-page-header @back="$router.back()">
        <template #content>
          <span>{{ isEdit ? '编辑派遣合同' : '新建派遣合同' }}</span>
        </template>
      </el-page-header>
    </div>
    
    <el-card class="form-card">
      <el-form label-width="120px" label-position="left">
        <el-divider content-position="left">基本信息</el-divider>
        
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="合同编号">
              <el-input placeholder="自动生成" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="合同状态">
              <el-select placeholder="请选择" style="width: 100%">
                <el-option label="草稿" value="draft" />
                <el-option label="生效" value="active" />
                <el-option label="到期" value="expired" />
                <el-option label="终止" value="terminated" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        
        <el-divider content-position="left">派遣员工</el-divider>
        
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="员工">
              <el-select placeholder="请选择员工" style="width: 100%">
                <el-option label="（开发中）" value="" disabled />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        
        <el-divider content-position="left">派遣先信息</el-divider>
        
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="派遣先企业">
              <el-select placeholder="请选择客户企业" style="width: 100%">
                <el-option label="（开发中）" value="" disabled />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="工作地点">
              <el-input placeholder="请输入工作地点" />
            </el-form-item>
          </el-col>
        </el-row>
        
        <el-divider content-position="left">合同期间</el-divider>
        
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="开始日期">
              <el-date-picker type="date" placeholder="请选择" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="结束日期">
              <el-date-picker type="date" placeholder="请选择" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>
        
        <el-divider content-position="left">费用设定</el-divider>
        
        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="计费方式">
              <el-select placeholder="请选择" style="width: 100%">
                <el-option label="时薪" value="hourly" />
                <el-option label="月薪" value="monthly" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="时薪/月薪">
              <el-input-number :min="0" :precision="0" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="账单周期">
              <el-select placeholder="请选择" style="width: 100%">
                <el-option label="月结" value="monthly" />
                <el-option label="半月结" value="biweekly" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        
        <el-form-item>
          <el-button type="primary" disabled>保存（开发中）</el-button>
          <el-button @click="$router.back()">取消</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const isEdit = computed(() => !!route.params.id)
</script>

<style scoped>
.staffing-contract-form-page {
  padding: 20px;
}

.page-header {
  margin-bottom: 20px;
}

.form-card {
  max-width: 900px;
}
</style>


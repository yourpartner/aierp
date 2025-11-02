<template>
  <div style="max-width: 380px; margin: 80px auto;">
    <h3 style="margin-bottom:16px;">登录</h3>
    <el-form :model="form" label-width="90px" @submit.native.prevent>
      <el-form-item label="公司代码">
        <el-input v-model="form.companyCode" autofocus />
      </el-form-item>
      <el-form-item label="员工编号">
        <el-input v-model="form.employeeCode" />
      </el-form-item>
      <el-form-item label="密码">
        <el-input v-model="form.password" type="password" show-password />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="loading" @click="login">登录</el-button>
      </el-form-item>
    </el-form>
  </div>
</template>
<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import api from '../api'
import store from '../utils/storage'

const route = useRoute()
const router = useRouter()
const loading = ref(false)
const form = reactive({ companyCode: 'JP01', employeeCode: '', password: '' })

async function login(){
  if (!form.companyCode || !form.employeeCode || !form.password) return
  try{
    loading.value = true
    const r = await api.post('/auth/login', {
      companyCode: form.companyCode,
      employeeCode: form.employeeCode,
      password: form.password
    })
    const token = r.data?.token
    if (token){
      store.setItem('auth_token', token)
      store.setItem('company_code', form.companyCode)
      api.defaults.headers.common['Authorization'] = `Bearer ${token}`
      api.defaults.headers.common['x-company-code'] = form.companyCode
      const redirect = (route.query.redirect as string) || '/chat'
      try {
        await router.replace(redirect)
      } catch {
        // 若因路由守卫或其它原因失败，退回硬跳转
        location.href = redirect
      }
    }
  }finally{
    loading.value = false
  }
}
</script>

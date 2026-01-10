<template>
  <div style="max-width: 380px; margin: 80px auto;">
    <h3 style="margin-bottom:16px;">ログイン</h3>
    <el-form :model="form" label-width="90px" @submit.native.prevent>
      <el-form-item label="会社コード">
        <el-input v-model="form.companyCode" autofocus />
      </el-form-item>
      <el-form-item label="社員コード">
        <el-input v-model="form.employeeCode" />
      </el-form-item>
      <el-form-item label="パスワード">
        <el-input v-model="form.password" type="password" show-password @keyup.enter="login" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="loading" @click="login">ログイン</el-button>
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
      // 保存用户信息和权限
      const name = r.data?.name
      const roles = r.data?.roles
      if (name) sessionStorage.setItem('currentUserName', name)
      sessionStorage.setItem('currentCompany', form.companyCode)
      // 解析JWT获取caps
      try {
        const [, payload] = token.split('.')
        const base64 = payload.replace(/-/g,'+').replace(/_/g,'/')
        const pad = '='.repeat((4 - (base64.length % 4)) % 4)
        const decoded = JSON.parse(atob(base64 + pad))
        if (decoded.caps) sessionStorage.setItem('userCaps', decoded.caps)
        if (decoded.roles) sessionStorage.setItem('userRoles', decoded.roles)
      } catch {}
      // 通知 App.vue 重新加载公司名称
      if (typeof window !== 'undefined') {
        window.dispatchEvent(new CustomEvent('company-settings-updated', { detail: {} }))
      }
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

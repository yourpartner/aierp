<template>
  <div style="max-width: 380px; margin: 80px auto;">
    <div style="text-align: center; margin-bottom: 32px;">
      <img src="/sfin-logo.png" alt="iTBank Sfin" style="width: 240px; height: auto;" />
    </div>
    <h3 style="margin-bottom:16px;">{{ t('login.title') }}</h3>
    <el-form :model="form" label-width="110px" @submit.native.prevent>
      <el-form-item :label="t('login.companyCode')">
        <el-input v-model="form.companyCode" autofocus />
      </el-form-item>
      <el-form-item :label="t('login.employeeCode')">
        <el-input v-model="form.employeeCode" />
      </el-form-item>
      <el-form-item :label="t('login.password')">
        <el-input v-model="form.password" type="password" show-password @keyup.enter="login" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="loading" @click="login">{{ t('login.submit') }}</el-button>
      </el-form-item>
    </el-form>
  </div>
</template>
<script setup lang="ts">
import { reactive, ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useI18n } from '../i18n'
import api from '../api'
import store from '../utils/storage'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const loading = ref(false)
const form = reactive({ companyCode: 'JP01', employeeCode: '', password: '' })

onMounted(() => {
  const savedCompany = localStorage.getItem('company_code')
  if (savedCompany) form.companyCode = savedCompany
})

async function login(){
  if (!form.companyCode || !form.employeeCode || !form.password) {
    ElMessage.warning(t('login.required'))
    return
  }
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
    } else {
      ElMessage.error(t('login.failed'))
    }
  } catch (e: any) {
    console.error('Login error:', e)
    let msg = t('login.failed')
    if (e.response?.status === 401) {
      msg = t('login.invalid')
    } else if (e.response?.data?.error) {
      msg = e.response.data.error
    } else if (e.message) {
      msg = e.message
    }
    ElMessage.error(msg)
  } finally {
    loading.value = false
  }
}
</script>

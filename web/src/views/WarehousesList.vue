<template>
  <div class="page">
    <el-card>
      <SchemaList entity="warehouse" :title="navText.inventoryWarehouses" create-path="/warehouse/new" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import SchemaList from '../components/SchemaList.vue'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ inventoryWarehouses:'' }, (msg) => msg.nav)
</script>

<style scoped>
</style>



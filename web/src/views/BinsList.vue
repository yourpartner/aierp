<template>
  <div class="page">
    <el-card>
      <SchemaList entity="bin" :title="navText.inventoryBins" create-path="/bin/new" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import SchemaList from '../components/SchemaList.vue'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ inventoryBins:'' }, (msg) => msg.nav)
</script>

<style scoped>
</style>



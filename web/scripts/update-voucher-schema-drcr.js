/*
  更新 voucher 的 schema：将行项目改为 { lineNo, accountCode, drcr, amount, ... }
  用法：node web/scripts/update-voucher-schema-drcr.js
*/
import axios from 'axios'

const BASE = 'http://localhost:5179'
const COMPANY = 'JP01'
const LOGIN = { companyCode: COMPANY, employeeCode: 'admin', password: 'admin123' }

async function main(){
  const auth = await axios.post(BASE + '/auth/login', LOGIN).then(r => r.data).catch(()=> ({}))
  const token = auth.token
  const headers = token ? { Authorization: 'Bearer ' + token } : {}

  const res = await axios.get(BASE + '/schemas/voucher', { headers })
  const doc = res.data || {}
  const schema = doc.schema || {}

  // 防御：确保结构存在
  schema.type = 'object'
  schema.properties = schema.properties || {}
  const lines = schema.properties.lines = schema.properties.lines || { type:'array', items:{} }
  lines.type = 'array'
  lines.items = lines.items || {}
  const item = lines.items
  item.type = 'object'
  item.properties = item.properties || {}

  // 设置行项目字段
  item.properties.lineNo = item.properties.lineNo || { type:'integer' }
  item.properties.accountCode = { type:'string' }
  item.properties.drcr = { type:'string', enum:['DR','CR'] }
  item.properties.amount = { type:'number' }

  // 删除旧的 debit/credit 字段（若存在）
  delete item.properties.debit
  delete item.properties.credit

  // required
  // 前端未发送 lineNo（由后端按 idx+1 生成），不将 lineNo 设为必填
  item.required = ['accountCode','drcr','amount']

  // Header 必要字段
  const header = schema.properties.header = schema.properties.header || { type:'object', properties:{} }
  header.type = 'object'
  header.properties = header.properties || {}
  header.properties.companyCode = header.properties.companyCode || { type:'string' }
  header.properties.postingDate = header.properties.postingDate || { type:'string', format:'date' }
  header.properties.voucherType = header.properties.voucherType || { type:'string', enum:['GL','AP','AR','AA','SA','IN','OT'] }
  header.required = ['companyCode','postingDate','voucherType']

  // 组装 payload 并激活
  const payload = {
    schema,
    ui: doc.ui || {},
    query: doc.query || {},
    core_fields: doc.core_fields || {},
    validators: doc.validators || [],
    numbering: doc.numbering || {},
    ai_hints: doc.ai_hints || {}
  }
  await axios.post(BASE + '/schemas/voucher', payload, { headers })
  console.log('voucher schema updated for drcr/amount')
}

main().catch(err => {
  console.error('update failed:', err?.response?.data || err.message)
  process.exit(1)
})



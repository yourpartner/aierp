// 为 voucher 增加 ui.form.layout：基本信息区块
// 运行：node web/scripts/update-voucher-ui.js

import axios from 'axios'

const BASE = process.env.BACKEND_BASE || 'http://localhost:5179'

function grid(cols){ return { type: 'grid', cols } }
function col(field, label, span=8, widget='input', props={}){ return { field, label, span, widget, props } }

async function main(){
  const get = async () => (await axios.get(`${BASE}/schemas/voucher`)).data
  const cur = await get()
  const ui = cur.ui || {}
  ui.form = ui.form || {}
  ui.form.layout = Array.isArray(ui.form.layout) ? ui.form.layout : []

  // 基本信息：过账日期、凭证类型、币种、摘要
  const basic = grid([
    col('header.postingDate','过账日期',6,'date'),
    col('header.voucherType','凭证类型',6,'select', { options:['GL','AP','AR','AA','SA','IN','OT'] }),
    col('header.currency','币种',6,'select', { options:['JPY','USD','CNY'] }),
    col('header.summary','摘要',12,'input')
  ])

  // 清空旧 layout（避免空数组导致 DynamicForm 渲染空白），设为仅 basic
  ui.form.layout = [basic]

  const body = {
    schema: cur.schema || { type:'object', properties:{} },
    ui,
    query: cur.query || {},
    core_fields: cur.core_fields || {},
    validators: cur.validators || [],
    numbering: cur.numbering || {},
    ai_hints: cur.ai_hints || {}
  }

  await axios.post(`${BASE}/schemas/voucher`, body)
  console.log('voucher ui updated and activated')
}

main().catch(err => {
  console.error('update failed:', err?.response?.data || err?.message || err)
  process.exit(1)
})



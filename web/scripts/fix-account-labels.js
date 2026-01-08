// 修复 account schema 中 fieldRules 下拉的中文标签乱码问题
// 同时：合并“输入状态控制”区块，仅保留一个，并加入“支付日 输入控制”

import axios from 'axios'

async function main() {
  const base = process.env.BACKEND_BASE || 'http://localhost:5179'
  const name = 'account'

  const get = async () => (await axios.get(`${base}/schemas/${name}`)).data

  const cur = await get()
  const ui = cur.ui || {}
  ui.form = ui.form || {}
  ui.form.layout = Array.isArray(ui.form.layout) ? ui.form.layout : []

  // 先删掉所有包含 fieldRules.* 的区块，避免重复
  const isFieldRulesBlock = (b) => {
    if (!b) return false
    if (b.type === 'section' && Array.isArray(b.layout)) {
      return b.layout.some(x => x && x.type === 'grid' && Array.isArray(x.cols) && x.cols.some(c => typeof c?.field === 'string' && c.field.startsWith('fieldRules.')))
    }
    if (b.type === 'grid' && Array.isArray(b.cols)) {
      return b.cols.some(c => typeof c?.field === 'string' && c.field.startsWith('fieldRules.'))
    }
    return false
  }
  ui.form.layout = ui.form.layout.filter(b => !isFieldRulesBlock(b))

  // 新建统一的“输入状态控制”分区，包含客户/供应商/员工/部门/支付日
  const block = {
    type: 'section',
    title: '输入状态控制',
    layout: [
      {
        type: 'grid',
        cols: [
          { label: '客户 输入控制', field: 'fieldRules.customerId', widget: 'select', span: 8, props: { options: [
            { label:'必填', value:'required' }, { label:'可选', value:'optional' }, { label:'隐藏', value:'hidden' }
          ] } },
          { label: '供应商 输入控制', field: 'fieldRules.vendorId', widget: 'select', span: 8, props: { options: [
            { label:'必填', value:'required' }, { label:'可选', value:'optional' }, { label:'隐藏', value:'hidden' }
          ] } },
          { label: '员工 输入控制', field: 'fieldRules.employeeId', widget: 'select', span: 8, props: { options: [
            { label:'必填', value:'required' }, { label:'可选', value:'optional' }, { label:'隐藏', value:'hidden' }
          ] } },
          { label: '部门 输入控制', field: 'fieldRules.departmentId', widget: 'select', span: 8, props: { options: [
            { label:'必填', value:'required' }, { label:'可选', value:'optional' }, { label:'隐藏', value:'hidden' }
          ] } },
          { label: '支付日 输入控制', field: 'fieldRules.paymentDate', widget: 'select', span: 8, props: { options: [
            { label:'必填', value:'required' }, { label:'可选', value:'optional' }, { label:'隐藏', value:'hidden' }
          ] } }
        ]
      }
    ]
  }
  // 将该分区插入到“银行/现金”分区之前（若能找到），否则追加到末尾
  const bankCashIdx = ui.form.layout.findIndex(b => b && b.title && String(b.title).includes('银行'))
  if (bankCashIdx >= 0) ui.form.layout.splice(bankCashIdx, 0, block)
  else ui.form.layout.push(block)

  const body = {
    schema: cur.schema || { type: 'object', properties: {} },
    ui,
    query: cur.query || {},
    core_fields: cur.core_fields || {},
    validators: cur.validators || [],
    numbering: cur.numbering || {},
    ai_hints: cur.ai_hints || {}
  }

  await axios.post(`${base}/schemas/${name}`, body)
  console.log('account schema fieldRules merged and labels fixed')
}

main().catch((e) => {
  console.error('fix failed:', e?.response?.data || e?.message || e)
  process.exit(1)
})



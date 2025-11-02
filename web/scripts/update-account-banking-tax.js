// 更新 account schema：新增消费税类型、银行/现金科目及其条件字段与校验
// 以 UTF-8 编码执行：node web/scripts/update-account-banking-tax.js

import axios from 'axios'

const BASE = process.env.BACKEND_BASE || 'http://localhost:5179'

async function main() {
  const get = async () => (await axios.get(`${BASE}/schemas/account`)).data
  const cur = await get()

  // 1) 更新 schema
  const schema = cur.schema || { type: 'object', properties: {} }
  schema.type = 'object'
  schema.properties = schema.properties || {}

  // 消费税类型
  schema.properties.taxType = {
    type: 'string',
    description: '消费税关联',
    enum: ['NON_TAX', 'INPUT_TAX', 'OUTPUT_TAX', 'TAX_ACCOUNT']
  }

  // 银行/现金标记
  schema.properties.isBank = { type: 'boolean', description: '银行科目' }
  schema.properties.isCash = { type: 'boolean', description: '现金科目' }

  // 银行信息对象
  schema.properties.bankInfo = {
    type: 'object',
    properties: {
      bankName: { type: 'string', maxLength: 120, description: '银行' },
      branchName: { type: 'string', maxLength: 120, description: '支店' },
      accountType: { type: 'string', enum: ['普通', '当座'], description: '口座种别' },
      accountNo: { type: 'string', maxLength: 30, description: '账户号' },
      holder: { type: 'string', maxLength: 120, description: '名义人' },
      currency: { type: 'string', enum: ['JPY', 'USD', 'CNY'], description: '币种' },
      zenginBranchCode: { type: 'string', maxLength: 10, description: '综合转账用支店代码' },
      payrollCompanyCode: { type: 'string', maxLength: 20, description: '给与/奖金转账用公司代码' },
      juminzeiCompanyCode: { type: 'string', maxLength: 20, description: '个人地方税缴纳用公司代码' }
    }
  }

  // 现金币种
  schema.properties.cashCurrency = { type: 'string', enum: ['JPY', 'USD', 'CNY'], description: '现金币种' }

  // 复合校验：不能同时是银行与现金；若是银行，要求银行字段；若是现金，要求现金币种
  schema.allOf = schema.allOf || []
  // not both true
  schema.allOf.push({
    not: {
      type: 'object',
      properties: {
        isBank: { const: true },
        isCash: { const: true }
      },
      required: ['isBank', 'isCash']
    }
  })
  // if isBank then require bankInfo + bankInfo.currency + accountType + accountNo
  schema.allOf.push({
    if: { properties: { isBank: { const: true } } },
    then: { required: ['bankInfo'], properties: { bankInfo: { required: ['bankName','branchName','accountType','accountNo','holder','currency'] } } }
  })
  // if isCash then require cashCurrency
  schema.allOf.push({
    if: { properties: { isCash: { const: true } } },
    then: { required: ['cashCurrency'] }
  })

  // 2) 更新 ui（DynamicForm 读取）
  const ui = cur.ui || {}
  ui.form = ui.form || {}
  ui.form.layout = Array.isArray(ui.form.layout) ? ui.form.layout : []

  // 顶部新增：消费税类型 + 银行/现金开关
  const headerBlock = {
    type: 'grid',
    cols: [
      { label: '消费税类型', field: 'taxType', widget: 'select', span: 8, options: [
        { label: '非课税', value: 'NON_TAX' },
        { label: '进项税', value: 'INPUT_TAX' },
        { label: '销项税', value: 'OUTPUT_TAX' },
        { label: '消费税科目', value: 'TAX_ACCOUNT' }
      ] },
      { label: '银行科目', field: 'isBank', widget: 'switch', span: 4 },
      { label: '现金科目', field: 'isCash', widget: 'switch', span: 4 }
    ]
  }

  // 银行信息区块（仅 isBank=true 可见）
  const bankBlock = {
    type: 'grid',
    cols: [
      { label: '银行', field: 'bankInfo.bankName', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '支店', field: 'bankInfo.branchName', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '口座种别', field: 'bankInfo.accountType', widget: 'select', span: 8, options: ['普通','当座'], visibleWhen: { field: 'isBank', equals: true } },
      { label: '账户号', field: 'bankInfo.accountNo', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '名义人', field: 'bankInfo.holder', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '币种', field: 'bankInfo.currency', widget: 'select', span: 8, options: ['JPY','USD','CNY'], visibleWhen: { field: 'isBank', equals: true } },
      { label: '综合转账用支店代码', field: 'bankInfo.zenginBranchCode', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '给与/奖金转账用公司代码', field: 'bankInfo.payrollCompanyCode', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } },
      { label: '个人地方税缴纳用公司代码', field: 'bankInfo.juminzeiCompanyCode', widget: 'input', span: 8, visibleWhen: { field: 'isBank', equals: true } }
    ]
  }

  // 现金信息（仅 isCash=true 可见）
  const cashBlock = {
    type: 'grid',
    cols: [
      { label: '现金币种', field: 'cashCurrency', widget: 'select', span: 8, options: ['JPY','USD','CNY'], visibleWhen: { field: 'isCash', equals: true } }
    ]
  }

  // 将区块插入到已有 layout 前面，避免覆盖已有基本信息与 fieldRules
  ui.form.layout = [headerBlock, ...ui.form.layout, bankBlock, cashBlock]

  // 去重：如果已有同名字段，保留后插入的（最新）
  const seen = new Set()
  ui.form.layout = ui.form.layout.filter(b => {
    if (!b || !Array.isArray(b.cols)) return true
    const sig = (b.cols||[]).map(c => c && c.field).filter(Boolean).join('|')
    if (seen.has(sig)) return false
    seen.add(sig)
    return true
  })

  // 3) 提交
  const body = {
    schema,
    ui,
    query: cur.query || {},
    core_fields: cur.core_fields || {},
    validators: cur.validators || [],
    numbering: cur.numbering || {},
    ai_hints: cur.ai_hints || {}
  }

  await axios.post(`${BASE}/schemas/account`, body)
  console.log('account schema updated (tax/bank/cash) and activated')
}

main().catch(err => {
  console.error('update failed:', err?.response?.data || err?.message || err)
  process.exit(1)
})

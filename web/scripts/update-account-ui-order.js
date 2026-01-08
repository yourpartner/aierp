// 用于重排 account 的 UI 字段顺序并分区显示
// 运行：node web/scripts/update-account-ui-order.js
import axios from 'axios'

const BACKEND = process.env.BACKEND_BASE || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'

function grid(cols){ return { type: 'grid', cols } }
function col(field, label, span=8, widget='input', props={}){ return { field, label, span, widget, props } }

async function main(){
  const api = axios.create({ baseURL: BACKEND })
  api.defaults.headers.common['x-company-code'] = COMPANY
  const r = await api.get('/schemas/account')
  const schemaAll = r.data
  const ui = schemaAll.ui || { form: { layout: [] } }

  // 1) 顶部基础信息顺序：科目号，科目名称，BS/PL科目，消费税关联，未清项管理
  const base = grid([
    col('code','科目号',6,'input'),
    col('name','科目名称',10,'input'),
    col('category','BS/PL区分',4,'select', { options: [
      { label:'BS资产负债科目', value:'BS' },
      { label:'PL损益科目', value:'PL' }
    ] }),
    col('taxType','消费税区分',6,'select', { options: [
      { label:'非课税', value:'NON_TAX' },
      { label:'进项税', value:'INPUT_TAX' },
      { label:'销项税', value:'OUTPUT_TAX' },
      { label:'消费税科目', value:'TAX_ACCOUNT' }
    ] }),
    col('openItem','未清项管理',6,'switch'),
    { ...col('openItemBaseline','清账基准',8,'select', { options: [
      { label:'无基准项目', value:'NONE' },
      { label:'客户', value:'CUSTOMER' },
      { label:'供应商', value:'VENDOR' },
      { label:'员工', value:'EMPLOYEE' }
    ] }), visibleWhen: { field: 'openItem', equals: true } }
  ])

  // 2) 输入状态控制字段区（客户，供应商，员工，部门，支付日）
  const fieldRules = {
    type: 'section',
    title: '输入状态控制',
    layout: [ grid([
      col('fieldRules.customerId','客户 输入控制',6,'select', { options:[
        { label:'必填', value:'required' },
        { label:'可选', value:'optional' },
        { label:'隐藏', value:'hidden' }
      ] }),
      col('fieldRules.vendorId','供应商 输入控制',6,'select', { options:[
        { label:'必填', value:'required' },
        { label:'可选', value:'optional' },
        { label:'隐藏', value:'hidden' }
      ] }),
      col('fieldRules.employeeId','员工 输入控制',6,'select', { options:[
        { label:'必填', value:'required' },
        { label:'可选', value:'optional' },
        { label:'隐藏', value:'hidden' }
      ] }),
      col('fieldRules.departmentId','部门 输入控制',6,'select', { options:[
        { label:'必填', value:'required' },
        { label:'可选', value:'optional' },
        { label:'隐藏', value:'hidden' }
      ] }),
      col('fieldRules.paymentDate','支付日 输入控制',6,'select', { options:[
        { label:'必填', value:'required' },
        { label:'可选', value:'optional' },
        { label:'隐藏', value:'hidden' }
      ] })
    ]) ]
  }

  // 3) 银行/现金分区
  const bankCash = {
    type: 'section',
    title: '银行 / 现金',
    layout: [
      grid([
        col('isBank','银行科目',6,'switch'),
        col('isCash','现金科目',6,'switch')
      ]),
      // 按钮行：由 DynamicForm 的 widget:button 渲染，触发 @action
      grid([
        { label:'选择银行', span:4, widget:'button', props:{ type:'primary', action:'openBankPicker' }, visibleWhen:{ field:'isBank', equals:true } },
        { label:'', span:8, widget:'text', field:'bankInfo.bankName', visibleWhen:{ field:'isBank', equals:true } },
        { label:'选择支店', span:4, widget:'button', props:{ action:'openBranchPicker', disabledWhen:{ field:'bankInfo.bankCode', empty:true } }, visibleWhen:{ field:'isBank', equals:true } },
        { label:'', span:8, widget:'text', field:'bankInfo.branchName', visibleWhen:{ field:'isBank', equals:true } }
      ]),
      grid([
        // 名称输入框已由按钮替代，不再渲染
        { ...col('bankInfo.accountType','口座種別',4,'select', { options:['普通','当座'] }), visibleWhen: { field: 'isBank', equals: true } },
        { ...col('bankInfo.accountNo','账户号',6,'input'), visibleWhen: { field: 'isBank', equals: true } },
        { ...col('bankInfo.holder','名义人',6,'input', { filter:'katakana-half' }), visibleWhen: { field: 'isBank', equals: true } },
        { ...col('bankInfo.currency','币种',4,'select', { options:['JPY','USD','CNY'] }), visibleWhen: { field: 'isBank', equals: true } }
      ]),
      grid([
        { ...col('cashCurrency','现金币种',6,'select', { options:['JPY','USD','CNY'] }), visibleWhen: { field: 'isCash', equals: true } }
      ])
    ]
  }

  // 后端 schema：为 holder 添加半角カタカナ正则约束 + isBank/isCash 互斥
  const schema = schemaAll.schema || {}
  if (schema?.properties?.bankInfo?.properties) {
    const p = schema.properties.bankInfo.properties
    p.holder = p.holder || { type:'string' }
    p.holder.pattern = '^[\uff65-\uff9f\s]+$'
  }
  // 互斥：不允许同时 true
  const allOf = schema.allOf || []
  allOf.push({ not: { properties: { isBank:{ const:true }, isCash:{ const:true } }, required:['isBank','isCash'] } })
  schema.allOf = allOf

  ui.form = ui.form || {}
  ui.form.layout = [ base, fieldRules, bankCash ]

  const payload = { schema, ui, query: schemaAll.query, core_fields: schemaAll.core_fields, validators: schemaAll.validators, numbering: schemaAll.numbering, ai_hints: schemaAll.ai_hints }
  const r2 = await api.post('/schemas/account', payload)
  console.log('account ui updated. version=', r2.data.version)
}

main().catch(e => { console.error(e?.response?.data || e) })



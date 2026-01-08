// 注册 open_item 投影与 bank_collect operation 的 schema/ui/query/auth
import axios from 'axios'

const BACKEND = process.env.BACKEND_BASE || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'

async function post(name, payload){
  const api = axios.create({ baseURL: BACKEND })
  api.defaults.headers.common['x-company-code'] = COMPANY
  const r = await api.post(`/schemas/${name}`, payload)
  console.log('registered', name, 'version=', r.data.version)
}

const openitem = {
  schema: {
    type:'object', properties:{
      voucherId:{type:'string'}, voucherLineNo:{type:'integer'}, accountCode:{type:'string'}, partnerId:{type:['string','null']},
      currency:{type:'string'}, docDate:{type:['string','null'], format:'date'}, originalAmount:{type:'number'}, residualAmount:{type:'number'}, clearedFlag:{type:'boolean'}
    }
  },
  ui: {
    list:{ columns:['account_code','partner_id','currency','residual_amount'] },
    form:{ layout:[ { type:'grid', cols:[
      { field:'accountCode', label:'科目', span:6 },
      { field:'partnerId', label:'对象', span:6 },
      { field:'currency', label:'币种', span:4 },
      { field:'residualAmount', label:'未清余额', span:6 }
    ] } ] }
  },
  query: { filters:['account_code','partner_id','currency','residual_amount'], sorts:['residual_amount'] }
}

const bankCollectOp = {
  schema: {
    type:'object', properties:{
      header:{ type:'object', properties:{ postingDate:{type:'string',format:'date'}, currency:{type:'string'}, bankAccountCode:{type:'string'} }, required:['postingDate','bankAccountCode'] },
      allocations:{ type:'array', items:{ type:'object', properties:{ openItemId:{type:'string', format:'uuid'}, applyAmount:{type:'number'} }, required:['openItemId','applyAmount'] } }
    }, required:['header','allocations']
  },
  ui: {
    form:{ layout:[ { type:'grid', cols:[
      { field:'header.postingDate', label:'入金日', widget:'date', span:6 },
      { field:'header.currency', label:'币种', widget:'select', props:{ options:['JPY','USD','CNY'] }, span:4 },
      { field:'header.bankAccountCode', label:'银行科目', span:8 }
    ] } ] }
  },
  query: { filters:[], sorts:[] },
  auth: { actions:{ perform:['FinanceManager','Cashier'] }, scopes:{ default:[] } }
}

const bankPaymentOp = {
  schema: {
    type:'object', properties:{
      header:{ type:'object', properties:{ postingDate:{type:'string',format:'date'}, currency:{type:'string'}, bankAccountCode:{type:'string'} }, required:['postingDate','bankAccountCode'] },
      allocations:{ type:'array', items:{ type:'object', properties:{ openItemId:{type:'string', format:'uuid'}, applyAmount:{type:'number'} }, required:['openItemId','applyAmount'] } }
    }, required:['header','allocations']
  },
  ui: {
    form:{ layout:[ { type:'grid', cols:[
      { field:'header.postingDate', label:'出金日', widget:'date', span:6 },
      { field:'header.currency', label:'币种', widget:'select', props:{ options:['JPY','USD','CNY'] }, span:4 },
      { field:'header.bankAccountCode', label:'银行科目', span:8 }
    ] } ] }
  },
  query: { filters:[], sorts:[] },
  auth: { actions:{ perform:['FinanceManager','Cashier'] }, scopes:{ default:[] } }
}

async function main(){
  await post('openitem', openitem)
  await post('bank_collect', bankCollectOp)
  await post('bank_payment', bankPaymentOp)
}

main().catch(e => console.error(e?.response?.data || e))



// 注册 bank 与 branch 的 schema/ui/query 到后端 jsonstructures
// 运行：node web/scripts/register-bank-branch-schema.js
import axios from 'axios'

const BACKEND = process.env.BACKEND_BASE || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'

async function post(name, payload){
  const api = axios.create({ baseURL: BACKEND })
  api.defaults.headers.common['x-company-code'] = COMPANY
  const r = await api.post(`/schemas/${name}`, payload)
  console.log('registered', name, 'version=', r.data.version)
}

const bank = {
  schema: {
    type:'object',
    properties: {
      bankCode:{ type:'string' },
      name:{ type:'string' },
      nameKana:{ type:['string','null'] }
    },
    required:['bankCode','name']
  },
  ui: {
    list:{ columns:['bank_code','name'] },
    form:{ layout:[ { type:'grid', cols:[
      { field:'bankCode', label:'银行代码', widget:'input', span:6 },
      { field:'name', label:'银行名称', widget:'input', span:12 },
      { field:'nameKana', label:'假名', widget:'input', span:6 }
    ] } ] }
  },
  query: { filters:['bank_code','name','payload.bankCode','payload.name'], sorts:['bank_code','name'] },
  core_fields: { coreFields:[
    { name:'bank_code', path:'bankCode', type:'string', index:{ strategy:'generated_column', unique:true, scope:['company_code'] } },
    { name:'name', path:'name', type:'string', index:{ strategy:'generated_column' } }
  ] }
}

const branch = {
  schema: {
    type:'object',
    properties: {
      bankCode:{ type:'string' },
      branchCode:{ type:'string' },
      branchName:{ type:'string' },
      branchKana:{ type:['string','null'] }
    },
    required:['bankCode','branchCode','branchName']
  },
  ui: {
    list:{ columns:['bank_code','branch_name','branch_code'] },
    form:{ layout:[ { type:'grid', cols:[
      { field:'bankCode', label:'银行代码', widget:'input', span:6 },
      { field:'branchCode', label:'支店代码', widget:'input', span:6 },
      { field:'branchName', label:'支店名称', widget:'input', span:12 },
      { field:'branchKana', label:'假名', widget:'input', span:6 }
    ] } ] }
  },
  query: { filters:['bank_code','branch_name','branch_code','payload.branchName','payload.branchCode'], sorts:['bank_code','branch_code'] },
  core_fields: { coreFields:[
    { name:'bank_code', path:'bankCode', type:'string', index:{ strategy:'generated_column' } },
    { name:'branch_code', path:'branchCode', type:'string', index:{ strategy:'generated_column' } },
    { name:'branch_name', path:'branchName', type:'string', index:{ strategy:'generated_column' } }
  ] }
}

async function main(){
  await post('bank', bank)
  await post('branch', branch)
}

main().catch(e => console.error(e?.response?.data || e))



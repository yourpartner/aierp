import axios from 'axios'

// 用法:
//   node web/scripts/register-batch-and-status-schemas.js http://localhost:5000 <JWT>
// 若未提供 AUTH_TOKEN，则使用 JP01/admin/admin123 登录获取
async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5000'
  let token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const company = process.env.COMPANY_CODE || 'JP01'
  const api = axios.create({ baseURL })
  api.defaults.headers.common['x-company-code'] = company
  if (!token){
    const emp = process.env.LOGIN_EMPLOYEE || 'admin'
    const pwd = process.env.LOGIN_PASSWORD || 'admin123'
    const r = await api.post('/auth/login', { companyCode: company, employeeCode: emp, password: pwd })
    token = r.data?.token || ''
  }
  if (token) api.defaults.headers.common['Authorization'] = 'Bearer ' + token

  // batch schema
  const batch = {
    schema: {
      type: 'object',
      properties: {
        materialCode: { type: 'string' },
        batchNo: { type: 'string' },
        mfgDate: { type: ['string','null'], format: 'date' },
        expDate: { type: ['string','null'], format: 'date' }
      },
      required: ['materialCode','batchNo']
    },
    ui: {
      list: { columns: ['material_code','batch_no','mfg_date','exp_date'] },
      form: { layout: [
        { type: 'grid', cols: [
          { field: 'materialCode', label: 'Material Code', span: 8 },
          { field: 'batchNo', label: 'Batch No', span: 8 },
          { field: 'mfgDate', label: 'Mfg Date', widget: 'date', span: 4 },
          { field: 'expDate', label: 'Exp Date', widget: 'date', span: 4 }
        ]}
      ]}
    },
    query: { filters: ['material_code','batch_no'], sorts: ['material_code','batch_no'] },
    core_fields: { coreFields: [] }
  }

  // stock_status schema（可选，但一并补齐）
  const status = {
    schema: { type:'object', properties: { code: { type:'string' }, name: { type:'string' } }, required:['code','name'] },
    ui: { list:{ columns:['status_code','name'] }, form:{ layout:[ { type:'grid', cols:[ { field:'code', label:'Code', span:6 }, { field:'name', label:'Name', span:12 } ] } ] } },
    query: { filters:['status_code','name'], sorts:['status_code','name'] },
    core_fields: { coreFields: [] }
  }

  const r1 = await api.post('/schemas/batch', batch).then(x=>x.status).catch(e=>{ throw new Error('batch schema: '+(e?.response?.status+':'+(e?.response?.data?.error||e?.message))) })
  const r2 = await api.post('/schemas/stock_status', status).then(x=>x.status).catch(async e=>{
    // 允许失败（不是本次关键）
    return e?.response?.status || 0
  })
  console.log('batch', r1, 'stock_status', r2)
}

main().catch(e=>{ console.error(e?.message||e); process.exit(1) })



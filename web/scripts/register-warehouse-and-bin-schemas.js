import axios from 'axios'

// 用法:
//   node web/scripts/register-warehouse-and-bin-schemas.js http://localhost:5000 <JWT>
// 若未提供 AUTH_TOKEN，则尝试使用 JP01/admin/admin123 登录获取
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
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`

  // warehouse schema
  const warehouseSchema = { type:'object', properties:{ code:{type:'string', maxLength:4, pattern:'^[A-Za-z0-9]{1,4}$'}, name:{type:'string'} }, required:['code','name'] }
  const warehouseUi = {
    list: { columns: ['warehouse_code','name'] },
    form: { layout: [
      { type:'grid', cols:[
        { field:'code', label:'Code', props:{ placeholder:'A001', maxlength:4, clearable:true }, span:6 },
        { field:'name', label:'Name', span:12 }
      ]}
    ]}
  }
  const warehouseBody = { schema: warehouseSchema, ui: warehouseUi, query: { filters:['warehouse_code','name'], sorts:['warehouse_code','name'] }, core_fields:{ coreFields:[] } }
  const r1 = await api.post('/schemas/warehouse', warehouseBody)
  console.log('Registered warehouse schema:', r1.status)

  // bin schema
  const binSchema = { type:'object', properties:{ warehouseCode:{type:'string'}, code:{type:'string', maxLength:4, pattern:'^[A-Za-z0-9]{1,4}$'}, name:{type:'string'} }, required:['warehouseCode','code','name'] }
  const binUi = {
    list: { columns: ['warehouse_code','bin_code','name'] },
    form: { layout: [
      { type:'grid', cols:[
        { field:'warehouseCode', label:'Warehouse', span:8 },
        { field:'code', label:'Bin Code', props:{ placeholder:'B001', maxlength:4, clearable:true }, span:8 },
        { field:'name', label:'Name', span:8 }
      ]}
    ]}
  }
  const binBody = { schema: binSchema, ui: binUi, query: { filters:['warehouse_code','bin_code'], sorts:['warehouse_code','bin_code'] }, core_fields:{ coreFields:[] } }
  const r2 = await api.post('/schemas/bin', binBody)
  console.log('Registered bin schema:', r2.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })




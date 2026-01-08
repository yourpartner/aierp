import axios from 'axios'

// 用法：
//   $env:AUTH_TOKEN=<JWT>; node web/scripts/register-openitem-schema.js http://localhost:5179
// 或 node web/scripts/register-openitem-schema.js http://localhost:5179 <JWT>
async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5179'
  const token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const api = axios.create({ baseURL })
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`
  api.defaults.headers.common['x-company-code'] = process.env.COMPANY_CODE || 'JP01'

  // openitem 仅用于查询，不支持创建/更新；schema 最小化即可
  const schema = { type:'object', properties:{} }
  const ui = {
    list: { columns: ['doc_date','partner_id','account_code','currency','original_amount','residual_amount'] },
    form: { layout: [] }
  }
  const query = {
    filters: ['doc_date','partner_id','account_code','currency','original_amount','residual_amount'],
    sorts: ['doc_date','residual_amount']
  }
  const core_fields = { coreFields: [] }

  const body = { schema, ui, query, core_fields }
  const res = await api.post('/schemas/openitem', body)
  console.log('Registered openitem schema:', res.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



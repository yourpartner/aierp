import axios from 'axios'

async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5179'
  const token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const company = process.env.COMPANY_CODE || 'JP01'
  const api = axios.create({ baseURL })
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`
  api.defaults.headers.common['x-company-code'] = company

  const schema = {
    type: 'object',
    properties: {
      code: { type: 'string', maxLength: 50 },
      name: { type: 'string', maxLength: 200 },
      parentCode: { type: ['string','null'], maxLength: 50 },
      path: { type: 'string' },
      level: { type: 'integer' },
      order: { type: 'integer' },
      status: { type: 'string', enum: ['active','inactive'], default: 'active' }
    },
    required: ['code','name']
  }

  const query = {
    filters: ['department_code','name','parentCode','path','level','order','status'],
    sorts: ['department_code','name','level','order']
  }

  const ui = {
    list: { columns: ['department_code','name','parentCode','level','order','status'] },
    form: {
      layout: [
        { type:'section', title:'基本信息', children:[
          { field:'code', label:'部门编码' },
          { field:'name', label:'部门名称' },
          { field:'parentCode', label:'上级部门' },
          { field:'status', label:'状态' }
        ]}
      ]
    }
  }

  const body = { schema, ui, query }
  const res = await api.post('/schemas/department', body)
  console.log('register department schema:', res.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



import axios from 'axios'

// 用法:
//   $env:AUTH_TOKEN=<JWT>; node web/scripts/register-material-schema.js http://localhost:5000
// 或 node web/scripts/register-material-schema.js http://localhost:5000 <JWT>
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

  const schema = {
    type: 'object',
    properties: {
      code: { type: 'string' },
      name: { type: 'string' },
      baseUom: { type: 'string', enum: ['EA','PCS','SET','BOX','PK','BAG','PAIR','ROLL','SHEET','KG','G','TON','LB','OZ','L','ML','M','CM','MM'] },
      materialType: { type: 'string', enum: ['Product','SemiProduct','RawMaterial','Consumable'] },
      status: { type: 'string', enum: ['Active','Inactive'] },
      categoryLarge: { type: ['string','null'] },
      categorySmall: { type: ['string','null'] },
      brand: { type: ['string','null'] },
      model: { type: ['string','null'] },
      batchManagement: { type: 'boolean' },
      spec: { type: 'string' },
      color: { type: ['string','null'] },
      material: { type: ['string','null'] },
      originCountry: { type: ['string','null'] },
      janCode: { type: ['string','null'], pattern: '^(\\d{8}|\\d{13})$' },
      eanCode: { type: ['string','null'], pattern: '^(\\d{8}|\\d{13})$' },
      description: { type: 'string' },
      primaryImageUrl: { type: ['string','null'], format: 'uri' },
      primaryImageBlobName: { type: ['string','null'] },
      primaryImageContentType: { type: ['string','null'] },
      primaryImageFileName: { type: ['string','null'] },
      primaryVideoUrl: { type: ['string','null'], format: 'uri' },
      primaryVideoBlobName: { type: ['string','null'] },
      primaryVideoContentType: { type: ['string','null'] },
      primaryVideoFileName: { type: ['string','null'] }
    },
    required: ['name','baseUom']
  }
  const ui = {
    list: { columns: ['material_code','name','base_uom','is_batch_mgmt'] },
    form: {
      layout: [
        { type:'grid', cols:[
          { field:'code', label:'Code', span:6, props:{ placeholder: 'auto', disabled: true } },
          { field:'name', label:'Name', span:10 },
          { field:'materialType', label:'Type', widget:'select', span:4, props:{ options:[
            { label:'Product', value:'Product' },
            { label:'Semi-Product', value:'SemiProduct' },
            { label:'Raw Material', value:'RawMaterial' },
            { label:'Consumable', value:'Consumable' }
          ]}},
          { field:'status', label:'Status', widget:'select', span:4, props:{ options:['Active','Inactive'] } }
        ]},
        { type:'grid', cols:[
          { field:'baseUom', label:'Base UoM', widget:'select', span:4, props: { options: ['EA','PCS','SET','BOX','PK','BAG','PAIR','ROLL','SHEET','KG','G','TON','LB','OZ','L','ML','M','CM','MM'] } },
          { field:'batchManagement', label:'Batch Mgmt', widget:'switch', span:4 },
          { field:'categoryLarge', label:'Category(L)', span:8 },
          { field:'categorySmall', label:'Category(S)', span:8 }
        ]},
        { type:'grid', cols:[
          { field:'brand', label:'Brand', span:6 },
          { field:'model', label:'Model', span:6 },
          { field:'spec', label:'Spec', span:12 }
        ]},
        { type:'grid', cols:[
          { field:'color', label:'Color', span:6 },
          { field:'material', label:'Material', span:6 },
          { field:'originCountry', label:'Origin Country', span:6 },
          { field:'janCode', label:'JAN Code', span:3 },
          { field:'eanCode', label:'EAN Code', span:3 }
        ]},
        { type:'grid', cols:[
          { field:'description', label:'Description', widget:'textarea', props:{ type:'textarea', rows:3 }, span:24 }
        ]}
      ]
    }
  }
  const query = { filters: ['material_code','name','is_batch_mgmt'], sorts: ['material_code','name'] }
  const core_fields = { coreFields: [] }

  const body = { schema, ui, query, core_fields }
  const res = await api.post('/schemas/material', body)
  console.log('Registered material schema:', res.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



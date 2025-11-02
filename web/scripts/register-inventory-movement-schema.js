import axios from 'axios'

// Usage:
//   $env:AUTH_TOKEN=<JWT>; node web/scripts/register-inventory-movement-schema.js http://localhost:5000
// or node web/scripts/register-inventory-movement-schema.js http://localhost:5000 <JWT>
// If no token provided, will try default login admin/admin123 on JP01.
async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5000'
  let token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const company = process.env.COMPANY_CODE || 'JP01'

  const api = axios.create({ baseURL })
  api.defaults.headers.common['x-company-code'] = company

  if (!token) {
    const emp = process.env.LOGIN_EMPLOYEE || 'admin'
    const pwd = process.env.LOGIN_PASSWORD || 'admin123'
    const r = await api.post('/auth/login', { companyCode: company, employeeCode: emp, password: pwd })
    token = r.data?.token || ''
  }
  if (!token) throw new Error('No AUTH_TOKEN and login failed')
  api.defaults.headers.common['Authorization'] = `Bearer ${token}`

  const schema = {
    type: 'object',
    properties: {
      movementType: { type: 'string', enum: ['IN','OUT','TRANSFER'] },
      movementDate: { type: 'string', format: 'date' },
      fromWarehouse: { type: ['string','null'] },
      fromBin: { type: ['string','null'] },
      toWarehouse: { type: ['string','null'] },
      toBin: { type: ['string','null'] },
      referenceNo: { type: ['string','null'] },
      lines: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            lineNo: { type: 'integer' },
            materialCode: { type: 'string' },
            quantity: { type: 'number' },
            uom: { type: 'string' },
            batchNo: { type: ['string','null'] },
            statusCode: { type: ['string','null'] }
          }
        }
      }
    }
  }

  const ui = {
    list: { columns: ['movement_date','movement_type','reference_no'] },
    form: {
      layout: [
        { type: 'grid', cols: [
          { field: 'movementType', label: 'Type', span: 6, widget: 'select', props: { options: [
            { label: 'IN', value: 'IN' }, { label: 'OUT', value: 'OUT' }, { label: 'TRANSFER', value: 'TRANSFER' }
          ] } },
          { field: 'movementDate', label: 'Date', span: 6, widget: 'date', props: { 'value-format':'YYYY-MM-DD' } }
        ]},
        { type: 'grid', cols: [
          { field: 'fromWarehouse', label: 'From Wh', span: 6, widget:'select', props: { optionsUrl:'/inventory/warehouses', optionValue:'warehouse_code', optionLabel:'{name}({warehouse_code})' }, visibleWhen: { field: 'movementType', in: ['OUT','TRANSFER'] } },
          { field: 'fromBin', label: 'From Bin', span: 6, widget:'select', props: { optionsUrl:'/inventory/bins', optionValue:'bin_code', optionLabel:'{name}({bin_code})', filterBy: { field:'warehouse_code', equalsField:'fromWarehouse' } }, visibleWhen: { field: 'movementType', in: ['OUT','TRANSFER'] } },
          { field: 'toWarehouse', label: 'To Wh', span: 6, widget:'select', props: { optionsUrl:'/inventory/warehouses', optionValue:'warehouse_code', optionLabel:'{name}({warehouse_code})' }, visibleWhen: { field: 'movementType', in: ['IN','TRANSFER'] } },
          { field: 'toBin', label: 'To Bin', span: 6, widget:'select', props: { optionsUrl:'/inventory/bins', optionValue:'bin_code', optionLabel:'{name}({bin_code})', filterBy: { field:'warehouse_code', equalsField:'toWarehouse' } }, visibleWhen: { field: 'movementType', in: ['IN','TRANSFER'] } },
          { field: 'referenceNo', label: 'Ref No', span: 8 }
        ]},
        { type: 'grid', cols: [
          { field: 'lines', label: 'Lines', span: 24, props: { columns: [
            { field: 'lineNo', label: '#', inputType: 'number', width: 80 },
            { field: 'materialCode', label: 'Material', widget:'select', props: { optionsUrl:'/inventory/materials', optionValue:'material_code', optionLabel:'{name}({material_code})' } },
            { field: 'quantity', label: 'Qty', inputType: 'number' },
            { field: 'uom', label: 'UoM' },
            { field: 'batchNo', label: 'Batch' },
            { field: 'statusCode', label: 'Status' }
          ] } }
        ]}
      ]
    }
  }

  const query = { filters: ['movement_date','movement_type','reference_no'], sorts: ['movement_date'] }
  const core_fields = { coreFields: [] }

  const body = { schema, ui, query, core_fields }
  const res = await api.post('/schemas/inventory_movement', body)
  console.log('Registered inventory_movement schema:', res.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



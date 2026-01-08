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
            uom: { type: 'string', enum: ['個','箱','kg','g','L','mL','m','cm','枚','本','台','セット'] },
            batchNo: { type: ['string','null'] },
            statusCode: { type: ['string','null'], enum: ['','GOOD','HOLD','DAMAGE','EXPIRED','QC'] }
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
          { field: 'movementType', label: '移動タイプ', span: 8, widget: 'select', props: { options: [
            { label: '入庫 (IN)', value: 'IN' }, { label: '出庫 (OUT)', value: 'OUT' }, { label: '移動 (TRANSFER)', value: 'TRANSFER' }
          ] } },
          { field: 'movementDate', label: '移動日', span: 8, widget: 'date', props: { 'value-format':'YYYY-MM-DD' } },
          { field: 'referenceNo', label: '参照番号', span: 8 }
        ]},
        { type: 'grid', cols: [
          { field: 'fromWarehouse', label: '出庫倉庫', span: 12, widget:'select', props: { optionsUrl:'/inventory/warehouses', optionValue:'warehouse_code', optionLabel:'{name} ({warehouse_code})', placeholder: '倉庫を選択' }, visibleWhen: { field: 'movementType', in: ['OUT','TRANSFER'] } },
          { field: 'fromBin', label: '出庫棚番', span: 12, widget:'select', props: { optionsUrl:'/inventory/bins', optionValue:'bin_code', optionLabel:'{name} ({bin_code})', filterBy: { field:'warehouse_code', equalsField:'fromWarehouse' }, placeholder: '棚番を選択' }, visibleWhen: { field: 'movementType', in: ['OUT','TRANSFER'] } }
        ]},
        { type: 'grid', cols: [
          { field: 'toWarehouse', label: '入庫倉庫', span: 12, widget:'select', props: { optionsUrl:'/inventory/warehouses', optionValue:'warehouse_code', optionLabel:'{name} ({warehouse_code})', placeholder: '倉庫を選択' }, visibleWhen: { field: 'movementType', in: ['IN','TRANSFER'] } },
          { field: 'toBin', label: '入庫棚番', span: 12, widget:'select', props: { optionsUrl:'/inventory/bins', optionValue:'bin_code', optionLabel:'{name} ({bin_code})', filterBy: { field:'warehouse_code', equalsField:'toWarehouse' }, placeholder: '棚番を選択' }, visibleWhen: { field: 'movementType', in: ['IN','TRANSFER'] } }
        ]},
        { type: 'grid', cols: [
          { field: 'lines', label: '明細', span: 24, props: { 
            addRowText: '行を追加',
            autoLineNo: true,
            columns: [
              { field: 'lineNo', label: '明細番号', inputType: 'number', width: 80, props: { disabled: true } },
              { field: 'materialCode', label: '品目コード', width: 280, widget:'select', props: { optionsUrl:'/inventory/materials', optionValue:'material_code', optionLabel:'{name} ({material_code})', placeholder: '品目を選択' } },
              { field: 'quantity', label: '数量', inputType: 'number', width: 100 },
              { field: 'uom', label: '単位', width: 100, widget: 'select', props: { options: [
                { label: '個', value: '個' },
                { label: '箱', value: '箱' },
                { label: 'kg', value: 'kg' },
                { label: 'g', value: 'g' },
                { label: 'L', value: 'L' },
                { label: 'mL', value: 'mL' },
                { label: 'm', value: 'm' },
                { label: 'cm', value: 'cm' },
                { label: '枚', value: '枚' },
                { label: '本', value: '本' },
                { label: '台', value: '台' },
                { label: 'セット', value: 'セット' }
              ], placeholder: '単位を選択' } },
              { field: 'batchNo', label: 'ロット番号', width: 120 },
              { field: 'statusCode', label: 'ステータスコード', width: 140, widget: 'select', props: { options: [
                { label: '-', value: '' },
                { label: '良品 (GOOD)', value: 'GOOD' },
                { label: '保留 (HOLD)', value: 'HOLD' },
                { label: '破損 (DAMAGE)', value: 'DAMAGE' },
                { label: '期限切れ (EXPIRED)', value: 'EXPIRED' },
                { label: '検査中 (QC)', value: 'QC' }
              ], placeholder: 'ステータスを選択' } }
            ] 
          } }
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

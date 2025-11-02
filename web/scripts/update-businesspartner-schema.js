import axios from 'axios'

// 用法：node web/scripts/update-businesspartner-schema.js http://localhost:5179 <JWT_TOKEN>
// 或者将 JWT_TOKEN 写入环境变量 AUTH_TOKEN
async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5179'
  const token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const api = axios.create({ baseURL })
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`
  api.defaults.headers.common['x-company-code'] = process.env.COMPANY_CODE || 'JP01'

  const schema = {
    type: 'object',
    properties: {
      code: { type: 'string', maxLength: 50 },
      name: { type: 'string', maxLength: 300 },
      shortName: { type: 'string', maxLength: 100 },
      industry: { type: ['string','null'], maxLength: 100 },
      location: { type: ['string','null'], maxLength: 200 },
      ownerUserId: { type: ['string','null'], maxLength: 64 },
      postalCode: { type: ['string','null'], pattern: '^\\d{3}-\\d{4}$' },
      address: { type: ['string','null'], maxLength: 500 },
      paymentTerms: { type: 'string', maxLength: 200 },
      invoiceRegistrationNumber: { type: ['string','null'], pattern: '^[Tt]\\d{13}$' },
      phone: { type: ['string','null'], pattern: '^(0\\d{1,4}-\\d{1,4}-\\d{4}|0(?:70|80|90)-\\d{4}-\\d{4})$' },
      fax: { type: ['string','null'], pattern: '^(0\\d{1,4}-\\d{1,4}-\\d{4}|0(?:70|80|90)-\\d{4}-\\d{4})$' },
      flags: {
        type: 'object',
        properties: {
          customer: { type: 'boolean' },
          vendor: { type: 'boolean' }
        },
        required: []
      },
      bankInfo: {
        type: ['object','null'],
        properties: {
          bankCode: { type: ['string','null'] },
          bankName: { type: ['string','null'] },
          branchCode: { type: ['string','null'] },
          branchName: { type: ['string','null'] },
          accountType: { type: ['string','null'], enum: ['Futsu','Toza','Chochiku', null] },
          accountNo: { type: ['string','null'], pattern: '^\\d{1,10}$' },
          holder: { type: ['string','null'], pattern: '^[\\uFF65-\\uFF9F\\s]+$' },
          currency: { type: ['string','null'], enum: ['JPY','USD','CNY', null] }
        }
      },
      status: { type: ['string','null'], maxLength: 50 }
    },
    // code 将由数据库生成或后续流程生成，这里不再要求用户输入
    required: ['name','shortName','paymentTerms']
  }

  const ui = {
    list: { columns: ['partner_code','name','flag_customer','flag_vendor','status'] },
    form: {
      layout: [
        // 基本信息：名称，略称，支付条件，インボイス番号，客户Flag，供应商Flag
        { type: 'section', title: '基本信息', layout: [
          { type: 'grid', cols: [
            { label: '名称', field: 'name', widget: 'input', span: 12, props: { maxlength: 300, showWordLimit: true } },
            { label: '略称', field: 'shortName', widget: 'input', span: 12, props: { maxlength: 100, showWordLimit: true } },
            { label: '支付条件', field: 'paymentTerms', widget: 'input', span: 12, props: { maxlength: 200, showWordLimit: true } },
            { label: 'インボイス番号', field: 'invoiceRegistrationNumber', widget: 'input', span: 12, props: { placeholder: 'T1234567890123' } },
            { label: '行业', field: 'industry', widget: 'input', span: 8 },
            { label: '所在地', field: 'location', widget: 'input', span: 8 },
            { label: '负责人(UserId)', field: 'ownerUserId', widget: 'input', span: 8 },
            { label: '客户', field: 'flags.customer', widget: 'switch', span: 6 },
            { label: '供应商', field: 'flags.vendor', widget: 'switch', span: 6 }
          ]}
        ]},
        // 联系信息：邮编，地址，电话，FAX
        { type: 'section', title: '联系信息', layout: [
          { type: 'grid', cols: [
            { label: '邮编', field: 'postalCode', widget: 'input', span: 6, props: { placeholder: '123-4567' } },
            { label: '地址', field: 'address', widget: 'textarea', span: 18, props: { rows: 2, maxlength: 500, showWordLimit: true } },
            { label: '电话', field: 'phone', widget: 'input', span: 12, props: { placeholder: '03-1234-5678' } },
            { label: 'FAX', field: 'fax', widget: 'input', span: 12, props: { placeholder: '03-1234-5678' } }
          ]}
        ]},
        { type: 'section', title: '银行账户', layout: [
          { type: 'grid', cols: [
            { label: '选择银行', widget: 'button', span: 4, props: { action: 'openBankPicker', type: 'primary' } },
            { label: '银行名称', field: 'bankInfo.bankName', widget: 'text', span: 8 },
            { label: '选择支店', widget: 'button', span: 4, props: { action: 'openBranchPicker', type: 'default' } },
            { label: '支店名称', field: 'bankInfo.branchName', widget: 'text', span: 8 },
            { label: '账户种别', field: 'bankInfo.accountType', widget: 'select', span: 6, props: { options: [
              { label:'普通(Futsu)', value:'Futsu' },{ label:'当座(Toza)', value:'Toza' },{ label:'储蓄(Chochiku)', value:'Chochiku' } ] } },
            { label: '账户号', field: 'bankInfo.accountNo', widget: 'input', span: 6, props: { maxlength: 10 } },
            { label: '名义人(半角カナ)', field: 'bankInfo.holder', widget: 'input', span: 6, props: { filter: 'katakana-half' } },
            { label: '币种', field: 'bankInfo.currency', widget: 'select', span: 6, props: { options: ['JPY','USD','CNY'] } }
          ]}
        ]}
      ]
    }
  }

  const query = {
    filters: ['partner_code','name','flag_customer','flag_vendor','status','phone','invoiceRegistrationNumber'],
    sorts: ['partner_code','name']
  }

  const core_fields = {
    coreFields: [
      { name:'partner_code', path:'code', type:'string', index:{ strategy:'generated_column', unique:true, scope:['company_code'] } },
      { name:'name', path:'name', type:'string', index:{ strategy:'generated_column' } },
      { name:'flag_customer', path:'flags.customer', type:'boolean', index:{ strategy:'generated_column' } },
      { name:'flag_vendor', path:'flags.vendor', type:'boolean', index:{ strategy:'generated_column' } },
      { name:'status', path:'status', type:'string', index:{ strategy:'generated_column' } }
    ]
  }

  const body = { schema, ui, query, core_fields }
  const res = await api.post('/schemas/businesspartner', body)
  console.log('Updated businesspartner schema:', res.status)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })

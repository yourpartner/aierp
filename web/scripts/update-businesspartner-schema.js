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
      labelWidth: '120px',
      layout: [
        // 基本情報
        { type: 'section', title: '基本情報', layout: [
          { type: 'grid', cols: [
            { label: '取引先名', field: 'name', widget: 'input', span: 16, props: { maxlength: 300, showWordLimit: true, placeholder: '正式名称を入力' } },
            { label: '略称', field: 'shortName', widget: 'input', span: 8, props: { maxlength: 100, showWordLimit: true } }
          ]},
          { type: 'grid', cols: [
            { label: '支払条件', field: 'paymentTerms', widget: 'input', span: 12, props: { maxlength: 200, showWordLimit: true, placeholder: '例：月末締め翌月末払い' } },
            { label: 'インボイス番号', field: 'invoiceRegistrationNumber', widget: 'input', span: 12, props: { placeholder: 'T1234567890123' } }
          ]},
          { type: 'grid', cols: [
            { label: '顧客', field: 'flags.customer', widget: 'switch', span: 4, props: { activeText: 'はい', inactiveText: 'いいえ' } },
            { label: '仕入先', field: 'flags.vendor', widget: 'switch', span: 4, props: { activeText: 'はい', inactiveText: 'いいえ' } },
            { label: '業種', field: 'industry', widget: 'input', span: 8, props: { placeholder: '例：製造業' } },
            { label: '担当者ID', field: 'ownerUserId', widget: 'input', span: 8 }
          ]}
        ]},
        // 連絡先情報
        { type: 'section', title: '連絡先情報', layout: [
          { type: 'grid', cols: [
            { label: '郵便番号', field: 'postalCode', widget: 'input', span: 6, props: { placeholder: '123-4567' } },
            { label: '住所', field: 'address', widget: 'input', span: 18, props: { maxlength: 500, showWordLimit: true, placeholder: '都道府県から入力' } }
          ]},
          { type: 'grid', cols: [
            { label: '電話番号', field: 'phone', widget: 'input', span: 12, props: { placeholder: '03-1234-5678' } },
            { label: 'FAX番号', field: 'fax', widget: 'input', span: 12, props: { placeholder: '03-1234-5678' } }
          ]}
        ]},
        // 振込先口座情報
        { type: 'section', title: '振込先口座情報', layout: [
          { type: 'grid', cols: [
            { label: '金融機関', widget: 'button', span: 6, props: { action: 'openBankPicker', type: 'primary', text: '銀行を選択' } },
            { label: '', field: 'bankInfo.bankName', widget: 'text', span: 6 },
            { label: '支店', widget: 'button', span: 6, props: { action: 'openBranchPicker', type: 'default', text: '支店を選択' } },
            { label: '', field: 'bankInfo.branchName', widget: 'text', span: 6 }
          ]},
          { type: 'grid', cols: [
            { label: '口座種別', field: 'bankInfo.accountType', widget: 'select', span: 6, props: { placeholder: '選択', options: [
              { label:'普通', value:'Futsu' },{ label:'当座', value:'Toza' },{ label:'貯蓄', value:'Chochiku' } ] } },
            { label: '口座番号', field: 'bankInfo.accountNo', widget: 'input', span: 6, props: { maxlength: 10, placeholder: '1234567' } },
            { label: '口座名義（ｶﾅ）', field: 'bankInfo.holder', widget: 'input', span: 6, props: { filter: 'katakana-half', placeholder: 'ｶﾌﾞｼｷｶﾞｲｼｬ' } },
            { label: '通貨', field: 'bankInfo.currency', widget: 'select', span: 6, props: { placeholder: '選択', options: [
              { label:'日本円 (JPY)', value:'JPY' },{ label:'米ドル (USD)', value:'USD' },{ label:'人民元 (CNY)', value:'CNY' } ] } }
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

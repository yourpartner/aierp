import axios from 'axios'

// 用法：
//   $env:AUTH_TOKEN=<JWT>; node web/scripts/register-crm-schemas.js http://localhost:5179
//   或 node web/scripts/register-crm-schemas.js http://localhost:5179 <JWT>
async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5179'
  const token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const company = process.env.COMPANY_CODE || 'JP01'
  const api = axios.create({ baseURL })
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`
  api.defaults.headers.common['x-company-code'] = company

  async function post(name, payload){
    const res = await api.post(`/schemas/${name}`, payload)
    console.log('registered', name, 'version=', res.data?.version)
  }

  // 1) contact
  const contact = {
    schema: {
      type:'object',
      properties:{
        code:{ type:'string' },
        name:{ type:'string' },
        partnerCode:{ type:'string' },
        email:{ type:['string','null'], format:'email' },
        mobile:{ type:['string','null'] },
        language:{ type:['string','null'], enum:['zh','ja','en', null] },
        status:{ type:['string','null'], enum:['active','inactive', null] }
      },
      required:['name','partnerCode']
    },
    ui: {
      list: { columns: ['contact_code','name','partner_code','email','status'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'name', label:'姓名', span:8 },
          { field:'partnerCode', label:'合作伙伴编码', span:8 },
          { field:'email', label:'邮箱', span:8 },
          { field:'mobile', label:'手机', span:8 },
          { field:'language', label:'语言', widget:'select', props:{ options:['zh','ja','en'] }, span:8 },
          { field:'status', label:'状态', widget:'select', props:{ options:['active','inactive'] }, span:8 }
        ]}
      ] }
    },
    query: { filters:['contact_code','name','partner_code','email','status'], sorts:['name'] },
    core_fields: { coreFields: [] }
  }

  // 2) deal
  const deal = {
    schema: {
      type:'object',
      properties:{
        code:{ type:'string' },
        partnerCode:{ type:'string' },
        ownerUserId:{ type:['string','null'] },
        stage:{ type:'string', enum:['prospect','proposal','won','invoiced'] },
        expectedAmount:{ type:['number','null'] },
        expectedCloseDate:{ type:['string','null'], format:'date' },
        source:{ type:['string','null'], enum:['referral','website','expo', null] },
        status:{ type:['string','null'] }
      },
      required:['code','partnerCode','stage']
    },
    ui: {
      list: { columns:['deal_code','partner_code','stage','expected_amount','expected_close_date','source'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'code', label:'商谈编号', span:6 },
          { field:'partnerCode', label:'合作伙伴', span:8 },
          { field:'ownerUserId', label:'负责人', span:6 },
          { field:'stage', label:'阶段', widget:'select', props:{ options:[
            {label:'潜在', value:'prospect'}, {label:'提案', value:'proposal'}, {label:'成约', value:'won'}, {label:'开票', value:'invoiced'}
          ] }, span:8 },
          { field:'expectedAmount', label:'预计金额', span:6 },
          { field:'expectedCloseDate', label:'预计成约日', widget:'date', span:6 },
          { field:'source', label:'来源', widget:'select', props:{ options:[
            {label:'介绍', value:'referral'}, {label:'网站', value:'website'}, {label:'展会', value:'expo'}
          ] }, span:6 }
        ]}
      ] }
    },
    query: { filters:['deal_code','partner_code','stage','owner_user_id','expected_close_date','source'], sorts:['expected_close_date'] },
    core_fields: { coreFields: [] }
  }

  // 3) quote
  const quote = {
    schema: {
      type:'object',
      properties:{
        quoteNo:{ type:'string' },
        partnerCode:{ type:'string' },
        lines:{ type:'array', items:{ type:'object', properties:{
          lineNo:{ type:'integer' },
          item:{ type:'string' },
          qty:{ type:'number' },
          uom:{ type:['string','null'] },
          price:{ type:'number' },
          amount:{ type:'number' }
        }, required:['lineNo','item','qty','price'] } },
        amountTotal:{ type:'number' },
        validUntil:{ type:['string','null'], format:'date' },
        status:{ type:'string', enum:['draft','sent','accepted','rejected'] }
      },
      required:['quoteNo','partnerCode','amountTotal','status']
    },
    ui: {
      list: { columns:['quote_no','partner_code','amount_total','valid_until','status'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'quoteNo', label:'报价编号', span:8 },
          { field:'partnerCode', label:'合作伙伴', span:8 },
          { field:'validUntil', label:'有效期至', widget:'date', span:8 },
          { field:'status', label:'状态', widget:'select', props:{ options:['draft','sent','accepted','rejected'] }, span:8 }
        ]},
        { type:'section', title:'明细', layout:[
          { type:'table', field:'lines', columns:[
            { field:'lineNo', label:'行号', width:80 },
            { field:'item', label:'项目', width:220 },
            { field:'qty', label:'数量', width:120 },
            { field:'uom', label:'单位', width:120 },
            { field:'price', label:'单价', width:120 },
            { field:'amount', label:'金额', width:140 }
          ]}
        ]}
      ] }
    },
    query: { filters:['quote_no','partner_code','status'], sorts:['quote_no'] },
    core_fields: { coreFields: [] }
  }

  // 4) sales_order
  const so = {
    schema: {
      type:'object',
      properties:{
        soNo:{ type:'string' },
        partnerCode:{ type:'string' },
        customerPhone:{ type:['string','null'] },
        customerEmail:{ type:['string','null'], format:'email' },
        customerAddress:{ type:['string','null'] },
        customerNote:{ type:['string','null'] },
        orderDate:{ type:'string', format:'date' },
        lines:{ type:'array', items:{ type:'object', properties:{
          lineNo:{ type:'integer' },
          item:{ type:'string' },
          qty:{ type:'number' },
          uom:{ type:['string','null'] },
          price:{ type:'number' },
          amount:{ type:'number' }
        }, required:['lineNo','item','qty','price'] } },
        amountTotal:{ type:'number' },
        status:{ type:'string', enum:['draft','confirmed','invoiced','cancelled'] },
        orderChannel:{ type:['string','null'] },
        orderOriginalNotes:{ type:['string','null'] },
        attachments:{ type:'array', items:{ type:'object', properties:{
          url:{ type:'string', format:'uri' },
          blobName:{ type:'string' },
          contentType:{ type:'string' },
          name:{ type:'string' },
          size:{ type:'number' }
        }}}
      },
      required:['soNo','partnerCode','orderDate','amountTotal','status']
    },
    ui: {
      list: { columns:['so_no','partner_code','amount_total','order_date','status'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'soNo', label:'受注编号', span:8 },
          { field:'partnerCode', label:'合作伙伴', span:8 },
          { field:'orderDate', label:'下单日期', widget:'date', span:8 },
          { field:'status', label:'状态', widget:'select', props:{ options:['draft','confirmed','invoiced','cancelled'] }, span:8 }
        ]},
        { type:'section', title:'明细', layout:[
          { type:'table', field:'lines', columns:[
            { field:'lineNo', label:'行号', width:80 },
            { field:'item', label:'项目', width:220 },
            { field:'qty', label:'数量', width:120 },
            { field:'uom', label:'单位', width:120 },
            { field:'price', label:'单价', width:120 },
            { field:'amount', label:'金额', width:140 }
          ]}
        ]}
      ] }
    },
    query: { filters:['so_no','partner_code','status','order_date'], sorts:['order_date'] },
    core_fields: { coreFields: [] }
  }

  // 6) delivery_note
  const dn = {
    schema: {
      type:'object',
      properties: {
        deliveryNo: { type:'string' },
        salesOrderNo: { type:'string' },
        salesOrderId: { type:'string' },
        customerCode: { type:'string' },
        customerName: { type:['string','null'] },
        deliveryDate: { type:'string', format:'date' },
        printStatus: { type:'string', enum:['pending','printed','failed'] },
        items: {
          type:'array',
          items: {
            type:'object',
            properties: {
              lineNo: { type:'integer' },
              materialCode: { type:['string','null'] },
              materialName: { type:['string','null'] },
              qty: { type:'number' },
              uom: { type:['string','null'] }
            },
            required:['lineNo','qty']
          }
        }
      },
      required:['deliveryNo','salesOrderNo','deliveryDate','printStatus','items']
    },
    ui: {
      list: { columns:['delivery_no','sales_order_no','customer_code','delivery_date','print_status'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'deliveryNo', label:'納品書番号', span:8 },
          { field:'salesOrderNo', label:'受注番号', span:8 },
          { field:'customerCode', label:'得意先', span:8 },
          { field:'customerName', label:'得意先名', span:8 },
          { field:'deliveryDate', label:'納品日', widget:'date', span:8 },
          { field:'printStatus', label:'印刷ステータス', widget:'select', props:{ options:['pending','printed','failed'] }, span:8 }
        ]},
        { type:'section', title:'納品明細', layout:[
          { type:'table', field:'items', columns:[
            { field:'lineNo', label:'行', width:80 },
            { field:'materialCode', label:'商品コード', width:160 },
            { field:'materialName', label:'商品名', width:220 },
            { field:'qty', label:'数量', width:120 },
            { field:'uom', label:'単位', width:120 }
          ]}
        ]}
      ] }
    },
    query: { filters:['delivery_no','sales_order_no','customer_code','print_status'], sorts:['delivery_no'] },
    core_fields: { coreFields: [] }
  }

  // 5) activity
  const activity = {
    schema: {
      type:'object',
      properties:{
        type:{ type:'string', enum:['email','phone','meeting','task'] },
        subject:{ type:['string','null'] },
        partnerCode:{ type:['string','null'] },
        contactCode:{ type:['string','null'] },
        ownerUserId:{ type:['string','null'] },
        dueDate:{ type:['string','null'], format:'date' },
        remindAt:{ type:['number','null'] },
        content:{ type:['string','null'] },
        status:{ type:['string','null'], enum:['open','done','cancelled', null] }
      },
      required:['type']
    },
    ui: {
      list: { columns:['activity_type','subject','partner_code','contact_code','due_date','status'] },
      form: { layout:[
        { type:'grid', cols:[
          { field:'type', label:'类型', widget:'select', props:{ options:['email','phone','meeting','task'] }, span:6 },
          { field:'subject', label:'主题', span:10 },
          { field:'partnerCode', label:'合作伙伴', span:8 },
          { field:'contactCode', label:'联系人', span:8 },
          { field:'ownerUserId', label:'负责人', span:8 },
          { field:'dueDate', label:'到期日期', widget:'date', span:6 },
          { field:'remindAt', label:'提醒时间(Unix秒)', span:8 },
          { field:'status', label:'状态', widget:'select', props:{ options:['open','done','cancelled'] }, span:6 }
        ]},
        { type:'section', title:'内容', layout:[
          { type:'grid', cols:[ { field:'content', label:'内容', widget:'textarea', props:{ rows:6 }, span:24 } ] }
        ]}
      ] }
    },
    query: { filters:['activity_type','due_date','owner_user_id','partner_code','status'], sorts:['due_date'] },
    core_fields: { coreFields: [] }
  }

  await post('contact', contact)
  await post('deal', deal)
  await post('quote', quote)
  await post('sales_order', so)
  await post('delivery_note', dn)
  await post('activity', activity)
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



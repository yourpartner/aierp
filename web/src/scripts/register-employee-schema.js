import axios from 'axios'

async function main(){
  const baseURL = process.argv[2] || 'http://localhost:5179'
  const token = process.argv[3] || process.env.AUTH_TOKEN || ''
  const company = process.env.COMPANY_CODE || 'JP01'
  const api = axios.create({ baseURL })
  if (token) api.defaults.headers.common['Authorization'] = `Bearer ${token}`
  api.defaults.headers.common['x-company-code'] = company

  const schema = {
    type:'object',
    properties:{
      code:{ type:'string' },
      nameKana:{ type:'string', maxLength:200 },
      nameKanji:{ type:'string', maxLength:200 },
      gender:{ type:'string', enum:['M','F','O'] },
      birthDate:{ type:'string', format:'date' },
      nationality:{ type:'string', maxLength:100 },
      myNumber:{ type:'string', maxLength:12 },
      contact:{ type:'object', properties:{ phone:{type:'string'}, email:{type:'string', format:'email'}, postalCode:{type:'string'}, address:{type:'string', maxLength:500}, note:{type:'string', maxLength:1000} } },
      emergencies:{ type:'array', items:{ type:'object', properties:{ nameKana:{type:'string'}, nameKanji:{type:'string'}, relation:{type:'string'}, phone:{type:'string'}, address:{type:'string'} } } },
      contracts:{ type:'array', items:{ type:'object', properties:{ contractType:{type:'string'}, periodFrom:{type:'string', format:'date'}, periodTo:{type:['string','null'], format:'date'}, note:{type:'string'} } } },
      departments:{ type:'array', items:{ type:'object', properties:{ departmentId:{type:'string', format:'uuid'}, fromDate:{type:'string', format:'date'}, toDate:{type:['string','null'], format:'date'}, position:{type:'string'} } } },
      bankAccounts:{ type:'array', items:{ type:'object', properties:{ bank:{type:'string'}, branch:{type:'string'}, accountType:{type:'string'}, accountNo:{type:'string'}, holder:{type:'string'}, effectiveDate:{type:'string', format:'date'} } } },
      attachments:{ type:'array', items:{ type:'object', properties:{ fileId:{type:'string', format:'uuid'}, kind:{type:'string'}, sensitivity:{type:'string', enum:['HR','Payroll']}, uploadedAt:{type:'string', format:'date-time'}, note:{type:'string'} } } }
    },
    required:['code','nameKanji']
  }

  const query = {
    filters: ['employee_code','nameKana','nameKanji','primary_department_id'],
    sorts: ['employee_code','nameKanji']
  }

  const ui = {
    list:{ columns:['employee_code','nameKanji','nameKana','primary_department_id'] },
    form:{ layout:[
      { type:'tabs', items:[
        { title:'基本', children:[{ field:'nameKanji', label:'姓名（汉字）' },{ field:'nameKana', label:'姓名（假名）' },{ field:'gender', label:'性别' },{ field:'birthDate', label:'生年月日' },{ field:'nationality', label:'国籍' },{ field:'myNumber', label:'个人番号' }]},
        { title:'联络', children:[{ field:'contact.phone', label:'电话' },{ field:'contact.email', label:'邮箱' },{ field:'contact.postalCode', label:'邮编' },{ field:'contact.address', label:'住所' },{ field:'contact.note', label:'备注' }]},
        { title:'紧急联络', children:[{ field:'emergencies', label:'紧急联络人' }]},
        { title:'雇佣契约', children:[{ field:'contracts', label:'契约' }]},
        { title:'所属部门', children:[{ field:'departments', label:'所属' }]},
        { title:'银行账户', children:[{ field:'bankAccounts', label:'银行账户' }]},
        { title:'附件', children:[{ field:'attachments', label:'附件' }]}
      ]}
    ]}
  }

  const body = { schema, ui, query }
  const res = await api.post('/schemas/employee', body)
  console.log('register employee schema:', res.status)

  // 顺带注册 timesheet schema（最小可用），方便前端直接使用
  const tsSchema = {
    type:'object',
    properties:{
      date:{ type:'string', format:'date' },
      startTime:{ type:'string', pattern:'^\\d{2}:\\d{2}$' },
      endTime:{ type:'string', pattern:'^\\d{2}:\\d{2}$' },
      lunchMinutes:{ type:'number', minimum:0, maximum:240 },
      projectCode:{ type:'string', maxLength:100 },
      task:{ type:'string', maxLength:200 },
      hours:{ type:'number', minimum:0, maximum:24 },
      overtime:{ type:'number', minimum:0, maximum:24 },
      status:{ type:'string', enum:['draft','submitted','approved','rejected'] },
      // 由后端注入
      creatorUserId:{ type:'string', readOnly:true },
      createdMonth:{ type:'string', readOnly:true }
    },
    required:['date','hours']
  }
  const tsQuery = { filters:['timesheet_date','status','month','project_code','created_by'], sorts:['timesheet_date','created_at'] }
  const tsUi = {
    list:{ columns:['timesheet_date','project_code','hours','status','created_at'] },
    form:{ layout:[
      { type:'grid', cols:[
        { field:'date', label:'日期' },
        { field:'startTime', label:'开始(HH:mm)' },
        { field:'endTime', label:'结束(HH:mm)' },
        { field:'lunchMinutes', label:'午休(分钟)' }
      ]},
      { type:'grid', cols:[
        { field:'projectCode', label:'项目' },
        { field:'task', label:'任务' },
        { field:'hours', label:'工时(自动)' },
        { field:'overtime', label:'加班' }
      ]},
      { type:'grid', cols:[
        { field:'status', label:'状态' }
      ]}
    ]}
  }
  const tsAuth = {
    actions: { create:['role:employee'], read:['role:employee','role:manager'], update:['role:employee'], delete:['role:manager'] },
    scopes: {
      default: [{ field:'created_by', op:'eq_user', value:'id' }],
      byRole: { manager: [] }
    }
  }
  await api.post('/schemas/timesheet', { schema: tsSchema, ui: tsUi, query: tsQuery, auth: tsAuth })
  console.log('register timesheet schema: ok')

  // 注册 certificate_request schema（含简单 UI 占位与查询白名单；approval 块后续由 /ai/approvals/compile 产出）
  const certSchema = {
    type: 'object',
    properties: {
      employeeId: { type: 'string' },
      type: { type: 'string' },
      language: { type: 'string' },
      purpose: { type: 'string' },
      toEmail: { type: 'string', format: 'email' },
      subject: { type: 'string' },
      bodyText: { type: 'string' },
      status: { type: 'string', enum: ['pending','approved','rejected'] }
    },
    required: ['employeeId','type']
  }
  const certUi = {
    list: { columns: ['created_at','status'] },
    form: { layout: [
      { type:'grid', cols:[
        { field:'employeeId', label:'员工ID/编码' },
        { field:'type', label:'类型' },
        { field:'language', label:'语言' },
        { field:'toEmail', label:'收件人' }
      ]},
      { field:'purpose', label:'用途', widget:'textarea', props:{ type:'textarea', rows:3 } },
      { field:'subject', label:'主题' },
      { field:'bodyText', label:'正文(用于PDF)' }
    ]}
  }
  const certQuery = { filters: ['status','created_at'], sorts: ['created_at'] }
  await api.post('/schemas/certificate_request', { schema: certSchema, ui: certUi, query: certQuery })
  console.log('register certificate_request schema: ok')
}

main().catch(e=>{ console.error(e?.response?.data || e); process.exit(1) })



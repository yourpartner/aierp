/*
  注册 inventory_count 的 schema：包含抬头和明细行
  用法：node web/scripts/register-inventory-count-schema.js [baseUrl]
  例如：node web/scripts/register-inventory-count-schema.js http://localhost:5179
*/
import axios from 'axios'

const BASE = process.argv[2] || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'
const LOGIN = { companyCode: COMPANY, employeeCode: 'admin', password: 'admin123' }

async function main() {
  // 登录获取 token
  const auth = await axios.post(BASE + '/auth/login', LOGIN).then(r => r.data).catch(() => ({}))
  const token = auth.token
  if (!token) throw new Error('Login failed')
  const headers = { 
    Authorization: 'Bearer ' + token,
    'x-company-code': COMPANY
  }

  // 定义 schema
  const schema = {
    type: 'object',
    properties: {
      header: {
        type: 'object',
        properties: {
          countNo: { type: 'string', title: '棚卸番号' },
          warehouseCode: { type: 'string', title: '倉庫' },
          countDate: { type: 'string', format: 'date', title: '棚卸日' },
          status: { 
            type: 'string', 
            enum: ['draft', 'in_progress', 'completed', 'posted', 'cancelled'],
            title: 'ステータス'
          },
          description: { type: 'string', title: '説明' },
          createdBy: { type: 'string', title: '作成者' },
          completedAt: { type: 'string', format: 'date-time', title: '完了日時' },
          completedBy: { type: 'string', title: '完了者' },
          postedAt: { type: 'string', format: 'date-time', title: '転記日時' },
          postedBy: { type: 'string', title: '転記者' }
        },
        required: ['warehouseCode', 'countDate']
      },
      lines: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            lineNo: { type: 'integer', title: '行番号' },
            materialCode: { type: 'string', title: '品目コード' },
            materialName: { type: 'string', title: '品目名' },
            binCode: { type: 'string', title: '棚番' },
            batchNo: { type: 'string', title: 'ロット番号' },
            uom: { type: 'string', title: '単位' },
            systemQty: { type: 'number', title: 'システム数量' },
            actualQty: { type: 'number', title: '実数量' },
            varianceQty: { type: 'number', title: '差異数量' },
            varianceReason: { type: 'string', title: '差異理由' },
            status: { 
              type: 'string', 
              enum: ['pending', 'counted', 'confirmed'],
              title: 'ステータス'
            }
          },
          required: ['materialCode']
        }
      }
    }
  }

  // 定义 UI
  const ui = {
    list: {
      columns: ['count_no', 'warehouse_code', 'count_date', 'status']
    },
    form: {
      layout: [
        // 抬头区域
        { type: 'grid', cols: [
          { field: 'header.countNo', label: '棚卸番号', span: 6, props: { disabled: true } },
          { field: 'header.warehouseCode', label: '倉庫', span: 6, widget: 'select', 
            props: { optionsUrl: '/inventory/warehouses', optionValue: 'warehouse_code', optionLabel: '{name} ({warehouse_code})' } },
          { field: 'header.countDate', label: '棚卸日', span: 6, widget: 'date', props: { 'value-format': 'YYYY-MM-DD' } },
          { field: 'header.status', label: 'ステータス', span: 6, widget: 'select',
            props: { options: [
              { label: '下書き', value: 'draft' },
              { label: '進行中', value: 'in_progress' },
              { label: '完了', value: 'completed' },
              { label: '転記済み', value: 'posted' },
              { label: 'キャンセル', value: 'cancelled' }
            ], disabled: true }
          }
        ]},
        { type: 'grid', cols: [
          { field: 'header.description', label: '説明', span: 24 }
        ]},
        // 明细区域
        { type: 'grid', cols: [
          { field: 'lines', label: '明細', span: 24, widget: 'table',
            props: { 
              columns: [
                { field: 'lineNo', label: '行', width: 60, inputType: 'number' },
                { field: 'materialCode', label: '品目コード', width: 120 },
                { field: 'materialName', label: '品目名', width: 180 },
                { field: 'binCode', label: '棚番', width: 80 },
                { field: 'batchNo', label: 'ロット', width: 100 },
                { field: 'uom', label: '単位', width: 60 },
                { field: 'systemQty', label: 'システム数', width: 100, inputType: 'number', props: { disabled: true } },
                { field: 'actualQty', label: '実数量', width: 100, inputType: 'number' },
                { field: 'varianceQty', label: '差異', width: 80, inputType: 'number', props: { disabled: true } },
                { field: 'varianceReason', label: '差異理由', width: 150 }
              ]
            }
          }
        ]}
      ]
    }
  }

  // 查询配置
  const query = {
    filters: ['count_no', 'warehouse_code', 'count_date', 'status'],
    sorts: ['count_date', 'count_no']
  }

  // 核心字段（用于 objects 表的核心列）
  const core_fields = {
    coreFields: [
      { source: 'header.countNo', target: 'count_no' },
      { source: 'header.warehouseCode', target: 'warehouse_code' },
      { source: 'header.countDate', target: 'count_date' },
      { source: 'header.status', target: 'status' }
    ]
  }

  // 编号规则
  const numbering = {
    enabled: true,
    prefix: 'IC',
    pattern: '{prefix}{YYYY}{MM}{0000}',
    field: 'header.countNo'
  }

  // AI 提示（可选）
  const ai_hints = {
    description: '棚卸伝票。倉庫の在庫を実地棚卸し、システム在庫との差異を記録する。',
    headerFields: ['warehouseCode', 'countDate', 'description'],
    lineFields: ['materialCode', 'actualQty', 'varianceReason']
  }

  // 注册 schema
  const payload = {
    schema,
    ui,
    query,
    core_fields,
    numbering,
    ai_hints
  }

  await axios.post(BASE + '/schemas/inventory_count', payload, { headers })
  console.log('inventory_count schema registered successfully')
}

main().catch(err => {
  console.error('Registration failed:', err?.response?.data || err.message)
  process.exit(1)
})

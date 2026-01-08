/*
  注册 delivery_note 的 schema：包含抬头和明细行
  用法：node web/scripts/register-delivery-note-schema.js [baseUrl]
  例如：node web/scripts/register-delivery-note-schema.js http://localhost:5179
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
          deliveryNo: { type: 'string', title: '納品書番号' },
          salesOrderId: { type: 'string', format: 'uuid', title: '受注ID' },
          salesOrderNo: { type: 'string', title: '受注番号' },
          customerId: { type: 'string', format: 'uuid', title: '顧客ID' },
          customerCode: { type: 'string', title: '顧客コード' },
          customerName: { type: 'string', title: '顧客名' },
          deliveryDate: { type: 'string', format: 'date', title: '納品日' },
          warehouseCode: { type: 'string', title: '出庫倉庫' },
          status: { 
            type: 'string', 
            enum: ['draft', 'confirmed', 'shipped', 'delivered', 'cancelled'],
            default: 'draft',
            title: 'ステータス'
          },
          shippingAddress: { type: 'string', title: '配送先住所' },
          shippingPostalCode: { type: 'string', title: '郵便番号' },
          contactPerson: { type: 'string', title: '担当者' },
          contactPhone: { type: 'string', title: '連絡先' },
          shippingMethod: { type: 'string', title: '配送方法' },
          trackingNo: { type: 'string', title: '追跡番号' },
          remarks: { type: 'string', title: '備考' },
          internalMemo: { type: 'string', title: '社内メモ' },
          confirmedAt: { type: 'string', format: 'date-time', title: '確認日時' },
          confirmedBy: { type: 'string', title: '確認者' },
          shippedAt: { type: 'string', format: 'date-time', title: '出荷日時' },
          shippedBy: { type: 'string', title: '出荷者' },
          deliveredAt: { type: 'string', format: 'date-time', title: '納品完了日時' },
          deliveredBy: { type: 'string', title: '納品完了者' }
        },
        required: ['deliveryDate']
      },
      lines: {
        type: 'array',
        items: {
          type: 'object',
          properties: {
            lineNo: { type: 'integer', title: '行番号' },
            salesOrderLineId: { type: 'string', format: 'uuid', title: '受注明細ID' },
            materialCode: { type: 'string', title: '品目コード' },
            materialName: { type: 'string', title: '品目名' },
            orderedQty: { type: 'number', title: '受注数量' },
            previouslyDeliveredQty: { type: 'number', default: 0, title: '既納品数量' },
            deliveryQty: { type: 'number', title: '今回納品数量' },
            uom: { type: 'string', title: '単位' },
            binCode: { type: 'string', title: '棚番' },
            batchNo: { type: 'string', title: 'ロット番号' },
            unitPrice: { type: 'number', title: '単価' },
            amount: { type: 'number', title: '金額' },
            remarks: { type: 'string', title: '備考' }
          },
          required: ['materialCode', 'deliveryQty']
        }
      }
    }
  }

  // 定义 UI
  const ui = {
    list: {
      columns: ['delivery_no', 'delivery_date', 'customer_name', 'status']
    },
    form: {
      layout: [
        // 抬头区域
        { type: 'grid', cols: [
          { field: 'header.deliveryNo', label: '納品書番号', span: 6, props: { disabled: true } },
          { field: 'header.deliveryDate', label: '納品日', span: 6, widget: 'date', props: { 'value-format': 'YYYY-MM-DD' } },
          { field: 'header.status', label: 'ステータス', span: 6, widget: 'select',
            props: { options: [
              { label: '下書き', value: 'draft' },
              { label: '確認済み', value: 'confirmed' },
              { label: '出荷済み', value: 'shipped' },
              { label: '納品完了', value: 'delivered' },
              { label: 'キャンセル', value: 'cancelled' }
            ], disabled: true }
          },
          { field: 'header.salesOrderNo', label: '受注番号', span: 6, props: { disabled: true } }
        ]},
        { type: 'grid', cols: [
          { field: 'header.customerName', label: '顧客名', span: 8, props: { disabled: true } },
          { field: 'header.warehouseCode', label: '出庫倉庫', span: 8, widget: 'select',
            props: { optionsUrl: '/inventory/warehouses', optionValue: 'warehouse_code', optionLabel: '{name} ({warehouse_code})' } },
          { field: 'header.shippingMethod', label: '配送方法', span: 8 }
        ]},
        { type: 'grid', cols: [
          { field: 'header.shippingAddress', label: '配送先住所', span: 16 },
          { field: 'header.trackingNo', label: '追跡番号', span: 8 }
        ]},
        { type: 'grid', cols: [
          { field: 'header.remarks', label: '備考', span: 24 }
        ]},
        // 明细区域
        { type: 'grid', cols: [
          { field: 'lines', label: '明細', span: 24, widget: 'table',
            props: { 
              columns: [
                { field: 'lineNo', label: '行', width: 50, inputType: 'number' },
                { field: 'materialCode', label: '品目コード', width: 120 },
                { field: 'materialName', label: '品目名', width: 180 },
                { field: 'uom', label: '単位', width: 60 },
                { field: 'orderedQty', label: '受注数', width: 80, inputType: 'number', props: { disabled: true } },
                { field: 'previouslyDeliveredQty', label: '既納品', width: 80, inputType: 'number', props: { disabled: true } },
                { field: 'deliveryQty', label: '今回納品', width: 90, inputType: 'number' },
                { field: 'binCode', label: '棚番', width: 80 },
                { field: 'batchNo', label: 'ロット', width: 100 }
              ]
            }
          }
        ]}
      ]
    }
  }

  // 查询配置
  const query = {
    filters: ['delivery_no', 'customer_code', 'delivery_date', 'status'],
    sorts: ['delivery_date', 'delivery_no']
  }

  // 核心字段（映射到 delivery_notes 表的核心列）
  const core_fields = {
    coreFields: [
      { source: 'header.deliveryNo', target: 'delivery_no' },
      { source: 'header.salesOrderNo', target: 'so_no' },
      { source: 'header.customerCode', target: 'customer_code' },
      { source: 'header.customerName', target: 'customer_name' },
      { source: 'header.deliveryDate', target: 'delivery_date' },
      { source: 'header.status', target: 'status' }
    ]
  }

  // 编号规则
  const numbering = {
    enabled: true,
    prefix: 'DN',
    pattern: '{prefix}{YYYY}{MM}{0000}',
    field: 'header.deliveryNo'
  }

  // AI 提示（可选）
  const ai_hints = {
    description: '納品書。受注に基づいて顧客へ商品を納品する伝票。出荷時に在庫が減少する。',
    headerFields: ['deliveryDate', 'warehouseCode', 'shippingAddress'],
    lineFields: ['materialCode', 'deliveryQty', 'binCode']
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

  await axios.post(BASE + '/schemas/delivery_note', payload, { headers })
  console.log('delivery_note schema registered successfully')
}

main().catch(err => {
  console.error('Registration failed:', err?.response?.data || err.message)
  process.exit(1)
})


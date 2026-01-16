-- 更新销售订单 Schema
-- 1. 从 required 中移除 soNo（后端自动生成）
-- 2. 更新 status 的 enum 值

UPDATE schemas
SET schema = '{
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "type": "object",
  "required": ["partnerCode", "amountTotal", "orderDate"],
  "properties": {
    "soNo": {
      "type": "string",
      "description": "受注番号"
    },
    "partnerCode": {
      "type": "string",
      "description": "取引先コード"
    },
    "partnerName": {
      "type": "string",
      "description": "取引先名"
    },
    "orderDate": {
      "type": "string",
      "format": "date",
      "description": "受注日"
    },
    "requestedDeliveryDate": {
      "type": "string",
      "format": "date",
      "description": "希望納期"
    },
    "currency": {
      "type": "string",
      "default": "JPY",
      "description": "通貨"
    },
    "amountTotal": {
      "type": "number",
      "description": "合計金額（税込）"
    },
    "taxAmountTotal": {
      "type": "number",
      "description": "消費税合計"
    },
    "status": {
      "type": "string",
      "enum": ["new", "partial_shipped", "shipped", "partial_invoiced", "invoiced", "completed", "cancelled"],
      "description": "ステータス"
    },
    "note": {
      "type": "string",
      "description": "備考"
    },
    "lines": {
      "type": "array",
      "description": "明細行",
      "items": {
        "type": "object",
        "required": ["lineNo", "materialCode", "quantity", "unitPrice"],
        "properties": {
          "lineNo": {
            "type": "integer",
            "description": "行番号"
          },
          "lineId": {
            "type": "string",
            "description": "行ID（UUID）"
          },
          "materialCode": {
            "type": "string",
            "description": "品目コード"
          },
          "materialName": {
            "type": "string",
            "description": "品目名"
          },
          "quantity": {
            "type": "number",
            "description": "数量"
          },
          "uom": {
            "type": "string",
            "description": "単位"
          },
          "unitPrice": {
            "type": "number",
            "description": "単価"
          },
          "amount": {
            "type": "number",
            "description": "金額（税抜）"
          },
          "taxRate": {
            "type": "number",
            "description": "消費税率（%）",
            "enum": [0, 8, 10],
            "default": 10
          },
          "taxAmount": {
            "type": "number",
            "description": "消費税額"
          },
          "amountWithTax": {
            "type": "number",
            "description": "金額（税込）"
          }
        }
      }
    },
    "customer": {
      "type": "object",
      "description": "顧客詳細情報"
    },
    "shipTo": {
      "type": "object",
      "description": "納品先情報"
    }
  }
}'::jsonb,
    ui = '{
  "list": {
    "columns": ["so_no", "partner_code", "amount_total", "order_date", "status"]
  },
  "form": {
    "labelWidth": "140px",
    "layout": [
      {
        "type": "grid",
        "cols": [
          {"field": "soNo", "label": "受注番号", "span": 8, "props": {"disabled": true}},
          {"field": "orderDate", "label": "受注日", "span": 8, "widget": "date"},
          {"field": "status", "label": "ステータス", "span": 8, "widget": "select", "props": {
            "options": [
              {"label": "新規登録", "value": "new"},
              {"label": "一部出庫", "value": "partial_shipped"},
              {"label": "出庫完了", "value": "shipped"},
              {"label": "一部請求", "value": "partial_invoiced"},
              {"label": "請求完了", "value": "invoiced"},
              {"label": "完了", "value": "completed"},
              {"label": "キャンセル", "value": "cancelled"}
            ]
          }}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "partnerCode", "label": "取引先コード", "span": 8},
          {"field": "partnerName", "label": "取引先名", "span": 16}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "requestedDeliveryDate", "label": "希望納期", "span": 8, "widget": "date"},
          {"field": "currency", "label": "通貨", "span": 8, "widget": "select", "props": {
            "options": [
              {"label": "JPY", "value": "JPY"},
              {"label": "USD", "value": "USD"}
            ]
          }}
        ]
      },
      {
        "type": "lines",
        "field": "lines",
        "label": "明細",
        "addButtonText": "行を追加",
        "columns": [
          {"field": "lineNo", "label": "#", "width": 60, "props": {"disabled": true}},
          {"field": "materialCode", "label": "品目コード", "width": 120},
          {"field": "materialName", "label": "品目名", "width": 200},
          {"field": "quantity", "label": "数量", "width": 100, "widget": "number"},
          {"field": "uom", "label": "単位", "width": 80},
          {"field": "unitPrice", "label": "単価", "width": 120, "widget": "number"},
          {"field": "amount", "label": "金額", "width": 120, "widget": "number", "props": {"disabled": true}},
          {"field": "taxRate", "label": "税率%", "width": 80, "widget": "select", "props": {
            "options": [
              {"label": "10%", "value": 10},
              {"label": "8%", "value": 8},
              {"label": "0%", "value": 0}
            ]
          }},
          {"field": "taxAmount", "label": "税額", "width": 100, "widget": "number", "props": {"disabled": true}}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "taxAmountTotal", "label": "消費税合計", "span": 8, "widget": "number", "props": {"disabled": true}},
          {"field": "amountTotal", "label": "合計金額（税込）", "span": 8, "widget": "number", "props": {"disabled": true}}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "note", "label": "備考", "span": 24, "widget": "textarea"}
        ]
      }
    ]
  }
}'::jsonb,
    updated_at = now()
WHERE name = 'sales_order' AND is_active = true;

-- 显示更新结果
SELECT company_code, name, version, is_active, 
       schema->'properties'->'status'->'enum' as status_enum
FROM schemas 
WHERE name = 'sales_order' AND is_active = true;


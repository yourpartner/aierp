-- 采购订单（Purchase Order / 発注）表结构和 Schema

-- 发注（Purchase Order）
CREATE TABLE IF NOT EXISTS purchase_orders (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  po_no TEXT GENERATED ALWAYS AS (payload->>'poNo') STORED,
  partner_code TEXT GENERATED ALWAYS AS (payload->>'partnerCode') STORED,
  amount_total NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'amountTotal')) STORED,
  order_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'orderDate')) STORED,
  expected_delivery_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'expectedDeliveryDate')) STORED,
  status TEXT GENERATED ALWAYS AS (payload->>'status') STORED -- new/partial_received/fully_received/partial_invoiced/fully_invoiced/closed
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_pos_company_no ON purchase_orders(company_code, po_no);
CREATE INDEX IF NOT EXISTS idx_pos_company_partner ON purchase_orders(company_code, partner_code);
CREATE INDEX IF NOT EXISTS idx_pos_company_status ON purchase_orders(company_code, status);
CREATE INDEX IF NOT EXISTS idx_pos_company_date ON purchase_orders(company_code, order_date DESC);

-- 采购订单明细行（展开存储，便于入库时匹配）
CREATE TABLE IF NOT EXISTS purchase_order_lines (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  po_id UUID NOT NULL REFERENCES purchase_orders(id) ON DELETE CASCADE,
  line_no INTEGER NOT NULL,
  material_code TEXT NOT NULL,
  material_name TEXT,
  quantity DECIMAL(18,4) NOT NULL,
  received_quantity DECIMAL(18,4) NOT NULL DEFAULT 0, -- 已入库数量
  unit_price DECIMAL(18,4),
  uom TEXT,
  tax_rate DECIMAL(5,2) DEFAULT 10,
  amount DECIMAL(18,2),
  status TEXT NOT NULL DEFAULT 'open', -- open/partial/closed/cancelled
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(po_id, line_no)
);
CREATE INDEX IF NOT EXISTS idx_po_lines_company ON purchase_order_lines(company_code);
CREATE INDEX IF NOT EXISTS idx_po_lines_material ON purchase_order_lines(company_code, material_code);
CREATE INDEX IF NOT EXISTS idx_po_lines_status ON purchase_order_lines(company_code, status);

-- 采购订单 Schema 定义
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'purchase_order',
  1,
  TRUE,
  '{
    "$schema": "https://json-schema.org/draft/2019-09/schema",
    "type": "object",
    "required": ["poNo", "partnerCode", "orderDate"],
    "properties": {
      "poNo": {
        "type": "string",
        "description": "発注番号"
      },
      "partnerCode": {
        "type": "string",
        "description": "仕入先コード"
      },
      "partnerName": {
        "type": "string",
        "description": "仕入先名"
      },
      "orderDate": {
        "type": "string",
        "format": "date",
        "description": "発注日"
      },
      "expectedDeliveryDate": {
        "type": "string",
        "format": "date",
        "description": "納期"
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
        "enum": ["new", "partial_received", "fully_received", "partial_invoiced", "fully_invoiced", "closed"],
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
          "required": ["lineNo", "materialCode", "quantity"],
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
              "description": "発注数量"
            },
            "receivedQuantity": {
              "type": "number",
              "description": "入庫済数量",
              "default": 0
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
            "status": {
              "type": "string",
              "enum": ["open", "partial", "closed", "cancelled"],
              "description": "行ステータス",
              "default": "open"
            }
          }
        }
      }
    }
  }'::jsonb,
  '{
    "list": {
      "columns": ["po_no", "partner_code", "order_date", "expected_delivery_date", "amount_total", "status"]
    },
    "form": {
      "labelWidth": "140px",
      "layout": [
        {
          "type": "section",
          "title": "基本情報",
          "children": [
            {
              "type": "grid",
              "cols": [
                {"field": "poNo", "label": "発注番号", "span": 8, "widget": "input", "disabled": true},
                {"field": "orderDate", "label": "発注日", "span": 8, "widget": "date", "required": true},
                {"field": "status", "label": "ステータス", "span": 8, "widget": "select", "options": [
                  {"label": "新規", "value": "new"},
                  {"label": "一部入庫", "value": "partial_received"},
                  {"label": "入庫完了", "value": "fully_received"},
                  {"label": "一部請求済", "value": "partial_invoiced"},
                  {"label": "請求済", "value": "fully_invoiced"},
                  {"label": "完了", "value": "closed"}
                ]}
              ]
            },
            {
              "type": "grid",
              "cols": [
                {"field": "partnerCode", "label": "仕入先", "span": 12, "widget": "vendor-picker", "required": true},
                {"field": "partnerName", "label": "仕入先名", "span": 12, "widget": "input", "disabled": true}
              ]
            },
            {
              "type": "grid",
              "cols": [
                {"field": "expectedDeliveryDate", "label": "納期", "span": 8, "widget": "date"},
                {"field": "currency", "label": "通貨", "span": 8, "widget": "select", "options": [
                  {"label": "JPY", "value": "JPY"},
                  {"label": "USD", "value": "USD"}
                ]}
              ]
            }
          ]
        },
        {
          "type": "section",
          "title": "明細",
          "children": [
            {"field": "lines", "widget": "purchase-order-lines"}
          ]
        },
        {
          "type": "section",
          "title": "備考",
          "children": [
            {"field": "note", "label": "備考", "widget": "textarea", "rows": 3}
          ]
        }
      ]
    }
  }'::jsonb,
  '{"filters": ["po_no", "partner_code", "status", "order_date"], "sorts": ["order_date", "po_no"]}'::jsonb,
  '{"coreFields": []}'::jsonb,
  '[]'::jsonb,
  '{"prefix": "PO", "digits": 8, "scope": ["company_code"], "resetCycle": "yearly"}'::jsonb,
  NULL
)
ON CONFLICT (company_code, name, version) DO UPDATE SET
  schema = EXCLUDED.schema,
  ui = EXCLUDED.ui,
  query = EXCLUDED.query,
  numbering = EXCLUDED.numbering,
  updated_at = now();


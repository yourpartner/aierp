-- 创建销售请求书表和Schema
-- 请求书使用 header + lines 的 Schema 方式存储

-- 创建 immutable 辅助函数（用于生成列的类型转换）
CREATE OR REPLACE FUNCTION fn_jsonb_to_date(j JSONB, key TEXT)
RETURNS DATE
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE 
        WHEN j->>key IS NULL OR j->>key = '' THEN NULL
        ELSE (j->>key)::date
    END;
$$;

CREATE OR REPLACE FUNCTION fn_jsonb_to_numeric(j JSONB, key TEXT)
RETURNS NUMERIC
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE 
        WHEN j->>key IS NULL OR j->>key = '' THEN NULL
        ELSE (j->>key)::numeric
    END;
$$;

-- 请求书编号序列表
CREATE TABLE IF NOT EXISTS sales_invoice_sequences (
    company_code VARCHAR(20) NOT NULL,
    prefix VARCHAR(10) NOT NULL DEFAULT 'INV',
    year INTEGER NOT NULL,
    month INTEGER NOT NULL,
    last_number INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(company_code, prefix, year, month)
);

-- 销售请求书表
CREATE TABLE IF NOT EXISTS sales_invoices (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    payload JSONB NOT NULL DEFAULT '{"header":{},"lines":[]}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    
    -- 从 payload.header 提取的计算列，便于查询和索引
    invoice_no TEXT GENERATED ALWAYS AS (payload->'header'->>'invoiceNo') STORED,
    customer_code TEXT GENERATED ALWAYS AS (payload->'header'->>'customerCode') STORED,
    customer_name TEXT GENERATED ALWAYS AS (payload->'header'->>'customerName') STORED,
    invoice_date DATE GENERATED ALWAYS AS (fn_jsonb_to_date(payload->'header', 'invoiceDate')) STORED,
    due_date DATE GENERATED ALWAYS AS (fn_jsonb_to_date(payload->'header', 'dueDate')) STORED,
    amount_total NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_to_numeric(payload->'header', 'amountTotal')) STORED,
    tax_amount NUMERIC(18,2) GENERATED ALWAYS AS (fn_jsonb_to_numeric(payload->'header', 'taxAmount')) STORED,
    status TEXT GENERATED ALWAYS AS (payload->'header'->>'status') STORED
);

-- 创建索引
CREATE UNIQUE INDEX IF NOT EXISTS uq_sales_invoices_no ON sales_invoices(company_code, invoice_no);
CREATE INDEX IF NOT EXISTS idx_sales_invoices_customer ON sales_invoices(company_code, customer_code);
CREATE INDEX IF NOT EXISTS idx_sales_invoices_date ON sales_invoices(company_code, invoice_date);
CREATE INDEX IF NOT EXISTS idx_sales_invoices_due_date ON sales_invoices(company_code, due_date);
CREATE INDEX IF NOT EXISTS idx_sales_invoices_status ON sales_invoices(company_code, status);
CREATE INDEX IF NOT EXISTS idx_sales_invoices_payload ON sales_invoices USING GIN(payload);

-- 插入 Schema 定义
INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES
(NULL, 'sales_invoice', 1, TRUE,
'{
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "type": "object",
  "properties": {
    "header": {
      "type": "object",
      "required": ["invoiceNo", "customerCode", "invoiceDate"],
      "properties": {
        "invoiceNo": {"type": "string", "description": "請求書番号"},
        "customerCode": {"type": "string", "description": "顧客コード"},
        "customerName": {"type": "string", "description": "顧客名"},
        "invoiceDate": {"type": "string", "format": "date", "description": "請求日"},
        "dueDate": {"type": "string", "format": "date", "description": "支払期限"},
        "amountTotal": {"type": "number", "description": "合計金額（税込）"},
        "taxAmount": {"type": "number", "description": "消費税額"},
        "currency": {"type": "string", "default": "JPY", "description": "通貨"},
        "status": {"type": "string", "enum": ["draft", "issued", "paid", "cancelled"], "description": "ステータス"},
        "note": {"type": "string", "description": "備考"},
        "createdBy": {"type": "string"},
        "createdAt": {"type": "string", "format": "date-time"},
        "issuedAt": {"type": "string", "format": "date-time"},
        "issuedBy": {"type": "string"},
        "cancelledAt": {"type": "string", "format": "date-time"},
        "cancelledBy": {"type": "string"},
        "cancelReason": {"type": "string"}
      }
    },
    "lines": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "lineNo": {"type": "integer", "description": "行番号"},
          "soNo": {"type": "string", "description": "受注番号"},
          "deliveryNo": {"type": "string", "description": "納品書番号"},
          "materialCode": {"type": "string", "description": "品目コード"},
          "materialName": {"type": "string", "description": "品目名"},
          "quantity": {"type": "number", "description": "数量"},
          "uom": {"type": "string", "description": "単位"},
          "unitPrice": {"type": "number", "description": "単価"},
          "amount": {"type": "number", "description": "金額（税抜）"},
          "taxRate": {"type": "number", "description": "消費税率（%）"},
          "taxAmount": {"type": "number", "description": "消費税額"},
          "amountWithTax": {"type": "number", "description": "金額（税込）"}
        }
      }
    }
  }
}'::jsonb,
'{
  "list": {
    "columns": ["invoice_no", "customer_code", "customer_name", "invoice_date", "due_date", "amount_total", "status"]
  },
  "form": {
    "labelWidth": "140px",
    "layout": [
      {
        "type": "grid",
        "cols": [
          {"field": "header.invoiceNo", "label": "請求書番号", "span": 8, "props": {"disabled": true}},
          {"field": "header.invoiceDate", "label": "請求日", "span": 8, "widget": "date"},
          {"field": "header.status", "label": "ステータス", "span": 8, "widget": "select", "props": {
            "options": [
              {"label": "下書き", "value": "draft"},
              {"label": "発行済", "value": "issued"},
              {"label": "入金済", "value": "paid"},
              {"label": "キャンセル", "value": "cancelled"}
            ]
          }}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "header.customerCode", "label": "顧客コード", "span": 8},
          {"field": "header.customerName", "label": "顧客名", "span": 16}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "header.dueDate", "label": "支払期限", "span": 8, "widget": "date"},
          {"field": "header.currency", "label": "通貨", "span": 8}
        ]
      },
      {
        "type": "lines",
        "field": "lines",
        "label": "明細",
        "columns": [
          {"field": "lineNo", "label": "#", "width": 50},
          {"field": "deliveryNo", "label": "納品書番号", "width": 140},
          {"field": "materialCode", "label": "品目コード", "width": 120},
          {"field": "materialName", "label": "品目名", "width": 200},
          {"field": "quantity", "label": "数量", "width": 80},
          {"field": "unitPrice", "label": "単価", "width": 100},
          {"field": "amount", "label": "金額", "width": 100},
          {"field": "taxRate", "label": "税率%", "width": 60},
          {"field": "taxAmount", "label": "税額", "width": 80}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "header.taxAmount", "label": "消費税合計", "span": 8, "widget": "number", "props": {"disabled": true}},
          {"field": "header.amountTotal", "label": "合計金額（税込）", "span": 8, "widget": "number", "props": {"disabled": true}}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "header.note", "label": "備考", "span": 24, "widget": "textarea"}
        ]
      }
    ]
  }
}'::jsonb,
'{"filters": ["invoice_no", "customer_code", "invoice_date", "due_date", "status"], "sorts": ["invoice_date", "due_date", "invoice_no"]}'::jsonb,
'{"coreFields": []}'::jsonb,
'[]'::jsonb,
NULL,
NULL
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- 显示创建结果
SELECT 'sales_invoices' as table_name, COUNT(*) as row_count FROM sales_invoices
UNION ALL
SELECT 'sales_invoice_sequences', COUNT(*) FROM sales_invoice_sequences;

SELECT name, version, is_active FROM schemas WHERE name = 'sales_invoice';


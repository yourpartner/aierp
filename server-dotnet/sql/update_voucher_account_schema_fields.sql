-- 更新 voucher 和 account schema，补齐缺失字段
-- 执行前请备份数据库

-- ============================================
-- 1. 更新 voucher schema
-- ============================================
UPDATE schemas
SET schema = '{
  "type": "object",
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "properties": {
    "header": {
      "type": "object",
      "properties": {
        "companyCode": { "type": "string", "minLength": 4, "maxLength": 4 },
        "postingDate": { "type": "string", "format": "date" },
        "voucherType": { "type": "string", "enum": ["GL","AP","AR","AA","SA","IN","OT"] },
        "voucherNo": { "type": "string", "pattern": "^\\d{10}$" },
        "summary": { "type": "string", "maxLength": 500 },
        "currency": { "type": "string", "enum": ["JPY","USD","CNY"] },
        "invoiceRegistrationNo": { "type": ["string","null"], "pattern": "^(T\\d{13})?$", "description": "インボイス登録番号" },
        "createdAt": { "type": ["string","null"], "format": "date-time", "description": "作成日時" },
        "createdBy": { "type": ["string","null"], "description": "作成者ID" },
        "updatedAt": { "type": ["string","null"], "format": "date-time", "description": "更新日時" },
        "updatedBy": { "type": ["string","null"], "description": "更新者ID" }
      },
      "required": ["companyCode","postingDate","voucherType","currency"]
    },
    "lines": {
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "properties": {
          "lineNo": { "type": "integer" },
          "accountCode": { "type": "string" },
          "drcr": { "type": "string", "enum": ["DR","CR"] },
          "amount": { "type": "number" },
          "departmentId": { "type": ["string","null"] },
          "employeeId": { "type": ["string","null"] },
          "vendorId": { "type": ["string","null"] },
          "customerId": { "type": ["string","null"] },
          "assetId": { "type": ["string","null"], "description": "固定資産ID" },
          "paymentDate": { "type": ["string","null"], "format": "date" },
          "note": { "type": ["string","null"], "maxLength": 200 },
          "isTaxLine": { "type": "boolean", "default": false, "description": "消費税明細行フラグ" },
          "baseLineNo": { "type": ["integer","null"], "description": "関連する税基明細の行番号" },
          "taxRate": { "type": ["number","null"], "description": "税率（パーセント）" },
          "taxType": { "type": ["string","null"], "enum": [null, "OUTPUT_TAX", "INPUT_TAX"], "description": "税種別" },
          "tax": {
            "type": ["object","null"],
            "description": "消費税入力用オブジェクト（FinanceServiceで展開される）",
            "properties": {
              "accountCode": { "type": "string", "description": "消費税科目コード" },
              "amount": { "type": "number", "description": "消費税額" },
              "side": { "type": "string", "enum": ["DR","CR"], "description": "借貸区分" },
              "rate": { "type": "number", "description": "税率" },
              "taxType": { "type": "string", "description": "税種別" }
            }
          }
        },
        "required": ["lineNo","accountCode","drcr","amount"]
      }
    },
    "attachments": {
      "type": "array",
      "description": "添付ファイル一覧",
      "items": {
        "type": "object",
        "properties": {
          "id": { "type": "string", "description": "ファイルID" },
          "name": { "type": "string", "description": "ファイル名" },
          "contentType": { "type": "string", "description": "MIMEタイプ" },
          "size": { "type": "integer", "description": "ファイルサイズ（バイト）" },
          "blobName": { "type": "string", "description": "Azure Blob名" },
          "url": { "type": "string", "description": "アクセスURL" },
          "uploadedAt": { "type": "string", "format": "date-time", "description": "アップロード日時" }
        },
        "required": ["id", "name"]
      }
    },
    "analysis": {
      "type": ["object","null"],
      "description": "AI解析結果（請求書OCR等）",
      "properties": {
        "vendorName": { "type": "string" },
        "totalAmount": { "type": "number" },
        "taxAmount": { "type": "number" },
        "taxRate": { "type": "number" },
        "invoiceDate": { "type": "string", "format": "date" },
        "invoiceRegistrationNo": { "type": "string" },
        "items": { "type": "array" }
      }
    }
  },
  "required": ["header","lines"],
  "allOf": [
    {
      "if": { "properties": { "header": { "properties": { "currency": { "const": "JPY" } } } } },
      "then": { "properties": { "lines": { "items": { "properties": { "amount": { "type": "integer", "minimum": 0 } } } } } },
      "else": { "properties": { "lines": { "items": { "properties": { "amount": { "type": "number", "minimum": 0, "multipleOf": 0.01 } } } } } }
    }
  ]
}'::jsonb,
    validators = '["voucher_balance_check_v2"]'::jsonb
WHERE name = 'voucher' AND is_active = true;

-- ============================================
-- 2. 更新 account schema
-- ============================================
UPDATE schemas
SET schema = '{
  "type": "object",
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "properties": {
    "code": { "type": "string", "minLength": 1, "maxLength": 20, "pattern": "^[0-9A-Za-z_-]{1,20}$" },
    "name": { "type": "string", "minLength": 1, "maxLength": 100 },
    "category": { "type": "string", "enum": ["PL","BS"] },
    "openItem": { "type": "boolean" },
    "openItemBaseline": { "type": ["string","null"], "enum": [null, "NONE","CUSTOMER","VENDOR","EMPLOYEE"] },
    "fieldRules": {
      "type": "object",
      "properties": {
        "customerId": { "type": "string", "enum": ["required","optional","hidden"] },
        "vendorId": { "type": "string", "enum": ["required","optional","hidden"] },
        "employeeId": { "type": "string", "enum": ["required","optional","hidden"] },
        "departmentId": { "type": "string", "enum": ["required","optional","hidden"] },
        "paymentDate": { "type": "string", "enum": ["required","optional","hidden"] },
        "assetId": { "type": "string", "enum": ["required","optional","hidden"] }
      },
      "additionalProperties": false
    },
    "taxType": {
      "type": ["string","null"],
      "enum": [null, "OUTPUT_TAX", "INPUT_TAX", "TAX_ACCOUNT", "NON_TAX", "NON_TAXABLE", "EXEMPT", "OUT_OF_SCOPE"],
      "description": "消費税区分"
    },
    "accountType": {
      "type": ["string","null"],
      "enum": [null, "asset", "liability", "equity", "revenue", "expense"],
      "description": "勘定科目タイプ"
    },
    "fsBalanceGroup": {
      "type": ["string","null"],
      "description": "財務諸表BS分類グループ"
    },
    "fsProfitGroup": {
      "type": ["string","null"],
      "description": "財務諸表PL分類グループ"
    },
    "isBank": {
      "type": "boolean",
      "default": false,
      "description": "銀行口座勘定フラグ"
    },
    "isCash": {
      "type": "boolean",
      "default": false,
      "description": "現金勘定フラグ"
    },
    "bankInfo": {
      "type": ["object","null"],
      "description": "銀行口座情報（isBank=trueの場合必須）",
      "properties": {
        "bankCode": { "type": "string", "description": "銀行コード" },
        "bankName": { "type": "string", "description": "銀行名" },
        "branchCode": { "type": "string", "description": "支店コード" },
        "branchName": { "type": "string", "description": "支店名" },
        "accountType": { "type": "string", "enum": ["\\u666e\\u901a", "\\u5f53\\u5ea7", "\\u8caf\\u84c4"], "description": "口座種別" },
        "accountNo": { "type": "string", "description": "口座番号" },
        "holder": { "type": "string", "description": "口座名義（カナ）" },
        "currency": { "type": "string", "enum": ["JPY", "USD", "CNY"], "description": "通貨" }
      },
      "required": ["bankName", "branchName", "accountType", "accountNo", "holder", "currency"]
    }
  },
  "required": ["code","name","category"],
  "allOf": [
    {
      "if": {
        "properties": { "isBank": { "const": true }, "isCash": { "const": true } }
      },
      "then": false
    },
    {
      "if": {
        "properties": { "isBank": { "const": true } }
      },
      "then": {
        "required": ["bankInfo"]
      }
    }
  ]
}'::jsonb,
    query = '{"filters":["account_code","name","pl_bs_type","taxType","accountType","isBank","isCash"],"sorts":["account_code","name"]}'::jsonb,
    core_fields = '{"coreFields":[
      {"name":"account_code","path":"code","type":"string","index":{"strategy":"generated_column","unique":true,"scope":["company_code"]}},
      {"name":"name","path":"name","type":"string","index":{"strategy":"generated_column","unique":false}},
      {"name":"pl_bs_type","path":"category","type":"string","index":{"strategy":"generated_column","unique":false}},
      {"name":"open_item_mgmt","path":"openItem","type":"boolean","index":{"strategy":"generated_column","unique":false}},
      {"name":"tax_type","path":"taxType","type":"string","index":{"strategy":"generated_column","unique":false}},
      {"name":"is_bank","path":"isBank","type":"boolean","index":{"strategy":"generated_column","unique":false}},
      {"name":"is_cash","path":"isCash","type":"boolean","index":{"strategy":"generated_column","unique":false}}
    ]}'::jsonb
WHERE name = 'account' AND is_active = true;

-- ============================================
-- 3. 确认更新结果
-- ============================================
SELECT name, 
       jsonb_pretty(schema->'properties'->'header'->'properties') as header_props
FROM schemas 
WHERE name = 'voucher' AND is_active = true;

SELECT name,
       schema->'properties'->>'taxType' as has_taxType,
       schema->'properties'->>'isBank' as has_isBank,
       schema->'properties'->>'bankInfo' as has_bankInfo
FROM schemas 
WHERE name = 'account' AND is_active = true;


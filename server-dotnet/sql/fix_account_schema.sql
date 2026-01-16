-- 修复 account schema 中重复的 isBank/isCash 互斥约束
-- 原始 allOf 中有 6 个相同的 not 约束，应该只保留 1 个

UPDATE schemas
SET schema = '{
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "type": "object",
  "required": ["code", "name", "category"],
  "properties": {
    "code": {
      "type": "string",
      "minLength": 1,
      "maxLength": 20,
      "pattern": "^[0-9A-Za-z_-]{1,20}$"
    },
    "name": {
      "type": "string",
      "minLength": 1,
      "maxLength": 100
    },
    "category": {
      "type": "string",
      "enum": ["PL", "BS"]
    },
    "isBank": {
      "type": "boolean",
      "description": "銀行口座科目"
    },
    "isCash": {
      "type": "boolean",
      "description": "現金科目"
    },
    "taxType": {
      "type": "string",
      "enum": ["NON_TAX", "INPUT_TAX", "OUTPUT_TAX", "TAX_ACCOUNT"],
      "description": "消費税区分"
    },
    "openItem": {
      "type": "boolean",
      "description": "未清項目管理"
    },
    "openItemBaseline": {
      "type": ["string", "null"],
      "enum": [null, "NONE", "CUSTOMER", "VENDOR", "EMPLOYEE"],
      "description": "未清項目基準"
    },
    "bankInfo": {
      "type": "object",
      "properties": {
        "bankName": {"type": "string", "maxLength": 120, "description": "銀行名"},
        "branchName": {"type": "string", "maxLength": 120, "description": "支店名"},
        "accountType": {"type": "string", "enum": ["普通", "当座"], "description": "口座種別"},
        "accountNo": {"type": "string", "maxLength": 30, "description": "口座番号"},
        "holder": {"type": "string", "maxLength": 120, "description": "名義人"},
        "currency": {"type": "string", "enum": ["JPY", "USD", "CNY"], "description": "通貨"},
        "zenginBranchCode": {"type": "string", "maxLength": 10, "description": "全銀支店コード"},
        "payrollCompanyCode": {"type": "string", "maxLength": 20, "description": "給与振込用会社コード"},
        "juminzeiCompanyCode": {"type": "string", "maxLength": 20, "description": "住民税納付用会社コード"}
      }
    },
    "cashCurrency": {
      "type": "string",
      "enum": ["JPY", "USD", "CNY"],
      "description": "現金通貨"
    },
    "fieldRules": {
      "type": "object",
      "properties": {
        "customerId": {"type": "string", "enum": ["required", "optional", "hidden"]},
        "vendorId": {"type": "string", "enum": ["required", "optional", "hidden"]},
        "employeeId": {"type": "string", "enum": ["required", "optional", "hidden"]},
        "departmentId": {"type": "string", "enum": ["required", "optional", "hidden"]},
        "paymentDate": {"type": "string", "enum": ["required", "optional", "hidden"]}
      },
      "additionalProperties": false
    },
    "aliases": {
      "type": "array",
      "items": {"type": "string"},
      "description": "科目別名（lookup用）"
    },
    "fsBalanceGroup": {
      "type": "string",
      "description": "貸借対照表グループ"
    },
    "fsProfitGroup": {
      "type": "string",
      "description": "損益計算書グループ"
    }
  },
  "allOf": [
    {
      "not": {
        "type": "object",
        "required": ["isBank", "isCash"],
        "properties": {
          "isBank": {"const": true},
          "isCash": {"const": true}
        }
      }
    },
    {
      "if": {
        "properties": {"isBank": {"const": true}}
      },
      "then": {
        "required": ["bankInfo"],
        "properties": {
          "bankInfo": {
            "required": ["bankName", "branchName", "accountType", "accountNo", "holder", "currency"]
          }
        }
      }
    },
    {
      "if": {
        "properties": {"isCash": {"const": true}}
      },
      "then": {
        "required": ["cashCurrency"]
      }
    }
  ]
}'::jsonb,
    version = version + 1,
    updated_at = now()
WHERE name = 'account' AND is_active = true AND version = 17;

-- 显示更新结果
SELECT name, version, is_active FROM schemas WHERE name = 'account' AND is_active = true;


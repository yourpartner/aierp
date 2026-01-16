-- 更新客户 Schema，添加偏好科目字段
-- 用于记住用户选择的应收账款科目和销售收入科目

-- 方案：直接更新当前活跃版本的 schema 和 ui，不递增版本号
-- 如果需要保留历史版本，请先手动备份

-- 更新 businesspartner schema 定义（只更新 schema 和 ui，不改变版本号）
UPDATE schemas
SET schema = '{
  "$schema": "https://json-schema.org/draft/2019-09/schema",
  "type": "object",
  "required": ["code", "name"],
  "properties": {
    "code": {
      "type": "string",
      "minLength": 1,
      "maxLength": 20,
      "description": "取引先コード"
    },
    "name": {
      "type": "string",
      "minLength": 1,
      "maxLength": 200,
      "description": "取引先名"
    },
    "nameKana": {
      "type": "string",
      "description": "取引先名カナ"
    },
    "flags": {
      "type": "object",
      "properties": {
        "customer": {
          "type": "boolean",
          "description": "顧客区分"
        },
        "vendor": {
          "type": "boolean",
          "description": "仕入先区分"
        }
      }
    },
    "status": {
      "type": "string",
      "enum": ["active", "inactive"],
      "default": "active",
      "description": "ステータス"
    },
    "address": {
      "type": "object",
      "properties": {
        "postalCode": {"type": "string"},
        "prefecture": {"type": "string"},
        "city": {"type": "string"},
        "street": {"type": "string"},
        "building": {"type": "string"}
      },
      "description": "住所"
    },
    "contact": {
      "type": "object",
      "properties": {
        "phone": {"type": "string"},
        "fax": {"type": "string"},
        "email": {"type": "string"},
        "contactPerson": {"type": "string"}
      },
      "description": "連絡先"
    },
    "invoiceCycle": {
      "type": "string",
      "enum": ["none", "per_order", "monthly", "custom"],
      "default": "monthly",
      "description": "請求サイクル: none=請求書不要, per_order=都度請求, monthly=月次請求"
    },
    "paymentTermDays": {
      "type": "integer",
      "default": 30,
      "description": "支払期限（日数）"
    },
    "preferredArAccountCode": {
      "type": "string",
      "description": "優先使用する売掛金科目コード"
    },
    "preferredRevenueAccountCode": {
      "type": "string",
      "description": "優先使用する売上科目コード"
    },
    "taxRateDefault": {
      "type": "number",
      "enum": [0, 8, 10],
      "default": 10,
      "description": "デフォルト消費税率"
    },
    "bankInfo": {
      "type": "object",
      "properties": {
        "bankName": {"type": "string"},
        "branchName": {"type": "string"},
        "accountType": {"type": "string", "enum": ["普通", "当座"]},
        "accountNo": {"type": "string"},
        "holder": {"type": "string"}
      },
      "description": "振込先銀行情報"
    },
    "note": {
      "type": "string",
      "description": "備考"
    }
  }
}'::jsonb,
    ui = '{
  "list": {
    "columns": ["partner_code", "name", "flag_customer", "flag_vendor", "status"]
  },
  "form": {
    "labelWidth": "160px",
    "layout": [
      {
        "type": "grid",
        "cols": [
          {"field": "code", "label": "取引先コード", "span": 8},
          {"field": "name", "label": "取引先名", "span": 16}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "nameKana", "label": "取引先名カナ", "span": 24}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "flags.customer", "label": "顧客区分", "span": 6, "widget": "switch", "props": {"activeText": "あり", "inactiveText": "なし"}},
          {"field": "flags.vendor", "label": "仕入先区分", "span": 6, "widget": "switch", "props": {"activeText": "あり", "inactiveText": "なし"}},
          {"field": "status", "label": "ステータス", "span": 6, "widget": "select", "props": {
            "options": [{"label": "有効", "value": "active"}, {"label": "無効", "value": "inactive"}]
          }},
          {"field": "taxRateDefault", "label": "デフォルト税率", "span": 6, "widget": "select", "props": {
            "options": [{"label": "10%", "value": 10}, {"label": "8%", "value": 8}, {"label": "0%", "value": 0}]
          }}
        ]
      },
      {
        "type": "divider",
        "label": "請求設定"
      },
      {
        "type": "grid",
        "cols": [
          {"field": "invoiceCycle", "label": "請求サイクル", "span": 8, "widget": "select", "props": {
            "options": [
              {"label": "請求書不要", "value": "none"},
              {"label": "都度請求", "value": "per_order"},
              {"label": "月次請求", "value": "monthly"},
              {"label": "カスタム", "value": "custom"}
            ]
          }},
          {"field": "paymentTermDays", "label": "支払期限（日）", "span": 8, "widget": "number"}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "preferredArAccountCode", "label": "売掛金科目", "span": 12, "widget": "select", "props": {
            "optionsUrl": "/objects/account/search?where=openItemBaseline:eq:CUSTOMER",
            "optionValue": "account_code",
            "optionLabel": "{account_code} - {name}",
            "placeholder": "自動選択",
            "clearable": true
          }},
          {"field": "preferredRevenueAccountCode", "label": "売上科目", "span": 12, "widget": "select", "props": {
            "optionsUrl": "/objects/account/search?where=taxType:eq:OUTPUT_TAX",
            "optionValue": "account_code",
            "optionLabel": "{account_code} - {name}",
            "placeholder": "自動選択",
            "clearable": true
          }}
        ]
      },
      {
        "type": "divider",
        "label": "住所"
      },
      {
        "type": "grid",
        "cols": [
          {"field": "address.postalCode", "label": "郵便番号", "span": 8},
          {"field": "address.prefecture", "label": "都道府県", "span": 8},
          {"field": "address.city", "label": "市区町村", "span": 8}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "address.street", "label": "番地", "span": 12},
          {"field": "address.building", "label": "建物名", "span": 12}
        ]
      },
      {
        "type": "divider",
        "label": "連絡先"
      },
      {
        "type": "grid",
        "cols": [
          {"field": "contact.phone", "label": "電話番号", "span": 8},
          {"field": "contact.fax", "label": "FAX番号", "span": 8},
          {"field": "contact.email", "label": "メール", "span": 8}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "contact.contactPerson", "label": "担当者名", "span": 12}
        ]
      },
      {
        "type": "divider",
        "label": "振込先銀行"
      },
      {
        "type": "grid",
        "cols": [
          {"field": "bankInfo.bankName", "label": "銀行名", "span": 8},
          {"field": "bankInfo.branchName", "label": "支店名", "span": 8},
          {"field": "bankInfo.accountType", "label": "口座種別", "span": 8, "widget": "select", "props": {
            "options": [{"label": "普通", "value": "普通"}, {"label": "当座", "value": "当座"}]
          }}
        ]
      },
      {
        "type": "grid",
        "cols": [
          {"field": "bankInfo.accountNo", "label": "口座番号", "span": 8},
          {"field": "bankInfo.holder", "label": "口座名義", "span": 16}
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
WHERE name = 'businesspartner' AND is_active = true;

-- 显示更新结果
SELECT company_code, name, version, is_active,
       schema->'properties'->'preferredArAccountCode' as ar_account,
       schema->'properties'->'invoiceCycle' as invoice_cycle
FROM schemas 
WHERE name = 'businesspartner' AND is_active = true;


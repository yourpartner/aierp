-- 在 businesspartner schema 中添加インボイス登録番号字段
-- 用于インボイス制度的仕入税額控除計算

-- 1. 在 schema 的 properties 中添加 invoiceRegistrationNumber 字段
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationNumber}',
    '{
      "type": ["string", "null"],
      "pattern": "^[Tt]\\d{13}$",
      "maxLength": 14,
      "description": "インボイス登録番号（T + 13桁）"
    }'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 2. 在 schema 的 properties 中添加 invoiceRegistrationStartDate 字段（生效开始日期）
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationStartDate}',
    '{
      "type": ["string", "null"],
      "format": "date",
      "description": "インボイス登録番号の生効開始日"
    }'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 3. 更新整个 UI 配置，添加インボイス登録番号和生效日期字段
UPDATE schemas
SET ui = '{
  "list": {
    "columns": ["partner_code", "name", "flag_customer", "flag_vendor", "invoiceRegistrationNumber", "status"]
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
        "label": "インボイス制度"
      },
      {
        "type": "grid",
        "cols": [
          {"field": "invoiceRegistrationNumber", "label": "インボイス登録番号", "span": 12, "props": {"placeholder": "T1234567890123", "maxlength": 14}},
          {"field": "invoiceRegistrationStartDate", "label": "登録番号生効日", "span": 12, "widget": "date", "props": {"placeholder": "2023-10-01"}}
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

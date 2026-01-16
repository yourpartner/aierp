-- =====================================================
-- 取引先マスタにインボイス登録番号フィールドを追加
-- 実行方法: psql -f add_invoice_fields_to_bp.sql
-- =====================================================

-- 1. Schema properties に invoiceRegistrationNumber を追加
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationNumber}',
    '{"type":["string","null"],"maxLength":14,"pattern":"^[Tt]\\d{13}$","description":"インボイス登録番号（T+13桁）"}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 2. Schema properties に invoiceRegistrationStartDate を追加
UPDATE schemas
SET schema = jsonb_set(
    schema,
    '{properties,invoiceRegistrationStartDate}',
    '{"type":["string","null"],"format":"date","description":"インボイス登録番号の生効開始日"}'::jsonb,
    true
),
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 3. UI配置を更新（インボイス制度セクションを追加）
UPDATE schemas
SET ui = '{
  "list": {
    "columns": ["partner_code", "name", "flag_customer", "flag_vendor", "invoiceRegistrationNumber", "status"]
  },
  "form": {
    "labelWidth": "130px",
    "layout": [
      {
        "type": "section",
        "title": "基本情報",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "name", "label": "名称", "span": 12, "props": {"maxlength": 200, "showWordLimit": true, "placeholder": "正式名称を入力"}},
              {"field": "nameKana", "label": "名称カナ", "span": 12, "props": {"placeholder": "カタカナで入力"}}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "flags.customer", "label": "顧客区分", "span": 6, "widget": "switch", "props": {"activeText": "あり", "inactiveText": "なし"}},
              {"field": "flags.vendor", "label": "仕入先区分", "span": 6, "widget": "switch", "props": {"activeText": "あり", "inactiveText": "なし"}},
              {"field": "status", "label": "ステータス", "span": 6, "widget": "select", "props": {
                "options": [{"label": "有効", "value": "active"}, {"label": "無効", "value": "inactive"}]
              }}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "note", "label": "備考", "span": 24, "widget": "textarea", "props": {"rows": 2}}
            ]
          }
        ]
      },
      {
        "type": "section",
        "title": "インボイス制度",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "invoiceRegistrationNumber", "label": "インボイス登録番号", "span": 12, "props": {"placeholder": "T1234567890123", "maxlength": 14}},
              {"field": "invoiceRegistrationStartDate", "label": "登録番号生効日", "span": 12, "widget": "date", "props": {"placeholder": "2023-10-01"}}
            ]
          }
        ]
      },
      {
        "type": "section",
        "title": "支払条件",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "paymentTerms.cutOffDay", "label": "締日", "span": 6, "widget": "select", "props": {
                "placeholder": "選択",
                "options": [
                  {"label": "月末", "value": 31},
                  {"label": "5日", "value": 5},
                  {"label": "10日", "value": 10},
                  {"label": "15日", "value": 15},
                  {"label": "20日", "value": 20},
                  {"label": "25日", "value": 25}
                ]
              }},
              {"field": "paymentTerms.paymentMonth", "label": "支払月", "span": 6, "widget": "select", "props": {
                "placeholder": "選択",
                "options": [
                  {"label": "当月", "value": 0},
                  {"label": "翌月", "value": 1},
                  {"label": "翌々月", "value": 2}
                ]
              }},
              {"field": "paymentTerms.paymentDay", "label": "支払日", "span": 6, "widget": "select", "props": {
                "placeholder": "選択",
                "options": [
                  {"label": "月末", "value": 31},
                  {"label": "5日", "value": 5},
                  {"label": "10日", "value": 10},
                  {"label": "15日", "value": 15},
                  {"label": "20日", "value": 20},
                  {"label": "25日", "value": 25}
                ]
              }},
              {"field": "paymentTerms.description", "label": "", "span": 6, "widget": "text", "props": {"prefix": "→ "}}
            ]
          }
        ]
      },
      {
        "type": "section",
        "title": "住所・連絡先",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "address.postalCode", "label": "郵便番号", "span": 6, "props": {"placeholder": "123-4567"}},
              {"field": "address.prefecture", "label": "都道府県", "span": 6},
              {"field": "address.address", "label": "住所", "span": 12, "props": {"placeholder": "市区町村・番地・建物名"}}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "contact.phone", "label": "電話番号", "span": 6, "props": {"placeholder": "03-1234-5678"}},
              {"field": "contact.fax", "label": "FAX番号", "span": 6, "props": {"placeholder": "03-1234-5678"}},
              {"field": "contact.email", "label": "メール", "span": 6, "props": {"placeholder": "mail@example.com"}},
              {"field": "contact.contactPerson", "label": "担当者名", "span": 6}
            ]
          }
        ]
      },
      {
        "type": "section",
        "title": "振込先銀行",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"label": "金融機関", "widget": "button", "span": 4, "props": {"action": "openBankPicker", "type": "primary", "text": "選択"}},
              {"field": "bankInfo.bankCode", "widget": "hidden"},
              {"field": "bankInfo.bankName", "label": "", "span": 8, "widget": "text"},
              {"label": "支店", "widget": "button", "span": 4, "props": {"action": "openBranchPicker", "type": "default", "text": "選択"}},
              {"field": "bankInfo.branchCode", "widget": "hidden"},
              {"field": "bankInfo.branchName", "label": "", "span": 8, "widget": "text"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "bankInfo.accountType", "label": "口座種別", "span": 6, "widget": "select", "props": {
                "placeholder": "選択",
                "options": [{"label": "普通", "value": "普通"}, {"label": "当座", "value": "当座"}, {"label": "貯蓄", "value": "貯蓄"}]
              }},
              {"field": "bankInfo.accountNo", "label": "口座番号", "span": 6, "props": {"maxlength": 10, "placeholder": "1234567"}},
              {"field": "bankInfo.holder", "label": "口座名義", "span": 12, "props": {"placeholder": "ｶﾌﾞｼｷｶﾞｲｼｬ"}}
            ]
          }
        ]
      }
    ]
  }
}'::jsonb,
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 確認用クエリ
SELECT 
    name, 
    schema->'properties'->'invoiceRegistrationNumber' as invoice_no_field,
    schema->'properties'->'invoiceRegistrationStartDate' as start_date_field
FROM schemas 
WHERE name = 'businesspartner' AND is_active = true;


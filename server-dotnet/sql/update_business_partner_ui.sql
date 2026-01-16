-- 更新取引先 Schema UI
-- 变更点：
-- 1. 名称和名称カナ放到一行
-- 2. 地址字段合并简化
-- 3. 担当者名输入框缩短
-- 4. 按字段内容划分区域

UPDATE schemas
SET ui = '{
  "list": {
    "columns": ["partner_code", "name", "flag_customer", "flag_vendor", "status"]
  },
  "form": {
    "labelWidth": "110px",
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

-- 更新 schema properties
-- 1. 添加 paymentTerms 结构
-- 2. 将 address 改为简化结构
UPDATE schemas
SET schema = jsonb_set(
      jsonb_set(
        schema,
        '{properties,paymentTerms}',
        '{
          "type": "object",
          "description": "支払条件",
          "properties": {
            "cutOffDay": {"type": "integer", "description": "締日（31=月末）"},
            "paymentMonth": {"type": "integer", "description": "支払月（0=当月, 1=翌月, 2=翌々月）"},
            "paymentDay": {"type": "integer", "description": "支払日（31=月末）"},
            "description": {"type": "string", "description": "自動生成される説明文"}
          }
        }'::jsonb
      ),
      '{properties,address}',
      '{
        "type": "object",
        "description": "住所",
        "properties": {
          "postalCode": {"type": "string", "description": "郵便番号"},
          "prefecture": {"type": "string", "description": "都道府県"},
          "address": {"type": "string", "description": "住所（市区町村・番地・建物名）"}
        }
      }'::jsonb
    ),
    updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 移除不再使用的字段
UPDATE schemas
SET schema = schema 
      #- '{properties,paymentTermDays}'
      #- '{properties,taxRateDefault}'
      #- '{properties,preferredArAccountCode}'  
      #- '{properties,preferredRevenueAccountCode}',
    updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;

-- 显示更新结果
SELECT company_code, name, version, is_active
FROM schemas 
WHERE name = 'businesspartner' AND is_active = true;

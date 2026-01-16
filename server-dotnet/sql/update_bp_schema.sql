-- Update businesspartner schema UI to Japanese
UPDATE schemas
SET ui = '{
  "list": {"columns": ["partner_code","name","flag_customer","flag_vendor","status"]},
  "form": {
    "labelWidth": "120px",
    "layout": [
      {"type": "section", "title": "基本情報", "layout": [
        {"type": "grid", "cols": [
          {"label": "取引先名", "field": "name", "widget": "input", "span": 16, "props": {"maxlength": 300, "showWordLimit": true, "placeholder": "正式名称を入力"}},
          {"label": "略称", "field": "shortName", "widget": "input", "span": 8, "props": {"maxlength": 100, "showWordLimit": true}}
        ]},
        {"type": "grid", "cols": [
          {"label": "支払条件", "field": "paymentTerms", "widget": "input", "span": 12, "props": {"maxlength": 200, "showWordLimit": true, "placeholder": "例：月末締め翌月末払い"}},
          {"label": "インボイス番号", "field": "invoiceRegistrationNumber", "widget": "input", "span": 12, "props": {"placeholder": "T1234567890123"}}
        ]},
        {"type": "grid", "cols": [
          {"label": "顧客", "field": "flags.customer", "widget": "switch", "span": 4, "props": {"activeText": "はい", "inactiveText": "いいえ"}},
          {"label": "仕入先", "field": "flags.vendor", "widget": "switch", "span": 4, "props": {"activeText": "はい", "inactiveText": "いいえ"}},
          {"label": "業種", "field": "industry", "widget": "input", "span": 8, "props": {"placeholder": "例：製造業"}},
          {"label": "担当者ID", "field": "ownerUserId", "widget": "input", "span": 8}
        ]}
      ]},
      {"type": "section", "title": "連絡先情報", "layout": [
        {"type": "grid", "cols": [
          {"label": "郵便番号", "field": "postalCode", "widget": "input", "span": 6, "props": {"placeholder": "123-4567"}},
          {"label": "住所", "field": "address", "widget": "input", "span": 18, "props": {"maxlength": 500, "showWordLimit": true, "placeholder": "都道府県から入力"}}
        ]},
        {"type": "grid", "cols": [
          {"label": "電話番号", "field": "phone", "widget": "input", "span": 12, "props": {"placeholder": "03-1234-5678"}},
          {"label": "FAX番号", "field": "fax", "widget": "input", "span": 12, "props": {"placeholder": "03-1234-5678"}}
        ]}
      ]},
      {"type": "section", "title": "振込先口座情報", "layout": [
        {"type": "grid", "cols": [
          {"label": "金融機関", "widget": "button", "span": 6, "props": {"action": "openBankPicker", "type": "primary", "text": "銀行を選択"}},
          {"label": "", "field": "bankInfo.bankName", "widget": "text", "span": 6},
          {"label": "支店", "widget": "button", "span": 6, "props": {"action": "openBranchPicker", "type": "default", "text": "支店を選択"}},
          {"label": "", "field": "bankInfo.branchName", "widget": "text", "span": 6}
        ]},
        {"type": "grid", "cols": [
          {"label": "口座種別", "field": "bankInfo.accountType", "widget": "select", "span": 6, "props": {"placeholder": "選択", "options": [{"label":"普通", "value":"Futsu"},{"label":"当座", "value":"Toza"},{"label":"貯蓄", "value":"Chochiku"}]}},
          {"label": "口座番号", "field": "bankInfo.accountNo", "widget": "input", "span": 6, "props": {"maxlength": 10, "placeholder": "1234567"}},
          {"label": "口座名義（ｶﾅ）", "field": "bankInfo.holder", "widget": "input", "span": 6, "props": {"filter": "katakana-half", "placeholder": "ｶﾌﾞｼｷｶﾞｲｼｬ"}},
          {"label": "通貨", "field": "bankInfo.currency", "widget": "select", "span": 6, "props": {"placeholder": "選択", "options": [{"label":"日本円 (JPY)", "value":"JPY"},{"label":"米ドル (USD)", "value":"USD"},{"label":"人民元 (CNY)", "value":"CNY"}]}}
        ]}
      ]}
    ]
  }
}'::jsonb,
updated_at = now()
WHERE name = 'businesspartner' AND is_active = true;


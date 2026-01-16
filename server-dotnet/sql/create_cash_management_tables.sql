-- ============================================
-- 現金・小口現金管理機能
-- Schema-based 設計に準拠
-- ============================================

-- ============================================
-- 1. 現金口座マスタ（cash_account）
-- ============================================
CREATE TABLE IF NOT EXISTS cash_accounts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    -- 生成列（検索・インデックス用）
    cash_code TEXT GENERATED ALWAYS AS (payload->>'cashCode') STORED,
    account_code TEXT GENERATED ALWAYS AS (payload->>'accountCode') STORED,
    cash_type TEXT GENERATED ALWAYS AS (payload->>'cashType') STORED,
    name TEXT GENERATED ALWAYS AS (payload->>'name') STORED,
    is_active BOOLEAN GENERATED ALWAYS AS (COALESCE((payload->>'isActive')::boolean, true)) STORED,
    CONSTRAINT uk_cash_account UNIQUE (company_code, cash_code)
);

CREATE INDEX IF NOT EXISTS idx_cash_accounts_company ON cash_accounts(company_code);
CREATE INDEX IF NOT EXISTS idx_cash_accounts_type ON cash_accounts(company_code, cash_type);

-- ============================================
-- 2. 現金取引（cash_transaction）
-- ============================================
CREATE TABLE IF NOT EXISTS cash_transactions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    -- 生成列
    cash_code TEXT GENERATED ALWAYS AS (payload->>'cashCode') STORED,
    transaction_no TEXT GENERATED ALWAYS AS (payload->>'transactionNo') STORED,
    transaction_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'transactionDate')) STORED,
    transaction_type TEXT GENERATED ALWAYS AS (payload->>'transactionType') STORED,
    amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'amount')) STORED,
    CONSTRAINT uk_cash_transaction UNIQUE (company_code, cash_code, transaction_no)
);

CREATE INDEX IF NOT EXISTS idx_cash_transactions_company ON cash_transactions(company_code);
CREATE INDEX IF NOT EXISTS idx_cash_transactions_date ON cash_transactions(company_code, cash_code, transaction_date DESC);
CREATE INDEX IF NOT EXISTS idx_cash_transactions_type ON cash_transactions(company_code, transaction_type);

-- ============================================
-- 3. 現金実査（cash_count）
-- ============================================
CREATE TABLE IF NOT EXISTS cash_counts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now(),
    -- 生成列
    cash_code TEXT GENERATED ALWAYS AS (payload->>'cashCode') STORED,
    count_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'countDate')) STORED,
    book_balance NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'bookBalance')) STORED,
    actual_balance NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'actualBalance')) STORED,
    difference NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'difference')) STORED
);

CREATE INDEX IF NOT EXISTS idx_cash_counts_company ON cash_counts(company_code);
CREATE INDEX IF NOT EXISTS idx_cash_counts_date ON cash_counts(company_code, cash_code, count_date DESC);

-- ============================================
-- 4. 小口現金補充申請（petty_cash_replenishment）
-- ============================================
CREATE TABLE IF NOT EXISTS petty_cash_replenishments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_code TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now(),
    -- 生成列
    cash_code TEXT GENERATED ALWAYS AS (payload->>'cashCode') STORED,
    request_date DATE GENERATED ALWAYS AS (fn_jsonb_date(payload, 'requestDate')) STORED,
    replenish_amount NUMERIC GENERATED ALWAYS AS (fn_jsonb_numeric(payload, 'replenishAmount')) STORED,
    status TEXT GENERATED ALWAYS AS (payload->>'status') STORED
);

CREATE INDEX IF NOT EXISTS idx_petty_cash_replenishments_company ON petty_cash_replenishments(company_code);
CREATE INDEX IF NOT EXISTS idx_petty_cash_replenishments_status ON petty_cash_replenishments(company_code, status);

-- ============================================
-- 5. Schema 定義
-- ============================================

-- cash_account schema
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, ai_hints)
VALUES (
    NULL,
    'cash_account',
    1,
    TRUE,
    '{
      "$schema": "https://json-schema.org/draft/2019-09/schema",
      "type": "object",
      "required": ["cashCode", "accountCode", "cashType", "name"],
      "properties": {
        "cashCode": {
          "type": "string",
          "minLength": 1,
          "maxLength": 20,
          "description": "現金コード"
        },
        "accountCode": {
          "type": "string",
          "description": "紐付く勘定科目コード"
        },
        "cashType": {
          "type": "string",
          "enum": ["cash", "petty_cash"],
          "description": "種別（現金/小口現金）"
        },
        "name": {
          "type": "string",
          "maxLength": 100,
          "description": "名称"
        },
        "currency": {
          "type": "string",
          "default": "JPY",
          "description": "通貨"
        },
        "imprestSystem": {
          "type": "boolean",
          "default": false,
          "description": "定額資金制かどうか"
        },
        "imprestAmount": {
          "type": "number",
          "description": "定額（小口現金の場合）"
        },
        "replenishThreshold": {
          "type": "number",
          "description": "補充閾値"
        },
        "custodianId": {
          "type": "string",
          "description": "出納担当者ID"
        },
        "custodianName": {
          "type": "string",
          "description": "出納担当者名"
        },
        "departmentCode": {
          "type": "string",
          "description": "所属部門コード"
        },
        "departmentName": {
          "type": "string",
          "description": "所属部門名"
        },
        "currentBalance": {
          "type": "number",
          "default": 0,
          "description": "現在残高"
        },
        "lastReconciledAt": {
          "type": "string",
          "format": "date-time",
          "description": "最終実査日時"
        },
        "isActive": {
          "type": "boolean",
          "default": true,
          "description": "有効フラグ"
        },
        "memo": {
          "type": "string",
          "description": "備考"
        }
      }
    }'::jsonb,
    '{
      "list": {
        "columns": ["cash_code", "name", "cash_type", "account_code", "is_active"]
      },
      "form": {
        "labelWidth": "140px",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "cashCode", "label": "現金コード", "span": 8, "props": {"placeholder": "例: CASH-001"}},
              {"field": "name", "label": "名称", "span": 8},
              {"field": "cashType", "label": "種別", "span": 8, "widget": "select", "props": {
                "options": [
                  {"label": "現金", "value": "cash"},
                  {"label": "小口現金", "value": "petty_cash"}
                ]
              }}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "accountCode", "label": "勘定科目", "span": 8, "widget": "account-select"},
              {"field": "currency", "label": "通貨", "span": 8, "widget": "select", "props": {
                "options": [
                  {"label": "JPY", "value": "JPY"},
                  {"label": "USD", "value": "USD"}
                ]
              }},
              {"field": "isActive", "label": "有効", "span": 8, "widget": "switch"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "imprestSystem", "label": "定額資金制", "span": 8, "widget": "switch"},
              {"field": "imprestAmount", "label": "定額", "span": 8, "widget": "number"},
              {"field": "replenishThreshold", "label": "補充閾値", "span": 8, "widget": "number"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "departmentCode", "label": "部門", "span": 12, "widget": "department-select"},
              {"field": "custodianId", "label": "出納担当者", "span": 12, "widget": "employee-select"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "memo", "label": "備考", "span": 24, "widget": "textarea"}
            ]
          }
        ]
      }
    }'::jsonb,
    '{
      "filters": ["cash_code", "cash_type", "is_active"],
      "sorts": ["cash_code", "name"]
    }'::jsonb,
    '{"displayNames": {"ja": "現金口座", "zh": "现金账户", "en": "Cash Account"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- cash_transaction schema
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, ai_hints)
VALUES (
    NULL,
    'cash_transaction',
    1,
    TRUE,
    '{
      "$schema": "https://json-schema.org/draft/2019-09/schema",
      "type": "object",
      "required": ["cashCode", "transactionDate", "transactionType", "amount", "description"],
      "properties": {
        "cashCode": {
          "type": "string",
          "description": "現金コード"
        },
        "transactionNo": {
          "type": "string",
          "description": "取引番号（自動採番）"
        },
        "transactionDate": {
          "type": "string",
          "format": "date",
          "description": "取引日"
        },
        "transactionType": {
          "type": "string",
          "enum": ["receipt", "payment", "replenish", "adjustment"],
          "description": "取引種別"
        },
        "amount": {
          "type": "number",
          "minimum": 0,
          "description": "金額"
        },
        "balanceAfter": {
          "type": "number",
          "description": "取引後残高"
        },
        "counterparty": {
          "type": "string",
          "description": "相手先"
        },
        "description": {
          "type": "string",
          "description": "摘要"
        },
        "category": {
          "type": "string",
          "description": "支出カテゴリ"
        },
        "voucherId": {
          "type": "string",
          "description": "紐付く仕訳ID"
        },
        "voucherNo": {
          "type": "string",
          "description": "紐付く仕訳番号"
        },
        "receiptImageUrl": {
          "type": "string",
          "description": "領収書画像URL"
        },
        "approvedBy": {
          "type": "string",
          "description": "承認者ID"
        },
        "approvedAt": {
          "type": "string",
          "format": "date-time",
          "description": "承認日時"
        },
        "createdBy": {
          "type": "string",
          "description": "作成者ID"
        },
        "createdByName": {
          "type": "string",
          "description": "作成者名"
        }
      }
    }'::jsonb,
    '{
      "list": {
        "columns": ["transaction_date", "transaction_no", "transaction_type", "amount", "cash_code"]
      },
      "form": {
        "labelWidth": "120px",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "transactionDate", "label": "日付", "span": 8, "widget": "date"},
              {"field": "transactionType", "label": "種別", "span": 8, "widget": "select", "props": {
                "options": [
                  {"label": "入金", "value": "receipt"},
                  {"label": "出金", "value": "payment"},
                  {"label": "補充", "value": "replenish"},
                  {"label": "調整", "value": "adjustment"}
                ]
              }},
              {"field": "amount", "label": "金額", "span": 8, "widget": "number"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "category", "label": "カテゴリ", "span": 8, "widget": "select"},
              {"field": "counterparty", "label": "相手先", "span": 16}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "description", "label": "摘要", "span": 24}
            ]
          }
        ]
      }
    }'::jsonb,
    '{
      "filters": ["cash_code", "transaction_date", "transaction_type"],
      "sorts": ["transaction_date", "transaction_no"]
    }'::jsonb,
    '{"displayNames": {"ja": "現金取引", "zh": "现金交易", "en": "Cash Transaction"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- cash_count schema
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, ai_hints)
VALUES (
    NULL,
    'cash_count',
    1,
    TRUE,
    '{
      "$schema": "https://json-schema.org/draft/2019-09/schema",
      "type": "object",
      "required": ["cashCode", "countDate", "actualBalance"],
      "properties": {
        "cashCode": {
          "type": "string",
          "description": "現金コード"
        },
        "countDate": {
          "type": "string",
          "format": "date",
          "description": "実査日"
        },
        "bookBalance": {
          "type": "number",
          "description": "帳簿残高"
        },
        "actualBalance": {
          "type": "number",
          "description": "実際残高"
        },
        "difference": {
          "type": "number",
          "description": "差異"
        },
        "adjustmentVoucherId": {
          "type": "string",
          "description": "差異調整仕訳ID"
        },
        "adjustmentVoucherNo": {
          "type": "string",
          "description": "差異調整仕訳番号"
        },
        "adjustmentReason": {
          "type": "string",
          "description": "差異理由"
        },
        "memo": {
          "type": "string",
          "description": "備考"
        },
        "countedBy": {
          "type": "string",
          "description": "実査者ID"
        },
        "countedByName": {
          "type": "string",
          "description": "実査者名"
        },
        "verifiedBy": {
          "type": "string",
          "description": "検証者ID"
        },
        "verifiedAt": {
          "type": "string",
          "format": "date-time",
          "description": "検証日時"
        }
      }
    }'::jsonb,
    '{
      "list": {
        "columns": ["count_date", "cash_code", "book_balance", "actual_balance", "difference"]
      },
      "form": {
        "labelWidth": "120px",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "countDate", "label": "実査日", "span": 8, "widget": "date"},
              {"field": "bookBalance", "label": "帳簿残高", "span": 8, "widget": "number", "props": {"disabled": true}},
              {"field": "actualBalance", "label": "実際残高", "span": 8, "widget": "number"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "difference", "label": "差異", "span": 8, "widget": "number", "props": {"disabled": true}},
              {"field": "adjustmentReason", "label": "差異理由", "span": 16}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "memo", "label": "備考", "span": 24, "widget": "textarea"}
            ]
          }
        ]
      }
    }'::jsonb,
    '{
      "filters": ["cash_code", "count_date"],
      "sorts": ["count_date"]
    }'::jsonb,
    '{"displayNames": {"ja": "現金実査", "zh": "现金盘点", "en": "Cash Count"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- petty_cash_replenishment schema
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, ai_hints)
VALUES (
    NULL,
    'petty_cash_replenishment',
    1,
    TRUE,
    '{
      "$schema": "https://json-schema.org/draft/2019-09/schema",
      "type": "object",
      "required": ["cashCode", "requestDate", "replenishAmount"],
      "properties": {
        "cashCode": {
          "type": "string",
          "description": "現金コード"
        },
        "requestDate": {
          "type": "string",
          "format": "date",
          "description": "申請日"
        },
        "currentBalance": {
          "type": "number",
          "description": "現在残高"
        },
        "imprestAmount": {
          "type": "number",
          "description": "定額"
        },
        "replenishAmount": {
          "type": "number",
          "description": "補充金額"
        },
        "expenseSummary": {
          "type": "array",
          "description": "支出内訳",
          "items": {
            "type": "object",
            "properties": {
              "category": {"type": "string"},
              "count": {"type": "integer"},
              "amount": {"type": "number"}
            }
          }
        },
        "status": {
          "type": "string",
          "enum": ["pending", "approved", "completed", "rejected"],
          "default": "pending",
          "description": "ステータス"
        },
        "replenishMethod": {
          "type": "string",
          "enum": ["bank_withdrawal", "main_cash"],
          "description": "補充方法"
        },
        "replenishVoucherId": {
          "type": "string",
          "description": "補充仕訳ID"
        },
        "replenishVoucherNo": {
          "type": "string",
          "description": "補充仕訳番号"
        },
        "requestedBy": {
          "type": "string",
          "description": "申請者ID"
        },
        "requestedByName": {
          "type": "string",
          "description": "申請者名"
        },
        "approvedBy": {
          "type": "string",
          "description": "承認者ID"
        },
        "approvedAt": {
          "type": "string",
          "format": "date-time",
          "description": "承認日時"
        },
        "completedAt": {
          "type": "string",
          "format": "date-time",
          "description": "完了日時"
        },
        "memo": {
          "type": "string",
          "description": "備考"
        }
      }
    }'::jsonb,
    '{
      "list": {
        "columns": ["request_date", "cash_code", "replenish_amount", "status"]
      },
      "form": {
        "labelWidth": "140px",
        "layout": [
          {
            "type": "grid",
            "cols": [
              {"field": "requestDate", "label": "申請日", "span": 8, "widget": "date"},
              {"field": "currentBalance", "label": "現在残高", "span": 8, "widget": "number", "props": {"disabled": true}},
              {"field": "replenishAmount", "label": "補充金額", "span": 8, "widget": "number"}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "replenishMethod", "label": "補充方法", "span": 12, "widget": "select", "props": {
                "options": [
                  {"label": "銀行から引出", "value": "bank_withdrawal"},
                  {"label": "本社現金から", "value": "main_cash"}
                ]
              }},
              {"field": "status", "label": "ステータス", "span": 12, "widget": "select", "props": {"disabled": true}}
            ]
          },
          {
            "type": "grid",
            "cols": [
              {"field": "memo", "label": "備考", "span": 24, "widget": "textarea"}
            ]
          }
        ]
      }
    }'::jsonb,
    '{
      "filters": ["cash_code", "status", "request_date"],
      "sorts": ["request_date"]
    }'::jsonb,
    '{"displayNames": {"ja": "小口現金補充", "zh": "小额现金补充", "en": "Petty Cash Replenishment"}}'::jsonb
)
ON CONFLICT (company_code, name, version) DO NOTHING;

-- ============================================
-- 6. 権限能力（Capabilities）
-- ============================================
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES 
('cash:read', '{"ja":"現金管理参照","zh":"查看现金管理","en":"View Cash Management"}', 'finance', 'action', false,
 '{"ja":"現金出納帳を閲覧する権限","zh":"允许查看现金出纳账","en":"Permission to view cash ledger"}', 50),
('cash:manage', '{"ja":"現金管理","zh":"现金管理","en":"Cash Management"}', 'finance', 'action', false,
 '{"ja":"現金入出金を登録する権限","zh":"允许登记现金收支","en":"Permission to manage cash transactions"}', 51),
('cash:reconcile', '{"ja":"現金実査","zh":"现金盘点","en":"Cash Reconciliation"}', 'finance', 'action', true,
 '{"ja":"現金実査を行う権限","zh":"允许进行现金盘点","en":"Permission to reconcile cash"}', 52)
ON CONFLICT (cap_code) DO NOTHING;

-- ============================================
-- 7. 菜単定義（permission_menus）
-- ============================================
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES 
('finance', 'cash.ledger', '{"ja":"現金出納帳","zh":"现金出纳账","en":"Cash Ledger"}', '/cash/ledger', ARRAY['cash:read'], 20),
('finance', 'cash.petty', '{"ja":"小口現金管理","zh":"小额现金管理","en":"Petty Cash"}', '/cash/petty', ARRAY['cash:read'], 21),
('finance', 'cash.accounts', '{"ja":"現金口座設定","zh":"现金账户设置","en":"Cash Accounts"}', '/cash/accounts', ARRAY['cash:manage'], 22)
ON CONFLICT (menu_key) DO NOTHING;

-- ============================================
-- 8. 支出カテゴリマスタ（初期データ）
-- ============================================
-- 会社設定に追加するカテゴリ例
-- company_settings.payload.cashExpenseCategories = [...]

SELECT 'Cash management tables and schemas created successfully' as result;


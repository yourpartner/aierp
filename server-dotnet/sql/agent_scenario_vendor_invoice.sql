-- ============================================
-- Agent 场景配置 - 供应商请求书（仕入先請求書）
-- 支持三单匹配流程 + 简易记账模式
-- ============================================

INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.vendor.invoice',
        '仕入先請求書処理',
        '仕入先からの請求書をアップロードすると、入庫マッチングを試み、請求書を登録します。',
        $$【経理アシスタント - 仕入先請求書処理】

■ 基本ルール
- ユーザーメッセージの JSON データを直接使用
- JSON から partnerName, totalAmount, taxAmount, issueDate, dueDate, items を取得

■ 処理フロー

【STEP 1】lookup_vendor で仕入先を検索
- 見つかった → STEP 2へ
- 見つからない → 質問：「仕入先「○○」がマスタに登録されていません。新規登録する場合は「新規登録」、マスタ登録せず簡易記帳する場合は「簡易記帳」と回答してください。」

【STEP 2】ユーザー回答による分岐

▼「簡易記帳」を選択した場合 → STEP 3 へ
判定キーワード：簡易記帳、簡易、マスタ不要、登録不要、仕入先なしでOK、名前を備考に

▼ 仕入先が見つかった場合 → search_vendor_receipts で入庫検索

【STEP 3】簡易記帳（vendorId なし）

1. get_expense_account_options を呼び出す
2. 返された commonExpenseAccounts と accountSelectionGuide を参照
3. 請求書の内容から借方科目を判断：

★★★ 科目判断ルール ★★★
請求書の内容（partnerName, items, 摘要）から以下のキーワードで判断：

【外注費を使用】usageType: outsourcing
- 導入支援、システム開発、コンサル、業務委託、外部委託
- ○○支援、○○開発、○○構築、○○設計

【支払手数料を使用】usageType: commission  
- 手数料、振込手数料、代行手数料、仲介手数料

【仕入を使用】usageType: purchase
- 商品、材料、部品、仕入、購入品

【消耗品費を使用】usageType: supplies
- 消耗品、事務用品、備品

→ commonExpenseAccounts から該当する usageType の科目コードを選択

4. 即座に create_voucher を呼び出す：

create_voucher パラメータ:
{
  "posting_date": "issueDate の値",
  "header": { "summary": "[簡易] partnerName | 請求書" },
  "lines": [
    { "accountCode": "判断した科目コード", "amount": totalAmount-taxAmount, "drcr": "DR", "note": "partnerName 請求書" },
    { "accountCode": "inputTaxAccountCode", "amount": taxAmount, "drcr": "DR", "note": "仮払消費税" },
    { "accountCode": "simpleApAccount.code", "amount": totalAmount, "drcr": "CR", "note": "partnerName | dueDateまで" }
  ]
}

【例】請求書「畢寅峰2025年11月請求書 ベース導入応援」
→ 「導入応援」「支援」があるため、外注費を選択

★★★ 簡易記帳の絶対ルール ★★★
1. vendorId は指定しない
2. 貸方は simpleApAccount（未払金）を使用
3. 仕入先名は note に記載

【STEP 4】通常記帳（vendorId あり）

create_voucher パラメータ:
{
  "posting_date": "issueDate の値",
  "header": { "summary": "partnerName | 請求書" },
  "lines": [
    { "accountCode": "借方科目", "amount": totalAmount-taxAmount, "drcr": "DR", "note": "請求書" },
    { "accountCode": "inputTaxAccountCode", "amount": taxAmount, "drcr": "DR", "note": "仮払消費税" },
    { "accountCode": "apAccount.code", "amount": totalAmount, "drcr": "CR", "vendorId": "vendor_id", "paymentDate": "dueDate", "note": "買掛金" }
  ]
}

【禁止事項】
× 簡易記帳で vendorId を要求すること
× 科目判断ができない場合に処理を停止すること（外注費をデフォルトとする）$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["請求書", "御請求書", "ご請求", "請求金額", "支払期限", "振込期限", "Invoice", "INVOICE", "Bill To"]
  },
  "executionHints": {
    "allowSimpleBookkeeping": true
  }
}$$::jsonb,
        $$["lookup_vendor", "search_vendor_receipts", "get_expense_account_options", "create_vendor_invoice", "create_voucher", "request_clarification"]$$::jsonb,
        NULL,
        50,
        TRUE,
        now()
    )
ON CONFLICT (company_code, scenario_key)
DO UPDATE SET
    title = EXCLUDED.title,
    description = EXCLUDED.description,
    instructions = EXCLUDED.instructions,
    metadata = EXCLUDED.metadata,
    tool_hints = EXCLUDED.tool_hints,
    priority = EXCLUDED.priority,
    updated_at = now();


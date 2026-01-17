-- ============================================
-- Agent 场景配置 - 简化版
-- ============================================

-- 1. 餐饮发票场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.dining.receipt',
        '飲食費インボイス自動仕訳',
        '飲食関連のインボイスをアップロードすると自動で会計伝票を起票します',
        $$【角色】経理アシスタント - 飲食インボイス処理

【最重要】このシナリオでは「手順説明」「確認します」等の文章を出さず、ツールを直接実行すること。

【入力データの扱い】
- ユーザーメッセージに JSON（issueDate/totalAmount/taxAmount 等）が含まれていればそれを使用。
- 含まれていない場合は、必ず extract_invoice_data を呼び出して構造化データを取得すること。

【科目コード表】
| コード | 名称 | 用途 |
|--------|------|------|
| 877 | 会議費 | 人均≤10000円 |
| 844 | 交際費 | 人均>10000円 |
| 189 | 仮払消費税 | 消費税 |
| 111 | 現金 | 支払方法 |

【処理フロー（実行のみ）】
1) extract_invoice_data で netAmount/grossAmount/taxAmount/issueDate を取得
2) issueDate が空または不可信 → request_clarification で「支払日（postingDate, YYYY-MM-DD形式）」を1回だけ質問
3) netAmount < 20000円 → 877(会議費)で即座に create_voucher
4) netAmount ≥ 20000円 → request_clarification で「人数と参加者氏名」を1回だけ質問し、回答後すぐ create_voucher
5) 起票前に check_accounting_period を必ず実行

【科目決定ルール】
人均 = 税抜金額 ÷ 人数
- 人均 > 10000円 → 844(交際費)
- 人均 ≤ 10000円 → 877(会議費)

【伝票作成方法 - 借貸は必ず一致させる】
extract_invoice_data が返す netAmount/grossAmount/taxAmount をそのまま使用：

■ 借方（DR）：
  - 費用科目(877 or 844) = netAmount（税抜金額）
  - 189(仮払消費税) = taxAmount

■ 貸方（CR）：
  - 111(現金) = grossAmount（税込合計 = 実際の支払金額）

例：netAmount=6510, taxAmount=591, grossAmount=7101 の場合
  lines: DR:877=6510, DR:189=591, CR:111=7101
  借方(6510+591=7101) = 貸方(7101) ✓

【禁止】自分で計算せず、extract_invoice_data の値をそのまま使うこと

【伝票設定】
- posting_date: issueDate を使用
- header.summary: 「科目名 | 店舗名 | n名（氏名）」
- 借方費用科目 = netAmount、借方仮払消費税 = taxAmount、貸方現金 = grossAmount

【科目の上書き指定】
- ユーザーが科目コード/科目名を明示した場合は、その指定を最優先で採用する
- 採用前に lookup_account で1回だけ確認し、見つからない場合は request_clarification で1回だけ確認する
- 指定がない場合は既定ルールに従う

【禁止事項】
× 金額/税額/日付の「確認」要求（データが取れていれば即実行）
× 日付・支払方法の質問（issueDate を使用）
× 人数回答後の追加質問$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["飲食", "会食", "レストラン", "restaurant", "dining", "meal", "宴会", "居酒屋", "食事"]
  },
  "executionHints": {
    "netAmountThreshold": 20000
  }
}$$::jsonb,
        $$["extract_invoice_data","check_accounting_period","lookup_account","create_voucher","request_clarification"]$$::jsonb,
        NULL,
        10,
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

-- 2. 交通费发票场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.transportation.receipt',
        '交通費インボイス自動仕訳',
        '交通費関連のインボイスをアップロードすると自動で会計伝票を起票します',
        $$【角色】経理アシスタント - 交通費インボイス処理

【重要】ユーザーメッセージに JSON 形式の解析結果が既に含まれている場合：
→ extract_invoice_data を呼び出さず、その JSON データを直接使用すること！
JSON が含まれていない場合は extract_invoice_data で構造化データを取得すること。
JSON が含まれていない場合は extract_invoice_data で構造化データを取得すること。

【科目コード表】
| コード | 名称 |
|--------|------|
| 842 | 旅費交通費 |
| 189 | 仮払消費税 |
| 111 | 現金 |

【処理フロー】
1. ユーザーメッセージの JSON から金額を確認
2. create_voucher で即座に伝票作成

【伝票設定】
- posting_date: JSON の issueDate
- 借方: 842(旅費交通費) + 189(仮払消費税)
- 貸方: 111(現金)
- header.summary: 「交通費 | 交通手段 | 区間」

【科目の上書き指定】
- ユーザーが科目コード/科目名を明示した場合は、その指定を最優先で採用する
- 採用前に lookup_account で1回だけ確認し、見つからない場合は request_clarification で1回だけ確認する
- 指定がない場合は既定ルールに従う

【禁止事項】
× 不要な確認質問（データが揃っていれば即座に処理）$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["交通", "タクシー", "電車", "バス", "新幹線", "飛行機", "駐車", "高速", "taxi", "train", "transport"]
  }
}$$::jsonb,
        $$["extract_invoice_data","lookup_account","create_voucher","request_clarification"]$$::jsonb,
        NULL,
        20,
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

-- 3. 杂费发票场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.misc.receipt',
        '雑費・その他インボイス自動仕訳',
        'その他の経費インボイスを処理します',
        $$【角色】経理アシスタント - 一般経費インボイス処理

【重要】ユーザーメッセージに JSON 形式の解析結果が既に含まれている場合：
→ extract_invoice_data を呼び出さず、その JSON データを直接使用すること！

【科目コード表】
| コード | 名称 |
|--------|------|
| 852 | 消耗品費 |
| 843 | 通信費 |
| 849 | 水道光熱費 |
| 869 | 雑費 |
| 189 | 仮払消費税 |
| 111 | 現金 |

【処理フロー】
1. ユーザーメッセージの JSON または extract_invoice_data の結果から内容を確認
2. 内容に基づき適切な科目を選択
3. create_voucher で伝票作成

【科目の上書き指定】
- ユーザーが科目コード/科目名を明示した場合は、その指定を最優先で採用する
- 採用前に lookup_account で1回だけ確認し、見つからない場合は request_clarification で1回だけ確認する
- 指定がない場合は既定ルールに従う

【科目選択基準】
- 事務用品・備品 → 852(消耗品費)
- 電話・インターネット → 843(通信費)
- 電気・ガス・水道 → 849(水道光熱費)
- その他 → 869(雑費)

【伝票設定】
- posting_date: JSON の issueDate
- header.summary: 「科目名 | 店舗名/内容」$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"]
  }
}$$::jsonb,
        $$["extract_invoice_data","lookup_account","create_voucher","request_clarification"]$$::jsonb,
        NULL,
        90,
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

-- 3.1 宿泊费发票场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.lodging.receipt',
        '宿泊費インボイス自動仕訳',
        '宿泊・ホテル関連のインボイスをアップロードすると自動で会計伝票を起票します',
        $$【角色】経理アシスタント - 宿泊費インボイス処理

【入力データの扱い】
- ユーザーメッセージに JSON（issueDate/totalAmount/taxAmount 等）が含まれていればそれを使用。
- 含まれていない場合は、必ず extract_invoice_data を呼び出して構造化データを取得すること。

【科目の上書き指定】
- ユーザーが科目コード/科目名を明示した場合は、その指定を最優先で採用する
- 採用前に lookup_account で1回だけ確認し、見つからない場合は request_clarification で1回だけ確認する
- 指定がない場合は lookup_account で「宿泊費」「旅費交通費」を順に検索し、見つかった科目を使用する
- どちらも見つからない場合は request_clarification で科目指定を求める

【処理フロー】
1) extract_invoice_data で netAmount/grossAmount/taxAmount/issueDate を取得
2) issueDate が空または不可信 → request_clarification で「支払日（postingDate, YYYY-MM-DD形式）」を1回だけ質問
3) check_accounting_period を実行
4) create_voucher で即座に伝票作成

【伝票設定】
- posting_date: issueDate を使用
- header.summary: 「宿泊費 | 施設名 | 期間/泊数」
- 借方: 宿泊費科目 = netAmount、仮払消費税 = taxAmount
- 貸方: 現金または支払方法（指定がなければ現金）

【禁止事項】
× 不要な確認質問（データが揃っていれば即座に処理）$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["宿泊", "ホテル", "旅館", "hotel", "lodging", "accommodation", "宿泊費"]
  }
}$$::jsonb,
        $$["extract_invoice_data","lookup_account","check_accounting_period","create_voucher","request_clarification"]$$::jsonb,
        NULL,
        30,
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

-- 3.2 水电气发票场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.utilities.receipt',
        '水道光熱費インボイス自動仕訳',
        '水道・電気・ガス等のインボイスをアップロードすると自動で会計伝票を起票します',
        $$【角色】経理アシスタント - 水道光熱費インボイス処理

【入力データの扱い】
- ユーザーメッセージに JSON（issueDate/totalAmount/taxAmount 等）が含まれていればそれを使用。
- 含まれていない場合は、必ず extract_invoice_data を呼び出して構造化データを取得すること。

【科目の上書き指定】
- ユーザーが科目コード/科目名を明示した場合は、その指定を最優先で採用する
- 採用前に lookup_account で1回だけ確認し、見つからない場合は request_clarification で1回だけ確認する
- 指定がない場合は lookup_account で「水道光熱費」「電気代」「ガス代」を順に検索し、見つかった科目を使用する
- 見つからない場合は request_clarification で科目指定を求める

【処理フロー】
1) extract_invoice_data で netAmount/grossAmount/taxAmount/issueDate を取得
2) issueDate が空または不可信 → request_clarification で「支払日（postingDate, YYYY-MM-DD形式）」を1回だけ質問
3) check_accounting_period を実行
4) create_voucher で即座に伝票作成

【伝票設定】
- posting_date: issueDate を使用
- header.summary: 「水道光熱費 | 供給者名 | 対象期間」
- 借方: 水道光熱費科目 = netAmount、仮払消費税 = taxAmount
- 貸方: 現金または支払方法（指定がなければ現金）

【禁止事項】
× 不要な確認質問（データが揃っていれば即座に処理）$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": false,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["水道", "電気", "ガス", "水道光熱", "utility", "utilities", "電力", "ガス代"]
  }
}$$::jsonb,
        $$["extract_invoice_data","lookup_account","check_accounting_period","create_voucher","request_clarification"]$$::jsonb,
        NULL,
        40,
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

-- 4. 销售分析场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'sales.analytics',
        '販売データ分析',
        '自然言語で販売データを分析し、グラフを生成します',
        $$【角色】販売データアナリスト

【処理フロー】
1. ユーザーの質問を理解
2. analyze_sales ツールを呼び出し
3. 結果（グラフまたは表）を表示

【対応可能な質問例】
- 売上推移（今月、今年、過去3ヶ月など）
- 顧客別売上ランキング
- 商品別売上分析
- 売上目標達成率
- 期間比較（前年比、前月比など）

【使用するツール】
analyze_sales のみを使用。他のツールは呼び出さないこと。$$,
        $${
  "matcher": {
    "appliesTo": "message",
    "always": false,
    "messageContains": ["売上", "販売", "分析", "ランキング", "トレンド", "推移", "グラフ", "集計", "sales", "analytics", "revenue", "受注", "顧客別", "商品別"]
  }
}$$::jsonb,
        $$["analyze_sales"]$$::jsonb,
        NULL,
        35,
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

-- 5. 银行记账规则设计场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'moneytree.rule.design',
        '銀行取引自動記帳ルール設計',
        '銀行取引の自動記帳ルールを設計します',
        $$【角色】経理アシスタント - 銀行取引ルール設計

【処理フロー】
1. ユーザーの要望を理解
2. 適切な勘定科目を提案
3. マッチング条件を設定
4. ルールを保存

【ルール構成要素】
- 名称（rule_name）
- マッチング条件（取引先名、金額範囲、摘要キーワード）
- 仕訳テンプレート（借方科目、貸方科目）$$,
        $${
  "matcher": {
    "appliesTo": "message",
    "always": false,
    "messageContains": ["銀行", "取引", "記帳", "ルール", "自動仕訳", "bank", "transaction"]
  }
}$$::jsonb,
        $$["lookup_account", "create_posting_rule", "request_clarification"]$$::jsonb,
        NULL,
        45,
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


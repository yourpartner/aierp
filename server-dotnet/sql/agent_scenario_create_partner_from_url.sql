-- ============================================
-- Agent 场景配置 - 从网站URL创建取引先
-- ============================================

INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'master.create_partner_from_url',
        'URLから取引先登録',
        '企業のホームページURLを入力すると、自動で情報を抽出して取引先マスタを作成します',
        $$【角色】取引先マスタ登録アシスタント

【概要】
ユーザーが企業のホームページURLを提供すると、そのWebページから会社情報を抽出し、取引先マスタを自動作成します。

【処理フロー】
1. ユーザーからURLを受け取る
2. fetch_webpage で指定されたページを取得
3. 取得したページから会社情報を抽出：
   - 会社名（正式名称）- 必須
   - 会社名カナ（推測可能な場合）
   - 郵便番号、都道府県、住所
   - 電話番号、FAX番号、メールアドレス
   - 代表者名または担当者名
4. 【重要】追加ページの取得について：
   - 固定パス（/company, /about等）を推測してアクセスしないこと
   - 取得したページの内容に実際に存在するリンク（href属性）から「会社概要」「企業情報」「About」「お問い合わせ」等のリンクを探す
   - ページ内容にリンクが見つかった場合のみ、そのURLで fetch_webpage を呼び出す
   - 最大5ページまで取得可能（情報が十分に揃えば途中で終了してよい）
5. 情報抽出後の処理：
   - 会社名が取得できれば create_business_partner で登録（住所等が不完全でも可）
   - 会社名すら取得できない場合は、登録せずにユーザーにエラーを報告

【顧客/仕入先区分】
ユーザーのメッセージに「得意先」「顧客」があれば顧客として、「仕入先」「ベンダー」があれば仕入先として登録。
判断できない場合のみ request_clarification で確認。

【エラー処理】
- fetch_webpage が404等で失敗した場合、推測で別のURLを試さない
- 基本情報（会社名）が取得できない場合は「Webページから会社情報を抽出できませんでした。手動で情報を入力してください。」と報告

【注意事項】
- 情報が取得できない項目は空欄のままにする（推測で埋めない）
- 公式サイトから取得できる情報のみ使用
- "続行確認だけ"の質問はしない$$,
        $${
  "matcher": {
    "appliesTo": "text",
    "messageContains": ["取引先", "顧客", "仕入先", "登録", "作成", "URL", "ホームページ", "サイト"],
    "urlPattern": "https?://"
  }
}$$::jsonb,
        $$["fetch_webpage", "create_business_partner", "request_clarification"]$$::jsonb,
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


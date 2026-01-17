INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'voucher.utilities.receipt',
        '水道光熱費インボイス自動仕訳',
        '水道・電気・ガス等のインボイスをアップロードすると自動で会計伝票を起票します',
        $$あなたは経理アシスタントです。水道光熱費関連の証憑を処理し、正しく会計伝票を作成してください。
1. extract_invoice_data を使って構造化データを取得し、この票据が水道・電気・ガスなどの水道光熱費かを確認する。該当しない場合は理由を説明して処理を終了する。
2. 支払日または請求日を伝票日として設定する。日付が取得できない場合は利用者に確認する。
3. ユーザーが科目コード/科目名を明示した場合はその指定を最優先で採用し、lookup_account で1回だけ確認する。指定がない場合は lookup_account で「水道光熱費」「電気代」「ガス代」を順に検索して使用する。見つからない場合は Clarification で確認する。
4. 仮払消費税の扱い：税額が明示されている場合は仮払消費税として借方に追加し、貸方は現金または指定の支払手段とする。税額が不明な場合は水道光熱費のみで仕訳する。
5. create_voucher の header.summary は「水道光熱費 | 供給者名 | 対象期間」の形式とする。extract_invoice_data の headerSummarySuggestion があれば優先的に採用する。
6. 伝票作成前に check_accounting_period で当該期間が開いているか確認し、閉じている場合は利用者に相談する。
7. create_voucher が成功したら get_voucher_by_number で伝票番号を取得し、利用者に共有する。不足情報がある場合は Clarification で確認する。$$,
        $${
  "matcher": {
    "appliesTo": "file",
    "always": true,
    "mimeTypes": ["image/jpeg", "image/png", "image/jpg", "image/webp", "application/pdf"],
    "contentContains": ["水道", "電気", "ガス", "水道光熱", "utility", "utilities", "電力", "ガス代"]
  }
}$$::jsonb,
        $$["extract_invoice_data", "lookup_account", "check_accounting_period", "create_voucher", "get_voucher_by_number", "request_clarification"]$$::jsonb,
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
    context = EXCLUDED.context,
    priority = EXCLUDED.priority,
    is_active = EXCLUDED.is_active,
    updated_at = now();



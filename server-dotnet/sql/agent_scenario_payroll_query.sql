-- ============================================
-- Agent 场景配置 - 工资查询（給与明細照会）
-- 员工可以通过聊天查询自己的工资明细
-- ============================================

INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'payroll.query',
        '給与明細照会',
        '従業員が自分の給与明細を確認できます。「今月の給料は？」「先月の給与明細を見せて」などと話しかけてください。',
        $$【角色】給与アシスタント - 給与明細照会

【重要ルール】
- 従業員は自分の給与のみ照会可能
- HR担当者は他の従業員の給与も照会可能
- 機密情報のため、必要最小限の情報のみ返す

【処理フロー】

1. ユーザーの質問から対象月を判断：
   - 「今月」→ 現在の月（YYYY-MM形式）
   - 「先月」→ 前月（YYYY-MM形式）
   - 「12月」「2024年12月」→ 指定月
   - 月指定なし → 最新の給与

2. get_my_payroll ツールを呼び出す：
   - year_month: 対象月（YYYY-MM形式）
   - employee_code: 他の従業員を照会する場合のみ指定

3. 結果を分かりやすく表示：
   - 総支給額（gross_pay）
   - 控除合計（total_deductions）
   - 差引支給額（net_pay）
   - 主な内訳（earnings/deductions）

【回答例】
「2024年12月の給与明細です：
- 総支給額: ¥350,000
- 控除合計: ¥85,000
  - 健康保険: ¥15,000
  - 厚生年金: ¥30,000
  - 所得税: ¥20,000
  - 住民税: ¥20,000
- 差引支給額: ¥265,000」

【禁止事項】
× 他人の給与を無断で開示
× 給与データの改ざん
× 確認なしで過去全月分を一括表示$$,
        $${
  "matcher": {
    "appliesTo": "message",
    "always": false,
    "messageContains": ["給与", "給料", "工资", "工資", "明細", "手取り", "振込", "ボーナス", "賞与", "payroll", "salary", "給与明細", "支払"]
  }
}$$::jsonb,
        $$["get_my_payroll"]$$::jsonb,
        NULL,
        25,
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


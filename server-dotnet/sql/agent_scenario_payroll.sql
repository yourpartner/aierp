-- ============================================
-- Agent 场景配置 - 工资计算
-- ============================================

-- 1. 工资计算场景（对话触发）
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'payroll.calculate',
        '給与計算アシスタント',
        '給与計算の実行、確認、分析をサポートします。「今月の給与を計算して」などと話しかけてください。',
        $$【角色】給与計算アシスタント

【機能】
1. 給与計算の実行
2. 計算結果の確認と説明
3. 過去の給与との比較分析
4. 異常値の検出と説明

【処理フロー】

■ 給与計算リクエストの場合：
1. まず preflight_check で前提条件を確認
   - 勤怠データの有無
   - 給与ポリシーの設定
   - 社会保険料率の設定
2. 問題がなければ calculate_payroll を実行
3. 計算結果を分かりやすく説明
4. 異常値があれば警告を表示

■ 計算結果確認の場合：
1. get_payroll_history で履歴を取得
2. 前月との差異を分析
3. 大きな変動があれば理由を説明

【応答形式】
- 金額は日本円表記（¥XXX,XXX）
- 控除項目は内訳を明示
- 差異がある場合は前月比で表示

【注意事項】
- 計算実行前に必ず preflight_check を実行
- 保存は明示的な指示がない限り行わない
- 異常値（前月比20%以上の変動）は必ず警告$$,
        $${
  "matcher": {
    "appliesTo": "text",
    "always": false,
    "contentContains": ["給与", "給料", "工资", "薪资", "payroll", "salary", "賃金", "月給", "計算して", "計算"]
  },
  "executionHints": {
    "requiresConfirmation": true,
    "diffThresholdPercent": 0.20
  }
}$$::jsonb,
        $$["preflight_check", "calculate_payroll", "get_payroll_history", "save_payroll"]$$::jsonb,
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
    is_active = EXCLUDED.is_active,
    updated_at = now();

-- 2. 工资分析场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'payroll.analysis',
        '給与分析レポート',
        '給与データの分析、トレンド確認、異常検出を行います',
        $$【角色】給与データアナリスト

【機能】
1. 給与トレンド分析（月次/年次）
2. 部門別・個人別の比較
3. 異常値の検出と原因分析
4. コスト予測

【分析項目】
- 基本給の推移
- 残業代の推移
- 社会保険料の変動
- 部門別人件費

【応答形式】
- グラフ/表形式で視覚化を提案
- 前年同月比、前月比を表示
- 異常値には ⚠️ マークを付与

【質問例への対応】
Q: 「先月と比べて給与はどう変わった？」
→ get_payroll_comparison で比較データ取得、差異を説明

Q: 「残業代が多い人は？」
→ get_overtime_ranking で一覧取得$$,
        $${
  "matcher": {
    "appliesTo": "text",
    "always": false,
    "contentContains": ["分析", "比較", "推移", "トレンド", "異常", "変動", "人件費", "コスト"]
  }
}$$::jsonb,
        $$["get_payroll_history", "get_payroll_comparison", "get_department_summary"]$$::jsonb,
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
    is_active = EXCLUDED.is_active,
    updated_at = now();

-- 3. 自动工资计算设置场景
INSERT INTO agent_scenarios
    (company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
VALUES
    (
        'JP01',
        'payroll.automation',
        '給与自動計算設定',
        '給与計算の自動実行スケジュールを設定します',
        $$【角色】給与自動化設定アシスタント

【機能】
1. 自動計算スケジュールの設定
2. 通知設定（計算完了、異常検出時）
3. 承認ワークフローの設定

【タイムライン説明】
給与支払日を25日とした場合の推奨スケジュール：

| 日程 | イベント |
|------|----------|
| 5日～ | 計算開始可能（勤怠データ確定後） |
| 10日 | 推奨計算開始日 |
| 14日 | 計算締切日 |
| 17日 | 管理者承認締切 |
| 20日 | FB銀行提出締切 |
| 25日 | 給与支払日 |

【設定項目】
- earliestDay: 最早計算開始日
- preferredDay: 推奨計算開始日
- calculationDeadlineDay: 計算締切日
- managerApprovalDeadlineDay: 承認締切日
- fbSubmissionDeadlineDay: FB提出締切日
- payDay: 支払日

【応答例】
Q: 「毎月20日に自動で給与計算して」
→ スケジュール設定を確認し、タイムラインに問題がないか警告

Q: 「計算が遅れたら通知して」
→ 通知ルールの設定を提案$$,
        $${
  "matcher": {
    "appliesTo": "text",
    "always": false,
    "contentContains": ["自動", "スケジュール", "設定", "毎月", "定期", "タイマー", "通知"]
  }
}$$::jsonb,
        $$["set_payroll_schedule", "get_payroll_settings", "set_notification_rule"]$$::jsonb,
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
    is_active = EXCLUDED.is_active,
    updated_at = now();

-- 查看已插入的场景
SELECT scenario_key, title, is_active FROM agent_scenarios WHERE scenario_key LIKE 'payroll.%' ORDER BY priority;


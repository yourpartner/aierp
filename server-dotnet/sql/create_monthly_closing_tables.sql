-- ============================================
-- 月次締め（月結）テーブル
-- 日本中小企業向け月次決算フロー対応
-- ============================================

-- 1. 月次締め履歴テーブル
CREATE TABLE IF NOT EXISTS monthly_closings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT NOT NULL,
  year_month TEXT NOT NULL,  -- 'YYYY-MM'
  
  -- 締め状態: open/checking/adjusting/pending_approval/closed/reopened
  status TEXT NOT NULL DEFAULT 'open',
  
  -- チェック結果
  checklist JSONB NOT NULL DEFAULT '[]'::jsonb,
  check_result JSONB,
  check_completed_at TIMESTAMPTZ,
  check_completed_by TEXT,
  
  -- 月末調整仕訳
  adjustment_vouchers JSONB DEFAULT '[]'::jsonb,
  
  -- 消費税集計
  consumption_tax_summary JSONB,
  
  -- 月次報告
  report_data JSONB,
  report_generated_at TIMESTAMPTZ,
  
  -- 承認
  approved_at TIMESTAMPTZ,
  approved_by TEXT,
  approval_comment TEXT,
  
  -- 締め
  closed_at TIMESTAMPTZ,
  closed_by TEXT,
  
  -- 再開
  reopened_at TIMESTAMPTZ,
  reopened_by TEXT,
  reopen_reason TEXT,
  
  -- 監査
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  
  CONSTRAINT uq_monthly_closings_company_month UNIQUE(company_code, year_month)
);

CREATE INDEX IF NOT EXISTS idx_monthly_closings_company_status 
  ON monthly_closings(company_code, status, year_month DESC);
CREATE INDEX IF NOT EXISTS idx_monthly_closings_year_month 
  ON monthly_closings(company_code, year_month DESC);

-- 2. 月次締めチェック項目マスタ
CREATE TABLE IF NOT EXISTS monthly_closing_check_items (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT,  -- NULL=グローバルデフォルト
  item_key TEXT NOT NULL,
  item_name_ja TEXT NOT NULL,
  item_name_en TEXT,
  item_name_zh TEXT,
  category TEXT NOT NULL,  -- receivable/payable/bank/tax/adjustment/report
  check_type TEXT NOT NULL,  -- auto/manual/info
  check_query TEXT,  -- 自動チェック用のロジックキー
  threshold JSONB,  -- 警告閾値設定
  priority INTEGER DEFAULT 100,
  is_required BOOLEAN DEFAULT true,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  
  CONSTRAINT uq_closing_check_items_key UNIQUE(company_code, item_key)
);

CREATE INDEX IF NOT EXISTS idx_closing_check_items_company
  ON monthly_closing_check_items(company_code, is_active, priority);

-- 3. 月次締めチェック結果
CREATE TABLE IF NOT EXISTS monthly_closing_check_results (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  closing_id UUID NOT NULL REFERENCES monthly_closings(id) ON DELETE CASCADE,
  item_key TEXT NOT NULL,
  status TEXT NOT NULL,  -- passed/warning/failed/skipped/pending
  result_data JSONB,  -- チェック結果詳細
  checked_at TIMESTAMPTZ,
  checked_by TEXT,
  comment TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE(closing_id, item_key)
);

CREATE INDEX IF NOT EXISTS idx_closing_check_results_closing 
  ON monthly_closing_check_results(closing_id);
CREATE INDEX IF NOT EXISTS idx_closing_check_results_status
  ON monthly_closing_check_results(closing_id, status);

-- 4. 月次報告テンプレート
CREATE TABLE IF NOT EXISTS monthly_report_templates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  company_code TEXT,
  template_name TEXT NOT NULL,
  template_type TEXT NOT NULL,  -- summary/detailed/tax/custom
  template_config JSONB NOT NULL,
  is_default BOOLEAN DEFAULT false,
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_report_templates_company
  ON monthly_report_templates(company_code, is_active, template_type);

-- ============================================
-- 5. デフォルトチェック項目（日本中小企業向け）
-- ============================================
INSERT INTO monthly_closing_check_items 
  (company_code, item_key, item_name_ja, item_name_en, item_name_zh, category, check_type, priority, is_required)
VALUES
  -- 売掛金関連
  (NULL, 'ar_overdue', '売掛金逾期チェック', 'Overdue Receivables', '应收账款逾期检查', 'receivable', 'auto', 10, true),
  (NULL, 'ar_balance_confirm', '売掛金残高確認', 'AR Balance Confirmation', '应收账款余额确认', 'receivable', 'manual', 12, true),
  
  -- 買掛金関連
  (NULL, 'ap_uncleared', '買掛金未消込チェック', 'Uncleared Payables', '应付账款未清账检查', 'payable', 'auto', 20, true),
  (NULL, 'ap_overdue', '買掛金支払期限チェック', 'Overdue Payables', '应付账款到期检查', 'payable', 'auto', 21, true),
  (NULL, 'ap_balance_confirm', '買掛金残高確認', 'AP Balance Confirmation', '应付账款余额确认', 'payable', 'manual', 22, true),
  
  -- 銀行関連
  (NULL, 'bank_balance', '銀行残高照合', 'Bank Balance Reconciliation', '银行余额核对', 'bank', 'auto', 30, true),
  (NULL, 'bank_unposted', '銀行明細未記帳チェック', 'Unposted Bank Transactions', '银行明细未入账检查', 'bank', 'auto', 31, true),
  
  -- 消費税関連
  (NULL, 'tax_temporary', '仮受・仮払消費税確認', 'Temporary Tax Accounts', '暂收暂付消费税确认', 'tax', 'auto', 40, true),
  (NULL, 'tax_invoice_valid', 'インボイス番号検証', 'Invoice Registration Validation', '发票登记号验证', 'tax', 'auto', 41, true),
  
  -- 月末調整関連
  (NULL, 'depreciation', '減価償却費計上', 'Depreciation Posting', '折旧费计提', 'adjustment', 'auto', 50, true),
  (NULL, 'payroll_posted', '給与計上確認', 'Payroll Posting Check', '工资计提确认', 'adjustment', 'auto', 51, true),
  (NULL, 'prepaid_expense', '前払費用調整', 'Prepaid Expense Adjustment', '预付费用调整', 'adjustment', 'manual', 52, false),
  (NULL, 'accrued_expense', '未払費用計上', 'Accrued Expense Posting', '应计费用计提', 'adjustment', 'manual', 53, false),
  
  -- 報告関連
  (NULL, 'trial_balance', '試算表出力', 'Trial Balance Output', '试算表输出', 'report', 'auto', 60, true),
  (NULL, 'balance_check', '貸借一致確認', 'Debit/Credit Balance Check', '借贷平衡确认', 'report', 'auto', 61, true),
  (NULL, 'monthly_report', '月次報告書生成', 'Monthly Report Generation', '月度报告生成', 'report', 'auto', 62, true)
ON CONFLICT (company_code, item_key) DO NOTHING;

-- ============================================
-- 6. Schema定義（monthly_closing）
-- ============================================
INSERT INTO schemas (company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
VALUES (
  NULL,
  'monthly_closing',
  1,
  TRUE,
  '{
    "type": "object",
    "properties": {
      "yearMonth": {"type": "string", "pattern": "^\\d{4}-\\d{2}$"},
      "status": {"type": "string", "enum": ["open", "checking", "adjusting", "pending_approval", "closed", "reopened"]},
      "checklist": {"type": "array"},
      "checkResult": {"type": "object"},
      "consumptionTaxSummary": {"type": "object"},
      "reportData": {"type": "object"},
      "approvedBy": {"type": "string"},
      "approvedAt": {"type": "string", "format": "date-time"},
      "closedBy": {"type": "string"},
      "closedAt": {"type": "string", "format": "date-time"}
    },
    "required": ["yearMonth"]
  }'::jsonb,
  '{
    "list": {"columns": ["year_month", "status", "check_completed_at", "closed_at"]},
    "form": {"layout": []}
  }'::jsonb,
  '{"filters": ["year_month", "status"], "sorts": ["year_month"]}'::jsonb,
  NULL,
  NULL,
  NULL,
  '{"displayNames": {"ja": "月次締め", "zh": "月结", "en": "Monthly Closing"}}'::jsonb
)
ON CONFLICT DO NOTHING;

-- ============================================
-- 7. 権限能力（Capabilities）
-- ============================================
INSERT INTO permission_caps (cap_code, cap_name, module_code, cap_type, is_sensitive, description, display_order)
VALUES 
('monthly_closing:view', '{"ja":"月次締め参照","zh":"查看月结","en":"View Monthly Closing"}', 'finance', 'action', false,
 '{"ja":"月次締め状況を閲覧する権限","zh":"允许查看月结状态","en":"Permission to view monthly closing status"}', 50),
('monthly_closing:start', '{"ja":"月次締め開始","zh":"启动月结","en":"Start Monthly Closing"}', 'finance', 'action', false,
 '{"ja":"月次締めプロセスを開始する権限","zh":"允许启动月结流程","en":"Permission to start monthly closing process"}', 51),
('monthly_closing:check', '{"ja":"月次締めチェック","zh":"月结检查","en":"Monthly Closing Check"}', 'finance', 'action', false,
 '{"ja":"チェック項目を実行・確認する権限","zh":"允许执行和确认检查项目","en":"Permission to execute and confirm check items"}', 52),
('monthly_closing:adjustment', '{"ja":"月末調整仕訳","zh":"月末调整分录","en":"Month-end Adjustment"}', 'finance', 'action', false,
 '{"ja":"月末調整仕訳を作成する権限","zh":"允许创建月末调整分录","en":"Permission to create month-end adjustments"}', 53),
('monthly_closing:approve', '{"ja":"月次締め承認","zh":"月结审批","en":"Approve Monthly Closing"}', 'finance', 'action', true,
 '{"ja":"月次締めを承認する権限","zh":"允许审批月结","en":"Permission to approve monthly closing"}', 54),
('monthly_closing:close', '{"ja":"月次締め確定","zh":"月结确认","en":"Close Monthly Period"}', 'finance', 'action', true,
 '{"ja":"月次締めを確定する権限","zh":"允许确认月结","en":"Permission to close monthly period"}', 55),
('monthly_closing:reopen', '{"ja":"月次締め再開","zh":"月结重开","en":"Reopen Monthly Period"}', 'finance', 'action', true,
 '{"ja":"締め済み期間を再開する権限（特権）","zh":"允许重开已结月份（特权）","en":"Permission to reopen closed period (privileged)"}', 56)
ON CONFLICT (cap_code) DO NOTHING;

-- ============================================
-- 8. メニュー定義
-- ============================================
INSERT INTO permission_menus (module_code, menu_key, menu_name, menu_path, caps_required, display_order)
VALUES 
('finance', 'fin.monthlyClosing', '{"ja":"月次締め","zh":"月结","en":"Monthly Closing"}', '/financial/monthly-closing', ARRAY['monthly_closing:view'], 20)
ON CONFLICT (menu_key) DO NOTHING;

-- ============================================
-- 9. 更新 accounting_periods 表添加关联字段
-- ============================================
ALTER TABLE accounting_periods 
  ADD COLUMN IF NOT EXISTS closing_id UUID REFERENCES monthly_closings(id);

CREATE INDEX IF NOT EXISTS idx_accounting_periods_closing 
  ON accounting_periods(closing_id) WHERE closing_id IS NOT NULL;

SELECT 'monthly_closing tables created successfully' as result;


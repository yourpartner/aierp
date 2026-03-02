using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 月次締め（月結）サービス
/// 日本中小企業向け月次決算フローをサポート
/// </summary>
public class MonthlyClosingService
{
    private readonly NpgsqlDataSource _ds;
    private readonly FinancialStatementService _fsService;

    public MonthlyClosingService(NpgsqlDataSource ds, FinancialStatementService fsService)
    {
        _ds = ds;
        _fsService = fsService;
    }

    #region Core Operations

    /// <summary>
    /// 月次締めを開始（または既存を取得）
    /// </summary>
    public async Task<MonthlyClosingDto> StartOrGetAsync(string companyCode, string yearMonth, string? startedBy = null)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        
        // 既存の月次締めレコードをチェック
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT id, status, checklist, check_result, consumption_tax_summary, report_data, closed_at FROM monthly_closings WHERE company_code = $1 AND year_month = $2";
        checkCmd.Parameters.AddWithValue(companyCode);
        checkCmd.Parameters.AddWithValue(yearMonth);
        
        await using var reader = await checkCmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var dto = new MonthlyClosingDto
            {
                Id = reader.GetGuid(0),
                CompanyCode = companyCode,
                YearMonth = yearMonth,
                Status = reader.GetString(1),
                Checklist = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2)),
                CheckResult = reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3)),
                ConsumptionTaxSummary = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)),
                ReportData = reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5)),
                ClosedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
            };
            await reader.CloseAsync();
            
            // チェック結果も読み込み
            dto.CheckResults = await GetCheckResultsAsync(conn, dto.Id);
            return dto;
        }
        await reader.CloseAsync();

        // 新規作成
        var id = Guid.NewGuid();
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO monthly_closings (id, company_code, year_month, status, created_at, updated_at)
            VALUES ($1, $2, $3, 'checking', now(), now())
            RETURNING id";
        insertCmd.Parameters.AddWithValue(id);
        insertCmd.Parameters.AddWithValue(companyCode);
        insertCmd.Parameters.AddWithValue(yearMonth);
        await insertCmd.ExecuteNonQueryAsync();

        // 対応する会計期間がなければ自動作成
        await EnsureAccountingPeriodAsync(conn, companyCode, yearMonth);

        return new MonthlyClosingDto
        {
            Id = id,
            CompanyCode = companyCode,
            YearMonth = yearMonth,
            Status = "checking",
            CheckResults = new List<CheckResultDto>()
        };
    }

    /// <summary>
    /// 月次締め状況を取得
    /// </summary>
    public async Task<MonthlyClosingDto?> GetAsync(string companyCode, string yearMonth)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, status, checklist, check_result, consumption_tax_summary, report_data, 
                   check_completed_at, check_completed_by, approved_at, approved_by, approval_comment,
                   closed_at, closed_by, reopened_at, reopened_by, reopen_reason,
                   created_at, updated_at
            FROM monthly_closings 
            WHERE company_code = $1 AND year_month = $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(yearMonth);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        
        var dto = new MonthlyClosingDto
        {
            Id = reader.GetGuid(0),
            CompanyCode = companyCode,
            YearMonth = yearMonth,
            Status = reader.GetString(1),
            Checklist = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2)),
            CheckResult = reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3)),
            ConsumptionTaxSummary = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)),
            ReportData = reader.IsDBNull(5) ? null : JsonNode.Parse(reader.GetString(5)),
            CheckCompletedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            CheckCompletedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
            ApprovedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            ApprovedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
            ApprovalComment = reader.IsDBNull(10) ? null : reader.GetString(10),
            ClosedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            ClosedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
            ReopenedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            ReopenedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            ReopenReason = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedAt = reader.GetDateTime(16),
            UpdatedAt = reader.GetDateTime(17)
        };
        await reader.CloseAsync();
        
        dto.CheckResults = await GetCheckResultsAsync(conn, dto.Id);
        return dto;
    }

    /// <summary>
    /// 月次締め一覧を取得
    /// </summary>
    public async Task<List<MonthlyClosingDto>> ListAsync(string companyCode, int? year = null, int limit = 24)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        
        var sql = @"
            SELECT id, year_month, status, check_completed_at, approved_at, closed_at, created_at
            FROM monthly_closings 
            WHERE company_code = $1";
        
        if (year.HasValue)
        {
            sql += " AND year_month LIKE $2";
            cmd.CommandText = sql + " ORDER BY year_month DESC LIMIT $3";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue($"{year}-%");
            cmd.Parameters.AddWithValue(limit);
        }
        else
        {
            cmd.CommandText = sql + " ORDER BY year_month DESC LIMIT $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(limit);
        }
        
        var list = new List<MonthlyClosingDto>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MonthlyClosingDto
            {
                Id = reader.GetGuid(0),
                CompanyCode = companyCode,
                YearMonth = reader.GetString(1),
                Status = reader.GetString(2),
                CheckCompletedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                ApprovedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                ClosedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                CreatedAt = reader.GetDateTime(6)
            });
        }
        return list;
    }

    #endregion

    #region Check Operations

    /// <summary>
    /// 全チェック項目を実行
    /// </summary>
    public async Task<List<CheckResultDto>> RunAllChecksAsync(string companyCode, string yearMonth, string? checkedBy = null)
    {
        var closing = await StartOrGetAsync(companyCode, yearMonth);
        if (closing.Status == "closed")
            throw new InvalidOperationException("締め済みの期間はチェックできません");

        // チェック項目マスタを取得
        var checkItems = await GetCheckItemsAsync(companyCode);
        var results = new List<CheckResultDto>();

        await using var conn = await _ds.OpenConnectionAsync();
        
        foreach (var item in checkItems.Where(i => i.IsActive))
        {
            CheckResultDto result;
            
            if (item.CheckType == "auto")
            {
                result = await RunAutoCheckAsync(conn, companyCode, yearMonth, item);
            }
            else
            {
                // manual/info タイプは既存の結果を取得、なければpending
                var existing = closing.CheckResults?.FirstOrDefault(r => r.ItemKey == item.ItemKey);
                result = existing ?? new CheckResultDto
                {
                    ItemKey = item.ItemKey,
                    Status = "pending",
                    CheckedAt = null
                };
            }
            
            result.CheckedBy = checkedBy;
            results.Add(result);

            // 結果を保存
            await SaveCheckResultAsync(conn, closing.Id, result);
        }

        // ステータス更新
        var hasFailures = results.Any(r => r.Status == "failed");
        var allPassed = results.All(r => r.Status == "passed" || r.Status == "info" || r.Status == "skipped");
        var newStatus = hasFailures ? "checking" : (allPassed ? "adjusting" : "checking");
        
        await UpdateStatusAsync(conn, closing.Id, newStatus);
        
        return results;
    }

    /// <summary>
    /// 単一チェック項目を実行
    /// </summary>
    public async Task<CheckResultDto> RunCheckAsync(string companyCode, string yearMonth, string itemKey, string? checkedBy = null)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");
        if (closing.Status == "closed")
            throw new InvalidOperationException("締め済みの期間はチェックできません");

        var checkItems = await GetCheckItemsAsync(companyCode);
        var item = checkItems.FirstOrDefault(i => i.ItemKey == itemKey);
        if (item == null)
            throw new InvalidOperationException($"チェック項目 '{itemKey}' が見つかりません");

        await using var conn = await _ds.OpenConnectionAsync();
        
        CheckResultDto result;
        if (item.CheckType == "auto")
        {
            result = await RunAutoCheckAsync(conn, companyCode, yearMonth, item);
        }
        else
        {
            result = new CheckResultDto
            {
                ItemKey = itemKey,
                Status = "pending"
            };
        }
        
        result.CheckedBy = checkedBy;
        result.CheckedAt = DateTime.UtcNow;
        
        await SaveCheckResultAsync(conn, closing.Id, result);
        
        return result;
    }

    /// <summary>
    /// 手動チェック結果を登録
    /// </summary>
    public async Task<CheckResultDto> SetManualCheckResultAsync(
        string companyCode, 
        string yearMonth, 
        string itemKey, 
        string status,
        string? comment = null,
        string? checkedBy = null)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");
        if (closing.Status == "closed")
            throw new InvalidOperationException("締め済みの期間は更新できません");

        var result = new CheckResultDto
        {
            ItemKey = itemKey,
            Status = status,
            Comment = comment,
            CheckedBy = checkedBy,
            CheckedAt = DateTime.UtcNow
        };

        await using var conn = await _ds.OpenConnectionAsync();
        await SaveCheckResultAsync(conn, closing.Id, result);
        
        return result;
    }

    /// <summary>
    /// 自動チェックを実行
    /// </summary>
    private async Task<CheckResultDto> RunAutoCheckAsync(NpgsqlConnection conn, string companyCode, string yearMonth, CheckItemDto item)
    {
        var result = new CheckResultDto
        {
            ItemKey = item.ItemKey,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var periodEnd = GetMonthEndDate(yearMonth);
            var periodStart = GetMonthStartDate(yearMonth);

            switch (item.ItemKey)
            {
                // ar_uncleared は ar_overdue（売掛金逾期チェック）に統合
                case "ar_overdue":
                    result = await CheckOverdueReceivablesAsync(conn, companyCode, periodEnd);
                    break;
                case "ap_uncleared":
                    result = await CheckUnclearedPayablesAsync(conn, companyCode, periodEnd);
                    break;
                case "ap_overdue":
                    result = await CheckOverduePayablesAsync(conn, companyCode, periodEnd);
                    break;
                case "bank_balance":
                    result = await CheckBankBalanceAsync(conn, companyCode, periodEnd);
                    break;
                case "bank_unposted":
                    result = await CheckUnpostedBankTransactionsAsync(conn, companyCode, periodEnd);
                    break;
                case "tax_temporary":
                    result = await CheckTemporaryTaxAccountsAsync(conn, companyCode, periodStart, periodEnd);
                    break;
                case "tax_invoice_valid":
                    result = await CheckInvoiceRegistrationAsync(conn, companyCode, periodStart, periodEnd);
                    break;
                case "depreciation":
                    result = await CheckDepreciationAsync(conn, companyCode, yearMonth);
                    break;
                case "payroll_posted":
                    result = await CheckPayrollPostedAsync(conn, companyCode, yearMonth);
                    break;
                case "trial_balance":
                    result = await CheckTrialBalanceAsync(conn, companyCode, periodEnd);
                    break;
                case "balance_check":
                    result = await CheckDebitCreditBalanceAsync(conn, companyCode, periodEnd);
                    break;
                case "monthly_report":
                    result = new CheckResultDto
                    {
                        ItemKey = item.ItemKey,
                        Status = "info",
                        Message = "報告書生成可能",
                        CheckedAt = DateTime.UtcNow
                    };
                    break;
                default:
                    result.Status = "skipped";
                    result.Message = "未実装のチェック項目";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            result.Message = $"チェック実行エラー: {ex.Message}";
        }

        result.ItemKey = item.ItemKey;
        return result;
    }

    #endregion

    #region Individual Checks

    private async Task<CheckResultDto> CheckOverdueReceivablesAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        // 売掛金の逾期チェック（未消込 + 支払期限超過）
        // - 支払期限(dueDate) が存在する場合：dueDate < periodEnd かつ residual > 0
        // - 支払期限がない場合：逾期判定できないため、エラー情報として返す（fallback は使わない）
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH base AS (
              SELECT
                oi.id,
                oi.voucher_id,
                oi.partner_id,
                oi.doc_date,
                oi.residual_amount,
                (oi.refs->>'invoiceNo') AS invoice_no,
                v.voucher_no AS voucher_no,
                bp.name AS partner_name,
                CASE
                  WHEN (oi.refs->>'dueDate') ~ '^\d{4}-\d{2}-\d{2}$' THEN (oi.refs->>'dueDate')::date
                  ELSE NULL
                END AS ref_due_date,
                COALESCE(
                  si.due_date,
                  CASE
                    WHEN (oi.refs->>'dueDate') ~ '^\d{4}-\d{2}-\d{2}$' THEN (oi.refs->>'dueDate')::date
                    ELSE NULL
                  END
                ) AS due_date
            FROM open_items oi
              LEFT JOIN vouchers v
                ON v.company_code = oi.company_code
               AND v.id = oi.voucher_id
              LEFT JOIN businesspartners bp
                ON bp.company_code = oi.company_code
               AND (bp.partner_code = oi.partner_id OR bp.id::text = oi.partner_id)
              LEFT JOIN sales_invoices si
                ON si.company_code = oi.company_code
               AND si.invoice_no = (oi.refs->>'invoiceNo')
            WHERE oi.company_code = $1
              AND oi.residual_amount > 0
              AND oi.doc_date IS NOT NULL
              AND EXISTS (
                SELECT 1
                FROM accounts a
                WHERE a.company_code = oi.company_code
                  AND a.account_code = oi.account_code
                  AND COALESCE((a.payload->>'openItem')::boolean, false) = true
                  AND COALESCE(a.payload->>'openItemBaseline','NONE') = 'CUSTOMER'
              )
            ),
            missing_due AS (
              SELECT *
              FROM base
              WHERE due_date IS NULL
            ),
            overdue AS (
              SELECT *
              FROM base
              WHERE due_date IS NOT NULL AND due_date < $2::date
            )
            SELECT
              (SELECT COUNT(*)::bigint FROM overdue) AS count,
              (SELECT COALESCE(SUM(residual_amount), 0)::numeric FROM overdue) AS total,
              (SELECT COUNT(*)::bigint FROM missing_due) AS missing_due_count,
              COALESCE(jsonb_agg(
                jsonb_build_object(
                  'openItemId', id,
                  'voucherId', voucher_id,
                  'voucherNo', COALESCE(voucher_no, ''),
                  'partnerId', partner_id,
                  'partnerName', COALESCE(partner_name, ''),
                  'docDate', to_char(doc_date, 'YYYY-MM-DD'),
                  'dueDate', to_char(due_date, 'YYYY-MM-DD'),
                  'residualAmount', residual_amount,
                  'termDays', GREATEST(0, (due_date - doc_date)),
                  'overdueDays', GREATEST(0, ($2::date - due_date))
                )
                ORDER BY due_date ASC, doc_date ASC
              ) FILTER (WHERE overdue.id IS NOT NULL), '[]'::jsonb) AS items,
              COALESCE((
                SELECT jsonb_agg(
                  jsonb_build_object(
                    'openItemId', id,
                    'voucherId', voucher_id,
                    'voucherNo', COALESCE(voucher_no, ''),
                    'partnerId', partner_id,
                    'partnerName', COALESCE(partner_name, ''),
                    'docDate', to_char(doc_date, 'YYYY-MM-DD'),
                    'residualAmount', residual_amount,
                    'error', '支払期限（dueDate）が未設定のため逾期判定できません'
                  )
                  ORDER BY doc_date ASC
                )
                FROM missing_due
              ), '[]'::jsonb) AS missing_due_items
            FROM overdue";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var count = reader.GetInt64(0);
            var total = reader.GetDecimal(1);
            var missingDueCount = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var itemsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
            var missingDueItemsJson = reader.IsDBNull(4) ? "[]" : reader.GetString(4);
            
            return new CheckResultDto
            {
                ItemKey = "ar_overdue",
                Status = missingDueCount > 0 ? "failed" : (count == 0 ? "passed" : "warning"),
                ResultData = new JsonObject
                {
                    ["count"] = count,
                    ["total"] = total,
                    ["items"] = JsonNode.Parse(itemsJson) as JsonArray ?? new JsonArray(),
                    ["missingDueDateCount"] = missingDueCount,
                    ["missingDueDateItems"] = JsonNode.Parse(missingDueItemsJson) as JsonArray ?? new JsonArray()
                },
                Message = missingDueCount > 0
                    ? $"支払期限が未設定の売掛金があります（{missingDueCount}件）。逾期判定できません。"
                    : (count == 0 ? "逾期の売掛金はありません" : $"逾期売掛金: {count}件 / ¥{total:N0}"),
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "ar_overdue", Status = "failed", Message = "チェック実行エラー" };
    }

    private async Task<CheckResultDto> CheckUnclearedPayablesAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) as count, COALESCE(SUM(residual_amount), 0) as total
            FROM open_items oi
            WHERE oi.company_code = $1
              AND oi.residual_amount > 0
              AND oi.doc_date <= $2
              AND EXISTS (
                SELECT 1
                FROM accounts a
                WHERE a.company_code = oi.company_code
                  AND a.account_code = oi.account_code
                  AND COALESCE((a.payload->>'openItem')::boolean, false) = true
                  AND COALESCE(a.payload->>'openItemBaseline','NONE') = 'VENDOR'
              )";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var count = reader.GetInt64(0);
            var total = reader.GetDecimal(1);
            
            return new CheckResultDto
            {
                ItemKey = "ap_uncleared",
                Status = count == 0 ? "passed" : "info",
                ResultData = new JsonObject { ["count"] = count, ["total"] = total },
                Message = count == 0 ? "買掛金の未消込はありません" : $"未消込買掛金: {count}件 / ¥{total:N0}",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "ap_uncleared", Status = "failed", Message = "チェック実行エラー" };
    }

    private async Task<CheckResultDto> CheckOverduePayablesAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        // 支払期限を過ぎた買掛金をチェック（簡易：30日以上）
        var overdueDate = periodEnd.AddDays(-30);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) as count, COALESCE(SUM(residual_amount), 0) as total
            FROM open_items oi
            WHERE oi.company_code = $1
              AND oi.residual_amount > 0
              AND oi.doc_date <= $2
              AND EXISTS (
                SELECT 1
                FROM accounts a
                WHERE a.company_code = oi.company_code
                  AND a.account_code = oi.account_code
                  AND COALESCE((a.payload->>'openItem')::boolean, false) = true
                  AND COALESCE(a.payload->>'openItemBaseline','NONE') = 'VENDOR'
              )";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(overdueDate.ToDateTime(TimeOnly.MinValue));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var count = reader.GetInt64(0);
            var total = reader.GetDecimal(1);
            
            return new CheckResultDto
            {
                ItemKey = "ap_overdue",
                Status = count == 0 ? "passed" : "warning",
                ResultData = new JsonObject { ["count"] = count, ["total"] = total },
                Message = count == 0 ? "支払期限超過の買掛金はありません" : $"期限超過買掛金: {count}件 / ¥{total:N0}",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "ap_overdue", Status = "failed", Message = "チェック実行エラー" };
    }

    private async Task<CheckResultDto> CheckBankBalanceAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        // 銀行残高の照合（簡易版：帳簿残高のみ表示）
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.account_code, SUM(m.net_amount) as balance
            FROM mv_gl_monthly m
              JOIN accounts a
                ON a.company_code = m.company_code
               AND a.account_code = m.account_code
            WHERE m.company_code = $1
              AND m.period_month <= $2
              AND COALESCE((a.payload->>'isBank')::boolean, false) = true
            GROUP BY m.account_code
            ORDER BY m.account_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(new DateTime(periodEnd.Year, periodEnd.Month, 1));
        
        var balances = new JsonArray();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            balances.Add(new JsonObject
            {
                ["accountCode"] = reader.GetString(0),
                ["bookBalance"] = reader.GetDecimal(1)
            });
        }
        
        return new CheckResultDto
        {
            ItemKey = "bank_balance",
            Status = "info",
            ResultData = new JsonObject { ["balances"] = balances },
            Message = $"銀行口座: {balances.Count}件（手動確認推奨）",
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<CheckResultDto> CheckUnpostedBankTransactionsAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) as count
            FROM moneytree_transactions
            WHERE company_code = $1
              AND transaction_date <= $2
              AND posting_status = 'pending'";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
        
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        
        return new CheckResultDto
        {
            ItemKey = "bank_unposted",
            Status = count == 0 ? "passed" : "warning",
            ResultData = new JsonObject { ["count"] = count },
            Message = count == 0 ? "未記帳の銀行明細はありません" : $"未記帳銀行明細: {count}件",
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<CheckResultDto> CheckTemporaryTaxAccountsAsync(NpgsqlConnection conn, string companyCode, DateOnly periodStart, DateOnly periodEnd)
    {
        await using var cmd = conn.CreateCommand();
        // 税科目は会社設定から取得（inputTaxAccountCode/outputTaxAccountCode）
        await using var settingsCmd = conn.CreateCommand();
        settingsCmd.CommandText = "SELECT payload->>'inputTaxAccountCode', payload->>'outputTaxAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
        settingsCmd.Parameters.AddWithValue(companyCode);
        await using var sr = await settingsCmd.ExecuteReaderAsync();
        string? inputTaxCode = null;
        string? outputTaxCode = null;
        if (await sr.ReadAsync())
        {
            inputTaxCode = sr.IsDBNull(0) ? null : sr.GetString(0);
            outputTaxCode = sr.IsDBNull(1) ? null : sr.GetString(1);
        }
        await sr.CloseAsync();
        if (string.IsNullOrWhiteSpace(inputTaxCode) || string.IsNullOrWhiteSpace(outputTaxCode))
        {
            return new CheckResultDto
            {
                ItemKey = "tax_temporary",
                Status = "failed",
                Message = "会社設定に仮払/仮受消費税科目（inputTaxAccountCode/outputTaxAccountCode）が設定されていません。"
            };
        }

        cmd.CommandText = @"
            SELECT account_code, SUM(net_amount) as balance
            FROM mv_gl_monthly
            WHERE company_code = $1
              AND period_month >= $2 AND period_month <= $3
              AND account_code = ANY($4)
            GROUP BY account_code";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(new DateTime(periodStart.Year, periodStart.Month, 1));
        cmd.Parameters.AddWithValue(new DateTime(periodEnd.Year, periodEnd.Month, 1));
        cmd.Parameters.AddWithValue(new[] { inputTaxCode!.Trim(), outputTaxCode!.Trim() });
        
        var results = new Dictionary<string, decimal>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results[reader.GetString(0)] = reader.GetDecimal(1);
        }
        
        var inputTax = results.GetValueOrDefault(inputTaxCode!.Trim(), 0);
        var outputTax = results.GetValueOrDefault(outputTaxCode!.Trim(), 0);
        var netTax = outputTax - inputTax;
        
        return new CheckResultDto
        {
            ItemKey = "tax_temporary",
            Status = "info",
            ResultData = new JsonObject
            {
                ["inputTax"] = inputTax,
                ["outputTax"] = outputTax,
                ["netTax"] = netTax,
                ["direction"] = netTax >= 0 ? "納付" : "還付"
            },
            Message = $"仮受: ¥{outputTax:N0} / 仮払: ¥{inputTax:N0} / 差額: ¥{Math.Abs(netTax):N0}（{(netTax >= 0 ? "納付" : "還付")}）",
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<CheckResultDto> CheckInvoiceRegistrationAsync(NpgsqlConnection conn, string companyCode, DateOnly periodStart, DateOnly periodEnd)
    {
        // FinanceService.ApplyInvoiceRegistrationToHeaderAsync() は以下の状態を書き込む：
        // - 検証成功（Matched）→ invoiceRegistrationStatus = 'matched'
        // - 検証失敗（NotFound/Inactive/Expired）→ 登録番号を空にし、status フィールドを削除
        // したがって、'invalid'/'valid' ではなく 'matched' と「登録番号あり但し未検証」をカウントする
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(*) FILTER (WHERE payload->'header'->>'invoiceRegistrationStatus' = 'matched') as matched_count,
                COUNT(*) FILTER (WHERE COALESCE(payload->'header'->>'invoiceRegistrationNo', '') <> '' 
                                   AND payload->'header'->>'invoiceRegistrationStatus' IS NULL) as unverified_count,
                COUNT(*) FILTER (WHERE payload->'header'->>'invoiceRegistrationStatus' IN ('not_found','inactive','expired')) as invalid_count,
                COUNT(*) as total_count
            FROM vouchers
            WHERE company_code = $1
              AND posting_date >= $2 AND posting_date <= $3";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodStart.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MaxValue));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var matchedCount = reader.GetInt64(0);
            var unverifiedCount = reader.GetInt64(1);
            var invalidCount = reader.GetInt64(2);
            var totalCount = reader.GetInt64(3);
            
            // 無効または未検証がある場合は警告
            var hasIssue = invalidCount > 0 || unverifiedCount > 0;
            
            return new CheckResultDto
            {
                ItemKey = "tax_invoice_valid",
                Status = hasIssue ? "warning" : "passed",
                ResultData = new JsonObject
                {
                    ["matchedCount"] = matchedCount,
                    ["unverifiedCount"] = unverifiedCount,
                    ["invalidCount"] = invalidCount,
                    ["totalCount"] = totalCount
                },
                Message = hasIssue 
                    ? $"インボイス検証OK: {matchedCount}件、未検証: {unverifiedCount}件、無効: {invalidCount}件" 
                    : $"インボイス検証OK（{matchedCount}件）",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "tax_invoice_valid", Status = "passed", Message = "伝票なし" };
    }

    private async Task<CheckResultDto> CheckDepreciationAsync(NpgsqlConnection conn, string companyCode, string yearMonth)
    {
        // 当月の償却実行をチェック
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, voucher_no, asset_count, executed_at
            FROM depreciation_runs
            WHERE company_code = $1 AND year_month = $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(yearMonth);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var voucherNo = reader.GetString(1);
            var assetCount = reader.GetInt32(2);
            var executedAt = reader.GetDateTime(3);
            
            return new CheckResultDto
            {
                ItemKey = "depreciation",
                Status = "passed",
                ResultData = new JsonObject
                {
                    ["voucherNo"] = voucherNo,
                    ["assetCount"] = assetCount,
                    ["executedAt"] = executedAt.ToString("yyyy-MM-dd HH:mm")
                },
                Message = $"償却済み: {assetCount}件（{voucherNo}）",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        // 未実行の場合、対象資産数を確認
        await using var countCmd = conn.CreateCommand();
        var periodEnd = GetMonthEndDate(yearMonth);
        countCmd.CommandText = @"
            SELECT COUNT(*) FROM fixed_assets 
            WHERE company_code = $1 
              AND depreciation_start_date <= $2
              AND book_value > 1";
        countCmd.Parameters.AddWithValue(companyCode);
        countCmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
        
        var pendingCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync() ?? 0);
        
        return new CheckResultDto
        {
            ItemKey = "depreciation",
            Status = pendingCount > 0 ? "warning" : "passed",
            ResultData = new JsonObject { ["pendingCount"] = pendingCount },
            Message = pendingCount > 0 ? $"未償却資産: {pendingCount}件（要実行）" : "償却対象なし",
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<CheckResultDto> CheckPayrollPostedAsync(NpgsqlConnection conn, string companyCode, string yearMonth)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT status, total_amount
            FROM payroll_runs
            WHERE company_code = $1 AND period_month = $2
            ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(yearMonth);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var totalAmount = reader.GetDecimal(1);
            
            var isPosted = status == "posted" || status == "approved";
            
            return new CheckResultDto
            {
                ItemKey = "payroll_posted",
                Status = isPosted ? "passed" : "warning",
                ResultData = new JsonObject { ["status"] = status, ["totalAmount"] = totalAmount },
                Message = isPosted ? $"給与計上済み: ¥{totalAmount:N0}" : $"給与ステータス: {status}",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto
        {
            ItemKey = "payroll_posted",
            Status = "info",
            Message = "給与計算データなし",
            CheckedAt = DateTime.UtcNow
        };
    }

    private async Task<CheckResultDto> CheckTrialBalanceAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                SUM(debit_amount) as total_debit,
                SUM(credit_amount) as total_credit
            FROM mv_gl_monthly
            WHERE company_code = $1
              AND period_month <= $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(new DateTime(periodEnd.Year, periodEnd.Month, 1));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var totalDebit = reader.IsDBNull(0) ? 0m : reader.GetDecimal(0);
            var totalCredit = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            
            return new CheckResultDto
            {
                ItemKey = "trial_balance",
                Status = "passed",
                ResultData = new JsonObject
                {
                    ["totalDebit"] = totalDebit,
                    ["totalCredit"] = totalCredit
                },
                Message = $"試算表: 借方 ¥{totalDebit:N0} / 貸方 ¥{totalCredit:N0}",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "trial_balance", Status = "info", Message = "仕訳データなし" };
    }

    private async Task<CheckResultDto> CheckDebitCreditBalanceAsync(NpgsqlConnection conn, string companyCode, DateOnly periodEnd)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                SUM(debit_amount) as total_debit,
                SUM(credit_amount) as total_credit
            FROM mv_gl_monthly
            WHERE company_code = $1
              AND period_month <= $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(new DateTime(periodEnd.Year, periodEnd.Month, 1));
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var totalDebit = reader.IsDBNull(0) ? 0m : reader.GetDecimal(0);
            var totalCredit = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            var diff = totalDebit - totalCredit;
            
            var isBalanced = Math.Abs(diff) < 0.01m;
            
            return new CheckResultDto
            {
                ItemKey = "balance_check",
                Status = isBalanced ? "passed" : "failed",
                ResultData = new JsonObject
                {
                    ["totalDebit"] = totalDebit,
                    ["totalCredit"] = totalCredit,
                    ["difference"] = diff
                },
                Message = isBalanced ? "貸借一致" : $"貸借不一致: 差額 ¥{Math.Abs(diff):N0}",
                CheckedAt = DateTime.UtcNow
            };
        }
        
        return new CheckResultDto { ItemKey = "balance_check", Status = "passed", Message = "仕訳データなし" };
    }

    #endregion

    #region Closing Operations

    /// <summary>
    /// 消費税集計を実行
    /// </summary>
    public async Task<JsonObject> CalculateTaxSummaryAsync(string companyCode, string yearMonth)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");

        var periodStart = GetMonthStartDate(yearMonth);
        var periodEnd = GetMonthEndDate(yearMonth);

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // 消費税行の統計：isTaxLine=true の行を対象とし、taxRate フィールドから税率を取得
        // FinanceService.CreateVoucher で展開された税行は line['tax'] オブジェクトではなく
        // line['isTaxLine']=true, line['taxRate'] の形式で保存される
        cmd.CommandText = @"
            SELECT 
                CASE WHEN line->>'drcr' = 'DR' THEN 'input' ELSE 'output' END as direction,
                COALESCE((line->>'taxRate')::numeric, 10) as tax_rate,
                SUM((line->>'amount')::numeric) as tax_amount
            FROM vouchers v
            CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
            WHERE v.company_code = $1
              AND v.posting_date >= $2 AND v.posting_date <= $3
              AND (line->>'isTaxLine')::boolean = true
            GROUP BY 1, 2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodStart.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MaxValue));

        var summary = new Dictionary<string, decimal>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var direction = reader.GetString(0);
            var rate = reader.GetDecimal(1);
            var taxAmount = reader.GetDecimal(2);  // 変更: 列インデックス 3 → 2（base_amount 列削除）
            var key = $"{direction}_{rate}";
            summary[key] = taxAmount;
        }

        var outputTax10 = summary.GetValueOrDefault("output_10", 0);
        var outputTax8 = summary.GetValueOrDefault("output_8", 0);
        var inputTax10 = summary.GetValueOrDefault("input_10", 0);
        var inputTax8 = summary.GetValueOrDefault("input_8", 0);

        var result = new JsonObject
        {
            ["yearMonth"] = yearMonth,
            ["outputTax10"] = outputTax10,
            ["outputTax8"] = outputTax8,
            ["inputTax10"] = inputTax10,
            ["inputTax8"] = inputTax8,
            ["totalOutputTax"] = outputTax10 + outputTax8,
            ["totalInputTax"] = inputTax10 + inputTax8,
            ["netTax"] = (outputTax10 + outputTax8) - (inputTax10 + inputTax8),
            ["direction"] = (outputTax10 + outputTax8) >= (inputTax10 + inputTax8) ? "納付" : "還付",
            ["calculatedAt"] = DateTime.UtcNow.ToString("o")
        };

        // 保存
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE monthly_closings 
            SET consumption_tax_summary = $1::jsonb, updated_at = now()
            WHERE id = $2";
        updateCmd.Parameters.AddWithValue(result.ToJsonString());
        updateCmd.Parameters.AddWithValue(closing.Id);
        await updateCmd.ExecuteNonQueryAsync();

        return result;
    }

    /// <summary>
    /// 承認申請
    /// </summary>
    public async Task SubmitForApprovalAsync(string companyCode, string yearMonth, string submittedBy)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");
        if (closing.Status == "closed")
            throw new InvalidOperationException("既に締め済みです");

        await using var conn = await _ds.OpenConnectionAsync();
        await UpdateStatusAsync(conn, closing.Id, "pending_approval");
    }

    /// <summary>
    /// 承認
    /// </summary>
    public async Task ApproveAsync(string companyCode, string yearMonth, string approvedBy, string? comment = null)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");
        if (closing.Status != "pending_approval")
            throw new InvalidOperationException("承認待ちステータスではありません");

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE monthly_closings 
            SET status = 'adjusting', approved_at = now(), approved_by = $1, approval_comment = $2, updated_at = now()
            WHERE id = $3";
        cmd.Parameters.AddWithValue(approvedBy);
        cmd.Parameters.AddWithValue((object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue(closing.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 月次締め確定
    /// </summary>
    public async Task CloseAsync(string companyCode, string yearMonth, string closedBy)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが開始されていません");
        if (closing.Status == "closed")
            throw new InvalidOperationException("既に締め済みです");

        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // 月次締めを閉鎖
            await using var closeCmd = conn.CreateCommand();
            closeCmd.CommandText = @"
                UPDATE monthly_closings 
                SET status = 'closed', closed_at = now(), closed_by = $1, updated_at = now()
                WHERE id = $2";
            closeCmd.Parameters.AddWithValue(closedBy);
            closeCmd.Parameters.AddWithValue(closing.Id);
            await closeCmd.ExecuteNonQueryAsync();

            // 会計期間を閉鎖
            var periodStart = GetMonthStartDate(yearMonth);
            var periodEnd = GetMonthEndDate(yearMonth);
            
            await using var periodCmd = conn.CreateCommand();
            periodCmd.CommandText = @"
                UPDATE accounting_periods 
                SET payload = jsonb_set(payload, '{isOpen}', 'false'::jsonb),
                    closing_id = $1,
                    updated_at = now()
                WHERE company_code = $2 
                  AND period_start = $3 AND period_end = $4";
            periodCmd.Parameters.AddWithValue(closing.Id);
            periodCmd.Parameters.AddWithValue(companyCode);
            periodCmd.Parameters.AddWithValue(periodStart.ToDateTime(TimeOnly.MinValue));
            periodCmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
            await periodCmd.ExecuteNonQueryAsync();

            // 物化ビューを更新
            await using var refreshCmd = conn.CreateCommand();
            refreshCmd.CommandText = "REFRESH MATERIALIZED VIEW CONCURRENTLY mv_gl_monthly";
            await refreshCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 月次締め再開（特権）
    /// </summary>
    public async Task ReopenAsync(string companyCode, string yearMonth, string reopenedBy, string reason)
    {
        var closing = await GetAsync(companyCode, yearMonth);
        if (closing == null)
            throw new InvalidOperationException("月次締めが見つかりません");
        if (closing.Status != "closed")
            throw new InvalidOperationException("締め済みの期間のみ再開できます");

        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // 月次締めを再開
            await using var reopenCmd = conn.CreateCommand();
            reopenCmd.CommandText = @"
                UPDATE monthly_closings 
                SET status = 'reopened', reopened_at = now(), reopened_by = $1, reopen_reason = $2, updated_at = now()
                WHERE id = $3";
            reopenCmd.Parameters.AddWithValue(reopenedBy);
            reopenCmd.Parameters.AddWithValue(reason);
            reopenCmd.Parameters.AddWithValue(closing.Id);
            await reopenCmd.ExecuteNonQueryAsync();

            // 会計期間を再開
            var periodStart = GetMonthStartDate(yearMonth);
            var periodEnd = GetMonthEndDate(yearMonth);
            
            await using var periodCmd = conn.CreateCommand();
            periodCmd.CommandText = @"
                UPDATE accounting_periods 
                SET payload = jsonb_set(payload, '{isOpen}', 'true'::jsonb),
                    updated_at = now()
                WHERE company_code = $1 
                  AND period_start = $2 AND period_end = $3";
            periodCmd.Parameters.AddWithValue(companyCode);
            periodCmd.Parameters.AddWithValue(periodStart.ToDateTime(TimeOnly.MinValue));
            periodCmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));
            await periodCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    #endregion

    #region Helper Methods

    private async Task<List<CheckItemDto>> GetCheckItemsAsync(string companyCode)
    {
        var list = new List<CheckItemDto>();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT item_key, item_name_ja, item_name_en, item_name_zh, category, check_type, priority, is_required, is_active
            FROM monthly_closing_check_items
            WHERE (company_code = $1 OR company_code IS NULL)
            ORDER BY priority, item_key";
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CheckItemDto
            {
                ItemKey = reader.GetString(0),
                ItemNameJa = reader.GetString(1),
                ItemNameEn = reader.IsDBNull(2) ? null : reader.GetString(2),
                ItemNameZh = reader.IsDBNull(3) ? null : reader.GetString(3),
                Category = reader.GetString(4),
                CheckType = reader.GetString(5),
                Priority = reader.GetInt32(6),
                IsRequired = reader.GetBoolean(7),
                IsActive = reader.GetBoolean(8)
            });
        }
        // ar_uncleared は ar_overdue に統合するため、一覧・実行対象から除外
        return list
            .Where(x => x.IsActive)
            .Where(x => x.ItemKey != "ar_uncleared")
            .ToList();
    }

    private async Task<List<CheckResultDto>> GetCheckResultsAsync(NpgsqlConnection conn, Guid closingId)
    {
        var list = new List<CheckResultDto>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT item_key, status, result_data, checked_at, checked_by, comment
            FROM monthly_closing_check_results
            WHERE closing_id = $1
            ORDER BY created_at";
        cmd.Parameters.AddWithValue(closingId);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new CheckResultDto
            {
                ItemKey = reader.GetString(0),
                Status = reader.GetString(1),
                ResultData = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2)) as JsonObject,
                CheckedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                CheckedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                Comment = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }
        return list;
    }

    private async Task SaveCheckResultAsync(NpgsqlConnection conn, Guid closingId, CheckResultDto result)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO monthly_closing_check_results (closing_id, item_key, status, result_data, checked_at, checked_by, comment)
            VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7)
            ON CONFLICT (closing_id, item_key) 
            DO UPDATE SET status = $3, result_data = $4::jsonb, checked_at = $5, checked_by = $6, comment = $7, updated_at = now()";
        cmd.Parameters.AddWithValue(closingId);
        cmd.Parameters.AddWithValue(result.ItemKey);
        cmd.Parameters.AddWithValue(result.Status);
        cmd.Parameters.AddWithValue((object?)result.ResultData?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)result.CheckedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)result.CheckedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)result.Comment ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task UpdateStatusAsync(NpgsqlConnection conn, Guid closingId, string status)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE monthly_closings SET status = $1, updated_at = now() WHERE id = $2";
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(closingId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureAccountingPeriodAsync(NpgsqlConnection conn, string companyCode, string yearMonth)
    {
        var periodStart = GetMonthStartDate(yearMonth);
        var periodEnd = GetMonthEndDate(yearMonth);

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = @"
            SELECT id FROM accounting_periods 
            WHERE company_code = $1 AND period_start = $2 AND period_end = $3";
        checkCmd.Parameters.AddWithValue(companyCode);
        checkCmd.Parameters.AddWithValue(periodStart.ToDateTime(TimeOnly.MinValue));
        checkCmd.Parameters.AddWithValue(periodEnd.ToDateTime(TimeOnly.MinValue));

        var existing = await checkCmd.ExecuteScalarAsync();
        if (existing != null) return;

        // 自動作成
        var payload = new JsonObject
        {
            ["periodStart"] = periodStart.ToString("yyyy-MM-dd"),
            ["periodEnd"] = periodEnd.ToString("yyyy-MM-dd"),
            ["isOpen"] = true
        };

        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO accounting_periods (company_code, payload)
            VALUES ($1, $2::jsonb)";
        insertCmd.Parameters.AddWithValue(companyCode);
        insertCmd.Parameters.AddWithValue(payload.ToJsonString());
        await insertCmd.ExecuteNonQueryAsync();
    }

    private static DateOnly GetMonthStartDate(string yearMonth)
    {
        var parts = yearMonth.Split('-');
        return new DateOnly(int.Parse(parts[0]), int.Parse(parts[1]), 1);
    }

    private static DateOnly GetMonthEndDate(string yearMonth)
    {
        var start = GetMonthStartDate(yearMonth);
        return start.AddMonths(1).AddDays(-1);
    }

    #endregion

    #region DTOs

    public class MonthlyClosingDto
    {
        public Guid Id { get; set; }
        public string CompanyCode { get; set; } = "";
        public string YearMonth { get; set; } = "";
        public string Status { get; set; } = "open";
        public JsonNode? Checklist { get; set; }
        public JsonNode? CheckResult { get; set; }
        public JsonNode? ConsumptionTaxSummary { get; set; }
        public JsonNode? ReportData { get; set; }
        public DateTime? CheckCompletedAt { get; set; }
        public string? CheckCompletedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovedBy { get; set; }
        public string? ApprovalComment { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? ClosedBy { get; set; }
        public DateTime? ReopenedAt { get; set; }
        public string? ReopenedBy { get; set; }
        public string? ReopenReason { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<CheckResultDto>? CheckResults { get; set; }
    }

    public class CheckItemDto
    {
        public string ItemKey { get; set; } = "";
        public string ItemNameJa { get; set; } = "";
        public string? ItemNameEn { get; set; }
        public string? ItemNameZh { get; set; }
        public string Category { get; set; } = "";
        public string CheckType { get; set; } = "";
        public int Priority { get; set; }
        public bool IsRequired { get; set; }
        public bool IsActive { get; set; }
    }

    public class CheckResultDto
    {
        public string ItemKey { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? Message { get; set; }
        public JsonObject? ResultData { get; set; }
        public DateTime? CheckedAt { get; set; }
        public string? CheckedBy { get; set; }
        public string? Comment { get; set; }
    }

    #endregion
}


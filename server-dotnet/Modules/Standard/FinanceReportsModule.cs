using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// 财务报表模块 - 包含仕訳帳、総勘定元帳、勘定明細、勘定残高、財務諸表等报表功能
/// </summary>
public class FinanceReportsModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "finance_reports",
        Name = "财务报表",
        Description = "仕訳帳、総勘定元帳、勘定明細、財務諸表等报表输出",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = Array.Empty<MenuConfig>() // 菜单已在 FinanceExtStandardModule 中定义
    };

    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // 报表服务使用 FinancialStatementService，已在 FinanceCoreModule 注册
    }

    public override void MapEndpoints(WebApplication app)
    {
        MapFinanceReportsModule(app);
    }

    /// <summary>
    /// 静态方法，用于直接从 Program.cs 注册端点
    /// </summary>
    public static void MapFinanceReportsModule(WebApplication app)
    {
        MapJournalBookEndpoint(app);
        MapGeneralLedgerEndpoint(app);
        MapAccountLedgerEndpoint(app);
        MapAccountBalanceEndpoint(app);
        MapFinancialStatementsEndpoints(app);
    }

    /// <summary>
    /// 仕訳帳（Journal Book）- すべての仕訳を日付順に出力
    /// </summary>
    private static void MapJournalBookEndpoint(WebApplication app)
    {
        app.MapPost("/reports/journal-book", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var startDate = root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
            var endDate = root.TryGetProperty("endDate", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null;
            var format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String ? fmt.GetString() : null;

            if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate))
                return Results.BadRequest(new { error = "startDate and endDate are required" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                WITH base_lines AS (
                    SELECT 
                        v.posting_date,
                        v.voucher_no,
                        v.payload->'header'->>'summary' as summary,
                        v.payload->'header'->>'invoiceRegistrationNo' as invoice_no,
                        line->>'lineNo' as line_no,
                        line->>'accountCode' as account_code,
                        COALESCE(a.payload->>'name', line->>'accountCode') as account_name,
                        a.payload->>'name' as account_name_raw,
                        COALESCE(line->>'taxType', a.payload->>'taxType') as tax_type,
                        line->>'taxRate' as tax_rate_raw,
                        line->>'drcr' as drcr,
                        (line->>'amount')::numeric as amount,
                        line->>'note' as description
                    FROM vouchers v
                    CROSS JOIN LATERAL jsonb_array_elements(v.payload->'lines') as line
                    LEFT JOIN accounts a ON a.company_code = v.company_code AND a.account_code = line->>'accountCode'
                    WHERE v.company_code = $1
                      AND v.posting_date >= $2::date
                      AND v.posting_date <= $3::date
                ),
                normalized AS (
                    SELECT
                        *,
                        CASE
                            WHEN (tax_type ILIKE '%INPUT%' OR account_name_raw ILIKE '%仮払消費税%') THEN 'INPUT'
                            WHEN (tax_type ILIKE '%OUTPUT%' OR account_name_raw ILIKE '%仮受消費税%') THEN 'OUTPUT'
                            ELSE NULL
                        END as tax_side,
                        CASE
                            WHEN tax_rate_raw IS NULL OR tax_rate_raw = '' THEN NULL
                            WHEN tax_rate_raw ~ '^[0-9]+(\.[0-9]+)?$' THEN
                                CASE
                                    WHEN (tax_rate_raw)::numeric > 0 AND (tax_rate_raw)::numeric < 1 THEN ROUND((tax_rate_raw)::numeric * 100)
                                    ELSE ROUND((tax_rate_raw)::numeric)
                                END
                            ELSE NULL
                        END as tax_rate_num,
                        UPPER(REGEXP_REPLACE(COALESCE(invoice_no, ''), '[^A-Za-z0-9]', '', 'g')) as invoice_no_norm
                    FROM base_lines
                )
                SELECT 
                    posting_date,
                    voucher_no,
                    summary,
                    line_no,
                    account_code,
                    account_name,
                    drcr,
                    amount,
                    description,
                    CASE
                        WHEN tax_side IS NULL THEN NULL
                        WHEN tax_side = 'INPUT' THEN
                            '課税仕入(' ||
                            COALESCE(
                                CASE
                                    WHEN tax_rate_num IS NULL THEN '税率不明'
                                    WHEN tax_rate_num = 0 THEN '0%'
                                    ELSE tax_rate_num::int::text || '%'
                                END, '税率不明'
                            ) || ',' ||
                            CASE
                                -- 有効なT番号がある場合は適格
                                WHEN invoice_no_norm LIKE 'T%' AND invoice_no_norm NOT IN ('T1234567890123','1234567890123') THEN '適格'
                                -- 銀行手数料は銀行が必ず適格請求書発行事業者なので適格とみなす
                                WHEN description ILIKE '%手数料%' OR summary ILIKE '%手数料%' THEN '適格'
                                ELSE '非適格'
                            END || ')'
                        WHEN tax_side = 'OUTPUT' THEN
                            '課税売上(' ||
                            COALESCE(
                                CASE
                                    WHEN tax_rate_num IS NULL THEN '税率不明'
                                    WHEN tax_rate_num = 0 THEN '0%'
                                    ELSE tax_rate_num::int::text || '%'
                                END, '税率不明'
                            ) || ')'
                        ELSE NULL
                    END as tax_code
                FROM normalized
                ORDER BY posting_date, voucher_no, (line_no)::int
            ";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(startDate);
            cmd.Parameters.AddWithValue(endDate);

            var rows = new List<object>();
            decimal totalDebit = 0;
            decimal totalCredit = 0;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var drcr = reader.GetString(6);
                var amount = reader.GetDecimal(7);
                decimal debitAmount = drcr == "DR" ? amount : 0;
                decimal creditAmount = drcr == "CR" ? amount : 0;

                totalDebit += debitAmount;
                totalCredit += creditAmount;

                rows.Add(new
                {
                    postingDate = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                    voucherNo = reader.IsDBNull(1) ? null : reader.GetString(1),
                    summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                    lineNo = reader.IsDBNull(3) ? null : reader.GetString(3),
                    accountCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    accountName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    debitAmount = debitAmount > 0 ? debitAmount : (decimal?)null,
                    creditAmount = creditAmount > 0 ? creditAmount : (decimal?)null,
                    description = reader.IsDBNull(8) ? null : reader.GetString(8),
                    taxCode = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            // CSV出力の場合
            if (format == "csv")
            {
                var csvBuilder = new StringBuilder();
                csvBuilder.AppendLine("日付,伝票番号,摘要,科目コード,科目名,借方金額,貸方金額,行摘要,税区分");
                foreach (dynamic row in rows)
                {
                    csvBuilder.AppendLine($"\"{row.postingDate}\",\"{row.voucherNo}\",\"{row.summary?.Replace("\"", "\"\"")}\",\"{row.accountCode}\",\"{row.accountName?.Replace("\"", "\"\"")}\",{row.debitAmount?.ToString() ?? ""},{row.creditAmount?.ToString() ?? ""},\"{row.description?.Replace("\"", "\"\"")}\",\"{row.taxCode}\"");
                }
                var csvBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray();
                return Results.File(csvBytes, "text/csv; charset=utf-8", $"仕訳帳_{startDate}_{endDate}.csv");
            }

            return Results.Ok(new
            {
                data = rows,
                totals = new
                {
                    debit = totalDebit,
                    credit = totalCredit,
                    isBalanced = totalDebit == totalCredit
                }
            });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 総勘定元帳（General Ledger）- 勘定科目ごとの明細と残高
    /// </summary>
    private static void MapGeneralLedgerEndpoint(WebApplication app)
    {
        app.MapPost("/reports/general-ledger", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var startDate = root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
            var endDate = root.TryGetProperty("endDate", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null;
            var accountCode = root.TryGetProperty("accountCode", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null;
            var format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String ? fmt.GetString() : null;

            if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate))
                return Results.BadRequest(new { error = "startDate and endDate are required" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取所有科目（如果指定了accountCode则只获取该科目）
            var accountsCmd = conn.CreateCommand();
            if (!string.IsNullOrWhiteSpace(accountCode))
            {
                accountsCmd.CommandText = @"
                    SELECT account_code, payload->>'name' as name, payload->>'category' as category
                    FROM accounts WHERE company_code = $1 AND account_code = $2
                    ORDER BY account_code";
                accountsCmd.Parameters.AddWithValue(cc.ToString());
                accountsCmd.Parameters.AddWithValue(accountCode);
            }
            else
            {
                accountsCmd.CommandText = @"
                    SELECT DISTINCT a.account_code, a.payload->>'name' as name, a.payload->>'category' as category
                    FROM accounts a
                    WHERE a.company_code = $1
                      AND EXISTS (
                          SELECT 1 FROM vouchers v, jsonb_array_elements(v.payload->'lines') as line
                          WHERE v.company_code = $1
                            AND v.posting_date >= $2::date AND v.posting_date <= $3::date
                            AND line->>'accountCode' = a.account_code
                      )
                    ORDER BY a.account_code";
                accountsCmd.Parameters.AddWithValue(cc.ToString());
                accountsCmd.Parameters.AddWithValue(startDate);
                accountsCmd.Parameters.AddWithValue(endDate);
            }

            var accounts = new List<(string code, string? name, string? category)>();
            await using (var accountsReader = await accountsCmd.ExecuteReaderAsync())
            {
                while (await accountsReader.ReadAsync())
                {
                    accounts.Add((
                        accountsReader.GetString(0),
                        accountsReader.IsDBNull(1) ? null : accountsReader.GetString(1),
                        accountsReader.IsDBNull(2) ? null : accountsReader.GetString(2)
                    ));
                }
            }

            var result = new List<object>();
            decimal totalPeriodDebit = 0;
            decimal totalPeriodCredit = 0;

            foreach (var acct in accounts)
            {
                // 获取期初余额（startDate之前的所有交易汇总）
                var balanceCmd = conn.CreateCommand();
                balanceCmd.CommandText = @"
                    SELECT 
                        COALESCE(SUM(CASE WHEN line->>'drcr' = 'DR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as debit,
                        COALESCE(SUM(CASE WHEN line->>'drcr' = 'CR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as credit
                    FROM vouchers v, jsonb_array_elements(v.payload->'lines') as line
                    WHERE v.company_code = $1
                      AND v.posting_date < $2::date
                      AND line->>'accountCode' = $3";
                balanceCmd.Parameters.AddWithValue(cc.ToString());
                balanceCmd.Parameters.AddWithValue(startDate);
                balanceCmd.Parameters.AddWithValue(acct.code);

                decimal openingBalance = 0;
                await using (var balanceReader = await balanceCmd.ExecuteReaderAsync())
                {
                    if (await balanceReader.ReadAsync())
                    {
                        var prevDebit = balanceReader.GetDecimal(0);
                        var prevCredit = balanceReader.GetDecimal(1);
                        var isDebitNature = acct.category == "資産" || acct.category == "費用" || acct.category == "asset" || acct.category == "expense";
                        openingBalance = isDebitNature ? (prevDebit - prevCredit) : (prevCredit - prevDebit);
                    }
                }

                // 获取期间明细
                var entriesCmd = conn.CreateCommand();
                entriesCmd.CommandText = @"
                    SELECT 
                        v.posting_date,
                        v.voucher_no,
                        v.payload->'header'->>'summary' as summary,
                        line->>'drcr' as drcr,
                        (line->>'amount')::numeric as amount,
                        (
                            SELECT string_agg(DISTINCT COALESCE(a2.payload->>'name', ol->>'accountCode'), ', ')
                            FROM jsonb_array_elements(v.payload->'lines') as ol
                            LEFT JOIN accounts a2 ON a2.company_code = v.company_code AND a2.account_code = ol->>'accountCode'
                            WHERE ol->>'accountCode' != $3
                        ) as counter_accounts
                    FROM vouchers v, jsonb_array_elements(v.payload->'lines') as line
                    WHERE v.company_code = $1
                      AND v.posting_date >= $2::date AND v.posting_date <= $4::date
                      AND line->>'accountCode' = $3
                    ORDER BY v.posting_date, v.voucher_no";
                entriesCmd.Parameters.AddWithValue(cc.ToString());
                entriesCmd.Parameters.AddWithValue(startDate);
                entriesCmd.Parameters.AddWithValue(acct.code);
                entriesCmd.Parameters.AddWithValue(endDate);

                var entries = new List<object>();
                var isDebitNatureAcct = acct.category == "資産" || acct.category == "費用" || acct.category == "asset" || acct.category == "expense";
                decimal runningBalance = openingBalance;
                decimal periodDebit = 0;
                decimal periodCredit = 0;

                // 添加期初余额行
                entries.Add(new
                {
                    postingDate = "",
                    voucherNo = (string?)null,
                    summary = "前期繰越",
                    counterAccounts = "",
                    debitAmount = (decimal?)null,
                    creditAmount = (decimal?)null,
                    balance = openingBalance
                });

                await using (var entriesReader = await entriesCmd.ExecuteReaderAsync())
                {
                    while (await entriesReader.ReadAsync())
                    {
                        var drcr = entriesReader.GetString(3);
                        var amount = entriesReader.GetDecimal(4);
                        decimal debitAmount = drcr == "DR" ? amount : 0;
                        decimal creditAmount = drcr == "CR" ? amount : 0;

                        periodDebit += debitAmount;
                        periodCredit += creditAmount;

                        if (isDebitNatureAcct)
                            runningBalance += debitAmount - creditAmount;
                        else
                            runningBalance += creditAmount - debitAmount;

                        entries.Add(new
                        {
                            postingDate = entriesReader.GetDateTime(0).ToString("yyyy-MM-dd"),
                            voucherNo = entriesReader.IsDBNull(1) ? null : entriesReader.GetString(1),
                            summary = entriesReader.IsDBNull(2) ? null : entriesReader.GetString(2),
                            counterAccounts = entriesReader.IsDBNull(5) ? null : entriesReader.GetString(5),
                            debitAmount = debitAmount > 0 ? debitAmount : (decimal?)null,
                            creditAmount = creditAmount > 0 ? creditAmount : (decimal?)null,
                            balance = runningBalance
                        });
                    }
                }

                totalPeriodDebit += periodDebit;
                totalPeriodCredit += periodCredit;

                result.Add(new
                {
                    accountCode = acct.code,
                    accountName = acct.name ?? acct.code,
                    category = acct.category,
                    openingBalance,
                    closingBalance = runningBalance,
                    entries
                });
            }

            // CSV出力の場合
            if (format == "csv")
            {
                var csvBuilder = new StringBuilder();
                csvBuilder.AppendLine("科目コード,科目名,区分,日付,伝票番号,摘要,相手勘定,借方金額,貸方金額,残高");
                foreach (dynamic acct in result)
                {
                    foreach (dynamic entry in acct.entries)
                    {
                        csvBuilder.AppendLine($"\"{acct.accountCode}\",\"{acct.accountName?.Replace("\"", "\"\"")}\",\"{acct.category}\",\"{entry.postingDate}\",\"{entry.voucherNo}\",\"{entry.summary?.Replace("\"", "\"\"")}\",\"{entry.counterAccounts?.Replace("\"", "\"\"")}\",{entry.debitAmount?.ToString() ?? ""},{entry.creditAmount?.ToString() ?? ""},{entry.balance}");
                    }
                }
                var csvBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csvBuilder.ToString())).ToArray();
                return Results.File(csvBytes, "text/csv; charset=utf-8", $"総勘定元帳_{startDate}_{endDate}.csv");
            }

            return Results.Ok(new
            {
                data = result,
                totals = new
                {
                    periodDebit = totalPeriodDebit,
                    periodCredit = totalPeriodCredit
                }
            });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 勘定明細一覧（Account Ledger）- 按科目检索凭证明细
    /// </summary>
    private static void MapAccountLedgerEndpoint(WebApplication app)
    {
        app.MapPost("/reports/account-ledger", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var startDate = root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
            var endDate = root.TryGetProperty("endDate", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null;
            var accountCodes = root.TryGetProperty("accountCodes", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? ac.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            var keyword = root.TryGetProperty("keyword", out var kw) && kw.ValueKind == JsonValueKind.String ? kw.GetString() : null;
            var customerId = root.TryGetProperty("customerId", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null;
            var vendorId = root.TryGetProperty("vendorId", out var vi) && vi.ValueKind == JsonValueKind.String ? vi.GetString() : null;
            var employeeId = root.TryGetProperty("employeeId", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() : null;

            var page = root.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 1;
            var pageSize = root.TryGetProperty("pageSize", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 100;
            var sortField = root.TryGetProperty("sortField", out var sf) && sf.ValueKind == JsonValueKind.String ? sf.GetString() : "postingDate";
            var sortOrder = root.TryGetProperty("sortOrder", out var so) && so.ValueKind == JsonValueKind.String ? so.GetString() : "ASC";

            // 验证排序字段，防止 SQL 注入
            var allowedSortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "postingDate", "voucherNo", "accountCode", "amount", "fiscalYear", "fiscalMonth", "lineNo"
            };
            if (!allowedSortFields.Contains(sortField ?? "postingDate")) sortField = "postingDate";
            var orderDir = string.Equals(sortOrder, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

            var sortColumn = sortField switch
            {
                "postingDate" => "vl.posting_date",
                "voucherNo" => "vl.voucher_no",
                "accountCode" => "vl.account_code",
                "amount" => "vl.amount",
                "fiscalYear" => "vl.fiscal_year",
                "fiscalMonth" => "vl.fiscal_month",
                "lineNo" => "vl.line_no::int",
                _ => "vl.posting_date"
            };

            await using var conn = await ds.OpenConnectionAsync();

            var sql = new StringBuilder();
            sql.Append(@"
                WITH voucher_lines AS (
                    SELECT 
                        v.id as voucher_id,
                        v.voucher_no,
                        v.posting_date,
                        v.payload->'header'->>'summary' as header_text,
                        v.payload->'header'->>'createdBy' as created_by,
                        v.updated_at,
                        EXTRACT(YEAR FROM v.posting_date)::int as fiscal_year,
                        EXTRACT(MONTH FROM v.posting_date)::int as fiscal_month,
                        line->>'lineNo' as line_no,
                        line->>'accountCode' as account_code,
                        line->>'drcr' as drcr,
                        (line->>'amount')::numeric as amount,
                        line->>'note' as line_text,
                        line->>'customerId' as customer_id,
                        line->>'vendorId' as vendor_id,
                        line->>'employeeId' as employee_id,
                        line->>'departmentCode' as department_code,
                        line->>'dueDate' as due_date
                    FROM vouchers v,
                         jsonb_array_elements(v.payload->'lines') as line
                    WHERE v.company_code = $1
            ");

            var parameters = new List<NpgsqlParameter> { new() { Value = cc.ToString() } };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(startDate))
            {
                sql.Append($" AND v.posting_date >= ${paramIndex}::date");
                parameters.Add(new() { Value = startDate });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(endDate))
            {
                sql.Append($" AND v.posting_date <= ${paramIndex}::date");
                parameters.Add(new() { Value = endDate });
                paramIndex++;
            }

            sql.Append(@"
                )
                SELECT 
                    vl.*,
                    a.payload->>'name' as account_name,
                    c.payload->>'name' as customer_name,
                    ve.payload->>'name' as vendor_name,
                    e.payload->>'name' as employee_name,
                    d.payload->>'name' as department_name,
                    oi.id as open_item_id,
                    CASE 
                        WHEN oi.residual_amount IS NULL THEN NULL
                        WHEN oi.residual_amount = 0 THEN 'cleared'
                        WHEN oi.residual_amount < oi.original_amount THEN 'partial'
                        ELSE 'open'
                    END as clearing_status,
                    oi.refs->>'clearingVoucherNo' as clearing_voucher_no,
                    oi.cleared_at as clearing_date
                FROM voucher_lines vl
                LEFT JOIN accounts a ON a.company_code = $1 AND a.account_code = vl.account_code
                LEFT JOIN businesspartners c ON c.company_code = $1 AND c.partner_code = vl.customer_id
                LEFT JOIN businesspartners ve ON ve.company_code = $1 AND ve.partner_code = vl.vendor_id
                LEFT JOIN employees e ON e.company_code = $1 AND e.id::text = vl.employee_id
                LEFT JOIN departments d ON d.company_code = $1 AND d.department_code = vl.department_code
                LEFT JOIN open_items oi ON oi.company_code = $1 AND oi.voucher_id = vl.voucher_id AND oi.voucher_line_no::text = vl.line_no
                WHERE 1=1
            ");

            if (accountCodes.Count > 0)
            {
                sql.Append($" AND vl.account_code = ANY(${paramIndex}::text[])");
                parameters.Add(new() { Value = accountCodes.ToArray() });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(customerId))
            {
                sql.Append($" AND vl.customer_id = ${paramIndex}");
                parameters.Add(new() { Value = customerId });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(vendorId))
            {
                sql.Append($" AND vl.vendor_id = ${paramIndex}");
                parameters.Add(new() { Value = vendorId });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(employeeId))
            {
                sql.Append($" AND vl.employee_id = ${paramIndex}");
                parameters.Add(new() { Value = employeeId });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(keyword))
            {
                sql.Append($" AND (vl.header_text ILIKE ${paramIndex} OR vl.line_text ILIKE ${paramIndex})");
                parameters.Add(new() { Value = $"%{keyword}%" });
                paramIndex++;
            }

            // 先查询总数
            var countSql = $"SELECT COUNT(*) FROM ({sql}) sub";
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = countSql;
            foreach (var p in parameters) countCmd.Parameters.Add(new NpgsqlParameter { Value = p.Value });
            var totalObj = await countCmd.ExecuteScalarAsync();
            var total = Convert.ToInt64(totalObj ?? 0);

            // 添加排序和分页
            sql.Append($" ORDER BY {sortColumn} {orderDir}, vl.voucher_no, vl.line_no");
            sql.Append($" LIMIT ${paramIndex} OFFSET ${paramIndex + 1}");
            parameters.Add(new() { Value = pageSize });
            parameters.Add(new() { Value = (page - 1) * pageSize });

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter { Value = p.Value });

            var rows = new List<object>();
            decimal runningBalance = 0m;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var drcr = reader.IsDBNull(reader.GetOrdinal("drcr")) ? "DR" : reader.GetString(reader.GetOrdinal("drcr"));
                var amount = reader.IsDBNull(reader.GetOrdinal("amount")) ? 0m : reader.GetDecimal(reader.GetOrdinal("amount"));

                runningBalance += drcr == "DR" ? amount : -amount;

                rows.Add(new
                {
                    voucherId = reader.IsDBNull(reader.GetOrdinal("voucher_id")) ? null : reader.GetGuid(reader.GetOrdinal("voucher_id")).ToString(),
                    voucherNo = reader.IsDBNull(reader.GetOrdinal("voucher_no")) ? null : reader.GetString(reader.GetOrdinal("voucher_no")),
                    postingDate = reader.IsDBNull(reader.GetOrdinal("posting_date")) ? null : reader.GetDateTime(reader.GetOrdinal("posting_date")).ToString("yyyy-MM-dd"),
                    fiscalYear = reader.IsDBNull(reader.GetOrdinal("fiscal_year")) ? 0 : reader.GetInt32(reader.GetOrdinal("fiscal_year")),
                    fiscalMonth = reader.IsDBNull(reader.GetOrdinal("fiscal_month")) ? 0 : reader.GetInt32(reader.GetOrdinal("fiscal_month")),
                    lineNo = reader.IsDBNull(reader.GetOrdinal("line_no")) ? 0 : int.Parse(reader.GetString(reader.GetOrdinal("line_no"))),
                    accountCode = reader.IsDBNull(reader.GetOrdinal("account_code")) ? null : reader.GetString(reader.GetOrdinal("account_code")),
                    accountName = reader.IsDBNull(reader.GetOrdinal("account_name")) ? null : reader.GetString(reader.GetOrdinal("account_name")),
                    drcr,
                    amount,
                    balance = runningBalance,
                    headerText = reader.IsDBNull(reader.GetOrdinal("header_text")) ? null : reader.GetString(reader.GetOrdinal("header_text")),
                    lineText = reader.IsDBNull(reader.GetOrdinal("line_text")) ? null : reader.GetString(reader.GetOrdinal("line_text")),
                    customerId = reader.IsDBNull(reader.GetOrdinal("customer_id")) ? null : reader.GetString(reader.GetOrdinal("customer_id")),
                    customerName = reader.IsDBNull(reader.GetOrdinal("customer_name")) ? null : reader.GetString(reader.GetOrdinal("customer_name")),
                    vendorId = reader.IsDBNull(reader.GetOrdinal("vendor_id")) ? null : reader.GetString(reader.GetOrdinal("vendor_id")),
                    vendorName = reader.IsDBNull(reader.GetOrdinal("vendor_name")) ? null : reader.GetString(reader.GetOrdinal("vendor_name")),
                    employeeId = reader.IsDBNull(reader.GetOrdinal("employee_id")) ? null : reader.GetString(reader.GetOrdinal("employee_id")),
                    employeeName = reader.IsDBNull(reader.GetOrdinal("employee_name")) ? null : reader.GetString(reader.GetOrdinal("employee_name")),
                    departmentCode = reader.IsDBNull(reader.GetOrdinal("department_code")) ? null : reader.GetString(reader.GetOrdinal("department_code")),
                    departmentName = reader.IsDBNull(reader.GetOrdinal("department_name")) ? null : reader.GetString(reader.GetOrdinal("department_name")),
                    dueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetString(reader.GetOrdinal("due_date")),
                    clearingStatus = reader.IsDBNull(reader.GetOrdinal("clearing_status")) ? null : reader.GetString(reader.GetOrdinal("clearing_status")),
                    clearingVoucherNo = reader.IsDBNull(reader.GetOrdinal("clearing_voucher_no")) ? null : reader.GetString(reader.GetOrdinal("clearing_voucher_no")),
                    clearingDate = reader.IsDBNull(reader.GetOrdinal("clearing_date")) ? null : reader.GetDateTime(reader.GetOrdinal("clearing_date")).ToString("yyyy-MM-dd"),
                    updatedAt = reader.IsDBNull(reader.GetOrdinal("updated_at")) ? null : reader.GetDateTime(reader.GetOrdinal("updated_at")).ToString("yyyy-MM-dd HH:mm"),
                    createdBy = reader.IsDBNull(reader.GetOrdinal("created_by")) ? null : reader.GetString(reader.GetOrdinal("created_by"))
                });
            }

            return Results.Ok(new { data = rows, total, page, pageSize });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 勘定残高（Account Balance）- 按科目查看月度余额汇总
    /// </summary>
    private static void MapAccountBalanceEndpoint(WebApplication app)
    {
        app.MapPost("/reports/account-balance", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var year = root.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : DateTime.Now.Year;
            var accountCode = root.TryGetProperty("accountCode", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null;

            if (string.IsNullOrEmpty(accountCode))
                return Results.BadRequest(new { error = "accountCode is required" });

            await using var conn = await ds.OpenConnectionAsync();

            // 查询该科目在指定年度之前的年度繰越（期初余额）
            var carryForwardSql = @"
                SELECT 
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'DR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as dr_total,
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'CR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as cr_total
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') as line
                WHERE v.company_code = $1
                  AND line->>'accountCode' = $2
                  AND v.posting_date < make_date($3, 1, 1)
            ";

            decimal carryForwardDr = 0m, carryForwardCr = 0m;
            await using (var cfCmd = conn.CreateCommand())
            {
                cfCmd.CommandText = carryForwardSql;
                cfCmd.Parameters.AddWithValue(cc.ToString());
                cfCmd.Parameters.AddWithValue(accountCode);
                cfCmd.Parameters.AddWithValue(year);
                await using var cfReader = await cfCmd.ExecuteReaderAsync();
                if (await cfReader.ReadAsync())
                {
                    carryForwardDr = cfReader.IsDBNull(0) ? 0m : cfReader.GetDecimal(0);
                    carryForwardCr = cfReader.IsDBNull(1) ? 0m : cfReader.GetDecimal(1);
                }
            }

            // 查询指定年度各月的借方/贷方发生额
            var monthlySql = @"
                SELECT 
                    EXTRACT(MONTH FROM v.posting_date)::int as month,
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'DR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as dr_amount,
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'CR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as cr_amount
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') as line
                WHERE v.company_code = $1
                  AND line->>'accountCode' = $2
                  AND EXTRACT(YEAR FROM v.posting_date) = $3
                GROUP BY EXTRACT(MONTH FROM v.posting_date)
                ORDER BY month
            ";

            var monthlyData = new Dictionary<int, (decimal Dr, decimal Cr)>();
            await using (var mCmd = conn.CreateCommand())
            {
                mCmd.CommandText = monthlySql;
                mCmd.Parameters.AddWithValue(cc.ToString());
                mCmd.Parameters.AddWithValue(accountCode);
                mCmd.Parameters.AddWithValue(year);
                await using var mReader = await mCmd.ExecuteReaderAsync();
                while (await mReader.ReadAsync())
                {
                    var month = mReader.GetInt32(0);
                    var dr = mReader.IsDBNull(1) ? 0m : mReader.GetDecimal(1);
                    var cr = mReader.IsDBNull(2) ? 0m : mReader.GetDecimal(2);
                    monthlyData[month] = (dr, cr);
                }
            }

            // 构建返回数据（12个月 + 年度繰越行）
            var rows = new List<object>();
            var carryForwardBalance = carryForwardDr - carryForwardCr;
            var runningBalance = carryForwardBalance;

            // 年度繰越行
            rows.Add(new
            {
                yearMonth = "年度繰越",
                month = 0,
                drAmount = carryForwardDr,
                crAmount = carryForwardCr,
                monthBalance = carryForwardBalance,
                cumulativeBalance = runningBalance
            });

            // 当年12个月
            for (int m = 1; m <= 12; m++)
            {
                var (dr, cr) = monthlyData.TryGetValue(m, out var data) ? data : (0m, 0m);
                var monthBalance = dr - cr;
                runningBalance += monthBalance;

                rows.Add(new
                {
                    yearMonth = $"{year}-{m:D2}",
                    month = m,
                    drAmount = dr,
                    crAmount = cr,
                    monthBalance,
                    cumulativeBalance = runningBalance
                });
            }

            // 下一年前3个月
            var nextYear = year + 1;
            var nextYearSql = @"
                SELECT 
                    EXTRACT(MONTH FROM v.posting_date)::int as month,
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'DR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as dr_amount,
                    COALESCE(SUM(CASE WHEN (line->>'drcr') = 'CR' THEN (line->>'amount')::numeric ELSE 0 END), 0) as cr_amount
                FROM vouchers v,
                     jsonb_array_elements(v.payload->'lines') as line
                WHERE v.company_code = $1
                  AND line->>'accountCode' = $2
                  AND EXTRACT(YEAR FROM v.posting_date) = $3
                  AND EXTRACT(MONTH FROM v.posting_date) <= 3
                GROUP BY EXTRACT(MONTH FROM v.posting_date)
                ORDER BY month
            ";

            await using (var nyCmd = conn.CreateCommand())
            {
                nyCmd.CommandText = nextYearSql;
                nyCmd.Parameters.AddWithValue(cc.ToString());
                nyCmd.Parameters.AddWithValue(accountCode);
                nyCmd.Parameters.AddWithValue(nextYear);
                await using var nyReader = await nyCmd.ExecuteReaderAsync();
                while (await nyReader.ReadAsync())
                {
                    var month = nyReader.GetInt32(0);
                    var dr = nyReader.IsDBNull(1) ? 0m : nyReader.GetDecimal(1);
                    var cr = nyReader.IsDBNull(2) ? 0m : nyReader.GetDecimal(2);
                    var monthBalance = dr - cr;
                    runningBalance += monthBalance;

                    rows.Add(new
                    {
                        yearMonth = $"{nextYear}-{month:D2}",
                        month = month + 12,
                        drAmount = dr,
                        crAmount = cr,
                        monthBalance,
                        cumulativeBalance = runningBalance
                    });
                }
            }

            return Results.Ok(new
            {
                data = rows,
                year,
                accountCode,
                carryForwardDr,
                carryForwardCr,
                carryForwardBalance
            });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 財務諸表（Balance Sheet / Income Statement）
    /// </summary>
    private static void MapFinancialStatementsEndpoints(WebApplication app)
    {
        app.MapGet("/reports/financial/balance-sheet", async (HttpRequest req, [FromQuery] string? period, [FromQuery] string? currency, [FromQuery] bool refresh, FinancialStatementService financial) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var user = Auth.GetUserCtx(req);
            if (!HasFinancialReportCapability(user))
                return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
            if (!TryParseMonth(period, out var month))
                return Results.BadRequest(new { error = "invalid period, expected yyyy-MM" });
            var curr = string.IsNullOrWhiteSpace(currency) ? "JPY" : currency!;
            try
            {
                var result = await financial.GetBalanceSheetAsync(cc.ToString()!, month, curr, refresh, req.HttpContext.RequestAborted);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();

        app.MapGet("/reports/financial/income-statement", async (HttpRequest req, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? currency, [FromQuery] bool refresh, FinancialStatementService financial) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var user = Auth.GetUserCtx(req);
            if (!HasFinancialReportCapability(user))
                return Results.Json(new { error = "forbidden: require capability report:financial" }, statusCode: 403);
            if (!TryParseMonth(from, out var fromMonth))
                return Results.BadRequest(new { error = "invalid from, expected yyyy-MM" });
            if (!TryParseMonth(to, out var toMonth))
                return Results.BadRequest(new { error = "invalid to, expected yyyy-MM" });
            if (fromMonth > toMonth)
                return Results.BadRequest(new { error = "from must be earlier than to" });
            var curr = string.IsNullOrWhiteSpace(currency) ? "JPY" : currency!;
            try
            {
                var result = await financial.GetIncomeStatementAsync(cc.ToString()!, fromMonth, toMonth, curr, refresh, req.HttpContext.RequestAborted);
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization();
    }

    // Helper methods
    private static bool HasFinancialReportCapability(Auth.UserCtx ctx)
        => (ctx.Caps?.Contains("report:financial") ?? false)
           || (ctx.Caps?.Contains("roles:manage") ?? false)
           || (ctx.Roles?.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase) || string.Equals(r, "FinanceManager", StringComparison.OrdinalIgnoreCase)) ?? false);

    private static bool TryParseMonth(string? value, out DateOnly month)
    {
        month = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length == 7 && DateTime.TryParseExact(value + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            month = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }
}


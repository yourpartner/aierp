using System.Text;
using System.Text.Json;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Standard;

/// <summary>
/// FB 支付模块 - 全銀協フォーマット自動支払機能
/// </summary>
public class FbPaymentModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "fb_payment",
        Name = "FB支払",
        Description = "全銀協フォーマット FB ファイル生成・自動支払機能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = Array.Empty<MenuConfig>() // 菜单已在其他模块定义
    };

    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // FB 支払サービスは必要に応じて追加
    }

    public override void MapEndpoints(WebApplication app)
    {
        MapFbPaymentModule(app);
    }

    /// <summary>
    /// 静态方法，用于直接从 Program.cs 注册端点
    /// </summary>
    public static void MapFbPaymentModule(WebApplication app)
    {
        MapFilesListEndpoint(app);
        MapPendingDebtsEndpoint(app);
        MapCreateEndpoint(app);
        MapDownloadEndpoint(app);
    }

    /// <summary>
    /// FB文件列表
    /// </summary>
    private static void MapFilesListEndpoint(WebApplication app)
    {
        app.MapPost("/fb-payment/files", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var startDate = root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;
            var endDate = root.TryGetProperty("endDate", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null;
            var page = root.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 1;
            var pageSize = root.TryGetProperty("pageSize", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 50;

            await using var conn = await ds.OpenConnectionAsync();

            var sql = new StringBuilder(@"
                SELECT id, file_name, record_type, bank_code, bank_name, branch_code, branch_name, 
                       payment_date, deposit_type, account_number, account_holder, 
                       total_count, total_amount, status, created_by, created_at
                FROM fb_payment_files
                WHERE company_code = $1
            ");
            var parameters = new List<NpgsqlParameter> { new() { Value = cc.ToString() } };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(startDate))
            {
                sql.Append($" AND payment_date >= ${paramIndex}::date");
                parameters.Add(new() { Value = startDate });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(endDate))
            {
                sql.Append($" AND payment_date <= ${paramIndex}::date");
                parameters.Add(new() { Value = endDate });
                paramIndex++;
            }

            // Count
            var countSql = $"SELECT COUNT(*) FROM ({sql}) sub";
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = countSql;
            foreach (var p in parameters) countCmd.Parameters.Add(new NpgsqlParameter { Value = p.Value });
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync() ?? 0);

            sql.Append($" ORDER BY payment_date DESC, created_at DESC LIMIT ${paramIndex} OFFSET ${paramIndex + 1}");
            parameters.Add(new() { Value = pageSize });
            parameters.Add(new() { Value = (page - 1) * pageSize });

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter { Value = p.Value });

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    id = reader.GetGuid(0).ToString(),
                    fileName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    recordType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    bankCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                    bankName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    branchCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    branchName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    paymentDate = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("yyyy-MM-dd"),
                    depositType = reader.IsDBNull(8) ? null : reader.GetString(8),
                    accountNumber = reader.IsDBNull(9) ? null : reader.GetString(9),
                    accountHolder = reader.IsDBNull(10) ? null : reader.GetString(10),
                    totalCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    totalAmount = reader.IsDBNull(12) ? 0m : reader.GetDecimal(12),
                    status = reader.IsDBNull(13) ? null : reader.GetString(13),
                    createdBy = reader.IsDBNull(14) ? null : reader.GetString(14),
                    createdAt = reader.IsDBNull(15) ? null : reader.GetDateTime(15).ToString("yyyy-MM-dd HH:mm")
                });
            }

            return Results.Ok(new { data = rows, total, page, pageSize });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 获取未支付债务列表（用于自动支付提案）
    /// </summary>
    private static void MapPendingDebtsEndpoint(WebApplication app)
    {
        app.MapPost("/fb-payment/pending-debts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var accountCodes = root.TryGetProperty("accountCodes", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? ac.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            var postingDateFrom = root.TryGetProperty("postingDateFrom", out var pdf) && pdf.ValueKind == JsonValueKind.String ? pdf.GetString() : null;
            var postingDateTo = root.TryGetProperty("postingDateTo", out var pdt) && pdt.ValueKind == JsonValueKind.String ? pdt.GetString() : null;
            var dueDateFrom = root.TryGetProperty("dueDateFrom", out var ddf) && ddf.ValueKind == JsonValueKind.String ? ddf.GetString() : null;
            var dueDateTo = root.TryGetProperty("dueDateTo", out var ddt) && ddt.ValueKind == JsonValueKind.String ? ddt.GetString() : null;

            await using var conn = await ds.OpenConnectionAsync();

            // 查询有未清余额的贷方凭证行（债务）
            var sql = new StringBuilder(@"
                WITH debt_lines AS (
                    SELECT 
                        v.id as voucher_id,
                        v.voucher_no,
                        v.posting_date,
                        v.payload->'header'->>'summary' as header_text,
                        line->>'lineNo' as line_no,
                        line->>'accountCode' as account_code,
                        line->>'drcr' as drcr,
                        (line->>'amount')::numeric as amount,
                        line->>'description' as line_text,
                        line->>'vendorId' as vendor_id,
                        line->>'employeeId' as employee_id,
                        line->>'dueDate' as due_date,
                        line->>'bankCode' as bank_code,
                        line->>'branchCode' as branch_code,
                        line->>'accountNumber' as account_number,
                        line->>'accountHolder' as account_holder,
                        line->>'depositType' as deposit_type
                    FROM vouchers v,
                         jsonb_array_elements(v.payload->'lines') as line
                    WHERE v.company_code = $1
                      AND (line->>'drcr') = 'CR'
                )
                SELECT 
                    dl.*,
                    a.payload->>'name' as account_name,
                    bp.payload->>'name' as vendor_name,
                    bp.payload->'bankAccounts'->0->>'bankCode' as bp_bank_code,
                    bp.payload->'bankAccounts'->0->>'branchCode' as bp_branch_code,
                    bp.payload->'bankAccounts'->0->>'accountNumber' as bp_account_number,
                    bp.payload->'bankAccounts'->0->>'accountHolder' as bp_account_holder,
                    bp.payload->'bankAccounts'->0->>'depositType' as bp_deposit_type,
                    e.payload->>'name' as employee_name,
                    e.payload->'bankAccount'->>'bankCode' as emp_bank_code,
                    e.payload->'bankAccount'->>'branchCode' as emp_branch_code,
                    e.payload->'bankAccount'->>'accountNumber' as emp_account_number,
                    e.payload->'bankAccount'->>'accountHolder' as emp_account_holder,
                    e.payload->'bankAccount'->>'depositType' as emp_deposit_type,
                    oi.id as open_item_id,
                    oi.residual_amount
                FROM debt_lines dl
                LEFT JOIN accounts a ON a.company_code = $1 AND a.account_code = dl.account_code
                LEFT JOIN businesspartners bp ON bp.company_code = $1 AND bp.partner_code = dl.vendor_id
                LEFT JOIN employees e ON e.company_code = $1 AND e.id::text = dl.employee_id
                LEFT JOIN open_items oi ON oi.company_code = $1 AND oi.voucher_id = dl.voucher_id AND oi.voucher_line_no::text = dl.line_no
                WHERE (oi.residual_amount IS NULL OR oi.residual_amount > 0)
            ");

            var parameters = new List<NpgsqlParameter> { new() { Value = cc.ToString() } };
            int paramIndex = 2;

            if (accountCodes.Count > 0)
            {
                sql.Append($" AND dl.account_code = ANY(${paramIndex}::text[])");
                parameters.Add(new() { Value = accountCodes.ToArray() });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(postingDateFrom))
            {
                sql.Append($" AND dl.posting_date >= ${paramIndex}::date");
                parameters.Add(new() { Value = postingDateFrom });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(postingDateTo))
            {
                sql.Append($" AND dl.posting_date <= ${paramIndex}::date");
                parameters.Add(new() { Value = postingDateTo });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(dueDateFrom))
            {
                sql.Append($" AND dl.due_date >= ${paramIndex}");
                parameters.Add(new() { Value = dueDateFrom });
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(dueDateTo))
            {
                sql.Append($" AND dl.due_date <= ${paramIndex}");
                parameters.Add(new() { Value = dueDateTo });
                paramIndex++;
            }

            sql.Append(" ORDER BY dl.due_date, dl.posting_date, dl.voucher_no");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            foreach (var p in parameters) cmd.Parameters.Add(new NpgsqlParameter { Value = p.Value });

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var vendorId = reader.IsDBNull(reader.GetOrdinal("vendor_id")) ? null : reader.GetString(reader.GetOrdinal("vendor_id"));
                var employeeId = reader.IsDBNull(reader.GetOrdinal("employee_id")) ? null : reader.GetString(reader.GetOrdinal("employee_id"));

                // 优先使用行级银行信息，其次使用取引先/员工的银行信息
                string? bankCode = null, branchCode = null, accountNumber = null, accountHolder = null, depositType = null;

                if (!reader.IsDBNull(reader.GetOrdinal("bank_code")))
                    bankCode = reader.GetString(reader.GetOrdinal("bank_code"));
                else if (!string.IsNullOrEmpty(vendorId) && !reader.IsDBNull(reader.GetOrdinal("bp_bank_code")))
                    bankCode = reader.GetString(reader.GetOrdinal("bp_bank_code"));
                else if (!string.IsNullOrEmpty(employeeId) && !reader.IsDBNull(reader.GetOrdinal("emp_bank_code")))
                    bankCode = reader.GetString(reader.GetOrdinal("emp_bank_code"));

                if (!reader.IsDBNull(reader.GetOrdinal("branch_code")))
                    branchCode = reader.GetString(reader.GetOrdinal("branch_code"));
                else if (!string.IsNullOrEmpty(vendorId) && !reader.IsDBNull(reader.GetOrdinal("bp_branch_code")))
                    branchCode = reader.GetString(reader.GetOrdinal("bp_branch_code"));
                else if (!string.IsNullOrEmpty(employeeId) && !reader.IsDBNull(reader.GetOrdinal("emp_branch_code")))
                    branchCode = reader.GetString(reader.GetOrdinal("emp_branch_code"));

                if (!reader.IsDBNull(reader.GetOrdinal("account_number")))
                    accountNumber = reader.GetString(reader.GetOrdinal("account_number"));
                else if (!string.IsNullOrEmpty(vendorId) && !reader.IsDBNull(reader.GetOrdinal("bp_account_number")))
                    accountNumber = reader.GetString(reader.GetOrdinal("bp_account_number"));
                else if (!string.IsNullOrEmpty(employeeId) && !reader.IsDBNull(reader.GetOrdinal("emp_account_number")))
                    accountNumber = reader.GetString(reader.GetOrdinal("emp_account_number"));

                if (!reader.IsDBNull(reader.GetOrdinal("account_holder")))
                    accountHolder = reader.GetString(reader.GetOrdinal("account_holder"));
                else if (!string.IsNullOrEmpty(vendorId) && !reader.IsDBNull(reader.GetOrdinal("bp_account_holder")))
                    accountHolder = reader.GetString(reader.GetOrdinal("bp_account_holder"));
                else if (!string.IsNullOrEmpty(employeeId) && !reader.IsDBNull(reader.GetOrdinal("emp_account_holder")))
                    accountHolder = reader.GetString(reader.GetOrdinal("emp_account_holder"));

                if (!reader.IsDBNull(reader.GetOrdinal("deposit_type")))
                    depositType = reader.GetString(reader.GetOrdinal("deposit_type"));
                else if (!string.IsNullOrEmpty(vendorId) && !reader.IsDBNull(reader.GetOrdinal("bp_deposit_type")))
                    depositType = reader.GetString(reader.GetOrdinal("bp_deposit_type"));
                else if (!string.IsNullOrEmpty(employeeId) && !reader.IsDBNull(reader.GetOrdinal("emp_deposit_type")))
                    depositType = reader.GetString(reader.GetOrdinal("emp_deposit_type"));

                var amount = reader.IsDBNull(reader.GetOrdinal("amount")) ? 0m : reader.GetDecimal(reader.GetOrdinal("amount"));
                var residual = reader.IsDBNull(reader.GetOrdinal("residual_amount")) ? amount : reader.GetDecimal(reader.GetOrdinal("residual_amount"));

                rows.Add(new
                {
                    voucherId = reader.IsDBNull(reader.GetOrdinal("voucher_id")) ? null : reader.GetGuid(reader.GetOrdinal("voucher_id")).ToString(),
                    voucherNo = reader.IsDBNull(reader.GetOrdinal("voucher_no")) ? null : reader.GetString(reader.GetOrdinal("voucher_no")),
                    lineNo = reader.IsDBNull(reader.GetOrdinal("line_no")) ? "0" : reader.GetString(reader.GetOrdinal("line_no")),
                    postingDate = reader.IsDBNull(reader.GetOrdinal("posting_date")) ? null : reader.GetDateTime(reader.GetOrdinal("posting_date")).ToString("yyyy-MM-dd"),
                    accountCode = reader.IsDBNull(reader.GetOrdinal("account_code")) ? null : reader.GetString(reader.GetOrdinal("account_code")),
                    accountName = reader.IsDBNull(reader.GetOrdinal("account_name")) ? null : reader.GetString(reader.GetOrdinal("account_name")),
                    amount = amount,
                    residualAmount = residual,
                    dueDate = reader.IsDBNull(reader.GetOrdinal("due_date")) ? null : reader.GetString(reader.GetOrdinal("due_date")),
                    headerText = reader.IsDBNull(reader.GetOrdinal("header_text")) ? null : reader.GetString(reader.GetOrdinal("header_text")),
                    lineText = reader.IsDBNull(reader.GetOrdinal("line_text")) ? null : reader.GetString(reader.GetOrdinal("line_text")),
                    vendorId,
                    vendorName = reader.IsDBNull(reader.GetOrdinal("vendor_name")) ? null : reader.GetString(reader.GetOrdinal("vendor_name")),
                    employeeId,
                    employeeName = reader.IsDBNull(reader.GetOrdinal("employee_name")) ? null : reader.GetString(reader.GetOrdinal("employee_name")),
                    bankCode,
                    branchCode,
                    accountNumber,
                    accountHolder,
                    depositType,
                    openItemId = reader.IsDBNull(reader.GetOrdinal("open_item_id")) ? null : reader.GetGuid(reader.GetOrdinal("open_item_id")).ToString()
                });
            }

            return Results.Ok(new { data = rows });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 创建FB支付文件
    /// </summary>
    private static void MapCreateEndpoint(WebApplication app)
    {
        app.MapPost("/fb-payment/create", async (HttpRequest req, NpgsqlDataSource ds, Auth.UserCtx? user) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var paymentDate = root.TryGetProperty("paymentDate", out var pd) && pd.ValueKind == JsonValueKind.String ? pd.GetString() : DateTime.Now.ToString("yyyy-MM-dd");
            var bankCode = root.TryGetProperty("bankCode", out var bc) && bc.ValueKind == JsonValueKind.String ? bc.GetString() : "";
            var bankName = root.TryGetProperty("bankName", out var bn) && bn.ValueKind == JsonValueKind.String ? bn.GetString() : "";
            var branchCode = root.TryGetProperty("branchCode", out var brc) && brc.ValueKind == JsonValueKind.String ? brc.GetString() : "";
            var branchName = root.TryGetProperty("branchName", out var brn) && brn.ValueKind == JsonValueKind.String ? brn.GetString() : "";
            var depositType = root.TryGetProperty("depositType", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : "1";
            var accountNumber = root.TryGetProperty("accountNumber", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : "";
            var accountHolder = root.TryGetProperty("accountHolder", out var ah) && ah.ValueKind == JsonValueKind.String ? ah.GetString() : "";

            var items = root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array
                ? it.EnumerateArray().ToList()
                : new List<JsonElement>();

            if (items.Count == 0)
                return Results.BadRequest(new { error = "No items selected" });

            // 生成全銀協フォーマット FB 文件
            var sb = new StringBuilder();
            var payDate = DateTime.Parse(paymentDate!);
            var totalAmount = 0m;
            var lineItems = new List<object>();
            var voucherIds = new List<string>();

            // ヘッダーレコード (種別コード=1)
            sb.AppendLine($"1210{bankCode?.PadLeft(4, '0') ?? "0000"}{bankName?.PadRight(15) ?? new string(' ', 15)}{branchCode?.PadLeft(3, '0') ?? "000"}{branchName?.PadRight(15) ?? new string(' ', 15)}{depositType ?? "1"}{accountNumber?.PadLeft(7, '0') ?? "0000000"}{accountHolder?.PadRight(30) ?? new string(' ', 30)}{payDate:MMdd}");

            // データレコード (種別コード=2)
            int seq = 1;
            foreach (var item in items)
            {
                var itemBankCode = item.TryGetProperty("bankCode", out var ibc) && ibc.ValueKind == JsonValueKind.String ? ibc.GetString() : "";
                var itemBankName = item.TryGetProperty("bankName", out var ibn) && ibn.ValueKind == JsonValueKind.String ? ibn.GetString() : "";
                var itemBranchCode = item.TryGetProperty("branchCode", out var ibrc) && ibrc.ValueKind == JsonValueKind.String ? ibrc.GetString() : "";
                var itemBranchName = item.TryGetProperty("branchName", out var ibrn) && ibrn.ValueKind == JsonValueKind.String ? ibrn.GetString() : "";
                var itemDepositType = item.TryGetProperty("depositType", out var idt) && idt.ValueKind == JsonValueKind.String ? idt.GetString() : "1";
                var itemAccountNumber = item.TryGetProperty("accountNumber", out var ian) && ian.ValueKind == JsonValueKind.String ? ian.GetString() : "";
                var itemAccountHolder = item.TryGetProperty("accountHolder", out var iah) && iah.ValueKind == JsonValueKind.String ? iah.GetString() : "";
                var itemAmount = item.TryGetProperty("amount", out var ia) && ia.ValueKind == JsonValueKind.Number ? ia.GetDecimal() : 0m;
                var itemVoucherId = item.TryGetProperty("voucherId", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : "";
                var itemVoucherNo = item.TryGetProperty("voucherNo", out var ivn) && ivn.ValueKind == JsonValueKind.String ? ivn.GetString() : "";

                // データレコード
                sb.AppendLine($"2{itemBankCode?.PadLeft(4, '0') ?? "0000"}{itemBankName?.PadRight(15) ?? new string(' ', 15)}{itemBranchCode?.PadLeft(3, '0') ?? "000"}{itemBranchName?.PadRight(15) ?? new string(' ', 15)}{itemDepositType ?? "1"}{itemAccountNumber?.PadLeft(7, '0') ?? "0000000"}{itemAccountHolder?.PadRight(30) ?? new string(' ', 30)}{((long)itemAmount).ToString().PadLeft(10, '0')}0{itemVoucherNo?.PadRight(20) ?? new string(' ', 20)}");

                totalAmount += itemAmount;
                lineItems.Add(new { seq, bankCode = itemBankCode, branchCode = itemBranchCode, accountNumber = itemAccountNumber, accountHolder = itemAccountHolder, amount = itemAmount, voucherNo = itemVoucherNo });
                if (!string.IsNullOrEmpty(itemVoucherId)) voucherIds.Add(itemVoucherId);
                seq++;
            }

            // トレーラーレコード (種別コード=8)
            sb.AppendLine($"8{items.Count.ToString().PadLeft(6, '0')}{((long)totalAmount).ToString().PadLeft(12, '0')}");

            // エンドレコード (種別コード=9)
            sb.AppendLine("9");

            var fileContent = sb.ToString();
            var fileName = $"FB_{payDate:yyyyMMdd}_{DateTime.Now:HHmmss}.txt";

            // 保存到数据库
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO fb_payment_files (company_code, file_name, file_content, record_type, bank_code, bank_name, branch_code, branch_name, payment_date, deposit_type, account_number, account_holder, total_count, total_amount, line_items, voucher_ids, created_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::date, $10, $11, $12, $13, $14, $15::jsonb, $16::jsonb, $17)
                RETURNING id
            ";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(fileName);
            cmd.Parameters.AddWithValue(fileContent);
            cmd.Parameters.AddWithValue("21");
            cmd.Parameters.AddWithValue(bankCode ?? "");
            cmd.Parameters.AddWithValue(bankName ?? "");
            cmd.Parameters.AddWithValue(branchCode ?? "");
            cmd.Parameters.AddWithValue(branchName ?? "");
            cmd.Parameters.AddWithValue(paymentDate!);
            cmd.Parameters.AddWithValue(depositType ?? "1");
            cmd.Parameters.AddWithValue(accountNumber ?? "");
            cmd.Parameters.AddWithValue(accountHolder ?? "");
            cmd.Parameters.AddWithValue(items.Count);
            cmd.Parameters.AddWithValue(totalAmount);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(lineItems));
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(voucherIds));
            cmd.Parameters.AddWithValue(user?.UserId ?? "system");

            var fileId = (Guid)(await cmd.ExecuteScalarAsync() ?? Guid.Empty);

            return Results.Ok(new
            {
                id = fileId.ToString(),
                fileName,
                totalCount = items.Count,
                totalAmount,
                fileContent
            });
        }).RequireAuthorization();
    }

    /// <summary>
    /// 下载FB文件
    /// </summary>
    private static void MapDownloadEndpoint(WebApplication app)
    {
        app.MapGet("/fb-payment/download/{id}", async (string id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!Guid.TryParse(id, out var fileId))
                return Results.BadRequest(new { error = "Invalid file ID" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_name, file_content FROM fb_payment_files WHERE company_code = $1 AND id = $2";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(fileId);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return Results.NotFound(new { error = "File not found" });

            var fileName = reader.GetString(0);
            var fileContent = reader.GetString(1);

            // 使用 Shift_JIS 编码（全銀協標準）
            var bytes = Encoding.GetEncoding("Shift_JIS").GetBytes(fileContent);

            return Results.File(bytes, "text/plain", fileName);
        }).RequireAuthorization();
    }
}


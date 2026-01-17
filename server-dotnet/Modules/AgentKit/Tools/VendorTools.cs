using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 供应商查询工具
/// </summary>
public sealed class LookupVendorTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupVendorTool(NpgsqlDataSource ds, ILogger<LookupVendorTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_vendor";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query が必要です", "query 必填"));
        }

        Logger.LogInformation("[LookupVendorTool] 查询供应商: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        // 先精确匹配代码
        cmd.CommandText = "SELECT vendor_code, payload::text FROM vendors WHERE company_code=$1 AND vendor_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(query.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var payload = reader.GetString(1);
            return SuccessResult(new { found = true, query, vendorCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }

        await reader.CloseAsync();

        // 模糊匹配名称
        cmd.Parameters.Clear();
        cmd.CommandText = @"SELECT vendor_code, payload::text FROM vendors 
                            WHERE company_code=$1 AND (
                                payload->>'name' ILIKE $2 
                                OR payload->>'shortName' ILIKE $2
                            ) ORDER BY vendor_code LIMIT 5";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue($"%{query.Trim()}%");

        await using var reader2 = await cmd.ExecuteReaderAsync(ct);
        var results = new List<object>();
        while (await reader2.ReadAsync(ct))
        {
            var code = reader2.GetString(0);
            var payload = reader2.GetString(1);
            results.Add(new { vendorCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }

        return results.Count > 0
            ? SuccessResult(new { found = true, query, results })
            : SuccessResult(new { found = false, query });
    }
}

/// <summary>
/// 搜索供应商请求书工具
/// </summary>
public sealed class SearchVendorReceiptsTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public SearchVendorReceiptsTool(NpgsqlDataSource ds, ILogger<SearchVendorReceiptsTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "search_vendor_receipts";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var vendorCode = GetString(args, "vendor_code") ?? GetString(args, "vendorCode");
        var dateFrom = GetString(args, "date_from") ?? GetString(args, "dateFrom");
        var dateTo = GetString(args, "date_to") ?? GetString(args, "dateTo");
        var status = GetString(args, "status");
        var limit = GetInt(args, "limit") ?? 20;

        Logger.LogInformation("[SearchVendorReceiptsTool] 搜索供应商请求书: VendorCode={VendorCode}, DateFrom={DateFrom}, DateTo={DateTo}",
            vendorCode, dateFrom, dateTo);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "company_code=$1" };
        var paramIndex = 2;
        cmd.Parameters.AddWithValue(context.CompanyCode);

        if (!string.IsNullOrWhiteSpace(vendorCode))
        {
            conditions.Add($"vendor_code=${paramIndex++}");
            cmd.Parameters.AddWithValue(vendorCode);
        }

        if (!string.IsNullOrWhiteSpace(dateFrom))
        {
            conditions.Add($"invoice_date >= ${paramIndex++}::date");
            cmd.Parameters.AddWithValue(dateFrom);
        }

        if (!string.IsNullOrWhiteSpace(dateTo))
        {
            conditions.Add($"invoice_date <= ${paramIndex++}::date");
            cmd.Parameters.AddWithValue(dateTo);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add($"status = ${paramIndex++}");
            cmd.Parameters.AddWithValue(status);
        }

        cmd.CommandText = $@"SELECT id, vendor_code, invoice_no, invoice_date, grand_total, status, payload::text
                             FROM vendor_invoices
                             WHERE {string.Join(" AND ", conditions)}
                             ORDER BY invoice_date DESC, created_at DESC
                             LIMIT {Math.Min(limit, 100)}";

        var results = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new
            {
                id = reader.GetGuid(0),
                vendorCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                invoiceNo = reader.IsDBNull(2) ? null : reader.GetString(2),
                invoiceDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                grandTotal = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                status = reader.IsDBNull(5) ? null : reader.GetString(5),
                payload = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<object>(reader.GetString(6), JsonOptions)
            });
        }

        return SuccessResult(new { count = results.Count, results });
    }
}

/// <summary>
/// 获取费用科目选项工具
/// </summary>
public sealed class GetExpenseAccountOptionsTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;
    private readonly AgentAccountingRuleService _ruleService;

    public GetExpenseAccountOptionsTool(NpgsqlDataSource ds, AgentAccountingRuleService ruleService, ILogger<GetExpenseAccountOptionsTool> logger) : base(logger)
    {
        _ds = ds;
        _ruleService = ruleService;
    }

    public override string Name => "get_expense_account_options";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var category = GetString(args, "category");
        var vendorCode = GetString(args, "vendor_code") ?? GetString(args, "vendorCode");

        Logger.LogInformation("[GetExpenseAccountOptionsTool] 获取费用科目选项: Category={Category}, VendorCode={VendorCode}",
            category, vendorCode);

        var rules = await _ruleService.ListAsync(context.CompanyCode, false, ct);
        var recommendations = new List<object>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT account_code, payload::text FROM accounts 
                            WHERE company_code=$1 
                            AND (account_code LIKE '6%' OR account_code LIKE '7%')
                            ORDER BY account_code 
                            LIMIT 50";
        cmd.Parameters.AddWithValue(context.CompanyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var payload = reader.GetString(1);
            string? name = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("name", out var nameEl))
                {
                    name = nameEl.GetString();
                }
            }
            catch { }

            recommendations.Add(new { accountCode = code, accountName = name ?? code });
        }

        return SuccessResult(new
        {
            category,
            vendorCode,
            rules = rules.Select(r => new { r.Id, r.Title, r.AccountCode, r.AccountName, r.Priority, r.IsActive }),
            options = recommendations
        });
    }
}

/// <summary>
/// 创建供应商请求书工具
/// </summary>
public sealed class CreateVendorInvoiceTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public CreateVendorInvoiceTool(NpgsqlDataSource ds, ILogger<CreateVendorInvoiceTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "create_vendor_invoice";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var vendorId = GetString(args, "vendor_id") ?? GetString(args, "vendorId");
        if (string.IsNullOrWhiteSpace(vendorId))
        {
            return ErrorResult(Localize(context.Language, "vendor_id が必要です", "vendor_id 必填"));
        }

        var invoiceDate = GetString(args, "invoice_date") ?? GetString(args, "invoiceDate") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dueDate = GetString(args, "due_date") ?? GetString(args, "dueDate");
        var totalAmount = GetDecimal(args, "total_amount") ?? GetDecimal(args, "totalAmount") ?? 0m;
        var taxAmount = GetDecimal(args, "tax_amount") ?? GetDecimal(args, "taxAmount") ?? 0m;
        var summary = GetString(args, "summary");

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Resolve vendor
        string? vendorCode = null;
        string? vendorName = null;
        await using (var vCmd = conn.CreateCommand())
        {
            vCmd.CommandText = "SELECT partner_code, name FROM businesspartners WHERE company_code=$1 AND (id::text = $2 OR partner_code = $2) LIMIT 1";
            vCmd.Parameters.AddWithValue(context.CompanyCode);
            vCmd.Parameters.AddWithValue(vendorId);
            await using var vRd = await vCmd.ExecuteReaderAsync(ct);
            if (await vRd.ReadAsync(ct))
            {
                vendorCode = vRd.IsDBNull(0) ? null : vRd.GetString(0);
                vendorName = vRd.IsDBNull(1) ? null : vRd.GetString(1);
            }
        }

        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            return ErrorResult(Localize(context.Language, "仕入先が見つかりません", "供应商不存在"));
        }

        var invoiceNo = await GenerateVendorInvoiceNoAsync(context.CompanyCode, ct);
        var netAmount = totalAmount > 0 ? totalAmount - taxAmount : 0m;
        var finalTaxAmount = taxAmount > 0 ? taxAmount : Math.Round(netAmount * 0.1m, 0);
        var grandTotal = netAmount + finalTaxAmount;

        var lines = new JsonArray();
        if (args.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            lines = JsonNode.Parse(linesEl.GetRawText())?.AsArray() ?? new JsonArray();
        }

        var payload = new JsonObject
        {
            ["invoiceNo"] = invoiceNo,
            ["vendorCode"] = vendorCode,
            ["vendorName"] = vendorName ?? "",
            ["invoiceDate"] = invoiceDate,
            ["dueDate"] = dueDate ?? "",
            ["subtotal"] = netAmount,
            ["taxTotal"] = finalTaxAmount,
            ["grandTotal"] = grandTotal,
            ["currency"] = "JPY",
            ["status"] = "draft",
            ["summary"] = summary ?? "",
            ["lines"] = lines,
            ["createdBy"] = context.UserCtx.UserName ?? context.UserCtx.UserId ?? "agent",
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["source"] = "agent"
        };

        await using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO vendor_invoices (company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
        ins.Parameters.AddWithValue(context.CompanyCode);
        ins.Parameters.AddWithValue(payload.ToJsonString());
        var idObj = await ins.ExecuteScalarAsync(ct);
        var invoiceId = idObj is Guid g ? g : Guid.Empty;
        if (invoiceId == Guid.Empty)
        {
            return ErrorResult(Localize(context.Language, "請求書の作成に失敗しました", "创建请求书失败"));
        }

        var msg = Localize(context.Language,
            $"仕入先請求書 {invoiceNo} を作成しました。",
            $"已创建供应商请求书 {invoiceNo}");

        return SuccessResult(new
        {
            status = "ok",
            invoiceId = invoiceId.ToString(),
            invoiceNo,
            vendorCode,
            vendorName,
            invoiceDate,
            dueDate,
            netAmount,
            taxAmount = finalTaxAmount,
            grandTotal,
            message = msg
        });
    }

    private async Task<string> GenerateVendorInvoiceNoAsync(string companyCode, CancellationToken ct)
    {
        var prefix = DateTime.UtcNow.ToString("yyyyMMdd");
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM vendor_invoices WHERE company_code=$1 AND invoice_no LIKE $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"VI-{prefix}-%");
        var countObj = await cmd.ExecuteScalarAsync(ct);
        var count = countObj is int i ? i : Convert.ToInt32(countObj ?? 0, CultureInfo.InvariantCulture);
        return $"VI-{prefix}-{(count + 1):D4}";
    }
}


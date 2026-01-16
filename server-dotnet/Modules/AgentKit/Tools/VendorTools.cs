using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 搜索供应商发票/请求书工具
/// </summary>
public sealed class SearchVendorReceiptsTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public SearchVendorReceiptsTool(NpgsqlDataSource ds, ILogger<SearchVendorReceiptsTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "search_vendor_receipts";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var vendorCode = GetString(args, "vendor_code") ?? GetString(args, "vendorCode");
        var dateFrom = GetString(args, "date_from") ?? GetString(args, "dateFrom");
        var dateTo = GetString(args, "date_to") ?? GetString(args, "dateTo");
        var status = GetString(args, "status");
        var limit = GetInt(args, "limit") ?? 20;

        Logger.LogInformation("[SearchVendorReceiptsTool] 搜索供应商发票: VendorCode={VendorCode}, DateFrom={DateFrom}, DateTo={DateTo}",
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
            conditions.Add($"issue_date >= ${paramIndex++}::date");
            cmd.Parameters.AddWithValue(dateFrom);
        }

        if (!string.IsNullOrWhiteSpace(dateTo))
        {
            conditions.Add($"issue_date <= ${paramIndex++}::date");
            cmd.Parameters.AddWithValue(dateTo);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add($"status = ${paramIndex++}");
            cmd.Parameters.AddWithValue(status);
        }

        cmd.CommandText = $@"SELECT id, vendor_code, issue_date, due_date, total_amount, status, payload::text 
                             FROM vendor_invoices 
                             WHERE {string.Join(" AND ", conditions)} 
                             ORDER BY issue_date DESC 
                             LIMIT {Math.Min(limit, 100)}";

        var results = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new
            {
                id = reader.GetInt64(0),
                vendorCode = reader.GetString(1),
                issueDate = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                dueDate = reader.IsDBNull(3) ? null : reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                totalAmount = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                status = reader.IsDBNull(5) ? null : reader.GetString(5),
                payload = reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<object>(reader.GetString(6), JsonOptions)
            });
        }

        return SuccessResult(new
        {
            count = results.Count,
            results
        });
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

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var category = GetString(args, "category");
        var vendorCode = GetString(args, "vendor_code") ?? GetString(args, "vendorCode");

        Logger.LogInformation("[GetExpenseAccountOptionsTool] 获取费用科目选项: Category={Category}, VendorCode={VendorCode}",
            category, vendorCode);

        // 从会计规则服务获取推荐科目
        var rules = await _ruleService.GetRulesAsync(context.CompanyCode, ct);
        var recommendations = new List<object>();

        // 查询常用费用科目
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

            recommendations.Add(new
            {
                accountCode = code,
                accountName = name ?? code
            });
        }

        return SuccessResult(new
        {
            category,
            vendorCode,
            options = recommendations
        });
    }
}





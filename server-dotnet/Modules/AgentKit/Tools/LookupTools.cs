using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 会计期间检查工具
/// </summary>
public sealed class CheckAccountingPeriodTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public CheckAccountingPeriodTool(NpgsqlDataSource ds, ILogger<CheckAccountingPeriodTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "check_accounting_period";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var postingDate = GetString(args, "posting_date") ?? GetString(args, "postingDate");
        if (string.IsNullOrWhiteSpace(postingDate))
        {
            return ErrorResult(Localize(context.Language, "posting_date が必要です", "posting_date 必填"));
        }

        Logger.LogInformation("[CheckAccountingPeriodTool] 检查会计期间: {PostingDate}, CompanyCode={CompanyCode}", postingDate, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_open FROM accounting_periods WHERE company_code=$1 AND period_start <= $2::date AND period_end >= $2::date ORDER BY period_end DESC LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(postingDate);
        
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is null)
        {
            return SuccessResult(new { exists = false, isOpen = true, message = "未找到对应期间，视为开放" });
        }
        
        var isOpen = Convert.ToBoolean(scalar, CultureInfo.InvariantCulture);
        return SuccessResult(new { exists = true, isOpen, message = isOpen ? "会计期间开放" : "会计期间已关闭" });
    }
}

/// <summary>
/// 发票登记号验证工具
/// </summary>
public sealed class VerifyInvoiceRegistrationTool : AgentToolBase
{
    private readonly InvoiceRegistryService _invoiceRegistry;

    public VerifyInvoiceRegistrationTool(InvoiceRegistryService invoiceRegistry, ILogger<VerifyInvoiceRegistrationTool> logger) : base(logger)
    {
        _invoiceRegistry = invoiceRegistry;
    }

    public override string Name => "verify_invoice_registration";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var regNo = GetString(args, "registration_no") ?? GetString(args, "registrationNo") ?? GetString(args, "reg_no");
        if (string.IsNullOrWhiteSpace(regNo))
        {
            return ErrorResult(Localize(context.Language, "registration_no が必要です", "registration_no 必填"));
        }

        Logger.LogInformation("[VerifyInvoiceRegistrationTool] 验证发票登记号: {RegNo}", regNo);

        var normalized = InvoiceRegistryService.Normalize(regNo);
        if (!InvoiceRegistryService.IsFormatValid(normalized))
        {
            return SuccessResult(new { valid = false, normalized, reason = "格式不正确" });
        }

        var result = await _invoiceRegistry.VerifyAsync(normalized);
        var isValid = result.Status == InvoiceVerificationStatus.Matched;
        return SuccessResult(new { valid = isValid, normalized, name = result.Name, reason = isValid ? "登记号有效" : $"登记号无效或未登记 ({InvoiceRegistryService.StatusKey(result.Status)})" });
    }
}

/// <summary>
/// 客户查询工具
/// </summary>
public sealed class LookupCustomerTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupCustomerTool(NpgsqlDataSource ds, ILogger<LookupCustomerTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_customer";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query が必要です", "query 必填"));
        }

        Logger.LogInformation("[LookupCustomerTool] 查询客户: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 先精确匹配代码
        cmd.CommandText = "SELECT customer_code, payload::text FROM customers WHERE company_code=$1 AND customer_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(query.Trim());
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var payload = reader.GetString(1);
            return SuccessResult(new { found = true, query, customerCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }
        
        await reader.CloseAsync();
        
        // 模糊匹配名称
        cmd.Parameters.Clear();
        cmd.CommandText = @"SELECT customer_code, payload::text FROM customers 
                            WHERE company_code=$1 AND (
                                payload->>'name' ILIKE $2 
                                OR payload->>'shortName' ILIKE $2
                            ) ORDER BY customer_code LIMIT 5";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue($"%{query.Trim()}%");
        
        await using var reader2 = await cmd.ExecuteReaderAsync(ct);
        var results = new List<object>();
        while (await reader2.ReadAsync(ct))
        {
            var code = reader2.GetString(0);
            var payload = reader2.GetString(1);
            results.Add(new { customerCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }
        
        if (results.Count > 0)
        {
            return SuccessResult(new { found = true, query, results });
        }
        
        return SuccessResult(new { found = false, query });
    }
}

/// <summary>
/// 物料/品目查询工具
/// </summary>
public sealed class LookupMaterialTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupMaterialTool(NpgsqlDataSource ds, ILogger<LookupMaterialTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_material";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query が必要です", "query 必填"));
        }

        Logger.LogInformation("[LookupMaterialTool] 查询物料: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 先精确匹配代码
        cmd.CommandText = "SELECT material_code, payload::text FROM materials WHERE company_code=$1 AND material_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(query.Trim());
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var code = reader.GetString(0);
            var payload = reader.GetString(1);
            return SuccessResult(new { found = true, query, materialCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }
        
        await reader.CloseAsync();
        
        // 模糊匹配名称
        cmd.Parameters.Clear();
        cmd.CommandText = @"SELECT material_code, payload::text FROM materials 
                            WHERE company_code=$1 AND (
                                payload->>'name' ILIKE $2 
                                OR payload->>'description' ILIKE $2
                            ) ORDER BY material_code LIMIT 5";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue($"%{query.Trim()}%");
        
        await using var reader2 = await cmd.ExecuteReaderAsync(ct);
        var results = new List<object>();
        while (await reader2.ReadAsync(ct))
        {
            var code = reader2.GetString(0);
            var payload = reader2.GetString(1);
            results.Add(new { materialCode = code, payload = JsonSerializer.Deserialize<object>(payload, JsonOptions) });
        }
        
        if (results.Count > 0)
        {
            return SuccessResult(new { found = true, query, results });
        }
        
        return SuccessResult(new { found = false, query });
    }
}






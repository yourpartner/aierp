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
/// 浼氳鏈熼棿妫€鏌ュ伐鍏?/// </summary>
public sealed class CheckAccountingPeriodTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public CheckAccountingPeriodTool(NpgsqlDataSource ds, ILogger<CheckAccountingPeriodTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "check_accounting_period";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var postingDate = GetString(args, "posting_date") ?? GetString(args, "postingDate");
        if (string.IsNullOrWhiteSpace(postingDate))
        {
            return ErrorResult(Localize(context.Language, "posting_date 銇屽繀瑕併仹銇?, "posting_date 蹇呭～"));
        }

        Logger.LogInformation("[CheckAccountingPeriodTool] 妫€鏌ヤ細璁℃湡闂? {PostingDate}, CompanyCode={CompanyCode}", postingDate, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_open FROM accounting_periods WHERE company_code=$1 AND period_start <= $2::date AND period_end >= $2::date ORDER BY period_end DESC LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(postingDate);
        
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is null)
        {
            return SuccessResult(new { exists = false, isOpen = true, message = "鏈壘鍒板搴旀湡闂达紝瑙嗕负寮€鏀? });
        }
        
        var isOpen = Convert.ToBoolean(scalar, CultureInfo.InvariantCulture);
        return SuccessResult(new { exists = true, isOpen, message = isOpen ? "浼氳鏈熼棿寮€鏀? : "浼氳鏈熼棿宸插叧闂? });
    }
}

/// <summary>
/// 鍙戠エ鐧昏鍙烽獙璇佸伐鍏?/// </summary>
public sealed class VerifyInvoiceRegistrationTool : AgentToolBase
{
    private readonly InvoiceRegistryService _invoiceRegistry;

    public VerifyInvoiceRegistrationTool(InvoiceRegistryService invoiceRegistry, ILogger<VerifyInvoiceRegistrationTool> logger) : base(logger)
    {
        _invoiceRegistry = invoiceRegistry;
    }

    public override string Name => "verify_invoice_registration";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var regNo = GetString(args, "registration_no") ?? GetString(args, "registrationNo") ?? GetString(args, "reg_no");
        if (string.IsNullOrWhiteSpace(regNo))
        {
            return ErrorResult(Localize(context.Language, "registration_no 銇屽繀瑕併仹銇?, "registration_no 蹇呭～"));
        }

        Logger.LogInformation("[VerifyInvoiceRegistrationTool] 楠岃瘉鍙戠エ鐧昏鍙? {RegNo}", regNo);

        var normalized = InvoiceRegistryService.Normalize(regNo);
        if (!InvoiceRegistryService.IsFormatValid(normalized))
        {
            return SuccessResult(new { valid = false, normalized, reason = "鏍煎紡涓嶆纭? });
        }

        var isValid = await _invoiceRegistry.VerifyAsync(normalized, ct);
        return SuccessResult(new { valid = isValid, normalized, reason = isValid ? "鐧昏鍙锋湁鏁? : "鐧昏鍙锋棤鏁堟垨鏈櫥璁? });
    }
}

/// <summary>
/// 瀹㈡埛鏌ヨ宸ュ叿
/// </summary>
public sealed class LookupCustomerTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupCustomerTool(NpgsqlDataSource ds, ILogger<LookupCustomerTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_customer";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query 銇屽繀瑕併仹銇?, "query 蹇呭～"));
        }

        Logger.LogInformation("[LookupCustomerTool] 鏌ヨ瀹㈡埛: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 鍏堢簿纭尮閰嶄唬鐮?        cmd.CommandText = "SELECT customer_code, payload::text FROM customers WHERE company_code=$1 AND customer_code=$2 LIMIT 1";
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
        
        // 妯＄硦鍖归厤鍚嶇О
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
/// 鐗╂枡/鍝佺洰鏌ヨ宸ュ叿
/// </summary>
public sealed class LookupMaterialTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupMaterialTool(NpgsqlDataSource ds, ILogger<LookupMaterialTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_material";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query 銇屽繀瑕併仹銇?, "query 蹇呭～"));
        }

        Logger.LogInformation("[LookupMaterialTool] 鏌ヨ鐗╂枡: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 鍏堢簿纭尮閰嶄唬鐮?        cmd.CommandText = "SELECT material_code, payload::text FROM materials WHERE company_code=$1 AND material_code=$2 LIMIT 1";
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
        
        // 妯＄硦鍖归厤鍚嶇О
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

/// <summary>
/// 渚涘簲鍟嗘煡璇㈠伐鍏?/// </summary>
public sealed class LookupVendorTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupVendorTool(NpgsqlDataSource ds, ILogger<LookupVendorTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_vendor";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query 銇屽繀瑕併仹銇?, "query 蹇呭～"));
        }

        Logger.LogInformation("[LookupVendorTool] 鏌ヨ渚涘簲鍟? {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 鍏堢簿纭尮閰嶄唬鐮?        cmd.CommandText = "SELECT vendor_code, payload::text FROM vendors WHERE company_code=$1 AND vendor_code=$2 LIMIT 1";
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
        
        // 妯＄硦鍖归厤鍚嶇О
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
        
        if (results.Count > 0)
        {
            return SuccessResult(new { found = true, query, results });
        }
        
        return SuccessResult(new { found = false, query });
    }
}



using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;



using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 科目查询工具 - 根据科目代码或名称查找会计科目
/// </summary>
public sealed class LookupAccountTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public LookupAccountTool(NpgsqlDataSource ds, ILogger<LookupAccountTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "lookup_account";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult(Localize(context.Language, "query が必要です", "query 必填"));
        }

        Logger.LogInformation("[LookupAccountTool] 查询科目: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        var result = await LookupAccountAsync(context.CompanyCode, query, ct);

        // 注册查询结果到上下文（用于科目白名单验证）
        if (result.Found && !string.IsNullOrWhiteSpace(result.AccountCode))
        {
            context.RegisterLookupAccountResult(result.AccountCode);
        }

        return SuccessResult(result);
    }

    private async Task<LookupAccountResult> LookupAccountAsync(string companyCode, string query, CancellationToken ct)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new LookupAccountResult(false, query, null, null, Array.Empty<string>());
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 精确匹配科目代码
        var exact = await QueryAccountAsync(conn, companyCode, 
            "SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1",
            trimmed, ct);
        if (exact is not null) return BuildResult(trimmed, exact.Value.code, exact.Value.payload);

        // 按名称匹配
        var byName = await QueryAccountAsync(conn, companyCode,
            "SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND LOWER(payload->>'name') = LOWER($2) LIMIT 1",
            trimmed, ct);
        if (byName is not null) return BuildResult(trimmed, byName.Value.code, byName.Value.payload);

        // 按别名匹配
        var byAlias = await QueryAccountAsync(conn, companyCode,
            @"SELECT account_code, payload::text FROM accounts 
              WHERE company_code=$1 AND EXISTS (
                  SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                  WHERE LOWER(alias) = LOWER($2)
              ) LIMIT 1",
            trimmed, ct);
        if (byAlias is not null) return BuildResult(trimmed, byAlias.Value.code, byAlias.Value.payload);

        // 模糊匹配
        var fuzzy = await QueryAccountAsync(conn, companyCode,
            @"SELECT account_code, payload::text FROM accounts 
              WHERE company_code=$1 AND (
                  payload->>'name' ILIKE $2
                  OR EXISTS (
                      SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                      WHERE alias ILIKE $2
                  )
              ) ORDER BY account_code LIMIT 1",
            $"%{trimmed}%", ct);
        if (fuzzy is not null) return BuildResult(trimmed, fuzzy.Value.code, fuzzy.Value.payload);

        return new LookupAccountResult(false, trimmed, null, null, Array.Empty<string>());
    }

    private static async Task<(string code, string payload)?> QueryAccountAsync(
        NpgsqlConnection conn, string companyCode, string sql, string param, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(param);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return (reader.GetString(0), reader.GetString(1));
        }
        return null;
    }

    private static LookupAccountResult BuildResult(string query, string code, string payloadJson)
    {
        string accountName = string.Empty;
        var aliases = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                accountName = nameEl.GetString() ?? string.Empty;
            }
            
            if (root.TryGetProperty("aliases", out var aliasEl) && aliasEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in aliasEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var alias = item.GetString();
                        if (!string.IsNullOrWhiteSpace(alias)) aliases.Add(alias);
                    }
                }
            }
        }
        catch
        {
            // ignore malformed payload
        }

        return new LookupAccountResult(true, query, code, accountName, aliases);
    }

    private sealed record LookupAccountResult(
        bool Found, 
        string Query, 
        string? AccountCode, 
        string? AccountName, 
        IReadOnlyList<string> Aliases);
}





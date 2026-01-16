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
/// 绉戠洰鏌ヨ宸ュ叿 - 鏍规嵁绉戠洰浠ｇ爜鎴栧悕绉版煡鎵句細璁＄鐩?/// </summary>
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
            return ErrorResult(Localize(context.Language, "query 銇屽繀瑕併仹銇?, "query 蹇呭～"));
        }

        Logger.LogInformation("[LookupAccountTool] 鏌ヨ绉戠洰: {Query}, CompanyCode={CompanyCode}", query, context.CompanyCode);

        var result = await LookupAccountAsync(context.CompanyCode, query, ct);

        // 娉ㄥ唽鏌ヨ缁撴灉鍒颁笂涓嬫枃锛堢敤浜庣鐩櫧鍚嶅崟楠岃瘉锛?        if (result.Found && !string.IsNullOrWhiteSpace(result.AccountCode))
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

        // 绮剧‘鍖归厤绉戠洰浠ｇ爜
        var exact = await QueryAccountAsync(conn, companyCode, 
            "SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1",
            trimmed, ct);
        if (exact is not null) return BuildResult(trimmed, exact.Value.code, exact.Value.payload);

        // 鎸夊悕绉板尮閰?        var byName = await QueryAccountAsync(conn, companyCode,
            "SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND LOWER(payload->>'name') = LOWER($2) LIMIT 1",
            trimmed, ct);
        if (byName is not null) return BuildResult(trimmed, byName.Value.code, byName.Value.payload);

        // 鎸夊埆鍚嶅尮閰?        var byAlias = await QueryAccountAsync(conn, companyCode,
            @"SELECT account_code, payload::text FROM accounts 
              WHERE company_code=$1 AND EXISTS (
                  SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                  WHERE LOWER(alias) = LOWER($2)
              ) LIMIT 1",
            trimmed, ct);
        if (byAlias is not null) return BuildResult(trimmed, byAlias.Value.code, byAlias.Value.payload);

        // 妯＄硦鍖归厤
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



using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;



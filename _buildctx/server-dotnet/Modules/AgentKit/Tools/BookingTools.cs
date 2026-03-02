using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Booking.com 结算单解析工具
/// </summary>
public sealed class ExtractBookingSettlementDataTool : AgentToolBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ExtractBookingSettlementDataTool(IHttpClientFactory httpClientFactory, ILogger<ExtractBookingSettlementDataTool> logger) : base(logger)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override string Name => "extract_booking_settlement_data";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var fileId = GetString(args, "file_id") ?? GetString(args, "fileId") ?? context.DefaultFileId;
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return ErrorResult(Localize(context.Language, "file_id が必要です", "file_id 必填"));
        }

        if (context.TryResolveAttachmentToken(fileId, out var resolved))
        {
            fileId = resolved;
        }

        var file = context.ResolveFile(fileId!);
        if (file is null)
        {
            return ErrorResult(Localize(context.Language, $"ファイル {fileId} が見つかりません", $"文件 {fileId} 未找到"));
        }

        Logger.LogInformation("[ExtractBookingSettlementDataTool] 解析 Booking 结算单 fileId={FileId}", fileId);

        var preview = AiFileHelpers.ExtractTextPreview(file.StoredPath, file.ContentType, 20000);
        var sanitized = AiFileHelpers.SanitizePreview(preview, 20000) ?? string.Empty;

        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, context.ApiKey);

        var systemPrompt = Localize(context.Language,
            @"你是Booking.com结算单解析助手。请从用户提供的PDF文本中提取以下字段并输出 JSON：
- paymentDate (YYYY-MM-DD)
- grossAmount (数字)
- commissionAmount (数字, 正数)
- paymentFeeAmount (数字, 正数)
- netAmount (数字)
- currency (如 JPY)
- facilityId (可选)
- statementPeriod (可选, 如 2024-10-01~2024-10-31)
如果无法识别字段，请返回空字符串或0，不要编造。",
            @"あなたはBooking.com結算書の解析アシスタントです。次の JSON を出力してください：
- paymentDate (YYYY-MM-DD)
- grossAmount (数値)
- commissionAmount (数値, 正の値)
- paymentFeeAmount (数値, 正の値)
- netAmount (数値)
- currency (JPY など)
- facilityId (任意)
- statementPeriod (任意、例：2024-10-01~2024-10-31)
取得できない項目は空文字または0を返し、推測しないこと。");

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = sanitized }
        };

        var response = await OpenAiApiHelper.CallOpenAiAsync(
            http, context.ApiKey, "gpt-4o-mini", messages,
            temperature: 0.1, maxTokens: 2048, jsonMode: true, ct: ct);

        var content = response.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return ErrorResult(Localize(context.Language, "解析結果が空です", "解析结果为空"));
        }

        var cleaned = content.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline > 0 ? cleaned[(firstNewline + 1)..] : cleaned[3..];
        }
        if (cleaned.EndsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^3];
        }

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(cleaned)?.AsObject();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ExtractBookingSettlementDataTool] 解析 JSON 失败: {Content}", cleaned);
            return ErrorResult(Localize(context.Language, "解析JSON失败", "解析JSON失败"));
        }

        if (node is not null)
        {
            context.RegisterDocument(fileId, node);
        }
        return AgentKitService.ToolExecutionResult.FromModel(node ?? new JsonObject { ["status"] = "error" });
    }
}

/// <summary>
/// 查找 Booking 结算对应的银行入金记录
/// </summary>
public sealed class FindMoneytreeDepositForSettlementTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public FindMoneytreeDepositForSettlementTool(NpgsqlDataSource ds, ILogger<FindMoneytreeDepositForSettlementTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "find_moneytree_deposit_for_settlement";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var paymentDate = GetString(args, "payment_date") ?? GetString(args, "paymentDate");
        if (string.IsNullOrWhiteSpace(paymentDate) || !DateTime.TryParse(paymentDate, out var payDate))
        {
            return ErrorResult(Localize(context.Language, "payment_date が必要です", "payment_date 必填"));
        }

        var netAmount = GetDecimal(args, "net_amount") ?? GetDecimal(args, "netAmount") ?? 0m;
        if (netAmount <= 0m)
        {
            return ErrorResult(Localize(context.Language, "net_amount が必要です", "net_amount 必填"));
        }

        var daysTol = GetInt(args, "days_tolerance") ?? 7;
        var amountTol = GetDecimal(args, "amount_tolerance") ?? 1m;
        var keywords = new List<string>();
        if (args.TryGetProperty("keywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in kwEl.EnumerateArray())
            {
                if (k.ValueKind == JsonValueKind.String)
                {
                    var s = k.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) keywords.Add(s.Trim());
                }
            }
        }

        var from = payDate.Date.AddDays(-daysTol);
        var to = payDate.Date.AddDays(daysTol);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, transaction_date, deposit_amount, description, voucher_id, voucher_no
FROM moneytree_transactions
WHERE company_code = $1
  AND transaction_date BETWEEN $2 AND $3
  AND deposit_amount IS NOT NULL
  AND ABS(deposit_amount - $4) <= $5";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        cmd.Parameters.AddWithValue(netAmount);
        cmd.Parameters.AddWithValue(amountTol);

        var matches = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var desc = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            if (keywords.Count > 0 && !keywords.Any(k => desc.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            matches.Add(new
            {
                id = reader.GetGuid(0),
                transactionDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1).ToString("yyyy-MM-dd"),
                depositAmount = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                description = desc,
                voucherId = reader.IsDBNull(4) ? null : reader.GetGuid(4).ToString(),
                voucherNo = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return SuccessResult(new
        {
            found = matches.Count > 0,
            count = matches.Count,
            results = matches
        });
    }
}


using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 根据凭证号查询凭证工具
/// </summary>
public sealed class GetVoucherByNumberTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public GetVoucherByNumberTool(NpgsqlDataSource ds, ILogger<GetVoucherByNumberTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "get_voucher_by_number";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var voucherNo = GetString(args, "voucher_no") ?? GetString(args, "voucherNo");
        if (string.IsNullOrWhiteSpace(voucherNo))
        {
            return ErrorResult(Localize(context.Language, "voucher_no が必要です", "voucher_no 必填"));
        }

        Logger.LogInformation("[GetVoucherByNumberTool] 查询凭证: {VoucherNo}", voucherNo);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM vouchers WHERE company_code=$1 AND payload->'header'->>'voucherNo' = $2 LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(voucherNo);

        var payload = await cmd.ExecuteScalarAsync(ct) as string;
        if (payload is null)
        {
            return SuccessResult(new { found = false, voucherNo });
        }

        var msg = new AgentResultMessage("assistant", $"已找到凭证 {voucherNo}", "info", new
        {
            label = voucherNo,
            action = "openEmbed",
            key = "vouchers.list",
            payload = new { voucherNo, detailOnly = true }
        });

        var model = new
        {
            found = true,
            voucherNo,
            payload = JsonSerializer.Deserialize<object>(payload, JsonOptions)
        };

        return new ToolExecutionResult(
            JsonSerializer.Serialize(model, JsonOptions),
            new List<AgentResultMessage> { msg });
    }
}





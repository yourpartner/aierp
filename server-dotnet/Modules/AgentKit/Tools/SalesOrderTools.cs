using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 创建销售订单工具
/// 注意：完整的业务逻辑保留在 AgentKitService 中
/// 此处提供工具接口封装和参数验证
/// </summary>
public sealed class CreateSalesOrderTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public CreateSalesOrderTool(NpgsqlDataSource ds, ILogger<CreateSalesOrderTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "create_sales_order";

    public override Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        // 基本参数验证
        Logger.LogInformation("[CreateSalesOrderTool] 验证销售订单参数");

        var root = JsonNode.Parse(args.GetRawText())?.AsObject() ?? new JsonObject();

        // 验证必填字段
        var customerCode = ReadJsonString(root, "customerCode");
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            var customerNode = root.TryGetPropertyValue("customer", out var c) && c is JsonObject cObj ? cObj : null;
            customerCode = ReadJsonString(customerNode, "code");
        }

        if (string.IsNullOrWhiteSpace(customerCode))
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "顧客コードが必須です", "customerCode 必须指定")));
        }

        if (!root.TryGetPropertyValue("lines", out var linesNode) || linesNode is not JsonArray lineArray || lineArray.Count == 0)
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "品目明細が不足しています", "缺少品目明细")));
        }

        // 验证每行明细
        var lineNo = 1;
        foreach (var item in lineArray)
        {
            if (item is not JsonObject lineObj) continue;
            
            var materialCode = ReadJsonString(lineObj, "materialCode") ?? ReadJsonString(lineObj, "code");
            if (string.IsNullOrWhiteSpace(materialCode))
            {
                return Task.FromResult(ErrorResult(Localize(context.Language, 
                    $"明細 {lineNo} に品目コードがありません", 
                    $"明细 {lineNo} 缺少 materialCode")));
            }

            var qty = ReadJsonDecimal(lineObj, "quantity");
            if (qty <= 0m)
            {
                return Task.FromResult(ErrorResult(Localize(context.Language,
                    $"明細 {lineNo} の数量が不正です",
                    $"明细 {lineNo} 的数量必须大于0")));
            }

            lineNo++;
        }

        // 返回验证通过的响应
        // 注意：实际创建逻辑在 AgentKitService.CreateSalesOrderAsync 中
        // 这个工具目前仅用于参数验证，不会被工具注册表使用
        return Task.FromResult(SuccessResult(new
        {
            status = "validated",
            message = Localize(context.Language, "受注パラメータの検証が完了しました", "销售订单参数验证通过"),
            customerCode,
            lineCount = lineArray.Count
        }));
    }

    private static string? ReadJsonString(JsonObject? obj, string propertyName)
    {
        if (obj is null) return null;
        if (obj.TryGetPropertyValue(propertyName, out var node) && node is JsonValue val && val.TryGetValue<string>(out var s))
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        return null;
    }

    private static decimal ReadJsonDecimal(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node)) return 0m;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<decimal>(out var d)) return d;
            if (val.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<string>(out var s) && decimal.TryParse(s, out var parsed)) return parsed;
        }
        return 0m;
    }
}





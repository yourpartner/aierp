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
/// 鍒涘缓閿€鍞鍗曞伐鍏?/// 娉ㄦ剰锛氬畬鏁寸殑涓氬姟閫昏緫淇濈暀鍦?AgentKitService 涓?/// 姝ゅ鎻愪緵宸ュ叿鎺ュ彛灏佽鍜屽弬鏁伴獙璇?/// </summary>
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
        // 鍩烘湰鍙傛暟楠岃瘉
        Logger.LogInformation("[CreateSalesOrderTool] 楠岃瘉閿€鍞鍗曞弬鏁?);

        var root = JsonNode.Parse(args.GetRawText())?.AsObject() ?? new JsonObject();

        // 楠岃瘉蹇呭～瀛楁
        var customerCode = ReadJsonString(root, "customerCode");
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            var customerNode = root.TryGetPropertyValue("customer", out var c) && c is JsonObject cObj ? cObj : null;
            customerCode = ReadJsonString(customerNode, "code");
        }

        if (string.IsNullOrWhiteSpace(customerCode))
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "椤у銈炽兗銉夈亴蹇呴爤銇с仚", "customerCode 蹇呴』鎸囧畾")));
        }

        if (!root.TryGetPropertyValue("lines", out var linesNode) || linesNode is not JsonArray lineArray || lineArray.Count == 0)
        {
            return Task.FromResult(ErrorResult(Localize(context.Language, "鍝佺洰鏄庣窗銇屼笉瓒炽仐銇︺亜銇俱仚", "缂哄皯鍝佺洰鏄庣粏")));
        }

        // 楠岃瘉姣忚鏄庣粏
        var lineNo = 1;
        foreach (var item in lineArray)
        {
            if (item is not JsonObject lineObj) continue;
            
            var materialCode = ReadJsonString(lineObj, "materialCode") ?? ReadJsonString(lineObj, "code");
            if (string.IsNullOrWhiteSpace(materialCode))
            {
                return Task.FromResult(ErrorResult(Localize(context.Language, 
                    $"鏄庣窗 {lineNo} 銇搧鐩偝銉笺儔銇屻亗銈娿伨銇涖倱", 
                    $"鏄庣粏 {lineNo} 缂哄皯 materialCode")));
            }

            var qty = ReadJsonDecimal(lineObj, "quantity");
            if (qty <= 0m)
            {
                return Task.FromResult(ErrorResult(Localize(context.Language,
                    $"鏄庣窗 {lineNo} 銇暟閲忋亴涓嶆銇с仚",
                    $"鏄庣粏 {lineNo} 鐨勬暟閲忓繀椤诲ぇ浜?")));
            }

            lineNo++;
        }

        // 杩斿洖楠岃瘉閫氳繃鐨勫搷搴?        // 娉ㄦ剰锛氬疄闄呭垱寤洪€昏緫鍦?AgentKitService.CreateSalesOrderAsync 涓?        // 杩欎釜宸ュ叿鐩墠浠呯敤浜庡弬鏁伴獙璇侊紝涓嶄細琚伐鍏锋敞鍐岃〃浣跨敤
        return Task.FromResult(SuccessResult(new
        {
            status = "validated",
            message = Localize(context.Language, "鍙楁敞銉戙儵銉°兗銈裤伄妞滆銇屽畬浜嗐仐銇俱仐銇?, "閿€鍞鍗曞弬鏁伴獙璇侀€氳繃"),
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



using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;



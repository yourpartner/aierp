using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Moneytree 瑙勫垯娉ㄥ唽宸ュ叿
/// </summary>
public sealed class RegisterMoneytreeRuleTool : AgentToolBase
{
    private readonly MoneytreePostingRuleService _ruleService;

    public RegisterMoneytreeRuleTool(MoneytreePostingRuleService ruleService, ILogger<RegisterMoneytreeRuleTool> logger) : base(logger)
    {
        _ruleService = ruleService;
    }

    public override string Name => "register_moneytree_rule";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var title = GetString(args, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return ErrorResult(Localize(context.Language, "title 銇屽繀瑕併仹銇?, "title 蹇呭～"));
        }

        Logger.LogInformation("[RegisterMoneytreeRuleTool] 娉ㄥ唽 Moneytree 瑙勫垯: {Title}", title);

        try
        {
            var ruleSpec = ParseMoneytreeRuleSpec(args);
            var ruleId = await _ruleService.UpsertAsync(context.CompanyCode, ruleSpec, ct);

            return SuccessResult(new
            {
                status = "ok",
                ruleId,
                title,
                message = Localize(context.Language,
                    $"銉兗銉€寋title}銆嶃倰鐧婚尣銇椼伨銇椼仧",
                    $"瑙勫垯銆寋title}銆嶅凡娉ㄥ唽")
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[RegisterMoneytreeRuleTool] 瑙勫垯娉ㄥ唽澶辫触");
            return ErrorResult(ex.Message);
        }
    }

    private static MoneytreePostingRuleService.MoneytreePostingRuleUpsert ParseMoneytreeRuleSpec(JsonElement args)
    {
        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var description = args.TryGetProperty("description", out var d) ? d.GetString() : null;
        int? priority = args.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pv) ? pv : null;
        var isActive = args.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True;

        var matchConditions = new JsonObject();
        if (args.TryGetProperty("match", out var matchEl) && matchEl.ValueKind == JsonValueKind.Object)
        {
            matchConditions = JsonNode.Parse(matchEl.GetRawText())?.AsObject() ?? new JsonObject();
        }

        var postingRules = new JsonArray();
        if (args.TryGetProperty("postingRules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
        {
            postingRules = JsonNode.Parse(rulesEl.GetRawText())?.AsArray() ?? new JsonArray();
        }

        return new MoneytreePostingRuleService.MoneytreePostingRuleUpsert(
            Id: null,
            Title: title!,
            Description: description,
            Priority: priority ?? 100,
            IsActive: isActive,
            MatchConditions: matchConditions,
            PostingRules: postingRules
        );
    }
}

/// <summary>
/// 鎵归噺娉ㄥ唽 Moneytree 瑙勫垯宸ュ叿
/// </summary>
public sealed class BulkRegisterMoneytreeRuleTool : AgentToolBase
{
    private readonly MoneytreePostingRuleService _ruleService;

    public BulkRegisterMoneytreeRuleTool(MoneytreePostingRuleService ruleService, ILogger<BulkRegisterMoneytreeRuleTool> logger) : base(logger)
    {
        _ruleService = ruleService;
    }

    public override string Name => "bulk_register_moneytree_rule";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        if (!args.TryGetProperty("rules", out var rulesEl) || rulesEl.ValueKind != JsonValueKind.Array)
        {
            return ErrorResult(Localize(context.Language, "rules 閰嶅垪銇屽繀瑕併仹銇?, "rules 鏁扮粍蹇呭～"));
        }

        Logger.LogInformation("[BulkRegisterMoneytreeRuleTool] 鎵归噺娉ㄥ唽 Moneytree 瑙勫垯");

        var results = new List<object>();
        var successCount = 0;
        var failCount = 0;

        foreach (var ruleEl in rulesEl.EnumerateArray())
        {
            if (ruleEl.ValueKind != JsonValueKind.Object) continue;

            var title = ruleEl.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                failCount++;
                results.Add(new { title = "(unknown)", status = "error", message = "title missing" });
                continue;
            }

            try
            {
                var ruleSpec = ParseMoneytreeRuleSpec(ruleEl);
                var ruleId = await _ruleService.UpsertAsync(context.CompanyCode, ruleSpec, ct);
                successCount++;
                results.Add(new { title, status = "ok", ruleId });
            }
            catch (Exception ex)
            {
                failCount++;
                results.Add(new { title, status = "error", message = ex.Message });
            }
        }

        return SuccessResult(new
        {
            status = "completed",
            successCount,
            failCount,
            results,
            message = Localize(context.Language,
                $"{successCount} 浠躲伄銉兗銉倰鐧婚尣銇椼伨銇椼仧锛坽failCount} 浠跺け鏁楋級",
                $"宸叉敞鍐?{successCount} 鏉¤鍒欙紙{failCount} 鏉″け璐ワ級")
        });
    }

    private static MoneytreePostingRuleService.MoneytreePostingRuleUpsert ParseMoneytreeRuleSpec(JsonElement args)
    {
        var title = args.TryGetProperty("title", out var t) ? t.GetString() : null;
        var description = args.TryGetProperty("description", out var d) ? d.GetString() : null;
        int? priority = args.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pv) ? pv : null;
        var isActive = !args.TryGetProperty("isActive", out var a) || a.ValueKind != JsonValueKind.False;

        var matchConditions = new JsonObject();
        if (args.TryGetProperty("match", out var matchEl) && matchEl.ValueKind == JsonValueKind.Object)
        {
            matchConditions = JsonNode.Parse(matchEl.GetRawText())?.AsObject() ?? new JsonObject();
        }

        var postingRules = new JsonArray();
        if (args.TryGetProperty("postingRules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
        {
            postingRules = JsonNode.Parse(rulesEl.GetRawText())?.AsArray() ?? new JsonArray();
        }

        return new MoneytreePostingRuleService.MoneytreePostingRuleUpsert(
            Id: null,
            Title: title!,
            Description: description,
            Priority: priority ?? 100,
            IsActive: isActive,
            MatchConditions: matchConditions,
            PostingRules: postingRules
        );
    }
}



using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;



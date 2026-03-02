using System.Text.Json;
using System.Text.Json.Nodes;
using Server.Modules;

namespace Server.Modules;

/// <summary>
/// Agent Skills REST API 端点处理器
/// </summary>
static class AgentSkillEndpoints
{
    // ====================== Skills ======================

    public static async Task<IResult> ListSkillsAsync(HttpRequest req, AgentSkillService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var cc) ? cc.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
            return Results.BadRequest(new { error = "Missing x-company-code" });

        var includeInactive = req.Query.TryGetValue("all", out var allVal) &&
            (bool.TryParse(allVal, out var b) && b || string.Equals(allVal, "1"));
        var items = await service.ListAsync(companyCode!, includeInactive, req.HttpContext.RequestAborted);
        return Results.Json(items);
    }

    public static async Task<IResult> GetSkillAsync(HttpRequest req, string skillKey, AgentSkillService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var cc) ? cc.ToString() : null;
        if (string.IsNullOrWhiteSpace(companyCode))
            return Results.BadRequest(new { error = "Missing x-company-code" });

        var skill = await service.GetEffectiveSkillAsync(companyCode!, skillKey, req.HttpContext.RequestAborted);
        if (skill == null) return Results.NotFound(new { error = "Skill not found" });
        return Results.Json(skill);
    }

    public static async Task<IResult> GetSkillByIdAsync(HttpRequest req, Guid id, AgentSkillService service)
    {
        var skill = await service.GetByIdAsync(id, req.HttpContext.RequestAborted);
        if (skill == null) return Results.NotFound(new { error = "Skill not found" });
        return Results.Json(skill);
    }

    public static async Task<IResult> UpsertSkillAsync(HttpRequest req, AgentSkillService service)
    {
        var companyCode = req.Headers.TryGetValue("x-company-code", out var cc) ? cc.ToString() : null;

        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
        var root = doc.RootElement;

        var skillKey = GetString(root, "skillKey");
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(skillKey) || string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "skillKey and name are required" });

        // 如果指定了 global=true，则 companyCode = null（全局模板）
        var isGlobal = root.TryGetProperty("global", out var gEl) && gEl.ValueKind == JsonValueKind.True;
        var effectiveCompany = isGlobal ? null : companyCode;

        Guid? id = null;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedId))
            id = parsedId;

        var input = new AgentSkillService.SkillInput(
            Id: id,
            SkillKey: skillKey!,
            Name: name!,
            Description: GetString(root, "description"),
            Category: GetString(root, "category"),
            Icon: GetString(root, "icon"),
            Triggers: GetJsonObject(root, "triggers"),
            SystemPrompt: GetString(root, "systemPrompt"),
            ExtractionPrompt: GetString(root, "extractionPrompt"),
            FollowupPrompt: GetString(root, "followupPrompt"),
            EnabledTools: GetStringArray(root, "enabledTools"),
            ModelConfig: GetJsonObject(root, "modelConfig"),
            BehaviorConfig: GetJsonObject(root, "behaviorConfig"),
            Priority: root.TryGetProperty("priority", out var pEl) && pEl.TryGetInt32(out var p) ? p : null,
            IsActive: root.TryGetProperty("isActive", out var aEl) && aEl.ValueKind != JsonValueKind.Undefined ? aEl.GetBoolean() : null
        );

        var result = await service.UpsertAsync(effectiveCompany, input, req.HttpContext.RequestAborted);
        return Results.Json(result);
    }

    public static async Task<IResult> DeleteSkillAsync(HttpRequest req, Guid id, AgentSkillService service)
    {
        var ok = await service.DeleteAsync(id, req.HttpContext.RequestAborted);
        return ok ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "not found" });
    }

    // ====================== Rules ======================

    public static async Task<IResult> ListRulesAsync(HttpRequest req, Guid skillId, AgentSkillService service)
    {
        var rules = await service.ListRulesAsync(skillId, req.HttpContext.RequestAborted);
        return Results.Json(rules);
    }

    public static async Task<IResult> UpsertRuleAsync(HttpRequest req, Guid skillId, AgentSkillService service)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
        var root = doc.RootElement;

        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required" });

        Guid? id = null;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedId))
            id = parsedId;

        var input = new AgentSkillService.RuleInput(
            Id: id,
            RuleKey: GetString(root, "ruleKey"),
            Name: name!,
            Conditions: GetJsonObject(root, "conditions") ?? new JsonObject(),
            Actions: GetJsonObject(root, "actions") ?? new JsonObject(),
            Priority: root.TryGetProperty("priority", out var pEl) && pEl.TryGetInt32(out var p) ? p : null,
            IsActive: root.TryGetProperty("isActive", out var aEl) && aEl.ValueKind != JsonValueKind.Undefined ? aEl.GetBoolean() : null
        );

        var result = await service.UpsertRuleAsync(skillId, input, req.HttpContext.RequestAborted);
        return Results.Json(result);
    }

    public static async Task<IResult> DeleteRuleAsync(HttpRequest req, Guid ruleId, AgentSkillService service)
    {
        var ok = await service.DeleteRuleAsync(ruleId, req.HttpContext.RequestAborted);
        return ok ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "not found" });
    }

    // ====================== Examples ======================

    public static async Task<IResult> ListExamplesAsync(HttpRequest req, Guid skillId, AgentSkillService service)
    {
        var examples = await service.ListExamplesAsync(skillId, req.HttpContext.RequestAborted);
        return Results.Json(examples);
    }

    public static async Task<IResult> UpsertExampleAsync(HttpRequest req, Guid skillId, AgentSkillService service)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
        var root = doc.RootElement;

        Guid? id = null;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedId))
            id = parsedId;

        var input = new AgentSkillService.ExampleInput(
            Id: id,
            Name: GetString(root, "name"),
            InputType: GetString(root, "inputType"),
            InputData: GetJsonObject(root, "inputData") ?? new JsonObject(),
            ExpectedOutput: GetJsonObject(root, "expectedOutput") ?? new JsonObject(),
            IsActive: root.TryGetProperty("isActive", out var aEl) && aEl.ValueKind != JsonValueKind.Undefined ? aEl.GetBoolean() : null
        );

        var result = await service.UpsertExampleAsync(skillId, input, req.HttpContext.RequestAborted);
        return Results.Json(result);
    }

    public static async Task<IResult> DeleteExampleAsync(HttpRequest req, Guid exampleId, AgentSkillService service)
    {
        var ok = await service.DeleteExampleAsync(exampleId, req.HttpContext.RequestAborted);
        return ok ? Results.Ok(new { deleted = true }) : Results.NotFound(new { error = "not found" });
    }

    // ====================== Helpers ======================

    private static string? GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string[]? GetStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToArray();
    }

    private static JsonObject? GetJsonObject(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return JsonNode.Parse(v.GetRawText()) as JsonObject;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// Runs workflow-based voucher automation rules that transform AI document payloads into
/// fully populated accounting vouchers by delegating persistence to <see cref="FinanceService"/>.
/// </summary>
public sealed class VoucherAutomationService
{
    private readonly WorkflowRulesService _rulesService;
    private readonly FinanceService _financeService;

    public VoucherAutomationService(WorkflowRulesService rulesService, FinanceService financeService)
    {
        _rulesService = rulesService;
        _financeService = financeService;
    }

    /// <summary>
    /// Executes the automation rule referenced by <c>ruleKey</c> inside <paramref name="workflowPayload"/>
    /// and, if the rule contains a <c>voucher.autoCreate</c> action, produces a voucher based on the
    /// AI-extracted document.
    /// </summary>
    /// <param name="companyCode">Tenant identifier taken from request headers.</param>
    /// <param name="workflowPayload">Workflow payload that includes rule key, document, totals, etc.</param>
    /// <param name="userCtx">Authenticated user used for authorization/auditing.</param>
    /// <param name="ct">Cancellation token propagated from the HTTP layer.</param>
    public async Task<VoucherAutomationResult> CreateVoucherFromDocumentAsync(string companyCode, JsonObject workflowPayload, Auth.UserCtx userCtx, CancellationToken ct)
    {
        if (!workflowPayload.TryGetPropertyValue("ruleKey", out var ruleKeyNode) || ruleKeyNode is not JsonValue ruleKeyValue || !ruleKeyValue.TryGetValue<string>(out var ruleKey) || string.IsNullOrWhiteSpace(ruleKey))
        {
            return VoucherAutomationResult.Failed("未识别到可执行的自动化规则", Array.Empty<string>());
        }

        var rule = await _rulesService.GetAsync(companyCode, ruleKey, ct);
        if (rule is null)
        {
            return VoucherAutomationResult.Failed($"规则 {ruleKey} 未配置或已禁用", Array.Empty<string>());
        }

        var context = WorkflowContext.FromPayload(workflowPayload);

        foreach (var action in rule.Actions)
        {
            if (string.Equals(action.Type, "voucher.autoCreate", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteVoucherAutoCreateAsync(rule, action.Params, context, companyCode, userCtx, ct);
            }
        }

        return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 未包含可以执行的动作", Array.Empty<string>());
    }

    /// <summary>
    /// Evaluates the <c>voucher.autoCreate</c> action by rendering header/line templates, resolving
    /// accounts, and calling <see cref="FinanceService.CreateVoucher"/> to persist the voucher.
    /// </summary>
    private async Task<VoucherAutomationResult> ExecuteVoucherAutoCreateAsync(WorkflowRule rule, JsonObject actionParams, WorkflowContext context, string companyCode, Auth.UserCtx userCtx, CancellationToken ct)
    {
        var headerConfig = actionParams.TryGetPropertyValue("header", out var headerNode) ? headerNode as JsonObject : null;
        var linesConfig = actionParams.TryGetPropertyValue("lines", out var linesNode) ? linesNode as JsonArray : null;
        if (linesConfig is null || linesConfig.Count == 0)
        {
            return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 的动作缺少分录配置", Array.Empty<string>());
        }

        var header = new JsonObject
        {
            ["companyCode"] = companyCode,
            ["voucherType"] = "GL"
        };

        if (headerConfig is not null)
        {
            foreach (var property in headerConfig)
            {
                var value = EvaluateNode(property.Value, context);
                if (value is null) continue;
                header[property.Key] = value;
            }
        }

        if (!header.TryGetPropertyValue("postingDate", out var _))
        {
            header["postingDate"] = JsonValue.Create(DateTime.UtcNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        if (!header.TryGetPropertyValue("currency", out var _))
        {
            if (context.Header.TryGetPropertyValue("currency", out var currencyNode) && currencyNode is JsonValue currencyVal && currencyVal.TryGetValue<string>(out var currencyStr) && !string.IsNullOrWhiteSpace(currencyStr))
            {
                header["currency"] = JsonValue.Create(currencyStr);
            }
            else
            {
                header["currency"] = JsonValue.Create("JPY");
            }
        }

        var lines = new JsonArray();
        foreach (var item in linesConfig)
        {
            if (item is not JsonObject lineConfig) continue;
            var sideText = EvaluateString(lineConfig.TryGetPropertyValue("side", out var sideNode) ? sideNode : null, context);
            bool? isDebitFlag = null;
            if (lineConfig.TryGetPropertyValue("isDebit", out var isDebitNode) && isDebitNode is JsonValue isDebitValue)
            {
                if (isDebitValue.TryGetValue<bool>(out var boolVal)) isDebitFlag = boolVal;
                else if (isDebitValue.TryGetValue<string>(out var boolText))
                {
                    if (bool.TryParse(boolText, out var parsedBool)) isDebitFlag = parsedBool;
                }
            }

            var drcr = "DR";
            if (isDebitFlag.HasValue)
            {
                drcr = isDebitFlag.Value ? "DR" : "CR";
            }
            else if (!string.IsNullOrWhiteSpace(sideText))
            {
                drcr = sideText.Equals("credit", StringComparison.OrdinalIgnoreCase) || sideText.Equals("cr", StringComparison.OrdinalIgnoreCase)
                    ? "CR"
                    : "DR";
            }

            var amountValue = EvaluateDecimal(lineConfig.TryGetPropertyValue("amount", out var amountNode) ? amountNode : null, context);
            if (amountValue <= 0m)
            {
                return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 的分录金额无效", Array.Empty<string>());
            }

            if (!lineConfig.TryGetPropertyValue("account", out var accountNode) || accountNode is null)
            {
                return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 的分录缺少 account 配置", Array.Empty<string>());
            }

            var accountCode = await ResolveAccountCodeAsync(accountNode, context, companyCode, ct);
            if (string.IsNullOrWhiteSpace(accountCode))
            {
                return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 未能识别到有效的会计科目", Array.Empty<string>());
            }

            var line = new JsonObject
            {
                ["accountCode"] = accountCode,
                ["drcr"] = drcr,
                ["amount"] = JsonValue.Create(Math.Abs(amountValue))
            };

            if (lineConfig.TryGetPropertyValue("note", out var noteNode))
            {
                var note = EvaluateString(noteNode, context);
                if (!string.IsNullOrWhiteSpace(note)) line["note"] = note;
            }

            lines.Add(line);
        }

        if (lines.Count < 2)
        {
            return VoucherAutomationResult.Failed($"规则 {rule.RuleKey} 生成的分录不足两行", Array.Empty<string>());
        }

        var payload = new JsonObject
        {
            ["header"] = header,
            ["lines"] = lines
        };

        if (context.Totals is not null)
        {
            payload["aiTotals"] = context.Totals.DeepClone();
        }
        if (context.OriginalPayload.TryGetPropertyValue("confidence", out var confNode) && confNode is JsonValue confVal && confVal.TryGetValue<double>(out var conf))
        {
            payload["aiConfidence"] = conf;
        }
        if (context.Document is not null)
        {
            payload["aiDocument"] = context.Document.DeepClone();
        }

        using var doc = JsonDocument.Parse(payload.ToJsonString());
        // VoucherAutomation 是后台自动场景，传入 targetUserId 以便创建警报任务
        var (insertedJson, _) = await _financeService.CreateVoucher(
            companyCode, "vouchers", doc.RootElement, userCtx, 
            VoucherSource.Auto, targetUserId: userCtx.UserId);
        using var insertedDoc = JsonDocument.Parse(insertedJson);
        var root = insertedDoc.RootElement;
        var voucherPayload = root.TryGetProperty("payload", out var payloadElement) ? payloadElement : root;
        string? voucherNo = null;
        Guid? voucherId = null;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var parsedId))
        {
            voucherId = parsedId;
        }
        if (voucherPayload.TryGetProperty("header", out var headerEl) && headerEl.ValueKind == JsonValueKind.Object && headerEl.TryGetProperty("voucherNo", out var noEl))
        {
            voucherNo = noEl.GetString();
        }

        var messageTemplate = actionParams.TryGetPropertyValue("successMessage", out var successNode) ? EvaluateString(successNode, context) : null;
        var message = !string.IsNullOrWhiteSpace(messageTemplate)
            ? messageTemplate!
            : (voucherNo is { Length: > 0 } ? $"已根据上传文档自动生成会计凭证 {voucherNo}" : "已根据上传文档自动生成会计凭证");

        var confidenceValue = context.OriginalPayload.TryGetPropertyValue("confidence", out var confidenceNode) && confidenceNode is JsonValue confidenceVal && confidenceVal.TryGetValue<double>(out var confidenceNumber)
            ? confidenceNumber
            : (double?)null;

        return VoucherAutomationResult.Succeed(message, voucherNo, voucherId, voucherPayload, confidenceValue);
    }

    /// <summary>
    /// Resolves an account code from the JSON definition (code/name template) by evaluating expressions
    /// against the workflow context and falling back to fuzzy name matching when necessary.
    /// </summary>
    private async Task<string?> ResolveAccountCodeAsync(JsonNode accountNode, WorkflowContext context, string companyCode, CancellationToken ct)
    {
        string? codeCandidate = null;
        string? nameCandidate = null;

        if (accountNode is JsonObject obj)
        {
            codeCandidate = EvaluateString(obj.TryGetPropertyValue("code", out var codeNode) ? codeNode : null, context)
                            ?? EvaluateString(obj.TryGetPropertyValue("accountCode", out var acNode) ? acNode : null, context);
            if (string.IsNullOrWhiteSpace(codeCandidate))
            {
                nameCandidate = EvaluateString(obj.TryGetPropertyValue("name", out var nameNode) ? nameNode : null, context)
                                ?? EvaluateString(obj.TryGetPropertyValue("alias", out var aliasNode) ? aliasNode : null, context);
            }
        }
        else if (accountNode is JsonValue value && value.TryGetValue<string>(out var rawValue))
        {
            var evaluated = EvaluateString(rawValue, context);
            if (!string.IsNullOrWhiteSpace(evaluated))
            {
                codeCandidate = evaluated;
            }
        }

        if (!string.IsNullOrWhiteSpace(codeCandidate))
        {
            var resolved = await TryResolveAccountCodeAsync(codeCandidate!, companyCode, ct);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
            if (string.IsNullOrWhiteSpace(nameCandidate))
                nameCandidate = codeCandidate;
        }

        if (!string.IsNullOrWhiteSpace(nameCandidate))
        {
            var byName = await ResolveAccountCodeByNameAsync(nameCandidate!, companyCode, ct);
            if (!string.IsNullOrWhiteSpace(byName))
                return byName;
        }

        return null;
    }

    /// <summary>
    /// Returns the exact account code if <paramref name="candidate"/> already matches an existing code.
    /// </summary>
    private async Task<string?> TryResolveAccountCodeAsync(string candidate, string companyCode, CancellationToken ct)
    {
        var accounts = await _financeService.GetAccountsAsync(companyCode, ct);
        var match = accounts.FirstOrDefault(a => string.Equals(a.Code, candidate, StringComparison.OrdinalIgnoreCase));
        return match?.Code;
    }

    /// <summary>
    /// Uses fuzzy similarity and alias matching to find the best account whose name resembles the input.
    /// </summary>
    private async Task<string?> ResolveAccountCodeByNameAsync(string name, string companyCode, CancellationToken ct)
    {
        var accounts = await _financeService.GetAccountsAsync(companyCode, ct);
        var match = FindBestAccount(accounts, name);
        return match?.Code;
    }

    private static FinanceService.FinanceAccountInfo? FindBestAccount(IReadOnlyList<FinanceService.FinanceAccountInfo> accounts, string query)
    {
        if (accounts.Count == 0 || string.IsNullOrWhiteSpace(query)) return null;
        var trimmed = query.Trim();
        var lowered = trimmed.ToLowerInvariant();

        FinanceService.FinanceAccountInfo? best = null;
        double bestScore = 0d;

        foreach (var account in accounts)
        {
            if (string.Equals(account.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                return account;

            var score = Similarity(account.Name, trimmed);
            if (score > bestScore)
            {
                bestScore = score;
                best = account;
            }

            if (account.Aliases is not null)
            {
                foreach (var alias in account.Aliases)
                {
                    var aliasScore = Similarity(alias, trimmed);
                    if (aliasScore > bestScore)
                    {
                        bestScore = aliasScore;
                        best = account;
                    }
                }
            }
        }

        if (bestScore < 0.6)
        {
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(digits))
            {
                var digitMatch = accounts.FirstOrDefault(a => a.Code.EndsWith(digits, StringComparison.OrdinalIgnoreCase));
                if (digitMatch is not null) return digitMatch;
            }
        }

        return bestScore >= 0.5 ? best : null;
    }

    /// <summary>
    /// 计算两个字符串的相似度（0.0 ~ 1.0）。
    /// 使用 Levenshtein 距离的归一化版本，并对包含关系给予额外加分。
    /// </summary>
    private static double Similarity(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return 0d;
        
        var s = source.ToLowerInvariant().Trim();
        var t = target.ToLowerInvariant().Trim();
        
        // 完全匹配
        if (s == t) return 1d;
        
        // 包含关系给予高分
        if (s.Contains(t) || t.Contains(s)) return 0.85d;
        
        // 使用 Levenshtein 距离计算相似度
        var distance = LevenshteinDistance(s, t);
        var maxLength = Math.Max(s.Length, t.Length);
        if (maxLength == 0) return 1d;
        
        return 1d - ((double)distance / maxLength);
    }

    /// <summary>
    /// 计算两个字符串的 Levenshtein 编辑距离。
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        
        if (n == 0) return m;
        if (m == 0) return n;
        
        var d = new int[n + 1, m + 1];
        
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        
        return d[n, m];
    }

    private static JsonNode? EvaluateNode(JsonNode? node, WorkflowContext context)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var str))
            {
                var result = EvaluateString(str, context);
                return result is null ? null : JsonValue.Create(result);
            }
            return value.DeepClone();
        }
        if (node is JsonObject obj)
        {
            var clone = new JsonObject();
            foreach (var property in obj)
            {
                var child = EvaluateNode(property.Value, context);
                if (child is not null)
                {
                    clone[property.Key] = child;
                }
            }
            return clone;
        }
        if (node is JsonArray array)
        {
            var clone = new JsonArray();
            foreach (var item in array)
            {
                var child = EvaluateNode(item, context);
                if (child is not null)
                {
                    clone.Add(child);
                }
            }
            return clone;
        }
        return null;
    }

    private static string? EvaluateString(JsonNode? node, WorkflowContext context)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return EvaluateString(str, context);
        }
        return node.ToJsonString();
    }

    private static string? EvaluateString(string? template, WorkflowContext context)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return TemplateRegex.Replace(template, match =>
        {
            var expr = match.Groups[1].Value.Trim();
            var resolved = EvaluateExpression(expr, context);
            return resolved ?? string.Empty;
        });
    }

    private static decimal EvaluateDecimal(JsonNode? node, WorkflowContext context)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var dec)) return dec;
            if (value.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
            if (value.TryGetValue<string>(out var str))
            {
                var evaluated = EvaluateString(str, context);
                if (decimal.TryParse(evaluated, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            }
        }
        if (node is JsonObject obj && obj.TryGetPropertyValue("expression", out var exprNode) && exprNode is JsonValue exprVal && exprVal.TryGetValue<string>(out var exprStr))
        {
            var evaluated = EvaluateString(exprStr, context);
            if (decimal.TryParse(evaluated, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return 0m;
    }

    private static string? EvaluateExpression(string expression, WorkflowContext context)
    {
        foreach (var part in expression.Split(new[] { "??" }, StringSplitOptions.None))
        {
            var term = part.Trim();
            var value = EvaluateTerm(term, context);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? EvaluateTerm(string term, WorkflowContext context)
    {
        if (string.Equals(term, "today", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (term.Length >= 2 && term.StartsWith("'") && term.EndsWith("'"))
        {
            return term.Substring(1, term.Length - 2);
        }

        var parts = term.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        if (!context.TryGetValue(parts[0], out var current) || current is null)
        {
            if (context.Document.TryGetPropertyValue(parts[0], out var docVal) && docVal is not null)
            {
                current = docVal;
            }
            else if (context.Header.TryGetPropertyValue(parts[0], out var headerVal) && headerVal is not null)
            {
                current = headerVal;
            }
            else if (context.Totals.TryGetPropertyValue(parts[0], out var totalVal) && totalVal is not null)
            {
                current = totalVal;
            }
            else if (context.Metadata.TryGetPropertyValue(parts[0], out var metaVal) && metaVal is not null)
            {
                current = metaVal;
            }
            else
            {
                return null;
            }
        }

        for (var i = 1; i < parts.Length; i++)
        {
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(parts[i], out current) || current is null)
                    return null;
            }
            else if (current is JsonValue value)
            {
                if (value.TryGetValue<string>(out var str)) return str;
                if (value.TryGetValue<double>(out var dbl)) return dbl.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue<decimal>(out var dec)) return dec.ToString(CultureInfo.InvariantCulture);
                return value.ToJsonString();
            }
            else
            {
                return null;
            }
        }

        if (current is JsonValue finalValue)
        {
            if (finalValue.TryGetValue<string>(out var str)) return str;
            if (finalValue.TryGetValue<double>(out var dbl)) return dbl.ToString(CultureInfo.InvariantCulture);
            if (finalValue.TryGetValue<decimal>(out var dec)) return dec.ToString(CultureInfo.InvariantCulture);
            return finalValue.ToJsonString();
        }

        if (current is JsonObject or JsonArray)
        {
            return current.ToJsonString();
        }

        return null;
    }

    private static readonly Regex TemplateRegex = new("\\{([^{}]+)\\}", RegexOptions.Compiled);
}

public sealed record VoucherAutomationResult
{
    private VoucherAutomationResult(bool success, string message, IReadOnlyList<string> issues, string? voucherNo, Guid? voucherId, JsonElement? payload, double? confidence)
    {
        Success = success;
        Message = message;
        Issues = issues;
        VoucherNo = voucherNo;
        VoucherId = voucherId;
        Payload = payload;
        Confidence = confidence;
    }

    public bool Success { get; }
    public string Message { get; }
    public IReadOnlyList<string> Issues { get; }
    public string? VoucherNo { get; }
    public Guid? VoucherId { get; }
    public JsonElement? Payload { get; }
    public double? Confidence { get; }

    public static VoucherAutomationResult Failed(string message, IReadOnlyList<string> issues)
        => new(false, message, issues, null, null, null, null);

    public static VoucherAutomationResult Succeed(string message, string? voucherNo, Guid? voucherId, JsonElement payload, double? confidence)
        => new(true, message, Array.Empty<string>(), voucherNo, voucherId, payload, confidence);
}

internal sealed class WorkflowContext
{
    private readonly Dictionary<string, JsonNode?> _values;
    public JsonObject Document { get; }
    public JsonObject Header { get; }
    public JsonObject Totals { get; }
    public JsonArray Lines { get; }
    public JsonObject Metadata { get; }
    public JsonObject OriginalPayload { get; }

    private WorkflowContext(Dictionary<string, JsonNode?> values, JsonObject document, JsonObject header, JsonObject totals, JsonArray lines, JsonObject metadata, JsonObject original)
    {
        _values = values;
        Document = document;
        Header = header;
        Totals = totals;
        Lines = lines;
        Metadata = metadata;
        OriginalPayload = original;
    }

    public static WorkflowContext FromPayload(JsonObject payload)
    {
        var document = payload.TryGetPropertyValue("document", out var docNode) && docNode is JsonObject doc ? doc : new JsonObject();
        var header = payload.TryGetPropertyValue("header", out var headerNode) && headerNode is JsonObject head ? head : new JsonObject();
        var totals = payload.TryGetPropertyValue("totals", out var totalsNode) && totalsNode is JsonObject tot ? tot : new JsonObject();
        var lines = payload.TryGetPropertyValue("lines", out var linesNode) && linesNode is JsonArray arr ? arr : new JsonArray();
        var metadata = payload.TryGetPropertyValue("metadata", out var metadataNode) && metadataNode is JsonObject meta ? meta : new JsonObject();

        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            ["document"] = document,
            ["header"] = header,
            ["totals"] = totals,
            ["lines"] = lines,
            ["metadata"] = metadata,
            ["payload"] = payload
        };

        if (payload.TryGetPropertyValue("assistant", out var assistantNode))
        {
            values["assistant"] = assistantNode;
        }
        if (payload.TryGetPropertyValue("analysis", out var analysisNode))
        {
            values["analysis"] = analysisNode;
        }

        return new WorkflowContext(values, document, header, totals, lines, metadata, payload);
    }

    public bool TryGetValue(string key, out JsonNode? value)
    {
        return _values.TryGetValue(key, out value);
    }
}



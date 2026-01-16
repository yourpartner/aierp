using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Server.Infrastructure;
using Server.Modules.AgentKit;
using Server.Modules.AgentKit.Tools;

namespace Server.Modules;

/// <summary>
/// Provides orchestration helpers for AgentKit, including scenario routing, task tracking,
/// document context building, and downstream service calls (finance, invoices, sales orders).
/// </summary>
public sealed class AgentKitService
{
    private readonly NpgsqlDataSource _ds;
    private readonly FinanceService _finance;
    private readonly InvoiceRegistryService _invoiceRegistry;
    private readonly InvoiceTaskService _invoiceTaskService;
    private readonly SalesOrderTaskService _salesOrderTaskService;
    private readonly AgentScenarioService _scenarioService;
    private readonly AgentAccountingRuleService _ruleService;
    private readonly MoneytreePostingRuleService _moneytreeRuleService;
    private readonly AzureBlobService _blobService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentKitService> _logger;
    private readonly AgentToolRegistry _toolRegistry;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HashSet<string> AllowedCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "JPY",
        "USD",
        "CNY"
    };

private static readonly Dictionary<string, string[]> ScenarioAccountLookupHints = new(StringComparer.OrdinalIgnoreCase)
{
    ["voucher.dining.receipt"] = new[] { "" },
    ["voucher.transportation.receipt"] = new[] { "" }
};

    private sealed record LookupAccountResult(bool Found, string Query, string? AccountCode, string? AccountName, IReadOnlyList<string> Aliases);

    private static string NormalizeLanguage(string? language)
    {
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) return "zh";
        return "ja";
    }

    private static string Localize(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;

    /// <summary>
    /// 棢�查异常消息是否表示必霢�字段缺失，用于返�?clarification 而非直接报错
    /// </summary>
    private static bool IsRequiredFieldMissing(string message, out (string FieldName, string AccountCode) fieldInfo)
    {
        fieldInfo = default;
        if (string.IsNullOrWhiteSpace(message)) return false;
        
        // 匹配格式: "lines[N].fieldName required by account accountCode"
        var match = System.Text.RegularExpressions.Regex.Match(
            message, 
            @"lines\[\d+\]\.(\w+)\s+required\s+by\s+account\s+(\w+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        if (match.Success)
        {
            fieldInfo = (match.Groups[1].Value, match.Groups[2].Value);
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// 清理 Claude 返回�?markdown 代码块格式，提取�?JSON
    /// </summary>
    private static string CleanMarkdownJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        
        var trimmed = content.Trim();
        
        // 处理 ```json ... ``` �?``` ... ``` 格式
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed.Substring(firstNewline + 1);
            }
            else
            {
                trimmed = trimmed.Substring(3);
            }
        }
        
        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }
        
        return trimmed.Trim();
    }

    private MoneytreePostingRuleService.MoneytreePostingRuleUpsert ParseMoneytreeRuleSpec(JsonElement args, string language)
    {
        var title = args.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
            throw new Exception("error");

        var description = args.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
            ? descEl.GetString()
            : null;

        int? priority = null;
        if (args.TryGetProperty("priority", out var priEl))
        {
            if (priEl.ValueKind == JsonValueKind.Number && priEl.TryGetInt32(out var priValue))
                priority = priValue;
            else if (priEl.ValueKind == JsonValueKind.String && int.TryParse(priEl.GetString(), out var priFromString))
                priority = priFromString;
        }

        bool? isActive = null;
        if (args.TryGetProperty("isActive", out var actEl))
        {
            if (actEl.ValueKind == JsonValueKind.True) isActive = true;
            else if (actEl.ValueKind == JsonValueKind.False) isActive = false;
            else if (actEl.ValueKind == JsonValueKind.String && bool.TryParse(actEl.GetString(), out var parsedActive))
                isActive = parsedActive;
        }

        JsonNode? matcherNode = null;
        if (args.TryGetProperty("matcher", out var matcherEl) && matcherEl.ValueKind != JsonValueKind.Null && matcherEl.ValueKind != JsonValueKind.Undefined)
        {
            try { matcherNode = JsonNode.Parse(matcherEl.GetRawText()); }
            throw new Exception("error");
        }

        JsonNode? actionNode = null;
        if (args.TryGetProperty("action", out var actionEl) && actionEl.ValueKind != JsonValueKind.Null && actionEl.ValueKind != JsonValueKind.Undefined)
        {
            try { actionNode = JsonNode.Parse(actionEl.GetRawText()); }
            throw new Exception("error");
        }

        if (matcherNode is null)
            throw new Exception("error");
        if (actionNode is null)
            throw new Exception("error");

        return new MoneytreePostingRuleService.MoneytreePostingRuleUpsert(
            title,
            description,
            priority,
            matcherNode,
            actionNode,
            isActive);
    }

    /// <summary>
    /// Creates a new service instance with all downstream dependencies required by the agent workflow.
    /// </summary>
    /// <param name="ds">Shared data source for persistence operations.</param>
    /// <param name="finance">Finance service used for voucher and account lookups.</param>
    /// <param name="invoiceRegistry">Registry helper for invoice numbers.</param>
    /// <param name="invoiceTaskService">Service that manages invoice related AI tasks.</param>
    /// <param name="salesOrderTaskService">Service that manages sales order tasks.</param>
    /// <param name="scenarioService">Scenario catalog used to route prompts.</param>
    /// <param name="ruleService">Accounting rule service used to suggest tool hints.</param>
    /// <param name="moneytreeRuleService">Posting rule service for Moneytree automations.</param>
    /// <param name="httpClientFactory">Factory for outbound HTTP calls.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <param name="logger">Logger for operational tracing.</param>
    public AgentKitService(
        NpgsqlDataSource ds,
        FinanceService finance,
        InvoiceRegistryService invoiceRegistry,
        InvoiceTaskService invoiceTaskService,
        SalesOrderTaskService salesOrderTaskService,
        AgentScenarioService scenarioService,
        AgentAccountingRuleService ruleService,
        MoneytreePostingRuleService moneytreeRuleService,
        AzureBlobService blobService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AgentKitService> logger,
        ILoggerFactory loggerFactory)
    {
        _ds = ds;
        _finance = finance;
        _invoiceRegistry = invoiceRegistry;
        _invoiceTaskService = invoiceTaskService;
        _salesOrderTaskService = salesOrderTaskService;
        _scenarioService = scenarioService;
        _ruleService = ruleService;
        _moneytreeRuleService = moneytreeRuleService;
        _blobService = blobService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _toolRegistry = new AgentToolRegistry(loggerFactory.CreateLogger<AgentToolRegistry>());
        RegisterTools(loggerFactory);
    }

    private void RegisterTools(ILoggerFactory loggerFactory)
    {
        _toolRegistry.Register(new LookupAccountTool(_ds, loggerFactory.CreateLogger<LookupAccountTool>()));
        _toolRegistry.Register(new LookupCustomerTool(_ds, loggerFactory.CreateLogger<LookupCustomerTool>()));
        _toolRegistry.Register(new LookupMaterialTool(_ds, loggerFactory.CreateLogger<LookupMaterialTool>()));
        _toolRegistry.Register(new LookupVendorTool(_ds, loggerFactory.CreateLogger<LookupVendorTool>()));
        _toolRegistry.Register(new GetVoucherByNumberTool(_ds, loggerFactory.CreateLogger<GetVoucherByNumberTool>()));
        _toolRegistry.Register(new SearchVendorReceiptsTool(_ds, loggerFactory.CreateLogger<SearchVendorReceiptsTool>()));
        _toolRegistry.Register(new GetExpenseAccountOptionsTool(_ds, _ruleService, loggerFactory.CreateLogger<GetExpenseAccountOptionsTool>()));
        _toolRegistry.Register(new RegisterMoneytreeRuleTool(_moneytreeRuleService, loggerFactory.CreateLogger<RegisterMoneytreeRuleTool>()));
        _toolRegistry.Register(new BulkRegisterMoneytreeRuleTool(_moneytreeRuleService, loggerFactory.CreateLogger<BulkRegisterMoneytreeRuleTool>()));
        _toolRegistry.Register(new CalculateTaxTool(loggerFactory.CreateLogger<CalculateTaxTool>()));
        _toolRegistry.Register(new ConvertCurrencyTool(loggerFactory.CreateLogger<ConvertCurrencyTool>()));
    }

    /// <summary>
    /// Processes a conversational request, resolving context (tasks, uploads, scenarios),
    /// constructing system prompts, and delegating execution to task-specific handlers.
    /// </summary>
    /// <param name="request">User request payload containing message, session, and API keys.</param>
    /// <param name="ct">Cancellation token used to abort long running operations.</param>
    /// <returns>Aggregated agent run result including tool calls and responses.</returns>
    internal async Task<AgentRunResult> ProcessUserMessageAsync(AgentMessageRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("error");

        if (request.TaskId.HasValue)
        {
            return await ProcessTaskMessageAsync(request, ct);
        }
        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var latestUpload = await GetLatestUploadAsync(sessionId, ct);
        var storedDocumentSessionId = await GetSessionActiveDocumentAsync(sessionId, ct);
        var taskSnapshot = await LoadTaskSnapshotAsync(sessionId, ct);
        Guid? inferredTaskId = null;
        string? inferReason = null;
        if (!request.TaskId.HasValue)
        {
            (inferredTaskId, inferReason) = InferTaskIdFromMessageWithReason(request.Message, taskSnapshot, storedDocumentSessionId);
            _logger.LogInformation("[AgentKit] 任务推断: {Reason}, TaskId={TaskId}", inferReason, inferredTaskId?.ToString() ?? "(null)");
        }
        else
        {
            _logger.LogInformation("[AgentKit] 前端明确指定任务 TaskId={TaskId}", request.TaskId.Value);
        }
        var attachedTaskId = request.TaskId ?? inferredTaskId;
        var attachedTaskEntry = attachedTaskId.HasValue
            ? taskSnapshot.FirstOrDefault(entry => entry.TaskId == attachedTaskId.Value)
            : null;
        var historySince = latestUpload?.CreatedAt;
        var history = await LoadHistoryAsync(sessionId, historySince, 20, attachedTaskId, ct);
        var allScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();
        var selectedScenarios = SelectScenariosForMessage(allScenarios, request.ScenarioKey, request.Message);

        var documentLabelEntries = latestUpload is not null
            ? BuildDocumentLabelEntries(latestUpload.Documents, d => d.DocumentSessionId, d => d.FileName ?? d.FileId ?? d.DocumentSessionId, d => d.Analysis, d => d.FileId, language: language)
            : new List<DocumentLabelEntry>();
        var documentLabelMap = documentLabelEntries.ToDictionary(e => e.SessionId, e => e, StringComparer.OrdinalIgnoreCase);

        var tokens = new Dictionary<string, string?> { ["input"] = request.Message };
        if (attachedTaskEntry is not null)
        {
            tokens["candidateTaskId"] = attachedTaskEntry.TaskId.ToString();
            var display = attachedTaskEntry.DisplayLabel;
            if (string.IsNullOrWhiteSpace(display))
            {
                display = attachedTaskEntry.SequenceLabel;
            }
            if (!string.IsNullOrWhiteSpace(display))
            {
                tokens["candidateTaskLabel"] = display;
            }
            if (!string.IsNullOrWhiteSpace(attachedTaskEntry.Status))
            {
                tokens["candidateTaskStatus"] = attachedTaskEntry.Status;
            }
        }
        if (documentLabelEntries.Count > 0)
        {
            tokens["documentGroups"] = string.Empty;
            {
                var summary = new StringBuilder();
                summary.Append(entry.Label).Append('�?).Append(entry.DisplayName);
                summary.Append("（fileId=").Append(entry.PrimaryFileId);
                summary.Append("；docSessionId=").Append(entry.SessionId);
                if (!string.IsNullOrWhiteSpace(entry.Highlight))
                {
                    summary.Append("�?).Append("");
                }
                summary.Append('�?);
                return summary.ToString();
            }));
            foreach (var entry in documentLabelEntries)
            {
                tokens[$"group.{entry.Label}.fileId"] = entry.PrimaryFileId;
                tokens[$"group.{entry.Label}.documentSessionId"] = entry.SessionId;
                tokens[$"group.{entry.Label}.name"] = entry.DisplayName;
            }
        }

        ClarificationInfo? clarification = null;
        _logger.LogInformation("[AgentKit] request.AnswerTo = {AnswerTo}", request.AnswerTo ?? "(null)");
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            clarification = await LoadClarificationAsync(sessionId, request.AnswerTo, ct);
            _logger.LogInformation("[AgentKit] clarification loaded = {Loaded}", clarification is not null);
            if (clarification is not null)
            {
                if (string.IsNullOrWhiteSpace(clarification.DocumentLabel) &&
                    !string.IsNullOrWhiteSpace(clarification.DocumentSessionId) &&
                    documentLabelMap.TryGetValue(clarification.DocumentSessionId, out var clarifyEntry))
                {
                    clarification = clarification with { DocumentLabel = clarifyEntry.Label };
                }
                tokens["answerQuestionId"] = clarification.QuestionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId)) tokens["answerDocumentSessionId"] = clarification.DocumentSessionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentId)) tokens["answerDocumentId"] = clarification.DocumentId;
                if (!string.IsNullOrWhiteSpace(clarification.Question)) tokens["answerQuestion"] = clarification.Question;
                if (!string.IsNullOrWhiteSpace(clarification.Detail)) tokens["answerDetail"] = clarification.Detail;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel)) tokens["answerDocumentLabel"] = clarification.DocumentLabel;
                // 用户回答问题时，使用保存的场�?key（场景确认允许用户在回答里覆�?scenario_key�?               _logger.LogInformation("[AgentKit] clarification.ScenarioKey = {ScenarioKey}", clarification.ScenarioKey ?? "(null)");
                if (!string.IsNullOrWhiteSpace(clarification.ScenarioKey))
                {
                    var effectiveScenarioKey = clarification.ScenarioKey!;
                    if (!string.IsNullOrWhiteSpace(clarification.Detail) &&
                        clarification.Detail.StartsWith("scenario_select:", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(request.Message))
                    {
                        var userText = request.Message.Trim();
                        // 如果用户直接输入/包含某个 scenario_key，则优先采用
                        var overrideKey = allScenarios.FirstOrDefault(s =>
                            userText.Contains(s.ScenarioKey, StringComparison.OrdinalIgnoreCase))?.ScenarioKey;
                        if (!string.IsNullOrWhiteSpace(overrideKey))
                        {
                            effectiveScenarioKey = overrideKey!;
                            _logger.LogInformation("[AgentKit] 用户覆盖场景选择�?{ScenarioKey}", effectiveScenarioKey);
                        }
                    }

                    var savedScenario = allScenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, effectiveScenarioKey, StringComparison.OrdinalIgnoreCase));
                    if (savedScenario is not null)
                    {
                        _logger.LogInformation("[AgentKit] 用户回答问题，使用保存的场景: {ScenarioKey}", effectiveScenarioKey);
                        selectedScenarios = new[] { savedScenario };
                    }
                    else
                    {
                        _logger.LogWarning("[AgentKit] 未找到场景{ScenarioKey}", effectiveScenarioKey);
                    }
                }
                else
                {
                    _logger.LogInformation("[AgentKit] clarification 没有 ScenarioKey，使用默认场景��择");
                }
            }
            else
            {
                _logger.LogWarning("[AgentKit] 未找到待回答的问题questionId={QuestionId}", request.AnswerTo);
            }
        }
        if (string.IsNullOrWhiteSpace(storedDocumentSessionId) &&
            attachedTaskEntry is not null &&
            string.Equals(attachedTaskEntry.Kind, "invoice", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(attachedTaskEntry.DocumentSessionId))
        {
            storedDocumentSessionId = attachedTaskEntry.DocumentSessionId;
        }
        string? activeDocumentSessionId = null;
        if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
        {
            activeDocumentSessionId = clarification.DocumentSessionId;
        }
        else if (!string.IsNullOrWhiteSpace(storedDocumentSessionId))
        {
            activeDocumentSessionId = storedDocumentSessionId;
        }
        else if (latestUpload?.ActiveDocumentSessionId is not null)
        {
            activeDocumentSessionId = latestUpload.ActiveDocumentSessionId;
        }

        UploadContextDoc? primaryDoc = null;
        if (latestUpload is not null)
        {
            if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
            {
                primaryDoc = latestUpload.Documents.FirstOrDefault(doc =>
                    string.Equals(doc.DocumentSessionId, activeDocumentSessionId, StringComparison.OrdinalIgnoreCase));
            }
            primaryDoc ??= latestUpload.Documents.FirstOrDefault(doc => !string.IsNullOrWhiteSpace(doc.FileId))
                ?? latestUpload.Documents.FirstOrDefault();
        }
        activeDocumentSessionId ??= primaryDoc?.DocumentSessionId;
        if (!string.IsNullOrWhiteSpace(activeDocumentSessionId) && documentLabelMap.TryGetValue(activeDocumentSessionId, out var activeEntry))
        {
            tokens["activeDocumentLabel"] = activeEntry.Label;
            tokens["activeDocumentName"] = activeEntry.DisplayName;
        }
        if (primaryDoc is not null)
        {
            if (!string.IsNullOrWhiteSpace(primaryDoc.FileId)) tokens["lastFileId"] = primaryDoc.FileId;
            if (!string.IsNullOrWhiteSpace(primaryDoc.FileName)) tokens["lastFileName"] = primaryDoc.FileName;
            if (primaryDoc.Analysis is not null) tokens["analysis"] = primaryDoc.Analysis.ToJsonString();
        }
        if (latestUpload is not null)
        {
            tokens["lastUploadKind"] = latestUpload.Kind;
            if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
            {
                tokens["activeDocumentSessionId"] = activeDocumentSessionId;
            }
        }
        else if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            tokens["activeDocumentSessionId"] = activeDocumentSessionId;
        }

        var messages = BuildInitialMessages(request.CompanyCode, selectedScenarios, accountingRules, history, tokens, language);
        if (documentLabelEntries.Count > 0)
        {
            var groupSummary = new StringBuilder();
            groupSummary.AppendLine("");
            foreach (var entry in documentLabelEntries)
            {
                var highlightJa = string.IsNullOrWhiteSpace(entry.Highlight) ? string.Empty : $"；ハイライト：{entry.Highlight}";
                var highlightZh = string.IsNullOrWhiteSpace(entry.Highlight) ? string.Empty : $"；{entry.Highlight}";
                var line = Localize(language,
                    // sanitized
                    // sanitized
                groupSummary.AppendLine(line);
            }
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = groupSummary.ToString().TrimEnd()
            });
        }
        if (clarification is not null)
        {
            var clarifyNote = new StringBuilder();
            clarifyNote.Append("");
            if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
            {
                clarifyNote.Append('[').Append(clarification.DocumentLabel).Append(']');
            }
            clarifyNote.Append(clarification.Question);
            var docLabel = !string.IsNullOrWhiteSpace(clarification.DocumentName) ? clarification.DocumentName : clarification.DocumentId;
            if (!string.IsNullOrWhiteSpace(docLabel))
            {
                clarifyNote.Append(Localize(language, "（ファイル：", "（文件：")).Append(docLabel).Append('�?);
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                clarifyNote.Append(Localize(language, "。補足：", "。补充说明：")).Append(clarification.Detail);
            }
            if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
            {
                clarifyNote.Append(Localize(language, "。関連コンテキスト（documentSessionId）：", "。关联上下文（documentSessionId）：")).Append(clarification.DocumentSessionId);
            }
            // 添加明确的下丢�步指�?           clarifyNote.AppendLine();
            // 如果是缺失字段的回答，告�?AI 用户的回答应该用于哪个字�?           if (!string.IsNullOrWhiteSpace(clarification.MissingField))
            {
                clarifyNote.Append(Localize(language, 
                    // sanitized
                    // sanitized
            }
            else
            {
                clarifyNote.Append(Localize(language, 
                    // sanitized
                    // sanitized
            }
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = clarifyNote.ToString()
            });
        }
        if (latestUpload is not null && latestUpload.Documents.Count > 0)
        {
            var hint = new StringBuilder();
            hint.Append(Localize(language, "直近にアップロードした証憑を続けて処理できます��?, "朢�近一次上传的票据可继续处理��?));
            if (latestUpload.Documents.Count > 1)
            {
                hint.Append(Localize(language, $" 合計 {latestUpload.Documents.Count} 件のファイル�?, $"�?{latestUpload.Documents.Count} 个文件��?));
            }
            if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
            {
                if (documentLabelMap.TryGetValue(activeDocumentSessionId, out var activeEntryInfo))
                {
                    hint.Append(Localize(language,
                        // sanitized
                        // sanitized
                }
                else
                {
                    hint.Append(Localize(language,
                        // sanitized
                        // sanitized
                }
            }
            if (primaryDoc?.Analysis is JsonObject analysisObj)
            {
                var partner = analysisObj.TryGetPropertyValue("partnerName", out var partnerNode) ? partnerNode?.GetValue<string>() : null;
                double? totalAmount = null;
                if (analysisObj.TryGetPropertyValue("totalAmount", out var totalNode))
                {
                    if (totalNode is JsonValue totalValue && totalValue.TryGetValue<double>(out var parsedDouble))
                    {
                        totalAmount = parsedDouble;
                    }
                    else if (totalNode is JsonValue totalValueStr && totalValueStr.TryGetValue<string>(out var totalStr) && double.TryParse(totalStr, out var parsedFromString))
                    {
                        totalAmount = parsedFromString;
                    }
                }
                if (!string.IsNullOrWhiteSpace(primaryDoc.FileId)) hint.Append(Localize(language, $" デフォル�?fileId={primaryDoc.FileId}�?, $" 默认文件ID={primaryDoc.FileId}�?));
                if (!string.IsNullOrWhiteSpace(partner)) hint.Append(Localize(language, $" 取引先：{partner}�?, $" 供应方：{partner}�?));
                if (totalAmount.HasValue) hint.Append(Localize(language, $" 税込金額：{totalAmount.Value:0.##}�?, $" 含税金额：{totalAmount.Value:0.##}�?));
            }
            hint.Append(Localize(language, " ユーザーが��この一枚����さっきのもの��などと指す場合は��今回のゃ6�9��プロードを継続して処理してください��?, " 若用户提及��这张����上丢�张��等指代，请延续本次上传的票据继续执行��?));
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = hint.ToString().Trim()
            });
        }
        var taskDecisionHint = BuildTaskDecisionHint(language, taskSnapshot, attachedTaskId);
        if (!string.IsNullOrWhiteSpace(taskDecisionHint))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = taskDecisionHint
            });
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = request.Message
        });

        var context = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, selectedScenarios, request.FileResolver ?? (_ => null), attachedTaskId);
        // 若本次是对��缺失字段��问题的回答，则把答案强制注入到后续 create_voucher（不依赖模型是否记得填）
        if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.MissingField))
        {
            var answer = (request.Message ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(answer))
            {
                _logger.LogInformation("[AgentKit] answerTo reply received missingField={MissingField} answer='{Answer}'", clarification.MissingField, answer);
                context.SetPendingFieldAnswer(clarification.MissingField, answer);
            }
            else
            {
                _logger.LogWarning("[AgentKit] answerTo reply missingField={MissingField} but message is empty", clarification.MissingField);
            }
        }
        foreach (var entry in documentLabelEntries)
        {
            context.AssignDocumentLabel(entry.SessionId, entry.Label);
        }
        if (latestUpload is not null)
        {
            foreach (var doc in latestUpload.Documents)
            {
                if (!string.IsNullOrWhiteSpace(activeDocumentSessionId) &&
                    !string.Equals(doc.DocumentSessionId, activeDocumentSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(doc.FileId))
                {
                    context.RegisterDocument(doc.FileId!, doc.Analysis, doc.DocumentSessionId);
                }
            }
        }
        if (clarification is not null)
        {
            if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                context.RegisterDocument(clarification.DocumentId!, clarification.DocumentAnalysis, clarification.DocumentSessionId);
            }
            if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId) && !string.IsNullOrWhiteSpace(clarification.DocumentLabel))
            {
                context.AssignDocumentLabel(clarification.DocumentSessionId, clarification.DocumentLabel);
            }
            if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
            {
                context.SetActiveDocumentSession(clarification.DocumentSessionId);
            }
            else if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                context.SetDefaultFileId(clarification.DocumentId!);
            }
        }
        else if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            context.SetActiveDocumentSession(activeDocumentSessionId);
        }
        else if (!string.IsNullOrWhiteSpace(primaryDoc?.FileId))
        {
            context.SetDefaultFileId(primaryDoc.FileId);
        }

        await SetSessionActiveDocumentAsync(sessionId, activeDocumentSessionId, ct);

        JsonObject? userPayload = null;
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            userPayload = new JsonObject
            {
                ["answerTo"] = request.AnswerTo
            };
        }

        var userMessageId = await PersistMessageAsync(sessionId, "user", request.Message, userPayload, null, attachedTaskId, ct);

        // 若是"缺失字段 + draft voucher"的回答，直接重试 create_voucher（跳�?LLM�?       if (clarification is not null
            && !string.IsNullOrWhiteSpace(clarification.MissingField)
            && clarification.DraftVoucher is not null
            && !string.IsNullOrWhiteSpace(request.Message))
        {
            var exec = await RetryDraftVoucherWithAnswerAsync(context, clarification, request.Message.Trim(), ct);
            if (exec is not null)
            {
                if (exec.Messages.Count > 0)
                {
                    await PersistAssistantMessagesAsync(sessionId, context.TaskId, exec.Messages, ct);
                }
                await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
                if (context.TaskId.HasValue && context.TaskId != attachedTaskId)
                {
                    await UpdateMessageTaskAsync(userMessageId, sessionId, context.TaskId.Value, ct);
                }
                return new AgentRunResult(sessionId, exec.Messages);
            }
        }

        // 其它类型�?clarification（如场景确认）仍按原流程标记已回�?       if (clarification is not null)
        {
            await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
        }

        var agentResult = await RunAgentAsync(messages, context, ct);

        await PersistAssistantMessagesAsync(sessionId, context.TaskId, agentResult.Messages, ct);

        if (context.TaskId.HasValue && context.TaskId != attachedTaskId)
        {
            await UpdateMessageTaskAsync(userMessageId, sessionId, context.TaskId.Value, ct);
        }

        return new AgentRunResult(sessionId, agentResult.Messages);
    }

    private async Task<AgentRunResult> ProcessTaskMessageAsync(AgentMessageRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (!request.TaskId.HasValue)
            throw new InvalidOperationException("error");

        var taskId = request.TaskId.Value;
        var task = await _invoiceTaskService.GetAsync(taskId, ct);
        if (task is null)
            throw new InvalidOperationException("error");
        if (!string.Equals(task.CompanyCode, request.CompanyCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localize(language, "この証憑タスクは現在の会社に属していませ�?, "票据任务不属于当前公�?));
        if (!string.IsNullOrWhiteSpace(task.UserId) &&
            !string.IsNullOrWhiteSpace(request.UserCtx.UserId) &&
            !string.Equals(task.UserId, request.UserCtx.UserId, StringComparison.Ordinal))
            throw new InvalidOperationException("error");

        var sessionId = await EnsureSessionAsync(task.SessionId, request.CompanyCode, request.UserCtx, ct);
        var history = await LoadHistoryAsync(sessionId, null, 20, task.Id, ct);
        var allScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();
        var selectedScenarios = SelectScenariosForMessage(allScenarios, request.ScenarioKey, request.Message);

        var tokens = new Dictionary<string, string?>
        {
            ["input"] = request.Message,
            ["taskId"] = task.Id.ToString(),
            ["activeDocumentSessionId"] = task.DocumentSessionId,
            ["activeDocumentName"] = task.FileName
        };
        if (!string.IsNullOrWhiteSpace(task.DocumentLabel))
        {
            tokens["activeDocumentLabel"] = task.DocumentLabel;
            tokens[$"group.{task.DocumentLabel}.fileId"] = task.FileId;
            tokens[$"group.{task.DocumentLabel}.documentSessionId"] = task.DocumentSessionId;
            tokens[$"group.{task.DocumentLabel}.name"] = task.FileName;
        }
        if (!string.IsNullOrWhiteSpace(task.Summary))
        {
            tokens["activeDocumentSummary"] = task.Summary;
        }

        ClarificationInfo? clarification = null;
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            clarification = await LoadClarificationAsync(sessionId, request.AnswerTo, ct);
            _logger.LogInformation("[AgentKit][Task] request.AnswerTo = {AnswerTo}, clarification loaded = {Loaded}", request.AnswerTo, clarification is not null);
            if (clarification is not null)
            {
                if (string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
                {
                    clarification = clarification with { DocumentSessionId = task.DocumentSessionId };
                }
                if (string.IsNullOrWhiteSpace(clarification.DocumentId))
                {
                    clarification = clarification with { DocumentId = task.FileId };
                }
                if (string.IsNullOrWhiteSpace(clarification.DocumentLabel) && !string.IsNullOrWhiteSpace(task.DocumentLabel))
                {
                    clarification = clarification with { DocumentLabel = task.DocumentLabel };
                }
                tokens["answerQuestionId"] = clarification.QuestionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId)) tokens["answerDocumentSessionId"] = clarification.DocumentSessionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentId)) tokens["answerDocumentId"] = clarification.DocumentId;
                if (!string.IsNullOrWhiteSpace(clarification.Question)) tokens["answerQuestion"] = clarification.Question;
                if (!string.IsNullOrWhiteSpace(clarification.Detail)) tokens["answerDetail"] = clarification.Detail;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel)) tokens["answerDocumentLabel"] = clarification.DocumentLabel;
                // 用户回答问题时，使用保存的场�?key（场景确认允许用户在回答里覆�?scenario_key�?               _logger.LogInformation("[AgentKit][Task] clarification.ScenarioKey = {ScenarioKey}", clarification.ScenarioKey ?? "(null)");
                if (!string.IsNullOrWhiteSpace(clarification.ScenarioKey))
                {
                    var effectiveScenarioKey = clarification.ScenarioKey!;
                    if (!string.IsNullOrWhiteSpace(clarification.Detail) &&
                        clarification.Detail.StartsWith("scenario_select:", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(request.Message))
                    {
                        var userText = request.Message.Trim();
                        var overrideKey = allScenarios.FirstOrDefault(s =>
                            userText.Contains(s.ScenarioKey, StringComparison.OrdinalIgnoreCase))?.ScenarioKey;
                        if (!string.IsNullOrWhiteSpace(overrideKey))
                        {
                            effectiveScenarioKey = overrideKey!;
                            _logger.LogInformation("[AgentKit][Task] 用户覆盖场景选择�?{ScenarioKey}", effectiveScenarioKey);
                        }
                    }

                    var savedScenario = allScenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, effectiveScenarioKey, StringComparison.OrdinalIgnoreCase));
                    if (savedScenario is not null)
                    {
                        _logger.LogInformation("[AgentKit][Task] 用户回答问题，使用保存的场景: {ScenarioKey}", effectiveScenarioKey);
                        selectedScenarios = new[] { savedScenario };
            }
            else
            {
                        _logger.LogWarning("[AgentKit][Task] 未找到场景{ScenarioKey}", effectiveScenarioKey);
                    }
                }
            }
            else
            {
                _logger.LogWarning("[AgentKit][Task] 未找到待回答的问题questionId={QuestionId}", request.AnswerTo);
            }
        }

        var messages = BuildInitialMessages(request.CompanyCode, selectedScenarios, accountingRules, history, tokens, language);
        var summaryBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(task.DocumentLabel))
        {
            summaryBuilder.AppendLine(Localize(language, $"現在の証憑：{task.DocumentLabel}（{task.FileName}�?, $"当前票据：{task.DocumentLabel}（{task.FileName}�?));
        }
        else
        {
            summaryBuilder.AppendLine(Localize(language, $"現在の証憑：{task.FileName}", $"当前票据：{task.FileName}"));
        }
        summaryBuilder.AppendLine(Localize(language, $"fileId={task.FileId}；docSessionId={task.DocumentSessionId}", $"fileId={task.FileId}；docSessionId={task.DocumentSessionId}"));
        if (!string.IsNullOrWhiteSpace(task.Summary))
        {
            summaryBuilder.AppendLine(task.Summary);
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "system",
            ["content"] = summaryBuilder.ToString().TrimEnd()
        });
        if (clarification is not null)
        {
            var clarifyNote = new StringBuilder();
            clarifyNote.Append("");
            if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
            {
                clarifyNote.Append('[').Append(clarification.DocumentLabel).Append(']');
            }
            clarifyNote.Append(clarification.Question);
            if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                clarifyNote.Append(Localize(language, "（fileId�?, "（fileId�?)).Append(clarification.DocumentId).Append('�?);
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                clarifyNote.Append(Localize(language, "。補足：", "。补充说明：")).Append(clarification.Detail);
            }
            // 添加明确的下丢�步指�?           clarifyNote.AppendLine();
            // 如果是缺失字段的回答，告�?AI 用户的回答应该用于哪个字�?           if (!string.IsNullOrWhiteSpace(clarification.MissingField))
            {
                clarifyNote.Append(Localize(language, 
                    // sanitized
                    // sanitized
            }
            else
            {
                clarifyNote.Append(Localize(language, 
                    // sanitized
                    // sanitized
            }
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = clarifyNote.ToString()
            });
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = request.Message
        });

        var resolver = CreateTaskFileResolver(task, request.UserCtx);
        var context = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, selectedScenarios, resolver, task.Id);
        context.AssignDocumentLabel(task.DocumentSessionId, task.DocumentLabel);
        var analysisClone = task.Analysis is not null ? task.Analysis.DeepClone().AsObject() : null;
        context.RegisterDocument(task.FileId, analysisClone, task.DocumentSessionId);
        context.SetDefaultFileId(task.FileId);
        context.SetActiveDocumentSession(task.DocumentSessionId);
        // 若本次是对��缺失字段��问题的回答，则把答案强制注入到后续 create_voucher（不依赖模型是否记得填）
        if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.MissingField))
        {
            var answer = (request.Message ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(answer))
            {
                _logger.LogInformation("[AgentKit][Task] answerTo reply received missingField={MissingField} answer='{Answer}'", clarification.MissingField, answer);
                context.SetPendingFieldAnswer(clarification.MissingField, answer);
            }
            else
            {
                _logger.LogWarning("[AgentKit][Task] answerTo reply missingField={MissingField} but message is empty", clarification.MissingField);
            }
        }

        await _invoiceTaskService.UpdateStatusAsync(task.Id, "in_progress", null, ct);
        JsonObject? userPayload = null;
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            userPayload = new JsonObject
            {
                ["answerTo"] = request.AnswerTo
            };
        }

        var userMessageId = await PersistMessageAsync(sessionId, "user", request.Message, userPayload, null, request.TaskId, ct);
        
        // 若是"缺失字段 + draft voucher"的回答，直接重试 create_voucher（跳�?LLM�?       if (clarification is not null
            && !string.IsNullOrWhiteSpace(clarification.MissingField)
            && clarification.DraftVoucher is not null
            && !string.IsNullOrWhiteSpace(request.Message))
        {
            var exec = await RetryDraftVoucherWithAnswerAsync(context, clarification, request.Message.Trim(), ct);
            if (exec is not null)
            {
                if (exec.Messages.Count > 0)
                {
                    await PersistAssistantMessagesAsync(sessionId, context.TaskId, exec.Messages, ct);
                }
                await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
                if (context.TaskId.HasValue && context.TaskId != request.TaskId)
                {
                    await UpdateMessageTaskAsync(userMessageId, sessionId, context.TaskId.Value, ct);
                }
                return new AgentRunResult(sessionId, exec.Messages);
            }
        }

        if (clarification is not null)
        {
            await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
        }

        var agentResult = await RunAgentAsync(messages, context, ct);
        await PersistAssistantMessagesAsync(sessionId, context.TaskId, agentResult.Messages, ct);

        if (context.TaskId.HasValue && context.TaskId != request.TaskId)
        {
            await UpdateMessageTaskAsync(userMessageId, sessionId, context.TaskId.Value, ct);
        }

        return new AgentRunResult(sessionId, agentResult.Messages);
    }
    internal async Task<AgentRunResult> ProcessFileAsync(AgentFileRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("error");

        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var history = Array.Empty<(string Role, string Content)>();
        var allScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();

        var fileRecord = request.FileResolver?.Invoke(request.FileId);

        JsonObject? parsedData = request.ParsedData;
        var documentSessionId = $"doc_{Guid.NewGuid():N}";
        const string documentLabel = "#1";
        // 重要：不在路由前调用 extract_invoice_data 预解析，否则会把"结算�?对账�?误导�?供应商请求书"�?
        // 霢�要结构化时由选中的场景自行调用相应工具（extract_invoice_data / extract_booking_settlement_data 等）�?
        var prompt = Localize(language,
            // sanitized
            // sanitized

        var uploadPayload = new JsonObject
        {
            ["kind"] = "user.upload",
            ["fileId"] = request.FileId,
            ["fileName"] = request.FileName,
            ["documentSessionId"] = documentSessionId,
            ["activeDocumentSessionId"] = documentSessionId,
            ["documentLabel"] = documentLabel
        };

        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            uploadPayload["answerTo"] = request.AnswerTo;
        }

        if (!string.IsNullOrWhiteSpace(request.BlobName))
        {
            var attachments = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = request.FileId,
                    ["name"] = request.FileName,
                    ["contentType"] = request.ContentType,
                    ["size"] = request.Size,
                    ["blobName"] = request.BlobName
                }
            };
            uploadPayload["attachments"] = attachments;
        }
        if (parsedData is not null)
        {
            uploadPayload["analysis"] = parsedData.DeepClone();
        }

        _ = await PersistMessageAsync(sessionId, "user", prompt, uploadPayload, null, null, ct);

        // GPT 路由：基于文件预览文�?+ 启用场景清单选择单一场景
        string? previewText = null;
        if (fileRecord is not null && !string.IsNullOrWhiteSpace(fileRecord.StoredPath))
        {
            previewText = AiFileHelpers.SanitizePreview(
                AiFileHelpers.ExtractTextPreview(fileRecord.StoredPath, request.ContentType, 9000),
                9000);
        }
        var fileContext = new FileMatchContext(request.FileName, request.ContentType, previewText, parsedData);

        IReadOnlyList<AgentScenarioService.AgentScenario> selectedScenarios;
        if (!string.IsNullOrWhiteSpace(request.ScenarioKey))
        {
            selectedScenarios = SelectScenariosForFile(allScenarios, request.ScenarioKey, fileContext);
        }
        else
        {
            var routing = await RouteScenarioForFileAsync(allScenarios, fileContext, request.ApiKey, language, ct);
            if (routing is not null && !string.IsNullOrWhiteSpace(routing.ScenarioKey))
            {
                var chosen = allScenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, routing.ScenarioKey, StringComparison.OrdinalIgnoreCase));
                if (chosen is not null)
                {
                    _logger.LogInformation("[AgentKit] GPT route(file) chosen={ScenarioKey} conf={Confidence:P0} reason={Reason}", routing.ScenarioKey, routing.Confidence, routing.Reason ?? "");
                    // 置信�?< 90%：先让用户确�?                   if (routing.Confidence < 0.90)
                    {
                        var questionId = Guid.NewGuid().ToString("N");
                        var title = string.IsNullOrWhiteSpace(chosen.Title) ? chosen.ScenarioKey : chosen.Title;
                        var alternativesText = routing.Alternatives.Count > 0
                            ? string.Join(" / ", routing.Alternatives.Take(3).Select(a => $"{a.ScenarioKey}({a.Confidence:P0})"))
                            : string.Empty;

                        var question = Localize(language,
                            $"このファイルは��{title}」（key={chosen.ScenarioKey}) の可能��が高いです（信頼度 {routing.Confidence:P0}）��続行してよろしいですか？よければ��確認��と返信してください。別のシナリオを使う場合�?scenario_key を返信してください��{(string.IsNullOrWhiteSpace(alternativesText) ? "" : "候補: " + alternativesText)}",
                            $"我判断该文件朢�可能属于「{title}」（key={chosen.ScenarioKey})，置信度 {routing.Confidence:P0}。请回复“确认��继续；如需切换场景，请直接回复 scenario_key。{(string.IsNullOrWhiteSpace(alternativesText) ? "" : "候補: " + alternativesText)}");

                        var detail = $"scenario_select:confidence={routing.Confidence:F3};chosen={chosen.ScenarioKey};alts={alternativesText}";
                        JsonObject? analysisClone = parsedData is not null ? parsedData.DeepClone().AsObject() : null;
                        var tag = new ClarificationTag(questionId, documentSessionId, request.FileId, question, detail, analysisClone, request.FileName, request.BlobName, documentLabel, chosen.ScenarioKey);
                        var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);

                        await SetSessionActiveDocumentAsync(sessionId, documentSessionId, ct);
                        await PersistAssistantMessagesAsync(sessionId, null, new[] { clarifyMessage }, ct);
                        return new AgentRunResult(sessionId, new List<AgentResultMessage> { clarifyMessage });
                    }

                    selectedScenarios = new[] { chosen };
                }
                else
                {
                    // GPT 返回�?key 未命中：回���到旧 matcher
                    selectedScenarios = SelectScenariosForFile(allScenarios, null, fileContext);
                }
            }
            else
            {
                // GPT 失败：回逢�到旧 matcher
                selectedScenarios = SelectScenariosForFile(allScenarios, null, fileContext);
            }
        }

        var tokens = new Dictionary<string, string?>
        {
            ["fileName"] = request.FileName,
            ["contentType"] = request.ContentType,
            ["prompt"] = prompt
        };
        if (parsedData is not null)
        {
            tokens["analysis"] = parsedData.ToJsonString();
        }
        if (!string.IsNullOrWhiteSpace(previewText))
        {
            tokens["preview"] = previewText;
        }
        tokens["documentGroups"] = $"{documentLabel}：{request.FileName}";
        tokens["activeDocumentLabel"] = documentLabel;
        tokens["activeDocumentName"] = request.FileName;
        if (!string.IsNullOrWhiteSpace(request.UserMessage))
        {
            tokens["userMessage"] = request.UserMessage;
        }

        var messages = BuildInitialMessages(request.CompanyCode, selectedScenarios, accountingRules, history, tokens, language);
        var highlight = BuildAnalysisHighlight(parsedData, language);
        var summaryBuilder = new StringBuilder();
        summaryBuilder.Append($"{documentLabel}：{request.FileName}");
        if (!string.IsNullOrWhiteSpace(highlight))
        {
            summaryBuilder.Append("�?).Append(highlight).Append("");
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "system",
            ["content"] = ""
        });
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = prompt
        });
        if (!string.IsNullOrWhiteSpace(request.UserMessage))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = request.UserMessage
            });
        }

        var context = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language,
            selectedScenarios,
            request.FileResolver ?? (_ => null));
        context.AssignDocumentLabel(documentSessionId, documentLabel);
        if (parsedData is not null)
        {
            context.RegisterDocument(request.FileId, parsedData, documentSessionId);
        }
        context.SetDefaultFileId(request.FileId);
        await SetSessionActiveDocumentAsync(sessionId, documentSessionId, ct);

        var agentResult = await RunAgentAsync(messages, context, ct);

        await PersistAssistantMessagesAsync(sessionId, context.TaskId, agentResult.Messages, ct);

        return new AgentRunResult(sessionId, agentResult.Messages);
    }

    internal async Task<ScenarioPreviewResult> PreviewScenariosAsync(
        string companyCode,
        string? scenarioKey,
        string? message,
        string? fileName,
        string? contentType,
        string? preview,
        CancellationToken ct)
    {
        var language = NormalizeLanguage(null);
        var scenarios = await _scenarioService.ListActiveAsync(companyCode, ct);

        IReadOnlyList<AgentScenarioService.AgentScenario> selected;
        IDictionary<string, string?> tokens;

        if (!string.IsNullOrWhiteSpace(message))
        {
            selected = SelectScenariosForMessage(scenarios, scenarioKey, message);
            tokens = new Dictionary<string, string?>
            {
                ["input"] = message
            };
        }
        else
        {
            var fileContext = new FileMatchContext(fileName, contentType, preview, null);
            if (!string.IsNullOrWhiteSpace(scenarioKey))
            {
            selected = SelectScenariosForFile(scenarios, scenarioKey, fileContext);
            }
            else
            {
                // PreviewScenariosAsync 没有 HttpRequest：只能从环境变量/配置获取 key；没有则回��� matcher
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    apiKey = _configuration["OpenAI:ApiKey"];
                }
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var routing = await RouteScenarioForFileAsync(scenarios, fileContext, apiKey!, language, ct);
                    var chosen = routing is not null
                        ? scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, routing.ScenarioKey, StringComparison.OrdinalIgnoreCase))
                        : null;
                    selected = chosen is not null ? new[] { chosen } : SelectScenariosForFile(scenarios, null, fileContext);
                }
                else
                {
                    selected = SelectScenariosForFile(scenarios, null, fileContext);
                }
            }
            tokens = new Dictionary<string, string?>
            {
                ["fileName"] = fileName,
                ["contentType"] = contentType,
                ["preview"] = preview
            };
        }

        var systemPrompt = BuildSystemPrompt(companyCode, selected, null, language);
        var contextMessages = new List<object>();
        foreach (var scenario in selected)
        {
            foreach (var ctx in ExtractContextMessages(scenario))
            {
                contextMessages.Add(new
                {
                    role = ctx.Role,
                    content = ApplyTokens(ctx.Resolve(language), tokens)
                });
            }
        }

        return new ScenarioPreviewResult(
            selected.Select(s => s.ScenarioKey).ToArray(),
            systemPrompt,
            contextMessages);
    }

    internal async Task<GeneratedAgentScenario> GenerateScenarioAsync(
        string companyCode,
        string prompt,
        string apiKey,
        IReadOnlyList<AgentScenarioService.AgentScenario> existingScenarios,
        CancellationToken ct)
    {
        var language = NormalizeLanguage(null);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("error");

        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);

        var existingSummary = existingScenarios.Count == 0
            ? Localize(language, "（現在シナリオは未設定）", "(当前尚未配置场景)")
            : string.Join('\n', existingScenarios
                .OrderBy(s => s.Priority)
                .Select(s => $"- {s.ScenarioKey}: {s.Title}"));

        var sbPrompt = new StringBuilder();
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine($"公司代码: {companyCode}");
            sbPrompt.AppendLine("当前已存在的场景:");
            sbPrompt.AppendLine(existingSummary);
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("请只输出丢��?JSON 对象，结构如下：");
            sbPrompt.AppendLine("{");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("  \"title\": string,               // 人类可读标题");
            sbPrompt.AppendLine("  \"description\": string,         // 场景说明");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("  \"priority\": number,            // 越小优先级越高，默认 100");
            sbPrompt.AppendLine("  \"isActive\": boolean,           // 是否启用");
            sbPrompt.AppendLine("  \"metadata\": object,            // matcher、contextMessages 等元数据");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("}");
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
        }
        else
        {
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine($"会社コー�? {companyCode}");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine(existingSummary);
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("{");
            sbPrompt.AppendLine("  \"scenarioKey\": string,          // 丢�意キー��小文字・数字・ドット・ハイフン推奨");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("  \"instructions\": string,        // Agent への実行ガイドライン");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("  \"isActive\": boolean,           // 有効化フラグ");
            sbPrompt.AppendLine("  \"metadata\": object,            // matcher・contextMessages などのメタデータ");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("}");
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
            sbPrompt.AppendLine("");
        }
        var systemPrompt = sbPrompt.ToString();

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = prompt }
        };

        var openAiResponse = await OpenAiApiHelper.CallOpenAiAsync(
            http, apiKey, "gpt-4o", messages,
            temperature: 0.2, maxTokens: 4096, jsonMode: true, ct: ct);

        if (string.IsNullOrWhiteSpace(openAiResponse.Content))
        {
            _logger.LogWarning("[AgentKit] 生成场景失败: 响应为空");
            throw new Exception(Localize(language, "プロンプトからシナリオ設定を生成できませんでした", "无法根据提示生成场景配置"));
        }

        var content = openAiResponse.Content;

        var cleanedContent = CleanMarkdownJson(content);
        using var scenarioDoc = JsonDocument.Parse(cleanedContent);
        var scenarioRoot = scenarioDoc.RootElement;

        string ReadString(string name, string? defaultValue = null)
        {
            return scenarioRoot.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? defaultValue ?? string.Empty
                : defaultValue ?? string.Empty;
        }

        bool ReadBool(string name, bool defaultValue)
        {
            if (scenarioRoot.TryGetProperty(name, out var el))
            {
                return el.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(el.GetString(), out var parsed) ? parsed : defaultValue,
                    JsonValueKind.Number => el.GetInt32() != 0,
                    _ => defaultValue
                };
            }
            return defaultValue;
        }

        int ReadInt(string name, int defaultValue)
        {
            if (scenarioRoot.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                return el.TryGetInt32(out var val) ? val : defaultValue;
            }
            return defaultValue;
        }

        var scenarioKey = NormalizeScenarioKey(ReadString("scenarioKey"));
        var title = ReadString("title", scenarioKey);
        if (string.IsNullOrWhiteSpace(scenarioKey))
        {
            scenarioKey = NormalizeScenarioKey(title);
        }
        if (string.IsNullOrWhiteSpace(scenarioKey))
        {
            scenarioKey = $"scenario.{Guid.NewGuid():N}";
        }

        var existingKeys = new HashSet<string>(existingScenarios.Select(s => s.ScenarioKey), StringComparer.OrdinalIgnoreCase);
        var baseKey = scenarioKey;
        var suffix = 1;
        while (existingKeys.Contains(scenarioKey))
        {
            scenarioKey = $"{baseKey}.{suffix++}";
        }

        var toolHints = new List<string>();
        if (scenarioRoot.TryGetProperty("toolHints", out var hintsEl) && hintsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in hintsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var hint = item.GetString();
                    if (!string.IsNullOrWhiteSpace(hint))
                    {
                        toolHints.Add(hint.Trim());
                    }
                }
            }
        }

        JsonNode? metadataNode = null;
        if (scenarioRoot.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind != JsonValueKind.Null && metadataEl.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                metadataNode = JsonNode.Parse(metadataEl.GetRawText());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AgentKit] metadata JSON 解析失败");
                metadataNode = null;
            }
        }

        JsonNode? contextNode = null;
        if (scenarioRoot.TryGetProperty("context", out var contextEl) && contextEl.ValueKind != JsonValueKind.Null && contextEl.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                contextNode = JsonNode.Parse(contextEl.GetRawText());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AgentKit] context JSON 解析失败");
                contextNode = null;
            }
        }

        var generated = new GeneratedAgentScenario(
            ScenarioKey: scenarioKey,
            Title: string.IsNullOrWhiteSpace(title) ? scenarioKey : title,
            Description: ReadString("description"),
            Instructions: ReadString("instructions"),
            ToolHints: toolHints,
            Metadata: metadataNode,
            Context: contextNode,
            Priority: ReadInt("priority", 100),
            IsActive: ReadBool("isActive", true));

        return generated;
    }

    private async Task<Guid> EnsureSessionAsync(Guid? sessionId, string companyCode, Auth.UserCtx userCtx, CancellationToken ct)
    {
        if (sessionId.HasValue && sessionId.Value != Guid.Empty)
            return sessionId.Value;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ai_sessions(company_code,user_id,title) VALUES ($1,$2,$3) RETURNING id";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userCtx.UserId ?? string.Empty);
        cmd.Parameters.AddWithValue("ERP Agent 会话");
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is Guid g) return g;
        if (scalar is string str && Guid.TryParse(str, out var parsed)) return parsed;
        throw new Exception("error");
    }

    /// <summary>
    /// 加载会话历史消息�?   /// 【重要��会话隔离规则：
    /// - 当指�?taskId 时，严格只加载该任务的消息（完全隔离�?   /// - �?taskId �?null 时，只加载没�?task_id 的��用消息（不混入其他任务的消息��?
    /// </summary>
    private async Task<List<(string Role, string Content)>> LoadHistoryAsync(Guid sessionId, DateTimeOffset? since, int maxMessages, Guid? taskId, CancellationToken ct)
    {
        if (maxMessages <= 0 && !since.HasValue) return new List<(string, string)>();

        var list = new List<(string, string)>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        if (taskId.HasValue)
        {
            // 【隔离��严格只加载指定任务的消�?           if (since.HasValue)
            {
                cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 AND task_id=$3 AND created_at >= $2 ORDER BY created_at";
                cmd.Parameters.AddWithValue(sessionId);
                cmd.Parameters.AddWithValue(since.Value.UtcDateTime);
                cmd.Parameters.AddWithValue(taskId.Value);
            }
            else
            {
                cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 AND task_id=$2 ORDER BY created_at DESC LIMIT $3";
                cmd.Parameters.AddWithValue(sessionId);
                cmd.Parameters.AddWithValue(taskId.Value);
                cmd.Parameters.AddWithValue(maxMessages);
            }
            _logger.LogInformation("log");
        }
        else
        {
            // 【隔离��只加载没有关联任务的��用消息，不混入其他任务的上下文
            if (since.HasValue)
            {
                cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 AND task_id IS NULL AND created_at >= $2 ORDER BY created_at";
                cmd.Parameters.AddWithValue(sessionId);
                cmd.Parameters.AddWithValue(since.Value.UtcDateTime);
            }
            else
            {
                cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 AND task_id IS NULL ORDER BY created_at DESC LIMIT $2";
                cmd.Parameters.AddWithValue(sessionId);
                cmd.Parameters.AddWithValue(maxMessages);
            }
            _logger.LogInformation("log");
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var role = reader.GetString(0);
            var content = reader.GetString(1);
            list.Add((role, content));
        }

        if (!since.HasValue)
        {
            list.Reverse();
        }

        _logger.LogInformation("log");
        return list;
    }

    internal async Task<AgentTaskPreparationResult> PrepareAgentTaskAsync(AgentTaskRequest request, IReadOnlyList<UploadedFileEnvelope> files, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("error");

        if (files.Count == 0)
            throw new InvalidOperationException(Localize(language, "少なくとも一件のファイルが必要で�?, "至少霢�要一个文�?));

        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var activeScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var hasBookingSettlement = activeScenarios.Any(s => string.Equals(s.ScenarioKey, "voucher.ota.booking.settlement", StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation("[AgentKit][Task] Active scenarios loaded={Count} hasBookingSettlement={HasBooking}", activeScenarios.Count, hasBookingSettlement);

        ClarificationInfo? clarification = null;
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            clarification = await LoadClarificationAsync(sessionId, request.AnswerTo, ct);
            if (clarification is not null)
            {
                _logger.LogInformation("[AgentKit] 用户通过附件回答问题 questionId={QuestionId} documentId={DocumentId}", clarification.QuestionId, clarification.DocumentId);
            }
            else
            {
                _logger.LogWarning("[AgentKit] answerTo question 未找到，questionId={QuestionId} session={SessionId}", request.AnswerTo, sessionId);
            }
        }

        _logger.LogInformation("[AgentKit] 准备处理批量上传 session={SessionId} fileCount={FileCount}", sessionId, files.Count);

        Func<string, UploadedFileRecord?> resolver = request.FileResolver ?? (id =>
        {
            foreach (var file in files)
            {
                if (string.Equals(file.FileId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return file.Record;
                }
            }
            return null;
        });

        var context = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, Array.Empty<AgentScenarioService.AgentScenario>(), resolver);
        var documents = new List<AgentTaskDocument>(files.Count);
        var documentSessions = new List<string>(files.Count);
        var taskInfos = new List<InvoiceTaskInfo>(files.Count);
        (string DocumentSessionId, string FileId, string FileName, string BlobName, string ScenarioKey, double Confidence, string? Reason, string AlternativesText)? pendingScenarioClarification = null;

        // 并行解析多张发票以提升��度
        _logger.LogInformation("log");
        var parseResults = await Task.WhenAll(files.Select(async file =>
        {
            var documentSessionId = $"doc_{Guid.NewGuid():N}";
            JsonObject? parsed = null;
            try
            {
                var fileName = file.Record.FileName ?? "uploaded";
                var contentType = file.Record.ContentType ?? "application/octet-stream";
                var preview = AiFileHelpers.SanitizePreview(
                    AiFileHelpers.ExtractTextPreview(file.Record.StoredPath, contentType, 9000),
                    9000);
                var fileContext = new FileMatchContext(fileName, contentType, preview, null);

                // 1) 用户全局指定 scenarioKeyOverride：直接使�?               string? scenarioKey = null;
                double conf = 1d;
                string? reason = null;
                string alternativesText = string.Empty;
                if (!string.IsNullOrWhiteSpace(request.ScenarioKeyOverride))
                {
                    scenarioKey = NormalizeScenarioKey(request.ScenarioKeyOverride);
                    conf = 1d;
                    reason = "forced by user override";
                }
                else
                {
                    // 2) 如果用户正在回答之前�?clarification，并且该 clarification 指向某个 document，则优先采用它的 scenarioKey
                    if (clarification is not null &&
                        !string.IsNullOrWhiteSpace(clarification.ScenarioKey) &&
                        !string.IsNullOrWhiteSpace(clarification.DocumentId) &&
                        string.Equals(clarification.DocumentId, file.FileId, StringComparison.OrdinalIgnoreCase))
                    {
                        scenarioKey = NormalizeScenarioKey(clarification.ScenarioKey);
                        conf = 1d;
                        reason = "forced by clarification";
                    }
                    else
                    {
                        // 3) GPT 路由：只从启用场景中选一�?                       var routing = await RouteScenarioForFileAsync(activeScenarios, fileContext, request.ApiKey, language, ct);
                        if (routing is not null && !string.IsNullOrWhiteSpace(routing.ScenarioKey))
                        {
                            scenarioKey = routing.ScenarioKey;
                            conf = routing.Confidence;
                            reason = routing.Reason;
                            alternativesText = routing.Alternatives.Count > 0
                                ? string.Join(" / ", routing.Alternatives.Take(3).Select(a => $"{a.ScenarioKey}({a.Confidence:P0})"))
                                : string.Empty;
                            _logger.LogInformation("[AgentKit] GPT route(task-file) fileId={FileId} chosen={ScenarioKey} conf={Confidence:P0} reason={Reason}", file.FileId, scenarioKey, conf, reason ?? "");
                        }
                        else
                        {
                            // 4) GPT 失败：回逢��?matcher
                            var fallback = SelectScenariosForFile(activeScenarios, null, fileContext).FirstOrDefault();
                            scenarioKey = fallback?.ScenarioKey;
                            conf = 0d;
                            reason = "fallback matcher";
                            _logger.LogInformation("[AgentKit] GPT route(task-file) fallback matcher fileId={FileId} chosen={ScenarioKey}", file.FileId, scenarioKey ?? "");
                        }
                    }
                }

                // 只有当确实属于��供应商请款/发票”场景时才做 invoice 结构化解析，
                // 避免�?Booking 结算单等误解析成"供应商外注费"                var shouldExtractInvoice = string.Equals(scenarioKey, "voucher.vendor.invoice", StringComparison.OrdinalIgnoreCase);
                if (shouldExtractInvoice)
                {
                    _logger.LogInformation("[AgentKit][Task] ExtractInvoiceDataAsync enabled fileId={FileId} scenario={ScenarioKey}", file.FileId, scenarioKey);
                parsed = await ExtractInvoiceDataAsync(file.FileId, file.Record, context, ct);
                }
                else
                {
                    parsed = new JsonObject();
                }

                // 写入 suggestedScenario（PlanTaskGroupsAsync 会优先用它）
                parsed ??= new JsonObject();
                if (!string.IsNullOrWhiteSpace(scenarioKey))
                {
                    parsed["suggestedScenario"] = scenarioKey;
                    parsed["scenarioRoutingConfidence"] = conf;
                    if (!string.IsNullOrWhiteSpace(reason)) parsed["scenarioRoutingReason"] = reason;
                    if (!string.IsNullOrWhiteSpace(alternativesText)) parsed["scenarioRoutingAlternatives"] = alternativesText;
                    parsed["scenarioRoutingBy"] = "gpt";
                }

                // 置信�?< 90%：准备在后续生成 clarification（等 documentLabel 计算完）
                if (pendingScenarioClarification is null &&
                    !string.IsNullOrWhiteSpace(scenarioKey) &&
                    conf > 0d && conf < 0.90)
                {
                    pendingScenarioClarification = (documentSessionId, file.FileId, fileName, file.Record.BlobName ?? string.Empty, scenarioKey!, conf, reason, alternativesText);
                }

                _logger.LogInformation("[AgentKit] 文件 {FileId} 初步解析/路由完成 scenario={ScenarioKey}", file.FileId, scenarioKey ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentKit] 解析文件 {FileId} 失败", file.FileId);
            }
            return (file, documentSessionId, parsed);
        }));

        foreach (var (file, documentSessionId, parsed) in parseResults)
        {
            if (parsed is not null)
            {
                context.RegisterDocument(file.FileId, parsed, documentSessionId);
            }
            else
            {
                _logger.LogWarning("[AgentKit] 文件 {FileId} 未能解析出结构化数据", file.FileId);
            }

            var normalizedName = (file.Record.FileName ?? string.Empty).Trim().ToLowerInvariant();
            documents.Add(new AgentTaskDocument(
                documentSessionId,
                file.FileId,
                file.Record.FileName ?? "uploaded",
                file.Record.ContentType ?? "application/octet-stream",
                file.Record.Size,
                file.Record.BlobName ?? string.Empty,
                file.Record.StoredPath,
                parsed,
                string.IsNullOrEmpty(normalizedName) ? null : normalizedName,
                null));
            documentSessions.Add(documentSessionId);
        }
        _logger.LogInformation("log");

        string? activeDocumentSessionId = clarification?.DocumentSessionId;
        if (activeDocumentSessionId is null && clarification is not null && !string.IsNullOrWhiteSpace(clarification.DocumentId))
        {
            var clarifiedDoc = documents.FirstOrDefault(d => string.Equals(d.FileId, clarification.DocumentId, StringComparison.OrdinalIgnoreCase));
            if (clarifiedDoc is not null)
            {
                activeDocumentSessionId = clarifiedDoc.DocumentSessionId;
            }
        }
        if (string.IsNullOrWhiteSpace(activeDocumentSessionId) && documentSessions.Count > 0)
        {
            activeDocumentSessionId = documentSessions[0];
        }

        var existingTasks = await _invoiceTaskService.ListAsync(sessionId, ct);
        var labelStartIndex = DetermineDocumentLabelStart(existingTasks);
        var documentLabelEntries = BuildDocumentLabelEntries(documents, d => d.DocumentSessionId, d => d.FileName, d => d.Data, d => d.FileId, language, labelStartIndex);
        var documentLabelMap = documentLabelEntries.ToDictionary(e => e.SessionId, e => e, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            if (!string.IsNullOrWhiteSpace(doc.DocumentSessionId) && documentLabelMap.TryGetValue(doc.DocumentSessionId, out var entry))
            {
                documents[i] = doc with { DocumentLabel = entry.Label };
                context.AssignDocumentLabel(doc.DocumentSessionId, entry.Label);
            }
        }

        // 如果霢�要用户确认场景（conf < 90%），这里生成 clarification 卡片并阻止后续自动执行（由外层控制）        if (pendingScenarioClarification is not null && string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            var p = pendingScenarioClarification.Value;
            var chosenScenario = activeScenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, p.ScenarioKey, StringComparison.OrdinalIgnoreCase));
            var docLabel = documentLabelMap.TryGetValue(p.DocumentSessionId, out var e) ? e.Label : null;
            var title = chosenScenario is null ? p.ScenarioKey : (string.IsNullOrWhiteSpace(chosenScenario.Title) ? chosenScenario.ScenarioKey : chosenScenario.Title);
            var questionId = $"q_{Guid.NewGuid():N}";
            var question = Localize(language,
                $"このファイルは��{title}」（key={p.ScenarioKey}) の可能��が高いです（信頼度 {p.Confidence:P0}）��続行してよろしいですか？よければ��確認��と返信してください。別のシナリオを使う場合�?scenario_key を返信してください��{(string.IsNullOrWhiteSpace(p.AlternativesText) ? "" : "候補: " + p.AlternativesText)}",
                $"我判断该文件朢�可能属于「{title}」（key={p.ScenarioKey})，置信度 {p.Confidence:P0}。请回复“确认��继续；如需切换场景，请直接回复 scenario_key。{(string.IsNullOrWhiteSpace(p.AlternativesText) ? "" : "候補: " + p.AlternativesText)}");
            var detail = $"scenario_select:confidence={p.Confidence:F3};chosen={p.ScenarioKey};alts={p.AlternativesText}";
            JsonObject? analysisClone = documents.FirstOrDefault(d => string.Equals(d.FileId, p.FileId, StringComparison.OrdinalIgnoreCase))?.Data?.DeepClone().AsObject();
            var tag = new ClarificationTag(questionId, p.DocumentSessionId, p.FileId, question, detail, analysisClone, p.FileName, p.BlobName, docLabel, p.ScenarioKey);
            var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);
            clarification = new ClarificationInfo(questionId, p.DocumentSessionId, p.FileId, question, detail, analysisClone, p.FileName, p.BlobName, docLabel, p.ScenarioKey);
            await PersistAssistantMessagesAsync(sessionId, null, new[] { clarifyMessage }, ct);
            if (!string.IsNullOrWhiteSpace(p.DocumentSessionId))
            {
                await SetSessionActiveDocumentAsync(sessionId, p.DocumentSessionId, ct);
            }
        }

        var uploadPayload = new JsonObject
        {
            ["kind"] = "user.uploadBatch",
            ["count"] = documents.Count
        };
        if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            uploadPayload["activeDocumentSessionId"] = activeDocumentSessionId;
        }

        var docArray = new JsonArray();
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var docNode = new JsonObject
            {
                ["documentSessionId"] = doc.DocumentSessionId,
                ["fileId"] = doc.FileId,
                ["fileName"] = doc.FileName,
                ["contentType"] = doc.ContentType,
                ["size"] = doc.Size,
                ["blobName"] = doc.BlobName
            };
            if (!string.IsNullOrWhiteSpace(doc.DocumentLabel))
            {
                docNode["documentLabel"] = doc.DocumentLabel;
            }
            if (doc.Data is not null)
            {
                docNode["analysis"] = doc.Data.DeepClone();
            }
            docArray.Add(docNode);
        }
        uploadPayload["documents"] = docArray;
        if (documentLabelEntries.Count > 0)
        {
            var labelNode = new JsonObject();
            foreach (var entry in documentLabelEntries)
            {
                labelNode[entry.SessionId] = entry.Label;
            }
            uploadPayload["documentLabels"] = labelNode;
        }
        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            uploadPayload["message"] = request.Message;
        }
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            uploadPayload["answerTo"] = request.AnswerTo;
        }

        var tasksArray = new JsonArray();
        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var envelope = files[i];
            var createdTask = await _invoiceTaskService.CreateAsync(
                sessionId,
                request.CompanyCode,
                doc.FileId,
                envelope.Record,
                doc.DocumentSessionId,
                doc.Data?.DeepClone().AsObject(),
                doc.DocumentLabel,
                ct);
            var analysisClone = createdTask.Analysis is not null ? createdTask.Analysis.DeepClone().AsObject() : null;
            taskInfos.Add(new InvoiceTaskInfo(
                createdTask.Id,
                createdTask.FileId,
                createdTask.DocumentSessionId,
                createdTask.FileName,
                createdTask.ContentType,
                createdTask.Size,
                createdTask.Status,
                createdTask.DocumentLabel,
                createdTask.Summary,
                analysisClone,
                createdTask.CreatedAt,
                createdTask.UpdatedAt));

            var taskNode = new JsonObject
            {
                ["taskId"] = createdTask.Id,
                ["status"] = createdTask.Status,
                ["fileId"] = createdTask.FileId,
                ["documentSessionId"] = createdTask.DocumentSessionId
            };
            if (!string.IsNullOrWhiteSpace(createdTask.DocumentLabel))
            {
                taskNode["documentLabel"] = createdTask.DocumentLabel;
            }
            tasksArray.Add(taskNode);
        }
        if (tasksArray.Count > 0)
        {
            uploadPayload["tasks"] = tasksArray;
        }

        var content = documents.Count == 1
            ? Localize(language, $"ユーザーがファイ�?{documents[0].FileName} をアップロードし、システムが初期解析を完了しました��?, $"用户上传了文件{documents[0].FileName}，系统已完成初步解析�?)
            : Localize(language, $"ユーザー�?{documents.Count} 件のファイルをアップロードし、システムが初期解析を完了しました��?, $"用户上传�?{documents.Count} 个文件，系统已完成初步解析��?);

        _ = await PersistMessageAsync(sessionId, "user", content, uploadPayload, null, null, ct);
        if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            await SetSessionActiveDocumentAsync(sessionId, activeDocumentSessionId, ct);
        }

        return new AgentTaskPreparationResult(sessionId, documents, clarification, activeDocumentSessionId, taskInfos);
    }

    internal async Task<AgentTaskPlanningResult> PlanTaskGroupsAsync(AgentTaskPlanningRequest request, IReadOnlyList<AgentTaskDocument> documents, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("error");

        var language = NormalizeLanguage(request.Language);

        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var allDocuments = documents.ToArray();
        if (allDocuments.Length == 0)
        {
            return new AgentTaskPlanningResult(sessionId, Array.Empty<TaskGroupPlan>(), Array.Empty<string>());
        }

        var clarification = request.Clarification;
        var workingDocuments = allDocuments;
        var suppressedDocumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var focusNotes = new List<string>();

        void ApplyFilterByIds(HashSet<string> allowedIds, string? note)
        {
            if (allowedIds.Count == 0) return;
            var filtered = workingDocuments.Where(d => allowedIds.Contains(d.FileId)).ToArray();
            if (filtered.Length == 0) return;
            foreach (var doc in workingDocuments)
            {
                if (!allowedIds.Contains(doc.FileId))
                {
                    suppressedDocumentIds.Add(doc.FileId);
                }
            }
            workingDocuments = filtered;
            if (!string.IsNullOrWhiteSpace(note))
            {
                focusNotes.Add(note);
                _logger.LogInformation("[AgentKit] 任务规划聚焦 {Note}，匹配文�?{Count}", note, filtered.Length);
            }
        }

        string? focusDocumentSessionId = clarification?.DocumentSessionId;
        if (string.IsNullOrWhiteSpace(focusDocumentSessionId))
        {
            focusDocumentSessionId = request.ActiveDocumentSessionId;
        }
        var shouldApplyFocus = !string.IsNullOrWhiteSpace(focusDocumentSessionId)
                               && (clarification is not null
                                   || !string.IsNullOrWhiteSpace(request.Message)
                                   || allDocuments.Length == 1);
        if (shouldApplyFocus && !string.IsNullOrWhiteSpace(focusDocumentSessionId))
        {
            var allowed = allDocuments
                .Where(d => string.Equals(d.DocumentSessionId, focusDocumentSessionId, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.FileId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed.Count > 0)
            {
                ApplyFilterByIds(allowed, $"documentSessionId={focusDocumentSessionId}");
            }
            else
            {
                _logger.LogWarning("[AgentKit] 指定�?documentSessionId={DocumentSessionId} 未匹配任何待处理文件", focusDocumentSessionId);
            }
        }

        if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.DocumentId))
        {
            var allowed = allDocuments
                .Where(d => string.Equals(d.FileId, clarification.DocumentId, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.FileId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed.Count > 0)
            {
                ApplyFilterByIds(allowed, $"clarification.documentId={clarification.DocumentId}");
            }
        }

        if (workingDocuments.Length == 0)
        {
            var suppressed = suppressedDocumentIds.Count > 0
                ? suppressedDocumentIds.ToArray()
                : allDocuments.Select(d => d.FileId).ToArray();
            return new AgentTaskPlanningResult(sessionId, Array.Empty<TaskGroupPlan>(), suppressed);
        }

        var scenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        
        // 优化：如果文档的 analysis 中已经包�?suggestedScenario，直接使用，跳过 AI 调用
        var docsWithSuggestedScenario = workingDocuments
            .Where(d => d.Data is not null && !string.IsNullOrWhiteSpace(d.Data["suggestedScenario"]?.GetValue<string>()))
            .ToArray();
        
        if (docsWithSuggestedScenario.Length == workingDocuments.Length && docsWithSuggestedScenario.Length > 0)
        {
            _logger.LogInformation("[AgentKit] 扢�有文档都�?suggestedScenario，跳�?AI 场景推断");
            
            // �?suggestedScenario �?documentSessionId 分组
            var directPlans = new List<TaskGroupPlan>();
            var grouped = docsWithSuggestedScenario
                .GroupBy(d => new { 
                    Scenario = d.Data!["suggestedScenario"]!.GetValue<string>(), 
                    SessionId = d.DocumentSessionId ?? string.Empty 
                })
                .ToArray();
            
            foreach (var group in grouped)
            {
                var scenarioKey = group.Key.Scenario;
                var matchedScenario = scenarios.FirstOrDefault(s => 
                    string.Equals(s.ScenarioKey, scenarioKey, StringComparison.OrdinalIgnoreCase));
                
                // 如果找不到精确匹配的场景，尝试匹配前缢�
                if (matchedScenario is null)
                {
                    matchedScenario = scenarios.FirstOrDefault(s => 
                        scenarioKey.StartsWith(s.ScenarioKey.Split('.')[0], StringComparison.OrdinalIgnoreCase));
                }
                
                if (matchedScenario is not null)
                {
                    var docIds = group.Select(d => d.FileId).ToArray();
                    var sessionIdKey = string.IsNullOrWhiteSpace(group.Key.SessionId)
                        ? $"legacy_{group.First().FileId}"
                        : group.Key.SessionId;
                    
                    directPlans.Add(new TaskGroupPlan(
                        matchedScenario.ScenarioKey,
                        sessionIdKey,
                        docIds,
                        // sanitized
                        request.Message));
                    
                    _logger.LogInformation("log");
                }
                else
                {
                    _logger.LogWarning("[AgentKit] 未找到匹配场�?{SuggestedScenario}，回逢��?AI 推断", scenarioKey);
                    goto FallbackToAi;
                }
            }
            
            if (directPlans.Count > 0)
            {
                return new AgentTaskPlanningResult(sessionId, directPlans, suppressedDocumentIds.ToArray());
            }
        }
        
        FallbackToAi:
        if (!string.IsNullOrWhiteSpace(request.ScenarioKeyOverride))
        {
            var overrideKey = NormalizeScenarioKey(request.ScenarioKeyOverride);
            var match = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, overrideKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException("error");
            }

            var overridePlans = new List<TaskGroupPlan>();
            var grouped = workingDocuments
                .GroupBy(d => d.DocumentSessionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var group in grouped)
            {
                var sessionIdKey = string.IsNullOrWhiteSpace(group.Key)
                    ? $"legacy_{group.First().FileId}"
                    : group.Key;
                var ids = group.Select(d => d.FileId).ToArray();
                var reason = grouped.Length > 1
                    ? Localize(language, "ユーザー指定シナリオ（文書ごとに分割�?, "用户指定场景（按文档拆分�?)
                    : Localize(language, "ユーザー指定シナリオ", "用户指定场景");
                overridePlans.Add(new TaskGroupPlan(match.ScenarioKey, sessionIdKey, ids, reason, request.Message));
            }
            return new AgentTaskPlanningResult(sessionId, overridePlans, suppressedDocumentIds.ToArray());
        }

        if (scenarios.Count == 0)
        {
            var unassignedList = new HashSet<string>(suppressedDocumentIds, StringComparer.OrdinalIgnoreCase);
            foreach (var doc in workingDocuments)
            {
                unassignedList.Add(doc.FileId);
            }
            return new AgentTaskPlanningResult(sessionId, Array.Empty<TaskGroupPlan>(), unassignedList.ToArray());
        }

        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, request.ApiKey);

        var scenarioSummary = string.Join("\n", scenarios
            .OrderBy(s => s.Priority)
            .Select(s =>
            {
                var sb = new StringBuilder();
                sb.Append($"- key: {s.ScenarioKey}; title: {s.Title}; priority: {s.Priority}");
                if (!string.IsNullOrWhiteSpace(s.Description)) sb.Append($"; desc: {s.Description}");
                if (!string.IsNullOrWhiteSpace(s.Instructions)) sb.Append($"; instructions: {s.Instructions}");
                return sb.ToString();
            }));

        string? Truncate(string? input, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            if (input.Length <= maxLen) return input;
            return string.Empty;
        }

        var documentByFileId = workingDocuments.ToDictionary(d => d.FileId, StringComparer.OrdinalIgnoreCase);

        var docsPayload = workingDocuments.Select(d =>
        {
            var analysisJson = d.Data?.ToJsonString();
            analysisJson = Truncate(analysisJson, 4000);
            return new Dictionary<string, object?>
            {
                ["documentSessionId"] = d.DocumentSessionId,
                ["fileId"] = d.FileId,
                ["fileName"] = d.FileName,
                ["contentType"] = d.ContentType,
                ["size"] = d.Size,
                ["blobName"] = d.BlobName,
                ["analysis"] = analysisJson
            };
        }).ToList();

        var systemPrompt = new StringBuilder();
        var plannerLinesJa = new[]
        {
            // sanitized
            "以下の原則に従ってください：",
            // sanitized
            // sanitized
            // sanitized
            scenarioSummary,
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        };
        var plannerLinesZh = new[]
        {
            // sanitized
            "请遵循以下原则：",
            // sanitized
            // sanitized
            // sanitized
            scenarioSummary,
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        };
        var plannerLines = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? plannerLinesZh : plannerLinesJa;
        foreach (var line in plannerLines)
        {
            systemPrompt.AppendLine(line);
        }
        systemPrompt.AppendLine("8. 返回 JSON，结构如下：");
        systemPrompt.AppendLine("{");
        systemPrompt.AppendLine("  \"tasks\": [");
        systemPrompt.AppendLine("    {");
        systemPrompt.AppendLine("      \"scenarioKey\": string,");
        systemPrompt.AppendLine("      \"documentSessionId\": string,");
        systemPrompt.AppendLine("      \"documents\": string[],");
        systemPrompt.AppendLine("      \"reason\": string,");
        systemPrompt.AppendLine("      \"message\"?: string,");
        systemPrompt.AppendLine("      \"confidence\"?: number");
        systemPrompt.AppendLine("    }");
        systemPrompt.AppendLine("  ],");
        systemPrompt.AppendLine("  \"unassignedDocuments\": string[]");
        systemPrompt.AppendLine("}");
        systemPrompt.AppendLine("");
        if (focusNotes.Count > 0)
        {
            systemPrompt.AppendLine();
            systemPrompt.AppendLine("");
            foreach (var note in focusNotes.Distinct())
            {
                systemPrompt.AppendLine($"- {note}");
            }
        }
        if (clarification is not null)
        {
            systemPrompt.AppendLine();
            systemPrompt.AppendLine(Localize(language, "補足: ユーザーは次の質問に回答しています��関連するファイルを優先し��計画理由に対応関係を明示してください��?, "补充说明：用户正在回应以下问题，请优先关注相关文件并在计划理由中标记对应关系�?));
            systemPrompt.AppendLine(Localize(language, $"質問：{clarification.Question}", $"问题：{clarification.Question}"));
            if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                systemPrompt.AppendLine(Localize(language, $"関��ファイルID：{clarification.DocumentId}", $"关联文件ID：{clarification.DocumentId}"));
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                systemPrompt.AppendLine(Localize(language, $"備��：{clarification.Detail}", $"备注：{clarification.Detail}"));
            }
        }

        var userPayload = new Dictionary<string, object?>
        {
            ["userMessage"] = request.Message,
            ["documents"] = docsPayload
        };
        if (focusNotes.Count > 0)
        {
            userPayload["focus"] = focusNotes.ToArray();
        }
        if (clarification is not null)
        {
            userPayload["clarification"] = new
            {
                clarification.QuestionId,
                clarification.Question,
                clarification.DocumentId,
                clarification.DocumentSessionId,
                clarification.Detail
            };
        }

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt.ToString() },
            new { role = "user", content = JsonSerializer.Serialize(userPayload, JsonOptions) }
        };

        var openAiResponse = await OpenAiApiHelper.CallOpenAiAsync(
            http, request.ApiKey, "gpt-4o", messages,
            temperature: 0.2, maxTokens: 4096, jsonMode: true, ct: ct);

        if (string.IsNullOrWhiteSpace(openAiResponse.Content))
        {
            _logger.LogWarning("[AgentKit] 任务分组模型调用失败: 响应为空");
            throw new Exception("error");
        }

        var content = openAiResponse.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new Exception(Localize(language, "ッ6�9��ルがタスク分割の結果を返しませんでし�?, "模型未返回任务分组结�?));
        }

        var cleanedContent = CleanMarkdownJson(content);
        using var planDoc = JsonDocument.Parse(cleanedContent);
        var root = planDoc.RootElement;

        var plans = new List<TaskGroupPlan>();
        var unassigned = new HashSet<string>(workingDocuments.Select(d => d.FileId), StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var taskEl in tasksEl.EnumerateArray())
            {
                var scenarioKey = taskEl.TryGetProperty("scenarioKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String
                    ? NormalizeScenarioKey(keyEl.GetString())
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(scenarioKey)) continue;

                var scenario = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, scenarioKey, StringComparison.OrdinalIgnoreCase));
                if (scenario is null) continue;

                var docIdList = new List<string>();
                var requestedSessionId = taskEl.TryGetProperty("documentSessionId", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.String
                    ? sessionEl.GetString()
                    : null;
                if (taskEl.TryGetProperty("documents", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var docIdEl in docsEl.EnumerateArray())
                    {
                        if (docIdEl.ValueKind == JsonValueKind.String)
                        {
                            var docId = docIdEl.GetString();
                            if (!string.IsNullOrWhiteSpace(docId) && unassigned.Contains(docId))
                            {
                                docIdList.Add(docId);
                            }
                        }
                    }
                }

                if (docIdList.Count == 0) continue;

                foreach (var id in docIdList)
                {
                    unassigned.Remove(id);
                }

                var reason = taskEl.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString()
                    : null;
                var messageOverride = taskEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString()
                    : null;

                var groupedBySession = docIdList
                    .Select(id => documentByFileId.TryGetValue(id, out var doc) ? doc : null)
                    .Where(doc => doc is not null)
                    .Select(doc => doc!)
                    .GroupBy(doc => doc.DocumentSessionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (groupedBySession.Length <= 1)
                {
                    var resolvedSessionId = groupedBySession.Length == 1
                        ? groupedBySession[0].Key
                        : (documentByFileId.TryGetValue(docIdList[0], out var firstDoc) ? firstDoc.DocumentSessionId ?? string.Empty : string.Empty);
                    if (string.IsNullOrWhiteSpace(resolvedSessionId))
                    {
                        resolvedSessionId = $"legacy_{docIdList[0]}";
                    }
                    if (!string.IsNullOrWhiteSpace(requestedSessionId) &&
                        !string.Equals(requestedSessionId, resolvedSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("log");
                    }
                    plans.Add(new TaskGroupPlan(
                        scenario.ScenarioKey,
                        resolvedSessionId,
                        docIdList,
                        reason ?? string.Empty,
                        string.IsNullOrWhiteSpace(messageOverride) ? null : messageOverride));
                }
                else
                {
                    _logger.LogWarning("[AgentKit] 模型计划包含多个文档上下文，已拆分执�?scenario={ScenarioKey}", scenario.ScenarioKey);
                    foreach (var sessionGroup in groupedBySession)
                    {
                        var sessionKey = string.IsNullOrWhiteSpace(sessionGroup.Key)
                            ? $"legacy_{sessionGroup.First().FileId}"
                            : sessionGroup.Key;
                        var sessionDocs = sessionGroup.Select(d => d.FileId).ToArray();
                        var localizedReason = string.IsNullOrWhiteSpace(reason)
                            ? Localize(language, "ファイルごとに自動分割しました��?, "按文档自动拆�?)
                            : reason;
                        plans.Add(new TaskGroupPlan(
                            scenario.ScenarioKey,
                            sessionKey,
                            sessionDocs,
                            localizedReason,
                            string.IsNullOrWhiteSpace(messageOverride) ? null : messageOverride));
                    }
                }
            }
        }

        if (root.TryGetProperty("unassignedDocuments", out var unassignedEl) && unassignedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in unassignedEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var docId = item.GetString();
                    if (!string.IsNullOrWhiteSpace(docId))
                    {
                        unassigned.Add(docId);
                    }
                }
            }
        }

        unassigned.UnionWith(suppressedDocumentIds);

        return new AgentTaskPlanningResult(sessionId, plans, unassigned.ToArray());
    }

    private async Task<UploadContext?> GetLatestUploadAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload::text, created_at FROM ai_messages WHERE session_id=$1 AND (payload->>'kind' = 'user.upload' OR payload->>'kind' = 'user.uploadBatch') ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var payloadText = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (string.IsNullOrWhiteSpace(payloadText)) return null;

        JsonObject? payloadNode;
        try
        {
            payloadNode = JsonNode.Parse(payloadText)?.AsObject();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentKit] upload payload JSON 解析失败");
            return null;
        }
        if (payloadNode is null) return null;

        var createdAt = reader.GetDateTime(1);
        var createdAtOffset = new DateTimeOffset(createdAt, TimeSpan.Zero);

        var kind = payloadNode.TryGetPropertyValue("kind", out var kindNode) ? kindNode?.GetValue<string>() : null;
        var docs = new List<UploadContextDoc>();
        string? activeDocumentSessionId = null;

        string ResolveDocumentSessionId(JsonObject? node, string? fileId, int index)
        {
            if (node is not null &&
                node.TryGetPropertyValue("documentSessionId", out var docSessionNode) &&
                docSessionNode is JsonValue docSessionValue &&
                docSessionValue.TryGetValue<string>(out var docSessionStr) &&
                !string.IsNullOrWhiteSpace(docSessionStr))
            {
                return docSessionStr.Trim();
            }

            if (payloadNode.TryGetPropertyValue("documentSessionId", out var rootSessionNode) &&
                rootSessionNode is JsonValue rootSessionValue &&
                rootSessionValue.TryGetValue<string>(out var rootSessionStr) &&
                !string.IsNullOrWhiteSpace(rootSessionStr))
            {
                return rootSessionStr.Trim();
            }

            if (!string.IsNullOrWhiteSpace(fileId))
            {
                return $"legacy_{fileId}";
            }

            return $"legacy_{createdAtOffset.ToUnixTimeMilliseconds()}_{index}";
        }

        if (payloadNode.TryGetPropertyValue("activeDocumentSessionId", out var activeNode) &&
            activeNode is JsonValue activeValue &&
            activeValue.TryGetValue<string>(out var activeStr) &&
            !string.IsNullOrWhiteSpace(activeStr))
        {
            activeDocumentSessionId = activeStr.Trim();
        }

        if (string.Equals(kind, "user.upload", StringComparison.OrdinalIgnoreCase))
        {
            var fileId = payloadNode.TryGetPropertyValue("fileId", out var fileIdNode) ? fileIdNode?.GetValue<string>() : null;
            var fileName = payloadNode.TryGetPropertyValue("fileName", out var fileNameNode) ? fileNameNode?.GetValue<string>() : null;
            JsonObject? analysis = null;
            if (payloadNode.TryGetPropertyValue("analysis", out var analysisNode) && analysisNode is JsonObject analysisObj)
            {
                analysis = analysisObj;
            }
            string? blobName = null;
            if (payloadNode.TryGetPropertyValue("attachments", out var attachmentsNode) && attachmentsNode is JsonArray attachmentArr)
            {
                foreach (var item in attachmentArr)
                {
                    if (item is not JsonObject obj) continue;
                    if (obj.TryGetPropertyValue("blobName", out var blobNode) && blobNode is JsonValue blobValue && blobValue.TryGetValue<string>(out var blobStr))
                    {
                        blobName = blobStr;
                        break;
                    }
                }
            }
            var documentSessionId = ResolveDocumentSessionId(payloadNode, fileId, 0);
            docs.Add(new UploadContextDoc(documentSessionId, fileId, fileName, blobName, analysis));
            activeDocumentSessionId ??= documentSessionId;
        }
        else if (string.Equals(kind, "user.uploadBatch", StringComparison.OrdinalIgnoreCase))
        {
            if (payloadNode.TryGetPropertyValue("documents", out var docsNode) && docsNode is JsonArray docArr)
            {
                var index = 0;
                foreach (var item in docArr)
                {
                    if (item is not JsonObject docObj) continue;
                    var fileId = docObj.TryGetPropertyValue("fileId", out var idNode) ? idNode?.GetValue<string>() : null;
                    var fileName = docObj.TryGetPropertyValue("fileName", out var nameNode) ? nameNode?.GetValue<string>() : null;
                    JsonObject? analysis = null;
                    if (docObj.TryGetPropertyValue("analysis", out var analysisNode) && analysisNode is JsonObject analysisObj)
                    {
                        analysis = analysisObj;
                    }
                    string? blobName = null;
                    if (docObj.TryGetPropertyValue("blobName", out var blobNode) && blobNode is JsonValue blobValue && blobValue.TryGetValue<string>(out var blobStr))
                    {
                        blobName = blobStr;
                    }
                    var documentSessionId = ResolveDocumentSessionId(docObj, fileId, index);
                    docs.Add(new UploadContextDoc(documentSessionId, fileId, fileName, blobName, analysis));
                    index++;
                }
                if (activeDocumentSessionId is null && docs.Count > 0)
                {
                    activeDocumentSessionId = docs[0].DocumentSessionId;
                }
            }
        }

        if (docs.Count == 0)
        {
            var fallbackId = $"legacy_{createdAtOffset.ToUnixTimeMilliseconds()}";
            docs.Add(new UploadContextDoc(fallbackId, null, null, null, null));
            activeDocumentSessionId ??= fallbackId;
        }

        return new UploadContext(kind ?? "user.upload", createdAtOffset, docs, activeDocumentSessionId);
    }

    private static List<Dictionary<string, object?>> BuildInitialMessages(
        string companyCode,
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        IReadOnlyList<AgentAccountingRuleService.AccountingRule>? rules,
        IReadOnlyList<(string Role, string Content)> history,
        IDictionary<string, string?>? tokens,
        string language)
    {
        var systemPrompt = BuildSystemPrompt(companyCode, scenarios, rules, language);
        var messages = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };

        foreach (var scenario in scenarios)
        {
            foreach (var ctxMsg in ExtractContextMessages(scenario))
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = ctxMsg.Role,
                    ["content"] = ApplyTokens(ctxMsg.Resolve(language), tokens)
                });
            }
        }

        foreach (var (role, content) in history)
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = content
            });
        }
        return messages;
    }

    private static string BuildSystemPrompt(
        string companyCode,
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        IReadOnlyList<AgentAccountingRuleService.AccountingRule>? rules,
        string language)
    {
        var sb = new StringBuilder();
        /*
        var jaRules = new[]
        {
            // sanitized
            $"会社コー�? {companyCode}",
            "業務ルール：",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            "8. 【重要��fileId �?documentSessionId は別物です：",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        };
        var zhRules = new[]
        {
            // sanitized
            $"公司代码: {companyCode}",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            "   - 系统消息中会提供「fileId=xxx；docSessionId=yyy」，两��不要混淆！",
            // sanitized
        };
        var rulesLines = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zhRules : jaRules;
        */
        var jaRules = new[]
        {
            // sanitized
            $"会社コー�? {companyCode}",
            "業務ルール：",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            "8. 【重要��fileId �?documentSessionId は別物です：",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        };
        var zhRules = new[]
        {
            // sanitized
            $"公司代码: {companyCode}",
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        };
        var rulesLines = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zhRules : jaRules;
        foreach (var line in rulesLines)
        {
            sb.AppendLine(line);
        }

        if (rules is { Count: > 0 })
        {
            sb.AppendLine();
            if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("��˾�Զ���Ʊ��ָ���������ȼ����������Ȳο�����");
            }
            else
            {
                sb.AppendLine("������Ф����U�����ɣ����ȶ�혁9�9ԓ��������Ϥσ��ȵĤ˲��դ��Ƥ�����������");
            }
            var index = 1;
            foreach (var rule in rules.Where(r => r.IsActive).Take(20))
            {
                var line = new StringBuilder();
                line.Append(index++).Append('.').Append(' ').Append(rule.Title);
                var keywords = rule.Keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToArray();
                if (keywords is { Length: > 0 })
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "���ؼ��ʣ�" : "�����`��`�ɣ�").Append(string.Join(" / ", keywords));
                }
                if (!string.IsNullOrWhiteSpace(rule.AccountCode) || !string.IsNullOrWhiteSpace(rule.AccountName))
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "���Ƽ��跽��" : "���ƊX�跽��");
                    if (!string.IsNullOrWhiteSpace(rule.AccountCode))
                    {
                        line.Append(rule.AccountCode!.Trim());
                    }
                    if (!string.IsNullOrWhiteSpace(rule.AccountName))
                    {
                        if (!string.IsNullOrWhiteSpace(rule.AccountCode)) line.Append(' ');
                        line.Append(rule.AccountName!.Trim());
                    }
                }
                if (!string.IsNullOrWhiteSpace(rule.Note))
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "�����鱸ע��" : "���俼���a��").Append(rule.Note!.Trim());
                }
                if (!string.IsNullOrWhiteSpace(rule.Description))
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "；说明：" : "；説明：").Append(rule.Description!.Trim());
                }
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                ? "����Ʊ��ʱ��ƥ������ģʽ�������ȵ��� lookup_account �ȹ���ȷ�Ͽ�Ŀ���룻���Բ�ȷ����Ӧ���û�ȷ�϶��������Լ��衣"
                : "�^�{����ӛ�ѥ��`���ԓ��������Ϥϡ�lookup_account �ʤɤΥĩ`��ǿ�Ŀ���`�ɤ�_�J��������Ǥⲻ���ʤȤ��τ��֤��Ɯy���������ߤش_�J���Ƥ���������");
        }

        if (scenarios.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("");
            var ordered = scenarios.OrderBy(s => s.Priority).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var scenario = ordered[i];
                sb.AppendLine($"{i + 1}. [{scenario.ScenarioKey}] {scenario.Title}");
                if (!string.IsNullOrWhiteSpace(scenario.Description))
                {
                    sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                        ? $"   描述：{scenario.Description.Trim()}"
                        : $"   説明：{scenario.Description.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(scenario.Instructions))
                {
                    var instructions = scenario.Instructions.Trim().Replace("\r\n", "\n");
                    foreach (var line in instructions.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                            ? $"   指引：{line.Trim()}"
                            : $"   ガイド：{line.Trim()}");
                    }
                }
                var hints = scenario.ToolHints?
                    .Select(node => node is JsonValue value && value.TryGetValue<string>(out var hint) ? hint?.Trim() : null)
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .ToArray() ?? Array.Empty<string>();
                if (hints.Length > 0)
                {
                    sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                        ? $"   推荐工具：{string.Join(" / ", hints)}"
                        : $"   推奨ツール：{string.Join(" / ", hints)}");
                }
            }
        }

        return sb.ToString();
    }

    private sealed record DocumentLabelEntry(string SessionId, string Label, string DisplayName, string? Highlight, string PrimaryFileId);

    private static int DetermineDocumentLabelStart(IEnumerable<InvoiceTaskService.InvoiceTask> existingTasks)
    {
        var max = 0;
        foreach (var task in existingTasks)
        {
            if (string.IsNullOrWhiteSpace(task.DocumentLabel)) continue;
            var label = task.DocumentLabel.Trim();
            if (label.StartsWith('#'))
            {
                label = label[1..];
            }
            if (int.TryParse(label, out var value) && value > max)
            {
                max = value;
            }
        }
        return max + 1;
    }

    private static List<DocumentLabelEntry> BuildDocumentLabelEntries<T>(
        IEnumerable<T> documents,
        Func<T, string?> sessionSelector,
        Func<T, string?> nameSelector,
        Func<T, JsonObject?> analysisSelector,
        Func<T, string?> fileIdSelector,
        string language,
        int startIndex = 1)
    {
        var entries = new List<DocumentLabelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = startIndex < 1 ? 1 : startIndex;
        foreach (var doc in documents)
        {
            var sessionId = sessionSelector(doc);
            if (string.IsNullOrWhiteSpace(sessionId)) continue;
            if (!seen.Add(sessionId!)) continue;
            var label = $"#{index++}";
            var displayName = nameSelector(doc);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = sessionId!;
            }
            var highlight = BuildAnalysisHighlight(analysisSelector(doc), language);
            var fileId = fileIdSelector(doc);
            if (string.IsNullOrWhiteSpace(fileId)) continue;
            entries.Add(new DocumentLabelEntry(sessionId!, label, displayName!, highlight, fileId!));
        }
        return entries;
    }

    private static string? BuildAnalysisHighlight(JsonObject? analysis, string language)
    {
        if (analysis is null) return null;
        var parts = new List<string>();
        if (TryGetJsonString(analysis, "partnerName", out var partner) && !string.IsNullOrWhiteSpace(partner))
        {
            parts.Add(Localize(language, $"ȡ����: {partner}", $"��Ӧ��: {partner}"));
        }
        if (TryGetJsonString(analysis, "issueDate", out var issueDate) && !string.IsNullOrWhiteSpace(issueDate))
        {
            parts.Add(Localize(language, $"�ո�: {issueDate}", $"����: {issueDate}"));
        }
        var amount = TryGetJsonDecimal(analysis, "totalAmount");
        if (amount.HasValue && amount.Value > 0)
        {
            parts.Add(Localize(language, $"���~: {amount.Value:0.##}", $"���: {amount.Value:0.##}"));
        }
        if (parts.Count == 0) return null;
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
            ? string.Join("��", parts)
            : string.Join(" / ", parts);
    }

    private static bool TryGetJsonString(JsonObject obj, string property, out string? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue jsonValue) return false;
        if (!jsonValue.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str)) return false;
        value = str.Trim();
        return true;
    }

    private static decimal? TryGetJsonDecimal(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<decimal>(out var dec)) return dec;
        if (jsonValue.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
        if (jsonValue.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
        return null;
    }

    private static bool IsPlaceholderRegistrationNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return string.Equals(normalized, "T1234567890123", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "1234567890123", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccountingPeriodNotOpen(string message, out string? yearMonth)
    {
        yearMonth = null;
        if (string.IsNullOrWhiteSpace(message)) return false;
        // Typical messages from FinanceService:
        // - ����λ�Ӌ���g��2023-11�����_���Ƥ��ޤ��󡣻�Ӌ���g���_���Ƥ�����ԇ�Ф��Ƥ���������
        // - ����λ�Ӌ���g��2023-11�����]�i����Ƥ��ޤ���
        if (!message.Contains("��Ӌ���g", StringComparison.OrdinalIgnoreCase)) return false;
        if (!(message.Contains("�_���Ƥ��ޤ���", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("�]�i", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("closed", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var m = Regex.Match(message, @"��Ӌ���g\(?([0-9]{4}-[0-9]{2})\)?");
        if (m.Success) yearMonth = m.Groups[1].Value;
        return true;
    }

    private static string? NormalizePostingDateAnswer(string answer, string? fallbackExistingPostingDate)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var text = answer.Trim();

        // 1) yyyy-MM-dd / yyyy/M/d
        var iso = Regex.Match(text, @"\b([0-9]{4})[-/\.]([0-9]{1,2})[-/\.]([0-9]{1,2})\b");
        if (iso.Success)
        {
            var y = int.Parse(iso.Groups[1].Value);
            var m = int.Parse(iso.Groups[2].Value);
            var d = int.Parse(iso.Groups[3].Value);
            return new DateTime(y, m, d).ToString("yyyy-MM-dd");
        }

        // sanitized
        if (jp.Success)
        {
            var y = int.Parse(jp.Groups[1].Value);
            var m = int.Parse(jp.Groups[2].Value);
            var d = int.Parse(jp.Groups[3].Value);
            return new DateTime(y, m, d).ToString("yyyy-MM-dd");
        }

        // 3) yyyy-MM (use existing day if possible)
        var ym = Regex.Match(text, @"\b([0-9]{4})[-/\.]([0-9]{1,2})\b");
        // sanitized
        if (ym.Success)
        {
            var y = int.Parse(ym.Groups[1].Value);
            var m = int.Parse(ym.Groups[2].Value);
            var day = 1;
            if (!string.IsNullOrWhiteSpace(fallbackExistingPostingDate) &&
                DateTime.TryParse(fallbackExistingPostingDate.Trim(), out var existing))
            {
                day = Math.Clamp(existing.Day, 1, DateTime.DaysInMonth(y, m));
            }
            return new DateTime(y, m, day).ToString("yyyy-MM-dd");
        }

        return null;
    }

    private static string? ResolveRegistrationNoFromContext(AgentExecutionContext context)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(context.DefaultFileId))
        {
            candidates.Add(context.DefaultFileId);
        }
        if (!string.IsNullOrWhiteSpace(context.ActiveDocumentSessionId))
        {
            foreach (var fileId in context.GetFileIdsByDocumentSession(context.ActiveDocumentSessionId))
            {
                candidates.Add(fileId);
            }
        }
        foreach (var sessionId in context.DocumentSessionLabels.Keys)
        {
            foreach (var fileId in context.GetFileIdsByDocumentSession(sessionId))
            {
                candidates.Add(fileId);
            }
        }
        foreach (var fileId in context.GetRegisteredFileIds())
        {
            candidates.Add(fileId);
        }
        foreach (var fileId in candidates)
        {
            if (context.TryGetDocument(fileId, out var doc) && doc is JsonObject obj)
            {
                if (TryGetJsonString(obj, "invoiceRegistrationNo", out var reg) && !string.IsNullOrWhiteSpace(reg))
                {
                    return reg;
                }
            }
        }
        return null;
    }

    private static Func<string, UploadedFileRecord?> CreateTaskFileResolver(InvoiceTaskService.InvoiceTask task, Auth.UserCtx userCtx)
    {
        string? storedPath = task.StoredPath;
        if (string.IsNullOrWhiteSpace(storedPath) &&
            task.Metadata is not null &&
            task.Metadata.TryGetPropertyValue("storedPath", out var storedPathNode) &&
            storedPathNode is JsonValue storedValue &&
            storedValue.TryGetValue<string>(out var path) &&
            !string.IsNullOrWhiteSpace(path))
        {
            storedPath = path.Trim();
        }
        var contentType = string.IsNullOrWhiteSpace(task.ContentType) ? "application/octet-stream" : task.ContentType;
        return fileId =>
        {
            // 容错：允许用 fileId �?blobName 解析文件
            if (!string.Equals(fileId, task.FileId, StringComparison.OrdinalIgnoreCase) &&
                !( !string.IsNullOrWhiteSpace(task.BlobName) && string.Equals(fileId, task.BlobName, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            var analysisClone = task.Analysis is not null ? task.Analysis.DeepClone().AsObject() : null;
            return new UploadedFileRecord(
                task.FileName,
                storedPath ?? string.Empty,
                contentType ?? "application/octet-stream",
                task.Size,
                task.CreatedAt,
                task.CompanyCode,
                userCtx.UserId,
                task.BlobName ?? string.Empty)
            {
                Analysis = analysisClone
            };
        };
    }

    private IReadOnlyList<AgentScenarioService.AgentScenario> SelectScenariosForMessage(
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        string? scenarioKey,
        string? message)
    {
        if (!string.IsNullOrWhiteSpace(scenarioKey))
        {
            var forced = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, scenarioKey, StringComparison.OrdinalIgnoreCase));
            if (forced is not null)
            {
                return new[] { forced };
            }
        }

        if (TryForceSalesOrderScenario(message, scenarios, out var forcedScenario))
        {
            return forcedScenario ?? Array.Empty<AgentScenarioService.AgentScenario>();
        }

        var result = new List<(AgentScenarioService.AgentScenario Scenario, ScenarioMetadata Metadata)>();
        foreach (var scenario in scenarios)
        {
            var metadata = ParseScenarioMetadata(scenario);
            if (metadata.AppliesTo == ScenarioTarget.FileOnly)
            {
                continue;
            }
            if (metadata.MatchesMessage(message))
            {
                result.Add((scenario, metadata));
            }
        }

        if (result.Count == 0)
        {
            foreach (var scenario in scenarios)
            {
                var metadata = ParseScenarioMetadata(scenario);
                if (metadata.AppliesTo != ScenarioTarget.FileOnly && metadata.Always)
                {
                    result.Add((scenario, metadata));
                }
            }
        }

        var ordered = result
            .OrderBy(r => r.Scenario.Priority)
            .ThenByDescending(r => r.Scenario.UpdatedAt)
            .Select(r => r.Scenario)
            .ToArray();

        return ordered;
    }

    private IReadOnlyList<AgentScenarioService.AgentScenario> SelectScenariosForFile(
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        string? scenarioKey,
        FileMatchContext file)
    {
        if (!string.IsNullOrWhiteSpace(scenarioKey))
        {
            var forced = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, scenarioKey, StringComparison.OrdinalIgnoreCase));
            if (forced is not null)
            {
                return new[] { forced };
            }
        }

        var result = new List<(AgentScenarioService.AgentScenario Scenario, ScenarioMetadata Metadata)>();
        foreach (var scenario in scenarios)
        {
            var metadata = ParseScenarioMetadata(scenario);
            if (metadata.AppliesTo == ScenarioTarget.MessageOnly)
            {
                continue;
            }
            if (metadata.MatchesFile(file))
            {
                result.Add((scenario, metadata));
            }
        }

        if (result.Count == 0)
        {
            foreach (var scenario in scenarios)
            {
                var metadata = ParseScenarioMetadata(scenario);
                if (metadata.AppliesTo != ScenarioTarget.MessageOnly && metadata.Always)
                {
                    result.Add((scenario, metadata));
                }
            }
        }

        static bool MatchesFilter(FileMatchContext fileContext, ScenarioFilter filter)
        {
            if (filter is null) return true;
            if (fileContext.ParsedData is null) return false;
            if (!fileContext.ParsedData.TryGetPropertyValue(filter.Field, out var valueNode)) return false;
            if (valueNode is JsonValue value && value.TryGetValue<string>(out var str))
            {
                return string.Equals(str?.Trim(), filter.Value, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        var filtered = result
            .Where(entry => entry.Metadata.Filter is null || MatchesFilter(file, entry.Metadata.Filter))
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = result.Where(entry => entry.Metadata.Filter is null).ToList();
        }

        if (filtered.Count == 0)
        {
            filtered = result;
        }

        return filtered
            .OrderBy(r => r.Scenario.Priority)
            .ThenByDescending(r => r.Scenario.UpdatedAt)
            .Select(r => r.Scenario)
            .ToArray();
    }

    private static IEnumerable<ScenarioContextMessage> ExtractContextMessages(AgentScenarioService.AgentScenario scenario)
    {
        var metadata = ParseScenarioMetadata(scenario);
        return metadata.ContextMessages;
    }

    internal static bool TryForceSalesOrderScenario(
        string? message,
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        out AgentScenarioService.AgentScenario[]? forced)
    {
        forced = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        // sanitized
        {
            return false;
        }

        var matched = scenarios.FirstOrDefault(s =>
            string.Equals(s.ScenarioKey, "sales_order", StringComparison.OrdinalIgnoreCase));

        AgentScenarioService.AgentScenario forcedScenario;
        if (matched is null)
        {
            forcedScenario = BuildInlineSalesOrderScenario();
        }
        else
        {
            var metadata = ParseScenarioMetadata(matched);
            if (metadata.AppliesTo == ScenarioTarget.FileOnly)
            {
                return false;
            }

            forcedScenario = EnhanceSalesOrderScenario(matched);
        }

        forced = new[] { forcedScenario };
        return true;
    }

    private static AgentScenarioService.AgentScenario BuildInlineSalesOrderScenario()
    {
        var metadata = new JsonObject();

        var toolHints = new JsonArray();

        var instructions = string.Join('\n', new[]
        {
            // sanitized
            // sanitized
            // sanitized
            // sanitized
            // sanitized
        });

        return new AgentScenarioService.AgentScenario(
            Guid.NewGuid(),
            "sales_order",
            "受注登録",
            // sanitized
            instructions,
            metadata,
            toolHints,
            null,
            40,
            true,
            DateTime.UtcNow);
    }

    private static AgentScenarioService.AgentScenario EnhanceSalesOrderScenario(AgentScenarioService.AgentScenario scenario)
    {
        var metadata = CloneJsonObject(scenario.Metadata);
        var toolHints = CloneJsonArray(scenario.ToolHints);

        EnsureToolHint(toolHints, "lookup_customer");
        EnsureToolHint(toolHints, "lookup_material");
        EnsureToolHint(toolHints, "create_sales_order");


        EnsureSalesOrderContext(metadata);

        var instructions = scenario.Instructions ?? string.Empty;
        if (!instructions.Contains("create_sales_order", StringComparison.OrdinalIgnoreCase))
        {
            var appendix = string.Join('\n', new[]
            {
                string.IsNullOrWhiteSpace(instructions) ? string.Empty : string.Empty,
                // sanitized
                // sanitized
                // sanitized
                // sanitized
            });
            instructions = string.IsNullOrWhiteSpace(instructions)
                ? appendix.Trim()
                : (instructions.TrimEnd() + "\n\n" + appendix.Trim());
        }

        return scenario with
        {
            Instructions = instructions,
            Metadata = metadata,
            ToolHints = toolHints,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static void EnsureSalesOrderContext(JsonObject metadata)
    {
        if (metadata is null)
        {
            return;
        }

        if (!metadata.TryGetPropertyValue("matcher", out var matcherNode) || matcherNode is not JsonObject matcherObj)
        {
            matcherObj = new JsonObject();
            metadata["matcher"] = matcherObj;
        }

        matcherObj["appliesTo"] = "message";
        matcherObj["always"] = false;

        if (!matcherObj.TryGetPropertyValue("messageContains", out var containsNode) || containsNode is not JsonArray containsArray || containsArray.Count == 0)
        {
            containsArray = new JsonArray();
            foreach (var keyword in new[] { "受注", "注文", "sales order", "受注登録", "下单", "订单" })
            {
                containsArray.Add(keyword);
            }
            matcherObj["messageContains"] = containsArray;
        }

        if (!metadata.TryGetPropertyValue("contextMessages", out var ctxNode) || ctxNode is not JsonArray ctxArray)
        {
            ctxArray = new JsonArray();
            metadata["contextMessages"] = ctxArray;
        }

        var hasGuideline = false;
        foreach (var entry in ctxArray)
        {
            if (entry is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("content", out var contentNode))
                {
                    switch (contentNode)
                    {
                        case JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                            if (text.Contains("受注処理ガイドライン", StringComparison.OrdinalIgnoreCase) || text.Contains("受注登録", StringComparison.OrdinalIgnoreCase))
                            {
                                hasGuideline = true;
                            }
                            break;
                        case JsonObject contentObj:
                            if (contentObj.TryGetPropertyValue("ja", out var jaNode) && jaNode is JsonValue jaVal && jaVal.TryGetValue<string>(out var jaText) && jaText.Contains("受注処理ガイドライン", StringComparison.OrdinalIgnoreCase))
                            {
                                hasGuideline = true;
                            }
                            if (contentObj.TryGetPropertyValue("zh", out var zhNode) && zhNode is JsonValue zhVal && zhVal.TryGetValue<string>(out var zhText) && zhText.Contains("受注订单", StringComparison.OrdinalIgnoreCase))
                            {
                                hasGuideline = true;
                            }
                            break;
                    }
                }
            }

            if (hasGuideline)
            {
                break;
            }
        }

        if (!hasGuideline)
        {
            var guideline = new JsonObject
            {
                ["role"] = "system",
                ["content"] = new JsonObject
                {
                    ["ja"] = "If user does not provide order/ship dates, keep relative terms like today/tomorrow and ask via request_clarification.",
                    ["zh"] = "If dates are missing, keep relative terms like today/tomorrow and ask via request_clarification."
                }
            };
            ctxArray.Add(guideline);
        }

        var hasDatePolicy = false;

        if (!hasDatePolicy)
        {
            var datePolicy = new JsonObject
            {
                ["role"] = "system",
                ["content"] = new JsonObject
                {
                    ["ja"] = "If user does not provide order/ship dates, keep relative terms like today/tomorrow and ask via request_clarification.",
                    ["zh"] = "If dates are missing, keep relative terms like today/tomorrow and ask via request_clarification."
                }
            };
            ctxArray.Add(datePolicy);
        }
    }

    private static JsonObject CloneJsonObject(JsonObject? source)
    {
        if (source is null) return new JsonObject();
        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? new JsonObject();
    }

    private static JsonArray CloneJsonArray(JsonArray? source)
    {
        if (source is null) return new JsonArray();
        return JsonNode.Parse(source.ToJsonString())?.AsArray() ?? new JsonArray();
    }

    private static void EnsureToolHint(JsonArray hints, string value)
    {
        var exists = hints
            .OfType<JsonValue>()
            .Any(val => val.TryGetValue<string>(out var str) && string.Equals(str, value, StringComparison.OrdinalIgnoreCase));

        if (!exists)
        {
            hints.Add(value);
        }
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var key in keywords)
        {
            if (text.Contains(key))
            {
                return true;
            }
        }
        return false;
    }

    private static string ApplyTokens(string content, IDictionary<string, string?>? tokens)
    {
        if (string.IsNullOrEmpty(content) || tokens is null || tokens.Count == 0)
        {
            return content;
        }
        var result = content;
        foreach (var kvp in tokens)
        {
            if (string.IsNullOrEmpty(kvp.Key)) continue;
            var placeholder = "{{" + kvp.Key + "}}";
            result = result.Replace(placeholder, kvp.Value ?? string.Empty);
        }
        return result;
    }

    private static string DetermineOrderDate(string? raw, DateTimeOffset now)
    {
        var normalized = NormalizeDateString(raw, now);
        if (!string.IsNullOrWhiteSpace(normalized) && DateTime.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            if (parsed >= now.Date.AddDays(-30))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? DetermineDeliveryDate(string? normalized, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (DateTime.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            if (parsed < now.Date.AddDays(-30))
            {
                return null;
            }
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return normalized;
    }

    private sealed record TaskSnapshotEntry(
        string Kind,
        Guid TaskId,
        string? DisplayLabel,
        string SequenceLabel,
        string Status,
        string? DocumentLabel,
        string? DocumentSessionId,
        string? FileId,
        string? FileName,
        string? Summary,
        string? SalesOrderNo,
        string? CustomerName,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public bool IsCompleted =>
            string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<TaskSnapshotEntry>> LoadTaskSnapshotAsync(Guid sessionId, CancellationToken ct)
    {
        var invoiceTasks = await _invoiceTaskService.ListAsync(sessionId, ct);
        var salesTasks = await _salesOrderTaskService.ListAsync(sessionId, ct);
        var combined = new List<(string Kind, DateTimeOffset CreatedAt, Guid TaskId, InvoiceTaskService.InvoiceTask? Invoice, SalesOrderTaskService.SalesOrderTask? Sales)>(invoiceTasks.Count + salesTasks.Count);
        combined.AddRange(invoiceTasks.Select(t => (
            Kind: "invoice",
            CreatedAt: t.CreatedAt,
            TaskId: t.Id,
            Invoice: (InvoiceTaskService.InvoiceTask?)t,
            Sales: (SalesOrderTaskService.SalesOrderTask?)null)));
        combined.AddRange(salesTasks.Select(t => (
            Kind: "sales_order",
            CreatedAt: t.CreatedAt,
            TaskId: t.Id,
            Invoice: (InvoiceTaskService.InvoiceTask?)null,
            Sales: (SalesOrderTaskService.SalesOrderTask?)t)));
        var ordered = combined
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.TaskId)
            .ToList();

        var result = new List<TaskSnapshotEntry>(ordered.Count);
        var sequence = 1;
        foreach (var item in ordered)
        {
            var sequenceLabel = $"#{sequence++}";
            if (item.Kind == "invoice")
            {
                var invoice = item.Invoice!;
                var displayLabel = string.IsNullOrWhiteSpace(invoice.DocumentLabel) ? sequenceLabel : invoice.DocumentLabel;
                result.Add(new TaskSnapshotEntry(
                    "invoice",
                    invoice.Id,
                    displayLabel,
                    sequenceLabel,
                    invoice.Status,
                    invoice.DocumentLabel,
                    invoice.DocumentSessionId,
                    invoice.FileId,
                    invoice.FileName,
                    invoice.Summary,
                    null,
                    null,
                    invoice.CreatedAt,
                    invoice.UpdatedAt));
            }
            else
            {
                var sales = item.Sales!;
                result.Add(new TaskSnapshotEntry(
                    "sales_order",
                    sales.Id,
                    sequenceLabel,
                    sequenceLabel,
                    sales.Status,
                    null,
                    null,
                    null,
                    null,
                    sales.Summary,
                    sales.SalesOrderNo,
                    sales.CustomerName,
                    sales.CreatedAt,
                    sales.UpdatedAt));
            }
        }

        return result;
    }

    /// <summary>
    /// 从用户消息中推断任务 ID�?
    /// 【任务推断规则��（按优先级排序）：
    /// 1. 前端选中的文�?(activeDocumentSessionId) 直接匹配
    /// 2. 消息中的任务标签 (#1, #2 ...) 精确匹配
    /// 3. 消息中的关键信息（订单号、客户名、文件名等）模糊匹配
    /// 4. 无法确定则返�?null，创建独立对话（不猜测）
    /// </summary>
    private static (Guid? TaskId, string? MatchReason) InferTaskIdFromMessageWithReason(string? message, IReadOnlyList<TaskSnapshotEntry> snapshot, string? activeDocumentSessionId)
    {
        if (snapshot.Count == 0) return (null, "no tasks");
        var pending = snapshot.Where(entry => !entry.IsCompleted).ToList();
        if (pending.Count == 0) return (null, "no pending tasks");

        if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            var match = pending.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.DocumentSessionId) &&
                string.Equals(entry.DocumentSessionId, activeDocumentSessionId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return (match.TaskId, $"active document {match.SequenceLabel}");
            }
        }

        var labelTokens = ExtractTaskLabels(message);
        if (labelTokens.Count > 0)
        {
            foreach (var token in labelTokens)
            {
                var labelMatch = pending.FirstOrDefault(entry =>
                    (!string.IsNullOrWhiteSpace(entry.DisplayLabel) && string.Equals(entry.DisplayLabel, token, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(entry.SequenceLabel, token, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(entry.DocumentLabel) && string.Equals(entry.DocumentLabel, token, StringComparison.OrdinalIgnoreCase)));
                if (labelMatch is not null)
                {
                    return (labelMatch.TaskId, $"message label {token}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            var lowered = message.ToLowerInvariant();
            foreach (var entry in pending)
            {
                if (!string.IsNullOrWhiteSpace(entry.SalesOrderNo) && lowered.Contains(entry.SalesOrderNo.ToLowerInvariant()))
                {
                    return (entry.TaskId, $"order no {entry.SalesOrderNo}");
                }
                if (!string.IsNullOrWhiteSpace(entry.CustomerName) && lowered.Contains(entry.CustomerName.ToLowerInvariant()))
                {
                    return (entry.TaskId, $"customer {entry.CustomerName}");
                }
                if (!string.IsNullOrWhiteSpace(entry.FileName) && lowered.Contains(entry.FileName.ToLowerInvariant()))
                {
                    return (entry.TaskId, $"file {entry.FileName}");
                }
            }
        }

        return (null, "no match");
    }

    private static Guid? InferTaskIdFromMessage(string? message, IReadOnlyList<TaskSnapshotEntry> snapshot, string? activeDocumentSessionId)
    {
        var (taskId, _) = InferTaskIdFromMessageWithReason(message, snapshot, activeDocumentSessionId);
        return taskId;
    }

    private static IReadOnlyList<string> ExtractTaskLabels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var matches = Regex.Matches(text, @"#\d+");
        if (matches.Count == 0) return Array.Empty<string>();
        return matches
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildTaskDecisionHint(string language, IReadOnlyList<TaskSnapshotEntry> snapshot, Guid? activeTaskId)
    {
        if (snapshot.Count == 0) return string.Empty;
        var pending = snapshot.Where(entry => !entry.IsCompleted).ToList();
        if (pending.Count == 0) return string.Empty;

        const int maxLines = 8;
        var sb = new StringBuilder();
        sb.AppendLine("Pending tasks:");

        foreach (var entry in pending.Take(maxLines))
        {
            var marker = entry.TaskId == activeTaskId ? ">" : " ";
            var label = string.IsNullOrWhiteSpace(entry.DisplayLabel) ? entry.SequenceLabel : entry.DisplayLabel;
            var status = string.IsNullOrWhiteSpace(entry.Status) ? "pending" : entry.Status;
            if (string.Equals(entry.Kind, "invoice", StringComparison.OrdinalIgnoreCase))
            {
                var docLabel = entry.DocumentLabel ?? "-";
                var fileId = entry.FileId ?? "-";
                var docSession = entry.DocumentSessionId ?? "-";
                sb.AppendLine($"{marker} {label} [invoice] status={status} group={docLabel} fileId={fileId} docSession={docSession} taskId={entry.TaskId}");
            }
            else
            {
                var soNo = entry.SalesOrderNo ?? "-";
                var customer = entry.CustomerName ?? "-";
                sb.AppendLine($"{marker} {label} [sales] status={status} orderNo={soNo} customer={customer} taskId={entry.TaskId}");
            }
        }

        if (pending.Count > maxLines)
        {
            sb.AppendLine($"... remaining {pending.Count - maxLines} items");
        }

        sb.AppendLine("If related to any task above, continue that task and include taskId when calling tools. Only create a new task for truly new requests.");

        return sb.ToString().TrimEnd();
    }

    private static string? NormalizeDateString(string? value, DateTimeOffset? reference = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var lower = trimmed.ToLowerInvariant();
        var refDate = reference ?? DateTimeOffset.UtcNow;

        if (MatchesRelativeDate(lower, refDate, out var relative))
        {
            return relative;
        }

        var cultures = new[]
        {
            CultureInfo.InvariantCulture,
            CultureInfo.GetCultureInfo("ja-JP"),
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("zh-TW"),
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("en-GB")
        };
        foreach (var culture in cultures)
        {
            if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        return trimmed;
    }

    private static bool MatchesRelativeDate(string text, DateTimeOffset reference, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        static bool ContainsAny(string source, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (source.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // sanitized
        {
            result = reference.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        if (ContainsAny(text, "tomorrow", "あし�?, "あす", "明日", "翌日", "次の�?, "翌朝", "翌晩", "明天"))
        {
            result = reference.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // sanitized
        {
            result = reference.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // sanitized
        {
            result = reference.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static ScenarioMetadata ParseScenarioMetadata(AgentScenarioService.AgentScenario scenario)
    {
        var metadataRoot = scenario.Metadata ?? new JsonObject();

        JsonObject? matcher = null;
        if (metadataRoot.TryGetPropertyValue("matcher", out var matcherNode) && matcherNode is JsonObject matcherObj)
        {
            matcher = matcherObj;
        }

        var appliesTo = ScenarioTarget.Both;
        var always = true;
        if (matcher is not null)
        {
            always = ReadBool(matcher, "always") ?? false;
            var appliesStr = ReadString(matcher, "appliesTo")?.Trim().ToLowerInvariant();
            appliesTo = appliesStr switch
            {
                "message" => ScenarioTarget.MessageOnly,
                "file" => ScenarioTarget.FileOnly,
                _ => ScenarioTarget.Both
            };
        }

        var messageContains = ReadStringArray(matcher, "messageContains", toLower: true);
        var messageExcludes = ReadStringArray(matcher, "messageExcludes", toLower: true);
        var messageRegex = ReadStringArray(matcher, "messageRegex", toLower: false);
        var fileNameContains = ReadStringArray(matcher, "fileNameContains", toLower: true);
        var contentContains = ReadStringArray(matcher, "contentContains", toLower: true);
        var mimeTypes = ReadStringArray(matcher, "mimeTypes", toLower: true);

        var contextMessages = new List<ScenarioContextMessage>();
        if (metadataRoot.TryGetPropertyValue("contextMessages", out var contextNode) && contextNode is JsonArray contextArray)
        {
            foreach (var item in contextArray)
            {
                if (item is JsonObject ctxObj)
                {
                    var role = ReadString(ctxObj, "role")?.Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(role)) role = "system";
                    if (role != "system" && role != "user" && role != "assistant")
                    {
                        role = "system";
                    }

                    string? contentJa = null;
                    string? contentZh = null;
                    if (ctxObj.TryGetPropertyValue("content", out var contentNode))
                    {
                        if (contentNode is JsonValue contentValue && contentValue.TryGetValue<string>(out var contentStr))
                        {
                            contentJa = contentStr.Trim();
                            contentZh = contentStr.Trim();
                        }
                        else if (contentNode is JsonObject contentObj)
                        {
                            contentJa = ReadString(contentObj, "ja")?.Trim() ?? contentJa;
                            contentZh = ReadString(contentObj, "zh")?.Trim() ?? contentZh;
                        }
                    }
                    if (ctxObj.TryGetPropertyValue("contentJa", out var jaNode) && jaNode is JsonValue jaVal && jaVal.TryGetValue<string>(out var jaStr))
                    {
                        contentJa = jaStr.Trim();
                    }
                    if (ctxObj.TryGetPropertyValue("contentZh", out var zhNode) && zhNode is JsonValue zhVal && zhVal.TryGetValue<string>(out var zhStr))
                    {
                        contentZh = zhStr.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(contentJa) && string.IsNullOrWhiteSpace(contentZh))
                    {
                        var fallback = ReadString(ctxObj, "content")?.Trim();
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            contentJa = fallback;
                            contentZh = fallback;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(contentJa) || !string.IsNullOrWhiteSpace(contentZh))
                    {
                        var defaultContent = contentJa ?? contentZh ?? string.Empty;
                        contextMessages.Add(new ScenarioContextMessage(role, defaultContent, contentZh));
                    }
                }
                else if (item is JsonValue value && value.TryGetValue<string>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
                {
                    var trimmed = strVal.Trim();
                    contextMessages.Add(new ScenarioContextMessage("system", trimmed, trimmed));
                }
            }
        }

        ScenarioFilter? filter = null;
        if (metadataRoot.TryGetPropertyValue("filter", out var filterNode) && filterNode is JsonObject filterObj)
        {
            var field = ReadString(filterObj, "field");
            var equalsValue = ReadString(filterObj, "equals");
            if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(equalsValue))
            {
                filter = new ScenarioFilter(field.Trim(), equalsValue.Trim());
            }
        }

        return new ScenarioMetadata(
            appliesTo,
            always || matcher is null,
            messageContains,
            messageExcludes,
            messageRegex,
            fileNameContains,
            contentContains,
            mimeTypes,
            contextMessages,
            filter);
    }

    private static ScenarioExecutionHints? ExtractExecutionHints(AgentScenarioService.AgentScenario scenario)
    {
        var metadataRoot = scenario.Metadata ?? new JsonObject();
        if (!metadataRoot.TryGetPropertyValue("executionHints", out var hintsNode))
        {
            metadataRoot.TryGetPropertyValue("execution_hints", out hintsNode);
        }

        if (hintsNode is not JsonObject hintsObj)
        {
            return null;
        }

        var threshold = ReadDecimal(hintsObj, "netAmountThreshold")
            ?? ReadDecimal(hintsObj, "threshold")
            ?? ReadDecimal(hintsObj, "net_amount_threshold");
        var lowMessage = ReadString(hintsObj, "lowAmountSystemMessage")
            ?? ReadString(hintsObj, "low_amount_system_message");
        var highMessage = ReadString(hintsObj, "highAmountSystemMessage")
            ?? ReadString(hintsObj, "high_amount_system_message");
        var clarificationMessage = ReadString(hintsObj, "clarificationSystemMessage")
            ?? ReadString(hintsObj, "clarification_system_message");
        var perPersonThreshold = ReadDecimal(hintsObj, "perPersonThreshold")
            ?? ReadDecimal(hintsObj, "per_person_threshold");
        var aboveThresholdAccount = ReadString(hintsObj, "aboveThresholdAccount")
            ?? ReadString(hintsObj, "above_threshold_account");
        var belowOrEqualAccount = ReadString(hintsObj, "belowOrEqualAccount")
            ?? ReadString(hintsObj, "below_or_equal_account");

        if (threshold is null && string.IsNullOrWhiteSpace(lowMessage) && string.IsNullOrWhiteSpace(highMessage) && 
            string.IsNullOrWhiteSpace(clarificationMessage) && perPersonThreshold is null)
        {
            return null;
        }

        return new ScenarioExecutionHints(threshold, lowMessage, highMessage, clarificationMessage, 
            perPersonThreshold, aboveThresholdAccount, belowOrEqualAccount);
    }

    private static string? ReadString(JsonObject? obj, string propertyName)
    {
        if (obj is null) return null;
        if (!obj.TryGetPropertyValue(propertyName, out var node)) return null;
        return node is JsonValue value && value.TryGetValue<string>(out var str) ? str : null;
    }

    private static decimal? ReadDecimal(JsonObject? obj, string propertyName)
    {
        if (obj is null) return null;
        if (!obj.TryGetPropertyValue(propertyName, out var node)) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var dec)) return dec;
            if (value.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (value.TryGetValue<float>(out var fl)) return (decimal)fl;
            if (value.TryGetValue<string>(out var str) &&
                decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static bool? ReadBool(JsonObject? obj, string propertyName)
    {
        if (obj is null) return null;
        if (!obj.TryGetPropertyValue(propertyName, out var node)) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<string>(out var str))
            {
                if (bool.TryParse(str, out var parsed)) return parsed;
            }
        }
        return null;
    }

    private static List<string> ReadStringArray(JsonObject? obj, string propertyName, bool toLower)
    {
        var list = new List<string>();
        if (obj is null) return list;
        if (!obj.TryGetPropertyValue(propertyName, out var node)) return list;
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str))
                {
                    var normalized = str.Trim();
                    if (toLower)
                    {
                        normalized = normalized.ToLowerInvariant();
                    }
                    list.Add(normalized);
                }
            }
        }
        else if (node is JsonValue single && single.TryGetValue<string>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
        {
            var normalized = strVal.Trim();
            if (toLower) normalized = normalized.ToLowerInvariant();
            list.Add(normalized);
        }
        return list;
    }

    private sealed record ScenarioExecutionHints(
        decimal? NetAmountThreshold,
        string? LowAmountSystemMessage,
        string? HighAmountSystemMessage,
        string? ClarificationSystemMessage,
        decimal? PerPersonThreshold,
        string? AboveThresholdAccount,
        string? BelowOrEqualAccount);

    private sealed record ScenarioFilter(string Field, string Value);

    private sealed class ScenarioMetadata
    {
        public ScenarioMetadata(
            ScenarioTarget appliesTo,
            bool always,
            IReadOnlyList<string> messageContains,
            IReadOnlyList<string> messageExcludes,
            IReadOnlyList<string> messageRegex,
            IReadOnlyList<string> fileNameContains,
            IReadOnlyList<string> contentContains,
            IReadOnlyList<string> mimeTypes,
            IReadOnlyList<ScenarioContextMessage> contextMessages,
            ScenarioFilter? filter)
        {
            AppliesTo = appliesTo;
            Always = always;
            MessageContains = messageContains;
            MessageExcludes = messageExcludes;
            MessageRegex = messageRegex;
            FileNameContains = fileNameContains;
            ContentContains = contentContains;
            MimeTypes = mimeTypes;
            ContextMessages = contextMessages;
            Filter = filter;
        }

        public ScenarioTarget AppliesTo { get; }
        public bool Always { get; }
        public IReadOnlyList<string> MessageContains { get; }
        public IReadOnlyList<string> MessageExcludes { get; }
        public IReadOnlyList<string> MessageRegex { get; }
        public IReadOnlyList<string> FileNameContains { get; }
        public IReadOnlyList<string> ContentContains { get; }
        public IReadOnlyList<string> MimeTypes { get; }
        public IReadOnlyList<ScenarioContextMessage> ContextMessages { get; }
        public ScenarioFilter? Filter { get; }

        public bool MatchesMessage(string? message)
        {
            if (AppliesTo == ScenarioTarget.FileOnly)
            {
                return false;
            }

            var text = message ?? string.Empty;
            var normalized = text.ToLowerInvariant();

            if (MessageExcludes.Count > 0 && MessageExcludes.Any(ex => normalized.Contains(ex)))
            {
                return false;
            }

            var includeOk = MessageContains.Count == 0 || MessageContains.Any(val => normalized.Contains(val));
            var regexOk = true;
            if (MessageRegex.Count > 0)
            {
                regexOk = false;
                foreach (var pattern in MessageRegex)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    try
                    {
                        if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                        {
                            regexOk = true;
                            break;
                        }
                    }
                    catch
                    {
                        // ignore invalid regex
                    }
                }
            }

            if (includeOk && regexOk)
            {
                return true;
            }

            return Always && MessageExcludes.Count == 0;
        }

        public bool MatchesFile(FileMatchContext file)
        {
            if (AppliesTo == ScenarioTarget.MessageOnly)
            {
                return false;
            }

            var nameLower = (file.FileName ?? string.Empty).ToLowerInvariant();
            var previewLower = (file.Preview ?? string.Empty).ToLowerInvariant();
            var mimeLower = (file.ContentType ?? string.Empty).ToLowerInvariant();
            var parsedLower = file.ParsedData is null ? string.Empty : file.ParsedData.ToJsonString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(parsedLower))
            {
                previewLower = string.IsNullOrEmpty(previewLower)
                    ? parsedLower
                    : previewLower + "\n" + parsedLower;
            }

            if (MimeTypes.Count > 0 && !MimeTypes.Any(mt => string.Equals(mt, mimeLower, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var nameOk = FileNameContains.Count == 0 || FileNameContains.Any(val => nameLower.Contains(val));
            var contentOk = ContentContains.Count == 0 || ContentContains.Any(val => previewLower.Contains(val));

            if (nameOk && contentOk)
            {
                return true;
            }

            return Always;
        }
    }

    private sealed record ScenarioContextMessage(string Role, string DefaultContent, string? ChineseContent = null)
    {
        public string Resolve(string language)
        {
            if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ChineseContent))
            {
                return ChineseContent!;
            }
            return DefaultContent;
        }
    }

    private readonly record struct FileMatchContext(string? FileName, string? ContentType, string? Preview, JsonObject? ParsedData);

    private enum ScenarioTarget
    {
        Both,
        MessageOnly,
        FileOnly
    }

    internal async Task<IReadOnlyList<AgentResultMessage>> ExecuteTaskGroupsAsync(AgentTaskExecutionRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        var allMessages = new List<AgentResultMessage>();
        var scenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();
        var clarification = request.Clarification;
        if (clarification is not null)
        {
            _logger.LogInformation("[AgentKit] 执行任务组时引用用户回答 questionId={QuestionId} documentId={DocumentId}", clarification.QuestionId, clarification.DocumentId);
        }

        string? focusDocumentSessionId = clarification?.DocumentSessionId;
        if (string.IsNullOrWhiteSpace(focusDocumentSessionId))
        {
            focusDocumentSessionId = request.ActiveDocumentSessionId;
        }
        var distinctDocumentSessions = request.Documents
            .Select(d => d.DocumentSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var shouldApplyFocus = !string.IsNullOrWhiteSpace(focusDocumentSessionId) &&
                               (clarification is not null || distinctDocumentSessions <= 1);

        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedDocumentSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var documentLabelEntries = BuildDocumentLabelEntries(request.Documents, d => d.DocumentSessionId, d => d.FileName, d => d.Data, d => d.FileId, language);
        var documentLabelMap = documentLabelEntries.ToDictionary(e => e.SessionId, e => e, StringComparer.OrdinalIgnoreCase);
        if (clarification is not null &&
            string.IsNullOrWhiteSpace(clarification.DocumentLabel) &&
            !string.IsNullOrWhiteSpace(clarification.DocumentSessionId) &&
            documentLabelMap.TryGetValue(clarification.DocumentSessionId, out var clarificationLabel))
        {
            clarification = clarification with { DocumentLabel = clarificationLabel.Label };
        }

        var taskLookupBySession = (request.Tasks ?? Array.Empty<InvoiceTaskInfo>())
            .Where(t => !string.IsNullOrWhiteSpace(t.DocumentSessionId))
            .ToDictionary(t => t.DocumentSessionId, t => t, StringComparer.OrdinalIgnoreCase);
        var taskLookupByFile = (request.Tasks ?? Array.Empty<InvoiceTaskInfo>())
            .Where(t => !string.IsNullOrWhiteSpace(t.FileId))
            .ToDictionary(t => t.FileId, t => t, StringComparer.OrdinalIgnoreCase);

        foreach (var plan in request.Plans)
        {
            InvoiceTaskInfo? taskInfo = null;
            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) &&
                taskLookupBySession.TryGetValue(plan.DocumentSessionId, out var mappedTask))
            {
                taskInfo = mappedTask;
            }
            else
            {
                foreach (var docId in plan.DocumentIds)
                {
                    if (taskLookupByFile.TryGetValue(docId, out var taskByFile))
                    {
                        taskInfo = taskByFile;
                        break;
                    }
                }
            }

            if (shouldApplyFocus && !string.IsNullOrWhiteSpace(focusDocumentSessionId))
            {
                if (string.IsNullOrWhiteSpace(plan.DocumentSessionId) ||
                    !string.Equals(plan.DocumentSessionId, focusDocumentSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[AgentKit] 跳过任务 {ScenarioKey}，因�?documentSessionId={PlanDocumentSessionId} 不在当前焦点 {FocusDocumentSessionId}", plan.ScenarioKey, plan.DocumentSessionId, focusDocumentSessionId);
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) && processedDocumentSessions.Contains(plan.DocumentSessionId))
            {
                var duplicateText = $"Document context {plan.DocumentSessionId} already processed; skipping task {plan.ScenarioKey}.";
                var dupMsg = new AgentResultMessage("assistant", duplicateText, "info", null);
                await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { dupMsg }, ct);
                allMessages.Add(dupMsg);
                continue;
            }

            var scenarioDef = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, plan.ScenarioKey, StringComparison.OrdinalIgnoreCase));
            if (scenarioDef is null)
            {
                var scenarioMissing = $"Scenario {plan.ScenarioKey} not found or disabled.";
                messages.Add(new AgentResultMessage("assistant", scenarioMissing, "warning", null));
                continue;
            }

            if (includeClarification && clarification is not null)
            {
                if (!string.IsNullOrWhiteSpace(clarification.Question))
                {
                    summaryBuilder.AppendLine($"Answer: {clarification.Question}");
                }
                if (!string.IsNullOrWhiteSpace(clarification.Detail))
                {
                    summaryBuilder.AppendLine($"Detail: {clarification.Detail}");
                }
                var docLabel = clarification.DocumentName ?? clarification.DocumentId;
                if (!string.IsNullOrWhiteSpace(docLabel))
                {
                    summaryBuilder.AppendLine($"Document: {docLabel}");
                }
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
                {
                    summaryBuilder.AppendLine($"DocumentSession: {clarification.DocumentSessionId}");
                }
                if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
                {
                    summaryBuilder.AppendLine($"DocumentGroup: {clarification.DocumentLabel}");
                }
            }
            decimal? planNetAmount = null;
            string? planCurrency = null;

            foreach (var docId in plan.DocumentIds)
            {
                var doc = request.Documents.FirstOrDefault(d => string.Equals(d.FileId, docId, StringComparison.OrdinalIgnoreCase));
                if (doc is null)
                {
                    var warnText = $"Task {plan.ScenarioKey} references unknown file {docId}; skipped.";
                    var warnMsg = new AgentResultMessage("assistant", warnText, "warning", null);
                    await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { warnMsg }, ct);
                    allMessages.Add(warnMsg);
                    continue;
                }

                if (processedFiles.Contains(doc.FileId))
                {
                    var infoText = $"File {doc.FileName ?? doc.FileId} already processed by another task; skipped.";
                    var infoMsg = new AgentResultMessage("assistant", infoText, "info", null);
                    await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { infoMsg }, ct);
                    allMessages.Add(infoMsg);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) &&
                    !string.Equals(doc.DocumentSessionId, plan.DocumentSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    var mismatchText = $"Task {plan.ScenarioKey} file {doc.FileName ?? doc.FileId} has mismatched document session; skipped.";
                    var mismatchMsg = new AgentResultMessage("assistant", mismatchText, "warning", null);
                    await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { mismatchMsg }, ct);
                    allMessages.Add(mismatchMsg);
                    continue;
                }

                var labelPrefix = string.Empty;
                if (!string.IsNullOrWhiteSpace(doc.DocumentSessionId) && documentLabelMap.TryGetValue(doc.DocumentSessionId, out var labelEntry))
                {
                    labelPrefix = $"[{labelEntry.Label}] ";
                }
                summaryBuilder.AppendLine($"File: {labelPrefix}{doc.FileName} ({doc.ContentType}, {doc.Size} bytes)");
                if (doc.Data is JsonObject obj)
                {
                    summaryBuilder.AppendLine(obj.ToJsonString());
                    if (!planNetAmount.HasValue)
                    {
                        var total = TryGetJsonDecimal(obj, "totalAmount");
                        var tax = TryGetJsonDecimal(obj, "taxAmount") ?? 0m;
                        if (total.HasValue)
                        {
                            var net = total.Value - tax;
                            if (net < 0m) net = total.Value;
                            planNetAmount = net;
                        }
                        if (obj.TryGetPropertyValue("currency", out var currencyNode) &&
                            currencyNode is JsonValue currencyValue &&
                            currencyValue.TryGetValue<string>(out var currencyStr) &&
                            !string.IsNullOrWhiteSpace(currencyStr))
                        {
                            planCurrency = currencyStr.Trim();
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId))
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine("");
            }

            if (!string.IsNullOrWhiteSpace(plan.UserMessageOverride))
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine("");
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = summaryBuilder.ToString()
            });

            Func<string, UploadedFileRecord?> resolver = id =>
            {
                // 容错：允许用 fileId �?blobName 解析文件
                var match = request.Documents.FirstOrDefault(d =>
                    string.Equals(d.FileId, id, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(d.BlobName) && string.Equals(d.BlobName, id, StringComparison.OrdinalIgnoreCase)));
                if (match is null) return null;
                return new UploadedFileRecord(match.FileName, match.StoredPath, match.ContentType, match.Size, DateTimeOffset.UtcNow, request.CompanyCode, request.UserCtx.UserId, match.BlobName)
                {
                    Analysis = match.Data?.DeepClone()?.AsObject()
                };
            };

            var context = new AgentExecutionContext(request.SessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, new[] { scenarioDef }, resolver, taskInfo?.TaskId);
            foreach (var entry in documentLabelEntries)
            {
                context.AssignDocumentLabel(entry.SessionId, entry.Label);
            }
            if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.DocumentId) && clarification.DocumentAnalysis is not null)
            {
                context.RegisterDocument(clarification.DocumentId!, clarification.DocumentAnalysis, clarification.DocumentSessionId);
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId) && !string.IsNullOrWhiteSpace(clarification.DocumentLabel))
                {
                    context.AssignDocumentLabel(clarification.DocumentSessionId, clarification.DocumentLabel);
                }
            }
            foreach (var docId in plan.DocumentIds)
            {
                var doc = request.Documents.FirstOrDefault(d => string.Equals(d.FileId, docId, StringComparison.OrdinalIgnoreCase));
                if (doc is null) continue;
                if (doc.Data is JsonObject parsed)
                {
                    context.RegisterDocument(doc.FileId, parsed, doc.DocumentSessionId);
                }
                else
                {
                    context.RegisterDocument(doc.FileId, null, doc.DocumentSessionId);
                }
                if (!string.IsNullOrWhiteSpace(doc.DocumentSessionId) && !string.IsNullOrWhiteSpace(doc.DocumentLabel))
                {
                    context.AssignDocumentLabel(doc.DocumentSessionId, doc.DocumentLabel);
                }
                processedFiles.Add(doc.FileId);
            }
            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId))
            {
                context.SetActiveDocumentSession(plan.DocumentSessionId);
            }
            else
            {
                string? defaultFileId = plan.DocumentIds.FirstOrDefault();
                if (clarification is not null && !string.IsNullOrWhiteSpace(clarification.DocumentId))
                {
                    defaultFileId = clarification.DocumentId;
                }
                context.SetDefaultFileId(defaultFileId);
            }

            var prefetchedAccounts = await PrefetchScenarioAccountsAsync(plan.ScenarioKey, context, ct);
            if (prefetchedAccounts.Count > 0)
            {
                var hintMessage = BuildAccountHintMessage(language, prefetchedAccounts);
                if (!string.IsNullOrWhiteSpace(hintMessage))
                {
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = hintMessage
                    });
                }
            }

            var executionHints = ExtractExecutionHints(scenarioDef);
            if (executionHints is not null && planNetAmount.HasValue)
            {
                var netText = $"{planNetAmount.Value:0}";
                var currencyText = string.IsNullOrWhiteSpace(planCurrency) ? "日元" : planCurrency;
                if (executionHints.NetAmountThreshold is { } thresholdValue && thresholdValue > 0m)
                {
                    var thresholdText = $"{thresholdValue:0}";
                    var tokenDict = new Dictionary<string, string?>
                    {
                        ["netAmount"] = netText,
                        ["currency"] = currencyText,
                        ["threshold"] = thresholdText
                    };

                    if (planNetAmount.Value <= thresholdValue)
                    {
                        var template = executionHints.LowAmountSystemMessage;
                        if (!string.IsNullOrWhiteSpace(template))
                        {
                            var content = ApplyTokens(template, tokenDict);
                            messages.Add(new Dictionary<string, object?>
                            {
                                ["role"] = "system",
                                ["content"] = content
                            });
                        }
                    }
                    else
                    {
                        var template = executionHints.HighAmountSystemMessage;
                        if (!string.IsNullOrWhiteSpace(template))
                        {
                            var content = ApplyTokens(template, tokenDict);
                            messages.Add(new Dictionary<string, object?>
                            {
                                ["role"] = "system",
                                ["content"] = content
                            });
                        }
                        
                    }
                }
            }

            var result = await RunAgentAsync(messages, context, ct);
            if (result.Messages.Count > 0)
            {
                await PersistAssistantMessagesAsync(request.SessionId, context.TaskId, result.Messages, ct);
            }
            allMessages.AddRange(result.Messages);
            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId))
            {
                processedDocumentSessions.Add(plan.DocumentSessionId);
                await SetSessionActiveDocumentAsync(request.SessionId, plan.DocumentSessionId, ct);
            }
        }

        if (clarification is not null)
        {
            await MarkClarificationAnsweredAsync(request.SessionId, clarification.QuestionId, ct);
        }

        return allMessages;
    }

    private async Task<AgentExecutionResult> RunAgentAsync(List<Dictionary<string, object?>> openAiMessages, AgentExecutionContext context, CancellationToken ct)
    {
        var aggregated = new List<AgentResultMessage>();
        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, context.ApiKey);

        var tools = BuildToolDefinitions();
        var openAiTools = OpenAiApiHelper.ConvertToolsToOpenAiFormat(tools);
        
        // 调试：输出发送给 AI 的所有消�?        _logger.LogInformation("[AgentKit] ========== 发��给 AI 的消�?(OpenAI) ==========");
        foreach (var msg in openAiMessages)
        {
            var role = msg.TryGetValue("role", out var r) ? r?.ToString() : "unknown";
            var content = msg.TryGetValue("content", out var c) ? c?.ToString() : "";
            _logger.LogInformation("[AgentKit] [{Role}] {Content}", role, content);
        }
        _logger.LogInformation("[AgentKit] ==========================================");
        
        // 调试：将完整�?AI 输入写入文件
        var debugLogPath = Path.Combine(AppContext.BaseDirectory, "ai_debug.log");
        var debugSb = new StringBuilder();
        debugSb.AppendLine($"\n\n========== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
        debugSb.AppendLine($"Session: {context.SessionId}");
        debugSb.AppendLine($"Scenario: {context.Scenarios.FirstOrDefault()?.ScenarioKey ?? "(none)"}");
        debugSb.AppendLine("\n--- INPUT MESSAGES ---");
        foreach (var msg in openAiMessages)
        {
            var role = msg.TryGetValue("role", out var r) ? r?.ToString() : "unknown";
            var content = msg.TryGetValue("content", out var c) ? c?.ToString() : "";
            debugSb.AppendLine($"[{role}] {content}");
        }
        try { File.AppendAllText(debugLogPath, debugSb.ToString()); } catch { }

        // 错误计数器：防止同一工具连续失败
        var consecutiveErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        const int MaxConsecutiveErrors = 2;
        string? lastErrorMessage = null;

        for (var step = 0; step < 8; step++)
        {
            // 调用 OpenAI API (带工具支�?
            var openAiResponse = await OpenAiApiHelper.CallOpenAiWithToolsAsync(
                http,
                context.ApiKey,
                "gpt-4o",
                openAiMessages.Cast<object>(),
                openAiTools,
                temperature: 0.1,
                maxTokens: 4096,
                ct: ct);
            
            // 调试：将 AI 响应写入文件
            try 
            { 
                var respSb = new StringBuilder();
                respSb.AppendLine($"\n--- AI RESPONSE (step {step}) ---");
                respSb.AppendLine($"TextContent: {openAiResponse.Content}");
                respSb.AppendLine($"ToolCalls: {openAiResponse.ToolCalls?.Count ?? 0}");
                File.AppendAllText(debugLogPath, respSb.ToString()); 
            } catch { }

            // 处理工具调用
            if (openAiResponse.ToolCalls != null && openAiResponse.ToolCalls.Count > 0)
            {
                // 将助手消息（包含工具调用）添加到消息列表
                var assistantMsg = OpenAiApiHelper.BuildAssistantMessageWithToolUse(openAiResponse.Content, openAiResponse.ToolCalls);
                openAiMessages.Add(assistantMsg!);

                var shouldBreakDueToError = false;
                foreach (var toolCall in openAiResponse.ToolCalls)
                {
                    JsonDocument argsDoc;
                    try
                    {
                        argsDoc = JsonDocument.Parse(toolCall.Arguments);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AgentKit] 解析工具参数失败: {Name} Args={Args}", toolCall.Name, toolCall.Arguments);
                        argsDoc = JsonDocument.Parse("{}");
                    }

                    var execution = await ExecuteToolAsync(toolCall.Name, argsDoc.RootElement, context, ct);
                    
                    // 添加工具结果消息
                    var toolResultMsg = OpenAiApiHelper.BuildToolResultMessage(toolCall.Id, execution.ContentForModel);
                    openAiMessages.Add(toolResultMsg!);
                    
                    // sanitized
                               || execution.ContentForModel.Contains("失败", StringComparison.Ordinal)
                               || execution.ContentForModel.Contains("失敗", StringComparison.Ordinal)
                               // sanitized
                               || execution.ContentForModel.Contains("not balanced", StringComparison.OrdinalIgnoreCase);
                    
                    if (isError)
                    {
                        consecutiveErrors.TryGetValue(toolCall.Name, out var count);
                        consecutiveErrors[toolCall.Name] = count + 1;
                        lastErrorMessage = execution.ContentForModel;
                        
                        _logger.LogWarning("[AgentKit] 工具 {Tool} �?{Count} 次错�? {Error}", 
                            toolCall.Name, consecutiveErrors[toolCall.Name], execution.ContentForModel);
                        
                        // 如果同一工具连续失败超过阈��，停止循环
                        if (consecutiveErrors[toolCall.Name] >= MaxConsecutiveErrors)
                        {
                            _logger.LogError("[AgentKit] 工具 {Tool} 连续失败 {Count} 次，停止循环", 
                                toolCall.Name, consecutiveErrors[toolCall.Name]);
                            shouldBreakDueToError = true;
        aggregated.Add(new AgentResultMessage("assistant", "Too many steps. Stopped.", "error", null));
                                Localize(context.Language, 
                                    $"ツー�?{toolCall.Name} が��続で失敗しました��エラー: {lastErrorMessage}", 
                                    $"工具 {toolCall.Name} 连续失败。错�? {lastErrorMessage}"), 
                                "error", null));
                        }
                    }
                    else
                    {
                        // 成功时重置该工具的错误计�?                        consecutiveErrors[toolCall.Name] = 0;
                    }
                    
                    if (execution.Messages is { Count: > 0 })
                    {
                        aggregated.AddRange(execution.Messages);
                    }
                    
                    // 如果工具要求中断循环（如 request_clarification），立即返回
                    if (execution.ShouldBreakLoop)
                    {
                        var finalMsgs = FinalizeMessages(aggregated, context);
                        return new AgentExecutionResult(finalMsgs);
                    }
                }
                
                // 如果因错误需要中断，返回结果
                if (shouldBreakDueToError)
                {
                    var finalMsgs = FinalizeMessages(aggregated, context);
                    return new AgentExecutionResult(finalMsgs);
                }
                continue;
            }

            // 没有工具调用，处理文本响�?            var content = openAiResponse.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(content))
            {
        aggregated.Add(new AgentResultMessage("assistant", "Too many steps. Stopped.", "error", null));
            }
            var finalized = FinalizeMessages(aggregated, context);
            return new AgentExecutionResult(finalized);
        }

        aggregated.Add(new AgentResultMessage("assistant", "Too many steps. Stopped.", "error", null));
        var finalMessages = FinalizeMessages(aggregated, context);
        return new AgentExecutionResult(finalMessages);
    }

    private static List<AgentResultMessage> FinalizeMessages(List<AgentResultMessage> messages, AgentExecutionContext context)
    {
        if (!context.HasVoucherCreated)
        {
            messages = messages
                .Where(m =>
                    !(m.Tag is null
                      && string.IsNullOrWhiteSpace(m.Status)
                      && !string.IsNullOrWhiteSpace(m.Content)
                      && (m.Content.Contains("voucher created", StringComparison.OrdinalIgnoreCase)
                          || m.Content.Contains("voucher created", StringComparison.OrdinalIgnoreCase))))
                .ToList();
            if (messages.Count == 0)
            {
                messages.Add(new AgentResultMessage("assistant", "AI failed to create voucher. Please retry.", "warning", null));
            }
        }
        return messages;
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(string name, JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        if (_toolRegistry.Contains(name))
        {
            return await _toolRegistry.ExecuteAsync(name, args, context, ct);
        }
        try
        {
            switch (name)
            {
                case "extract_invoice_data":
                {
                    var fileId = args.TryGetProperty("file_id", out var fileEl) ? fileEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(fileId))
                    {
                        fileId = context.DefaultFileId;
                    }
                    if (string.IsNullOrWhiteSpace(fileId))
                        throw new Exception(Localize(context.Language, "file_id ��ָ������Ƥ��ޤ���", "file_id ȱʧ"));
                    if (context.TryGetDocument(fileId!, out var cached) && cached is JsonObject cachedObj)
                    {
                        _logger.LogInformation("[AgentKit] 使用缓存的发票解析结�?fileId={FileId}", fileId);
                        return ToolExecutionResult.FromModel(cachedObj);
                    }

                    _logger.LogInformation("[AgentKit] 调用工具 extract_invoice_data，fileId={FileId}", fileId);
                    if (context.TryResolveAttachmentToken(fileId, out var resolvedFileId))
                    {
                        fileId = resolvedFileId;
                    }
                    var file = context.ResolveFile(fileId!);
                    // 容错：模型可能传�?file_id，尝试回逢�到当前默认文�?                    if (file is null && !string.IsNullOrWhiteSpace(context.DefaultFileId) &&
                        !string.Equals(fileId, context.DefaultFileId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[AgentKit] resolve file failed for fileId={FileId}, fallback to defaultFileId={DefaultFileId}", fileId, context.DefaultFileId);
                        var fallbackId = context.DefaultFileId!;
                        var fallbackFile = context.ResolveFile(fallbackId);
                        if (fallbackFile is not null)
                        {
                            fileId = fallbackId;
                            file = fallbackFile;
                        }
                    }
                    // Extra fallback: use the first file in current active document session
                    if (file is null && !string.IsNullOrWhiteSpace(context.ActiveDocumentSessionId))
                    {
                        var sessionFiles = context.GetFileIdsByDocumentSession(context.ActiveDocumentSessionId).ToArray();
                        if (sessionFiles.Length > 0)
                        {
                            var sid = sessionFiles[0];
                            var sfile = context.ResolveFile(sid);
                            if (sfile is not null)
                            {
                                _logger.LogWarning("[AgentKit] resolve file failed for fileId={FileId}, fallback to sessionFileId={SessionFileId}", fileId, sid);
                                fileId = sid;
                                file = sfile;
                            }
                        }
                    }
                    if (file is null)
                        throw new Exception(Localize(context.Language, $"File {fileId} not found or expired", $"File {fileId} not found or expired"));
                    var data = await ExtractInvoiceDataAsync(fileId!, file, context, ct);
                    context.RegisterDocument(fileId, data);
                    return ToolExecutionResult.FromModel(data ?? new JsonObject { ["status"] = "error" });
                }
                case "extract_booking_settlement_data":
                {
                    var fileId = args.TryGetProperty("file_id", out var fileEl) ? fileEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(fileId))
                    {
                        fileId = context.DefaultFileId;
                    }
                    if (string.IsNullOrWhiteSpace(fileId))
                        throw new Exception("error");
                    if (context.TryGetDocument(fileId!, out var cached) && cached is JsonObject cachedObj)
                    {
                        _logger.LogInformation("[AgentKit] ʹ�û���� Booking ���������� fileId={FileId}", fileId);
                        return ToolExecutionResult.FromModel(cachedObj);
                    }

                    _logger.LogInformation("[AgentKit] ���ù��� extract_booking_settlement_data, fileId={FileId}", fileId);
                    if (context.TryResolveAttachmentToken(fileId, out var resolvedFileId))
                    {
                        fileId = resolvedFileId;
                    }
                    var file = context.ResolveFile(fileId!);
                    // 容错：模型可能传�?file_id，尝试回逢�到当前默认文�?                    if (file is null && !string.IsNullOrWhiteSpace(context.DefaultFileId) &&
                        !string.Equals(fileId, context.DefaultFileId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[AgentKit] resolve file failed for fileId={FileId}, fallback to defaultFileId={DefaultFileId}", fileId, context.DefaultFileId);
                        var fallbackId = context.DefaultFileId!;
                        var fallbackFile = context.ResolveFile(fallbackId);
                        if (fallbackFile is not null)
                        {
                            fileId = fallbackId;
                            file = fallbackFile;
                        }
                    }
                    // Extra fallback: use the first file in current active document session
                    if (file is null && !string.IsNullOrWhiteSpace(context.ActiveDocumentSessionId))
                    {
                        var sessionFiles = context.GetFileIdsByDocumentSession(context.ActiveDocumentSessionId).ToArray();
                        if (sessionFiles.Length > 0)
                        {
                            var sid = sessionFiles[0];
                            var sfile = context.ResolveFile(sid);
                            if (sfile is not null)
                            {
                                _logger.LogWarning("[AgentKit] resolve file failed for fileId={FileId}, fallback to sessionFileId={SessionFileId}", fileId, sid);
                                fileId = sid;
                                file = sfile;
                            }
                        }
                    }
                    if (file is null)
                        throw new Exception(Localize(context.Language, $"File {fileId} not found or expired", $"File {fileId} not found or expired"));
                    var data = await ExtractBookingSettlementDataAsync(fileId!, file, context, ct);
                    // If totals are missing/zero, force a single clarification asking for all required fields.
                    // This prevents the model from inventing placeholder amounts (e.g., 100000/95000/5000).
                    if (data is not null)
                    {
                        decimal ReadNum(string key)
                        {
                            if (data.TryGetPropertyValue(key, out var n) && n is JsonValue nv)
                            {
                                if (nv.TryGetValue<decimal>(out var d)) return d;
                                if (nv.TryGetValue<double>(out var dd)) return Convert.ToDecimal(dd);
                                if (nv.TryGetValue<string>(out var s) && decimal.TryParse(s, out var parsed)) return parsed;
                            }
                            return 0m;
                        }

                        var gross = ReadNum("grossAmount");
                        var comm = ReadNum("commissionAmount");
                        var fee = ReadNum("paymentFeeAmount");
                        var net = ReadNum("netAmount");
                        var paymentDate = data.TryGetPropertyValue("paymentDate", out var pd) && pd is JsonValue pdVal && pdVal.TryGetValue<string>(out var pds)
                            ? (pds ?? string.Empty).Trim()
                            : string.Empty;

                        var missingTotals = gross <= 0m || net <= 0m || (comm + fee) < 0m;
                        if (missingTotals)
                        {
                            var questionId = $"q_{Guid.NewGuid():N}";
                            var docId = context.DefaultFileId ?? fileId ?? "unknown";
                            var documentSessionId = context.ActiveDocumentSessionId;
                            var question = Localize(context.Language,
                                "Booking.com statement totals were not extracted. Please provide: payment_date (YYYY-MM-DD), gross, commission, paymentFee, net.",
                                "Booking.com statement totals were not extracted. Please provide: payment_date (YYYY-MM-DD), gross, commission, paymentFee, net.");
                            var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey;
                            var documentLabel = !string.IsNullOrWhiteSpace(documentSessionId) ? context.GetDocumentSessionLabel(documentSessionId) : null;
                            // Use missingField = bookingTotals so ProcessTaskMessage can inject a structured answer later.
                            var tag = new ClarificationTag(questionId, documentSessionId, docId, question, null, data?.DeepClone().AsObject(), file?.FileName, file?.BlobName, documentLabel, scenarioKey, MissingField: "bookingTotals");
                            var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);
                            return ToolExecutionResult.FromModel(
                                new { status = "clarify", questionId, documentId = docId, documentSessionId, missingField = "bookingTotals", paymentDate },
                                new List<AgentResultMessage> { clarifyMessage },
                                shouldBreakLoop: true);
                        }
                    }
                    // Guardrails: if paymentDate cannot be extracted, DO NOT fail the tool repeatedly.
                    // Instead, return a clarification card asking for postingDate (same as payment date) to prevent hallucinated years.
                    if (data is not null)
                    {
                        var status = data.TryGetPropertyValue("status", out var st) && st is JsonValue stVal && stVal.TryGetValue<string>(out var s)
                            ? (s ?? string.Empty).Trim()
                            : string.Empty;
                        var paymentDate = data.TryGetPropertyValue("paymentDate", out var pd) && pd is JsonValue pdVal && pdVal.TryGetValue<string>(out var pds)
                            ? (pds ?? string.Empty).Trim()
                            : string.Empty;
                        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(paymentDate))
                        {
                            var questionId = $"q_{Guid.NewGuid():N}";
                            var docId = context.DefaultFileId ?? fileId ?? "unknown";
                            var documentSessionId = context.ActiveDocumentSessionId;
                            var question = Localize(context.Language,
                                "Booking.com payment date was not extracted. Please provide payment_date (YYYY-MM-DD).",
                                "Booking.com payment date was not extracted. Please provide payment_date (YYYY-MM-DD).");
                            var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey;
                            var documentLabel = !string.IsNullOrWhiteSpace(documentSessionId) ? context.GetDocumentSessionLabel(documentSessionId) : null;
                            var tag = new ClarificationTag(questionId, documentSessionId, docId, question, null, data?.DeepClone().AsObject(), file?.FileName, file?.BlobName, documentLabel, scenarioKey, MissingField: "postingDate");
                            var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);
                            return ToolExecutionResult.FromModel(
                                new { status = "clarify", questionId, documentId = docId, documentSessionId, missingField = "postingDate" },
                                new List<AgentResultMessage> { clarifyMessage },
                                shouldBreakLoop: true);
                        }
                    }
                    context.RegisterDocument(fileId, data);
                    return ToolExecutionResult.FromModel(data ?? new JsonObject { ["status"] = "error" });
                }
                case "find_moneytree_deposit_for_settlement":
                {
                    var paymentDate = args.TryGetProperty("payment_date", out var pdEl) ? pdEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(paymentDate))
                        throw new Exception(Localize(context.Language, "payment_date ����Ҫ�Ǥ�", "payment_date ����"));
                    if (!DateTime.TryParse(paymentDate, out var payDate))
                        throw new Exception(Localize(context.Language, "payment_date ����ʽ�������Ǥ�", "payment_date ��ʽ����"));

                    decimal netAmount = 0m;
                    if (args.TryGetProperty("net_amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number)
                        netAmount = amtEl.GetDecimal();
                    if (netAmount <= 0m)
                        throw new Exception(Localize(context.Language, "net_amount ����Ҫ�Ǥ�", "net_amount �������0"));

                    var daysTol = args.TryGetProperty("days_tolerance", out var dtEl) && dtEl.ValueKind == JsonValueKind.Number ? dtEl.GetInt32() : 7;
                    var amtTol = args.TryGetProperty("amount_tolerance", out var atEl) && atEl.ValueKind == JsonValueKind.Number ? atEl.GetDecimal() : 1m;

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

                    var found = await FindMoneytreeDepositForSettlementAsync(
                        context.CompanyCode,
                        payDate.Date,
                        netAmount,
                        daysTol,
                        amtTol,
                        keywords,
                        ct);
                    return ToolExecutionResult.FromModel(found);
                }
                case "lookup_account":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception(Localize(context.Language, "query ��ָ������Ƥ��ޤ���", "query ����Ϊ��"));
                    _logger.LogInformation("[AgentKit] ���ù��� lookup_account, query={Query}", query);
                    var match = await LookupAccountAsync(context.CompanyCode, query!, ct);
                    if (match.Found && !string.IsNullOrWhiteSpace(match.AccountCode))
                    {
                        context.RegisterLookupAccountResult(match.AccountCode);
                    }
                    return ToolExecutionResult.FromModel(match);
                }
                case "check_accounting_period":
                {
                    var posting = args.TryGetProperty("posting_date", out var pdEl) ? pdEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(posting))
                        throw new Exception(Localize(context.Language, "posting_date ��ָ������Ƥ��ޤ���", "posting_date ����Ϊ��"));
                    _logger.LogInformation("[AgentKit] ���ù��� check_accounting_period, postingDate={Posting}", posting);
                    var status = await CheckAccountingPeriodAsync(context.CompanyCode, posting!, ct);
                    return ToolExecutionResult.FromModel(status);
                }
                case "verify_invoice_registration":
                {
                    var regNo = args.TryGetProperty("registration_no", out var regEl) ? regEl.GetString() : null;
                    // インボイス登録番号は必須ではない：取得できない場合はスキップ（空保存可）
                    if (string.IsNullOrWhiteSpace(regNo))
                    {
                        return ToolExecutionResult.FromModel(new { status = "skipped", reason = "registration_no is empty" });
                    }
                    var trimmedRegNo = regNo.Trim();
                    if (IsPlaceholderRegistrationNo(trimmedRegNo))
                    {
                        var resolved = ResolveRegistrationNoFromContext(context);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            _logger.LogInformation("[AgentKit] verify_invoice_registration 收到占位登记号，占位�?{Placeholder}，使用上下文登记�?{Resolved}", trimmedRegNo, resolved);
                            regNo = resolved;
                        }
                        else
                        {
                            // Placeholder only and no real number in context -> skip
                            _logger.LogInformation("[AgentKit] verify_invoice_registration 占位登记号且上下文无真实登记号，跳过校验");
                            return ToolExecutionResult.FromModel(new { status = "skipped", reason = "placeholder only" });
                        }
                    }
                    _logger.LogInformation("[AgentKit] 调用工具 verify_invoice_registration，registrationNo={RegistrationNo}", regNo);
                    var result = await VerifyInvoiceRegistrationAsync(regNo!, ct);
                    return ToolExecutionResult.FromModel(result);
                }
                case "lookup_customer":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception("error");
                    var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
                    _logger.LogInformation("[AgentKit] 调用工具 lookup_customer，query={Query} limit={Limit}", query, limit);
                    var payload = await LookupCustomerAsync(context.CompanyCode, query!.Trim(), limit, ct);
                    return ToolExecutionResult.FromModel(JsonSerializer.Deserialize<object>(payload.ToJsonString(), JsonOptions) ?? new { status = "ok", items = Array.Empty<object>() });
                }
                case "lookup_vendor":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception("error");
                    var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
                    _logger.LogInformation("[AgentKit] 调用工具 lookup_vendor，query={Query} limit={Limit}", query, limit);
                    var payload = await LookupVendorAsync(context.CompanyCode, query!.Trim(), limit, ct);
                    return ToolExecutionResult.FromModel(JsonSerializer.Deserialize<object>(payload.ToJsonString(), JsonOptions) ?? new { status = "ok", items = Array.Empty<object>() });
                }
                case "search_vendor_receipts":
                {
                    // 查询供应商的可匹配入库凭�?                    var vendorId = args.TryGetProperty("vendor_id", out var vidEl) ? vidEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(vendorId))
                        throw new Exception("error");
                    _logger.LogInformation("[AgentKit] 调用工具 search_vendor_receipts，vendorId={VendorId}", vendorId);
                    var receipts = await SearchVendorReceiptsAsync(context.CompanyCode, vendorId!, ct);
                    return ToolExecutionResult.FromModel(receipts);
                }
                case "get_expense_account_options":
                {
                    // 获取可��的借方费用/库存科目（用于供应商请求书直接记账）
                    var vendorId = args.TryGetProperty("vendor_id", out var vidEl) ? vidEl.GetString() : null;
                    _logger.LogInformation("[AgentKit] 调用工具 get_expense_account_options，vendorId={VendorId}", vendorId);
                    var options = await GetExpenseAccountOptionsAsync(context.CompanyCode, vendorId, ct);
                    return ToolExecutionResult.FromModel(options);
                }
                case "create_vendor_invoice":
                {
                    // ������Ӧ�������飨���ĵ�ƥ�䣩
                    _logger.LogInformation("[AgentKit] ���ù��� create_vendor_invoice ��ʼ");
                    var result = await CreateVendorInvoiceAsync(context, args, ct);
                    _logger.LogInformation("[AgentKit] 调用工具 create_vendor_invoice 完成");
                    return result;
                }
                case "lookup_material":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception(Localize(context.Language, "query ��ָ������Ƥ��ޤ���", "query ����Ϊ��"));
                    var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
                    _logger.LogInformation("[AgentKit] ���ù��� lookup_material, query={Query} limit={Limit}", query, limit);
                    var payload = await LookupMaterialAsync(context.CompanyCode, query!.Trim(), limit, ct);
                    return ToolExecutionResult.FromModel(JsonSerializer.Deserialize<object>(payload.ToJsonString(), JsonOptions) ?? new { status = "ok", items = Array.Empty<object>() });
                }
                case "register_moneytree_rule":
                {
                    var upsert = ParseMoneytreeRuleSpec(args, context.Language);
                    _logger.LogInformation("[AgentKit] register_moneytree_rule title={Title}", upsert.Title);
                    var rule = await _moneytreeRuleService.CreateAsync(
                        context.CompanyCode,
                        upsert,
                        context.UserCtx,
                        ct);

                    var successMessage = new AgentResultMessage(
                        "assistant",
                        Localize(context.Language, $"Moneytree ��`�롸{rule.Title}������h���ޤ�����", $"�Ѵ��� Moneytree ����{rule.Title}����"),
                        "info",
                        new { kind = "moneytreeRule", ruleId = rule.Id });

                    return ToolExecutionResult.FromModel(new { status = "ok", rule }, new List<AgentResultMessage> { successMessage });
                }
                case "bulk_register_moneytree_rule":
                {
                    if (!args.TryGetProperty("rules", out var rulesNode) || rulesNode.ValueKind != JsonValueKind.Array || rulesNode.GetArrayLength() == 0)
                        throw new Exception(Localize(context.Language, "rules ���Ф���Ҫ�Ǥ�", "rules �������"));

                    var created = new List<object>();
                    foreach (var entry in rulesNode.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        var upsert = ParseMoneytreeRuleSpec(entry, context.Language);
                        var rule = await _moneytreeRuleService.CreateAsync(
                            context.CompanyCode,
                            upsert,
                            context.UserCtx,
                            ct);
                        created.Add(new { rule.Id, rule.Title });
                    }

                    var bulkMessage = new AgentResultMessage(
                        "assistant",
                        Localize(context.Language, $"Moneytree ��`��� {created.Count} �����h���ޤ�����", $"���������� {created.Count} �� Moneytree ����"),
                        "info",
                        new { kind = "moneytreeRuleBulk", count = created.Count });

                    return ToolExecutionResult.FromModel(new { status = "ok", count = created.Count, rules = created }, new List<AgentResultMessage> { bulkMessage });
                }
                case "request_clarification":
                {
                    var docId = args.TryGetProperty("document_id", out var docIdEl) ? docIdEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(docId))
                        throw new Exception(Localize(context.Language, "document_id ��ָ������Ƥ��ޤ���", "document_id ȱʧ"));
                    var question = args.TryGetProperty("question", out var questionEl) ? questionEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(question))
                        throw new Exception(Localize(context.Language, "question ��ָ������Ƥ��ޤ���", "question ȱʧ"));
                    var detail = args.TryGetProperty("detail", out var detailEl) ? detailEl.GetString() : null;
                    var questionId = $"q_{Guid.NewGuid():N}";
                    var documentSessionId = context.GetDocumentSessionIdByFileId(docId!) ?? context.ActiveDocumentSessionId;
                    string? documentLabel = null;
                    if (!string.IsNullOrWhiteSpace(documentSessionId))
                    {
                        documentLabel = context.GetDocumentSessionLabel(documentSessionId);
                    }
                    if (string.IsNullOrWhiteSpace(documentLabel))
                    {
                        var resolvedSessionId = context.GetDocumentSessionIdByFileId(docId!);
                        if (!string.IsNullOrWhiteSpace(resolvedSessionId))
                        {
                            documentLabel = context.GetDocumentSessionLabel(resolvedSessionId);
                        }
                    }
                    JsonObject? analysisClone = null;
                    if (context.TryGetDocument(docId!, out var docNode) && docNode is JsonObject docObj)
                    {
                        analysisClone = docObj.DeepClone().AsObject();
                    }
                    var file = context.ResolveFile(docId!);
                    var trimmedQuestion = question.Trim();
                    if (!string.IsNullOrWhiteSpace(documentLabel))
                    {
                        trimmedQuestion = $"[{documentLabel}] {trimmedQuestion}";
                    }
                    var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey;
                    var tag = new ClarificationTag(questionId, documentSessionId, docId, question.Trim(), detail?.Trim(), analysisClone, file?.FileName, file?.BlobName, documentLabel, scenarioKey);
                    var clarifyMessage = new AgentResultMessage("assistant", trimmedQuestion, "clarify", tag);
                    if (!string.IsNullOrWhiteSpace(documentSessionId))
                    {
                        await SetSessionActiveDocumentAsync(context.SessionId, documentSessionId, ct);
                    }
                    return ToolExecutionResult.FromModel(new { status = "clarify", questionId, documentId = docId, documentSessionId, documentLabel }, new List<AgentResultMessage> { clarifyMessage }, shouldBreakLoop: true);
                }
                case "create_voucher":
                {
                    _logger.LogInformation("log");
                    JsonDocument? rewrittenArgsDoc = null;
                    try
                    {
                        var argsToUse = args;
                        if (ShouldForceInvoiceDefaults(context))
                        {
                            var node = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
                            if (!node.TryGetPropertyValue("header", out var headerNode) || headerNode is not JsonObject headerObj)
                            {
                                headerObj = new JsonObject();
                                node["header"] = headerObj;
                            }

                            if (ApplyInvoiceHeaderDefaults(headerObj))
                            {
                                rewrittenArgsDoc = JsonDocument.Parse(node.ToJsonString());
                                argsToUse = rewrittenArgsDoc.RootElement;
                            }
                        }

                        var result = await CreateVoucherAsync(context, argsToUse, ct);
                        _logger.LogInformation("[AgentKit] 调用工具 create_voucher 完成");
                        return result;
                    }
                    catch (Exception ex) when (IsRequiredFieldMissing(ex.Message, out var fieldInfo))
                    {
                        // 字段缺失时，返回 clarification 请求而不是直接报�?                        _logger.LogInformation("[AgentKit] create_voucher 字段缺失，返�?clarification: {FieldInfo}", fieldInfo);
                        var (fieldName, accountCode) = fieldInfo;
                        var questionId = $"q_{Guid.NewGuid():N}";
                        var docId = context.DefaultFileId ?? "unknown";
                        var documentSessionId = context.ActiveDocumentSessionId;
                        
                        // 保存这次失败�?draft 传票（用于用户回答后直接注入并重试，避免 LLM 反复循环�?                        JsonObject? draftVoucher = null;
                        try
                        {
                            draftVoucher = JsonNode.Parse(args.GetRawText()) as JsonObject;
                        }
                        catch { /* ignore */ }
                        
                        // 根据缺失的字段生成合适的问题
                        string question;
                        if (fieldName == "paymentDate")
                        {
                            question = Localize(context.Language,
                                // sanitized
                                // sanitized
                        }
                        else if (fieldName == "vendorId")
                        {
                            question = Localize(context.Language,
                                // sanitized
                                // sanitized
                        }
                        else if (fieldName == "customerId")
                        {
                            question = Localize(context.Language,
                                // sanitized
                                // sanitized
                        }
                        else
                        {
                            question = Localize(context.Language,
                                // sanitized
                                // sanitized
                        }
                        
                        JsonObject? analysisClone = null;
                        if (!string.IsNullOrWhiteSpace(docId) && context.TryGetDocument(docId, out var docNode) && docNode is JsonObject docObj)
                        {
                            analysisClone = docObj.DeepClone().AsObject();
                        }
                        var file = !string.IsNullOrWhiteSpace(docId) ? context.ResolveFile(docId) : null;
                        var documentLabel = !string.IsNullOrWhiteSpace(documentSessionId) ? context.GetDocumentSessionLabel(documentSessionId) : null;
                        var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey;
                        
                        var tag = new ClarificationTag(questionId, documentSessionId, docId, question, null, analysisClone, file?.FileName, file?.BlobName, documentLabel, scenarioKey, DraftVoucher: draftVoucher);
                        tag = tag with { MissingField = fieldName, AccountCode = accountCode };
                        
                        var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);
                        return ToolExecutionResult.FromModel(
                            new { status = "clarify", questionId, documentId = docId, documentSessionId, missingField = fieldName, accountCode }, 
                            new List<AgentResultMessage> { clarifyMessage }, 
                            shouldBreakLoop: true);
                    }
                    catch (Exception ex) when (IsAccountingPeriodNotOpen(ex.Message, out var ym))
                    {
                        // 会計期間が未オープン/閉鎖：clarification �?postingDate をユーザーに確認し��回答後�?draft voucher を自動注入して再試行する
                        _logger.LogInformation("[AgentKit] create_voucher 会計期間エラー��返�?clarification: {Message}", ex.Message);
                        var questionId = $"q_{Guid.NewGuid():N}";
                        var docId = context.DefaultFileId ?? "unknown";
                        var documentSessionId = context.ActiveDocumentSessionId;
                        JsonObject? draftVoucher = null;
                        try { draftVoucher = JsonNode.Parse(args.GetRawText()) as JsonObject; } catch { /* ignore */ }

                        var ymText = string.IsNullOrWhiteSpace(ym) ? string.Empty : ym;
                        var question = Localize(context.Language,
                            string.IsNullOrWhiteSpace(ymText)
                                // sanitized
                                // sanitized
                            string.IsNullOrWhiteSpace(ymText)
                                // sanitized
                                // sanitized

                        JsonObject? analysisClone = null;
                        if (!string.IsNullOrWhiteSpace(docId) && context.TryGetDocument(docId, out var docNode) && docNode is JsonObject docObj)
                        {
                            analysisClone = docObj.DeepClone().AsObject();
                        }
                        var file = !string.IsNullOrWhiteSpace(docId) ? context.ResolveFile(docId) : null;
                        var documentLabel = !string.IsNullOrWhiteSpace(documentSessionId) ? context.GetDocumentSessionLabel(documentSessionId) : null;
                        var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey;

                        var tag = new ClarificationTag(questionId, documentSessionId, docId, question, ex.Message, analysisClone, file?.FileName, file?.BlobName, documentLabel, scenarioKey, MissingField: "postingDate", AccountCode: null, DraftVoucher: draftVoucher);
                        var clarifyMessage = new AgentResultMessage("assistant", question, "clarify", tag);
                        return ToolExecutionResult.FromModel(
                            new { status = "clarify", questionId, documentId = docId, documentSessionId, missingField = "postingDate" },
                            new List<AgentResultMessage> { clarifyMessage }, 
                            shouldBreakLoop: true);
                    }
                    finally
                    {
                        rewrittenArgsDoc?.Dispose();
                    }
                }
                case "create_sales_order":
                {
                    _logger.LogInformation("[AgentKit] 调用工具 create_sales_order");
                    return await CreateSalesOrderAsync(context, args, ct);
                }
                case "get_voucher_by_number":
                {
                    var voucherNo = args.TryGetProperty("voucher_no", out var vEl) ? vEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(voucherNo))
                        throw new Exception("error");
                    _logger.LogInformation("[AgentKit] 调用工具 get_voucher_by_number，voucherNo={VoucherNo}", voucherNo);
                    var payload = await GetVoucherByNumberAsync(context.CompanyCode, voucherNo!, ct);
                    return ToolExecutionResult.FromModel(payload);
                }
                case "analyze_sales":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception("error");
                    var dateFrom = args.TryGetProperty("date_from", out var dfEl) ? dfEl.GetString() : null;
                    var dateTo = args.TryGetProperty("date_to", out var dtEl) ? dtEl.GetString() : null;
                    _logger.LogInformation("[AgentKit] 调用工具 analyze_sales，query={Query} dateFrom={DateFrom} dateTo={DateTo}", query, dateFrom, dateTo);
                    
                    var result = await AnalyzeSalesAsync(context.CompanyCode, query!, context.UserCtx?.UserId ?? "anonymous", dateFrom, dateTo, context.ApiKey, ct);
                    
                    if (result.Success && result.EchartsConfig != null)
                    {
                        // 返回图表配置给前端渲�?                        var chartMessage = new AgentResultMessage(
                            "assistant",
                            result.Explanation ?? query!,
                            "chart",
                            new { 
                                kind = "salesChart",
                                chartType = result.ChartType,
                                chartTitle = result.ChartTitle,
                                echartsConfig = result.EchartsConfig,
                                data = result.Data,
                                sql = result.Sql
                            });
                        return ToolExecutionResult.FromModel(
                            new { status = "ok", chartType = result.ChartType, explanation = result.Explanation },
                            new List<AgentResultMessage> { chartMessage });
                    }
                    else if (result.Success && result.Data != null)
                    {
                        // 没有图表配置，返回表格数�?                        var tableMessage = new AgentResultMessage(
                            "assistant",
                            // sanitized
                            "table",
                            new { kind = "salesTable", data = result.Data });
                        return ToolExecutionResult.FromModel(
                            new { status = "ok", explanation = result.Explanation },
                            new List<AgentResultMessage> { tableMessage });
                    }
                    else
                    {
                        throw new Exception("error");
                    }
                }
                case "fetch_webpage":
                {
                    var url = args.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url))
                        throw new Exception("error");
                    _logger.LogInformation("[AgentKit] 调用工具 fetch_webpage，url={Url}", url);
                    var content = await FetchWebpageContentAsync(url!, ct);
                    return ToolExecutionResult.FromModel(new { status = "ok", url, content });
                }
                case "create_business_partner":
                {
                    var partnerName = args.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(partnerName))
                        throw new Exception("error");
                    
                    var nameKana = args.TryGetProperty("nameKana", out var kanaEl) ? kanaEl.GetString() : null;
                    var isCustomer = args.TryGetProperty("isCustomer", out var custEl) && custEl.GetBoolean();
                    var isVendor = args.TryGetProperty("isVendor", out var vendEl) && vendEl.GetBoolean();
                    var postalCode = args.TryGetProperty("postalCode", out var pcEl) ? pcEl.GetString() : null;
                    var prefecture = args.TryGetProperty("prefecture", out var prefEl) ? prefEl.GetString() : null;
                    var address = args.TryGetProperty("address", out var addrEl) ? addrEl.GetString() : null;
                    var phone = args.TryGetProperty("phone", out var phoneEl) ? phoneEl.GetString() : null;
                    var fax = args.TryGetProperty("fax", out var faxEl) ? faxEl.GetString() : null;
                    var email = args.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
                    var contactPerson = args.TryGetProperty("contactPerson", out var cpEl) ? cpEl.GetString() : null;
                    var partnerNote = args.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;
                    
                    _logger.LogInformation("[AgentKit] 调用工具 create_business_partner，name={Name}", partnerName);
                    var bpResult = await CreateBusinessPartnerAsync(context.CompanyCode, partnerName!, nameKana, isCustomer, isVendor, postalCode, prefecture, address, phone, fax, email, contactPerson, partnerNote, ct);
                    return ToolExecutionResult.FromModel(bpResult);
                }
                default:
                    throw new Exception(Localize(context.Language, $"未登録のツールです：{name}", $"未知的工具：{name}"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] 执行工具 {Tool} 失败", name);
            var errorModel = new
            {
                status = "error",
                message = ex.Message
            };
            var failMessage = new AgentResultMessage("assistant", Localize(context.Language, $"ツー�?{name} の実行に失敗しました：{ex.Message}", $"工具 {name} 执行失败：{ex.Message}"), "error", null);
            return ToolExecutionResult.FromModel(errorModel, new List<AgentResultMessage> { failMessage });
        }
    }

    private static bool ApplyInvoiceHeaderDefaults(JsonObject headerObj)
    {
        var changed = NormalizeVoucherType(headerObj);
        changed |= NormalizeCurrency(headerObj);
        return changed;
    }

    private static bool NormalizeVoucherType(JsonObject headerObj)
    {
        if (headerObj is null) return false;
        if (!headerObj.TryGetPropertyValue("voucherType", out var voucherTypeNode) || voucherTypeNode is not JsonValue voucherTypeValue || !voucherTypeValue.TryGetValue<string>(out var rawValue))
        {
            headerObj["voucherType"] = "GL";
            return true;
        }

        var trimmed = rawValue?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            headerObj["voucherType"] = "GL";
            return true;
        }

        var normalized = trimmed.ToUpperInvariant();
        if (!string.Equals(rawValue, normalized, StringComparison.Ordinal))
        {
            headerObj["voucherType"] = normalized;
            return true;
        }

        return false;
    }

    private static bool NormalizeCurrency(JsonObject headerObj)
    {
        if (headerObj is null) return false;
        if (!headerObj.TryGetPropertyValue("currency", out var currencyNode) || currencyNode is not JsonValue currencyValue || !currencyValue.TryGetValue<string>(out var rawValue))
        {
            headerObj["currency"] = "JPY";
            return true;
        }

        if (!TryNormalizeCurrency(rawValue, out var normalized))
        {
            headerObj["currency"] = "JPY";
            return true;
        }

        if (!string.Equals(rawValue, normalized, StringComparison.Ordinal))
        {
            headerObj["currency"] = normalized;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeCurrency(string? currency, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(currency)) return false;
        var trimmed = currency.Trim().ToUpperInvariant();
        if (!AllowedCurrencies.Contains(trimmed)) return false;
        normalized = trimmed;
        return true;
    }

    private static bool ShouldForceInvoiceDefaults(AgentExecutionContext context)
    {
        foreach (var scenario in context.Scenarios)
        {
            if (scenario is null) continue;
            var key = scenario.ScenarioKey;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (key.StartsWith("voucher.", StringComparison.OrdinalIgnoreCase) &&
                (key.Contains(".receipt", StringComparison.OrdinalIgnoreCase) || key.Contains(".invoice", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        foreach (var fileId in context.GetRegisteredFileIds())
        {
            if (!context.TryGetDocument(fileId, out var parsed) || parsed is not JsonObject doc) continue;
            if (doc.TryGetPropertyValue("documentType", out var docTypeNode) &&
                docTypeNode is JsonValue docTypeValue &&
                docTypeValue.TryGetValue<string>(out var docType) &&
                string.Equals(docType, "invoice", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (doc.TryGetPropertyValue("category", out var categoryNode) &&
                categoryNode is JsonValue categoryValue &&
                categoryValue.TryGetValue<string>(out var category) &&
                !string.IsNullOrWhiteSpace(category))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ToolExecutionResult> CreateVoucherAsync(AgentExecutionContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("header", out var headerEl) || headerEl.ValueKind != JsonValueKind.Object)
            throw new Exception("error");
        if (!args.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
            throw new Exception("error");

        try
        {
            _logger.LogInformation("[AgentKit] create_voucher 输入: {Payload}", args.GetRawText());
        }
        catch
        {
            // ignore logging errors
        }

        var documentSessionId = args.TryGetProperty("documentSessionId", out var docSessionEl) && docSessionEl.ValueKind == JsonValueKind.String
            ? docSessionEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(documentSessionId))
            throw new Exception("documentSessionId が指定されていません");
        documentSessionId = documentSessionId.Trim();

        var sessionFileIds = context.GetFileIdsByDocumentSession(documentSessionId).ToArray();
        if (sessionFileIds.Length == 0)
        {
            throw new Exception("error");
        }

        context.SetActiveDocumentSession(documentSessionId);
        if (!string.Equals(context.ActiveDocumentSessionId, documentSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"无法切换到指定的 documentSessionId {documentSessionId}");
        }

        // 从公司设置获取进项税科目代码
        var inputTaxAccountCode = await GetInputTaxAccountCodeAsync(context.CompanyCode, ct);

        var sessionFileIdSet = new HashSet<string>(sessionFileIds, StringComparer.OrdinalIgnoreCase);

        if (context.ShouldEnforceAccountWhitelist)
        {
            var disallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lineNode in linesEl.EnumerateArray())
            {
                if (lineNode.ValueKind != JsonValueKind.Object) continue;
                if (!lineNode.TryGetProperty("accountCode", out var accNode) || accNode.ValueKind != JsonValueKind.String)
                    continue;
                var accountCode = accNode.GetString();
                if (string.IsNullOrWhiteSpace(accountCode)) continue;
                if (!context.IsAccountAllowed(accountCode))
                {
                    disallowed.Add(accountCode.Trim());
                }
            }
            if (disallowed.Count > 0)
            {
                var denied = string.Join(", ", disallowed);
                var allowedText = context.ApprovedAccounts.Count > 0
                    ? string.Join(", ", context.ApprovedAccounts)
                    : Localize(context.Language, "会議�?交際�?仮払消費�?現金", "会議�?交际�?仮払消费�?现金");
                var msg = Localize(context.Language,
                    $"科目 {denied} はこのシナリオで許可されていません��lookup_account を呼び出し��次の科目��補を使用してくださ�? {allowedText}",
                    $"科目 {denied} 不允许，请先调用 lookup_account 并使用这些科目：{allowedText}");
                throw new Exception(msg);
            }
        }

        static decimal ReadAmount(JsonObject line)
        {
            if (line.TryGetPropertyValue("amount", out var node) && node is JsonValue value)
            {
                if (value.TryGetValue<decimal>(out var dec)) return dec;
                if (value.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
                if (value.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
            }
            return 0m;
        }

        static void WriteAmount(JsonObject line, decimal amount)
        {
            line["amount"] = amount;
        }

        static string ReadSide(JsonObject line)
        {
            string? Extract(JsonNode? node)
            {
                if (node is JsonValue val && val.TryGetValue<string>(out var raw))
                {
                    if (string.IsNullOrWhiteSpace(raw)) return null;
                    var upper = raw.Trim().ToUpperInvariant();
                    // Normalize variants to DR/CR
                    // Common variants: DR/CR, Debit/Credit, D/C, �?�?                    return upper switch
                    {
                        "DEBIT" => "DR",
                        "CREDIT" => "CR",
                        "D" => "DR",
                        "C" => "CR",
                        // sanitized
                        // sanitized
                        "借方" => "DR",
                        "貸方" => "CR",
                        _ => upper
                    };
                }
                return null;
            }

            if (line.TryGetPropertyValue("drcr", out var drcrNode))
            {
                var side = Extract(drcrNode);
                if (!string.IsNullOrEmpty(side))
                {
                    line["drcr"] = side;
                    return side;
                }
            }

            if (line.TryGetPropertyValue("side", out var sideNode))
            {
                var side = Extract(sideNode);
                if (!string.IsNullOrEmpty(side))
                {
                    line["drcr"] = side;
                    line.Remove("side");
                    return side;
                }
                line.Remove("side");
            }

            return string.Empty;
        }

        var headerNode = JsonNode.Parse(headerEl.GetRawText())?.AsObject() ?? new JsonObject();
        // UI 只展示一个��摘要��（header.summary）��为避免出现 summary/note 双轨造成困惑�?        // - 若模�?调用方写�?header.note（或类似字段）但没写 summary，则提升�?summary
        // - voucher header �?note/remarks/memo 等字段不�?VoucherForm 里展示，统一收敛�?summary
        if (!headerNode.TryGetPropertyValue("summary", out var summaryNode) || string.IsNullOrWhiteSpace(ReadStringFrom(summaryNode)))
        {
            foreach (var altKey in new[] { "note", "remarks", "memo", "description", "comment", "remark" })
            {
                if (headerNode.TryGetPropertyValue(altKey, out var altNode) && !string.IsNullOrWhiteSpace(ReadStringFrom(altNode)))
                {
                    headerNode["summary"] = ReadStringFrom(altNode)!.Trim();
                    break;
                }
            }
        }
        // 统一清理 header.note 等，避免后端存两份��摘要类字段�?        foreach (var altKey in new[] { "note", "remarks", "memo", "description", "comment", "remark" })
        {
            if (headerNode.ContainsKey(altKey))
            {
                headerNode.Remove(altKey);
            }
        }

        var linesNode = JsonNode.Parse(linesEl.GetRawText())?.AsArray() ?? new JsonArray();

        static string? ReadStringFrom(JsonNode? node)
        {
            return node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }

        // Booking 结算单：确保�?create_voucher 前已经有结构化抽取结果�?        // 现实�?LLM 可能跳过 extract_booking_settlement_data 直接 create_voucher，导致：
        // - 金额=0、字段缺�?�?反复 clarification/step limit
        // - postingDate 幻觉�?2023 �?会计期间错误
        // 因此这里后端强制做一次抽取兜底，并把抽取结果注册�?context 文档里，后续统一以此为准�?        JsonObject? ensuredBookingDoc = null;
        try
        {
            var scenarioKey0 = context.Scenarios.FirstOrDefault()?.ScenarioKey ?? string.Empty;
            if (scenarioKey0.Contains("voucher.ota.booking.settlement", StringComparison.OrdinalIgnoreCase))
            {
                var fileId0 = context.DefaultFileId;
                if (!string.IsNullOrWhiteSpace(fileId0))
                {
                    // if already extracted and has key amounts, reuse
                    if (context.TryGetDocument(fileId0, out var existingDocNode) && existingDocNode is JsonObject existingDoc)
                    {
                        var hasTotals =
                            TryGetJsonDecimal(existingDoc, "grossAmount").HasValue &&
                            TryGetJsonDecimal(existingDoc, "netAmount").HasValue &&
                            TryGetJsonDecimal(existingDoc, "commissionAmount").HasValue &&
                            TryGetJsonDecimal(existingDoc, "paymentFeeAmount").HasValue &&
                            !string.IsNullOrWhiteSpace(ReadJsonString(existingDoc, "paymentDate"));
                        if (hasTotals)
                        {
                            ensuredBookingDoc = existingDoc;
                        }
                    }

                    if (ensuredBookingDoc is null)
                    {
                        var file = context.ResolveFile(fileId0);
                        if (file is null)
                        {
                            throw new Exception($"resolve file failed for fileId={fileId0} in create_voucher Booking ensure-extract");
                        }
                        var data = await ExtractBookingSettlementDataAsync(fileId0, file, context, ct);
                        if (data is not null)
                        {
                            // Register / overwrite in context for consistency
                            context.RegisterDocument(fileId0, data);
                            ensuredBookingDoc = data;
                            try
                            {
                                _logger.LogInformation("[AgentKit] booking settlement ensured extracted json: fileId={FileId} docSessionId={DocSessionId} json={Json}",
                                    fileId0, context.ActiveDocumentSessionId ?? "", data.ToJsonString());
                            }
                            catch { /* ignore */ }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Don't block voucher creation here; downstream will either clarify or fail with a clear error.
            _logger.LogWarning(ex, "[AgentKit] Booking settlement ensure-extract failed");
        }

        // Booking 结算单：自动生成你想要的摘要格式
        // 例：2025�?1�?日支払予�?Booking 10�?9日~11�?�?        // 日期范围来自 extract_booking_settlement_data �?statementPeriod（由代码从明�?check-in/out 计算�?        try
        {
            var scenarioKey = context.Scenarios.FirstOrDefault()?.ScenarioKey ?? string.Empty;
            if (scenarioKey.Contains("voucher.ota.booking.settlement", StringComparison.OrdinalIgnoreCase))
            {
                // 从已解析�?document 中取 paymentDate/statementPeriod（存在则优先�?                JsonObject? bookingDoc = null;
                if (ensuredBookingDoc is not null)
                {
                    bookingDoc = ensuredBookingDoc;
                }
                else if (!string.IsNullOrWhiteSpace(context.DefaultFileId) &&
                         context.TryGetDocument(context.DefaultFileId, out var bookingDocNode) &&
                         bookingDocNode is JsonObject bookingDocObj)
                {
                    bookingDoc = bookingDocObj;
                }

                var payDate = ReadJsonString(bookingDoc, "paymentDate") ?? ReadJsonString(headerNode, "postingDate") ?? string.Empty;
                var period = ReadJsonString(bookingDoc, "statementPeriod") ?? string.Empty;

                static string FormatPayDateJp(string ymd)
                {
                    if (DateTime.TryParse(ymd, out var dt))
                    {
                        return $"{dt:yyyy年M月d日}";
                    }
                    return ymd;
                }

                static string FormatPeriodJp(string periodYmd)
                {
                    // sanitized
                    if (parts.Length != 2) return periodYmd;
                    if (!DateTime.TryParse(parts[0], out var a) || !DateTime.TryParse(parts[1], out var b)) return periodYmd;
                    if (a.Year == b.Year)
                    {
                        return string.Empty;
                    }
                    return $"{a:yyyy年M月d日}~{b:yyyy年M月d日}";
                }

                if (!string.IsNullOrWhiteSpace(payDate))
                {
                    // 强制用抽取到�?paymentDate 作为 postingDate，避免模型幻觉年份导致会计期间错�?                    // （如果用户正在回�?postingDate clarification，会在后面的 pendingField 注入逻辑覆盖�?                    if (DateTime.TryParse(payDate, out _))
                    {
                        headerNode["postingDate"] = payDate;
                    }
                    var summary = $"{FormatPayDateJp(payDate)}支払予定 Booking";
                    if (!string.IsNullOrWhiteSpace(period))
                    {
                        summary += $" {FormatPeriodJp(period)}";
                    }
                    headerNode["summary"] = summary;
                }

                // 同步到売掛金�?note（便于银行消�?棢�索）
                var periodNote = !string.IsNullOrWhiteSpace(period) ? FormatPeriodJp(period) : string.Empty;
                foreach (var item in linesNode)
                {
                    if (item is not JsonObject lineObj) continue;
                    var code = ReadAccountCode(lineObj);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    if (!string.Equals(code, "1100", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(code, "1110", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(code, "1120", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var current = ReadStringFrom(lineObj.TryGetPropertyValue("note", out var n) ? n : null) ?? string.Empty;
                    var desired = $"BOOKING.COM";
                    if (!string.IsNullOrWhiteSpace(periodNote)) desired += $" {periodNote}";
                    if (string.IsNullOrWhiteSpace(current) || !current.Contains("BOOKING", StringComparison.OrdinalIgnoreCase))
                    {
                        lineObj["note"] = desired;
                    }
                }

                // Booking 结算单：把��コミッション����決済サービスの手数料��拆成两条明细（即使科目相同也要分行，便于审计与对账�?                // 画面上如果只看到丢�条��支払手数料”，用户会误以为缺少字段�?                try
                {
                    decimal? comm = null;
                    decimal? payFee = null;
                    decimal? netAmt = null;
                    decimal? grossAmt = null;
                    if (bookingDoc is not null)
                    {
                        comm = TryGetJsonDecimal(bookingDoc, "commissionAmount");
                        payFee = TryGetJsonDecimal(bookingDoc, "paymentFeeAmount");
                        netAmt = TryGetJsonDecimal(bookingDoc, "netAmount");
                        grossAmt = TryGetJsonDecimal(bookingDoc, "grossAmount");
                    }

                    // Extra guard for possible column mix-up:
                    // expected: gross �?net + comm + fee, and gross >= net
                    // If it looks swapped (net is larger and equation fails), swap gross/net once.
                    if (grossAmt.HasValue && netAmt.HasValue && comm.HasValue && payFee.HasValue)
                    {
                        var diff0 = grossAmt.Value - (netAmt.Value + comm.Value + payFee.Value);
                        if ((grossAmt.Value < netAmt.Value && Math.Abs(diff0) > 1m) ||
                            (Math.Abs(diff0) > 10m && Math.Abs(netAmt.Value - (grossAmt.Value + comm.Value + payFee.Value)) <= 1m))
                        {
                            _logger.LogWarning("[AgentKit][Booking] totals look swapped; swapping gross/net. gross={Gross} net={Net} comm={Comm} fee={Fee}", grossAmt, netAmt, comm, payFee);
                            (grossAmt, netAmt) = (netAmt, grossAmt);
                        }
                    }

                    if (comm.HasValue && payFee.HasValue && comm.Value >= 0m && payFee.Value >= 0m && (comm.Value + payFee.Value) > 0m)
                    {
                        var roundedComm = decimal.Round(comm.Value, 0, MidpointRounding.AwayFromZero);
                        var roundedFee = decimal.Round(payFee.Value, 0, MidpointRounding.AwayFromZero);
                        var roundedNet = netAmt.HasValue ? decimal.Round(netAmt.Value, 0, MidpointRounding.AwayFromZero) : (decimal?)null;
                        var roundedGross = grossAmt.HasValue ? decimal.Round(grossAmt.Value, 0, MidpointRounding.AwayFromZero) : (decimal?)null;

                        // 1) Force line amounts to match extracted totals (do not trust model numbers).
                        // - AR(1100) = net
                        // - Revenue(4100) = gross
                        // - Fee lines: split into 2 rows (same account allowed)
                        if (roundedNet.HasValue || roundedGross.HasValue)
                        {
                            foreach (var item in linesNode)
                            {
                                if (item is not JsonObject lineObj) continue;
                                var code = ReadAccountCode(lineObj);
                                if (string.IsNullOrWhiteSpace(code)) continue;
                                if (roundedNet.HasValue && string.Equals(code, "1100", StringComparison.OrdinalIgnoreCase))
                                {
                                    lineObj["amount"] = roundedNet.Value;
                                }
                                if (roundedGross.HasValue && string.Equals(code, "4100", StringComparison.OrdinalIgnoreCase))
                                {
                                    lineObj["amount"] = roundedGross.Value;
                                }
                            }
                        }

                        // 2) Replace any existing 5500 lines with two explicit rows
                        var feeAccountCode = "5500";
                        var feeSide = "DR";
                        var toRemove = new List<JsonNode>();
                        foreach (var item in linesNode)
                        {
                            if (item is not JsonObject l) continue;
                            var code = ReadAccountCode(l);
                            if (!string.Equals(code, feeAccountCode, StringComparison.OrdinalIgnoreCase)) continue;
                            feeSide = ReadSide(l);
                            if (string.IsNullOrWhiteSpace(feeSide)) feeSide = "DR";
                            toRemove.Add(l);
                        }
                        foreach (var r in toRemove) linesNode.Remove(r);

                        JsonObject BuildFee(string label, decimal amount)
                        {
                            var line = new JsonObject
                            {
                                ["accountCode"] = feeAccountCode,
                                ["amount"] = amount,
                                ["drcr"] = feeSide,
                                ["note"] = label
                            };
                            return line;
                        }
                        linesNode.Add(BuildFee("コミッション", roundedComm));
                        linesNode.Add(BuildFee("決済サービスの手数料", roundedFee));
                        RecalculateTotals();

                        // Extra guard: if we have doc totals, ensure gross = net + comm + fee (±1)
                        if (grossAmt.HasValue && netAmt.HasValue)
                        {
                            var diff = decimal.Round(grossAmt.Value - (netAmt.Value + comm.Value + payFee.Value), 0, MidpointRounding.AwayFromZero);
                            if (Math.Abs(diff) > 1m)
                            {
                                _logger.LogWarning("[AgentKit][Booking] totals mismatch gross={Gross} net={Net} comm={Comm} fee={Fee} diff={Diff}", grossAmt, netAmt, comm, payFee, diff);
                            }
                        }
                    }
                }
                catch { /* don't block voucher creation */ }
            }
        }
        catch { /* avoid blocking voucher creation */ }

        static string? ReadNestedString(JsonObject? root, params string[] path)
        {
            if (root is null) return null;
            JsonNode? cur = root;
            foreach (var key in path)
            {
                if (cur is not JsonObject obj) return null;
                if (!obj.TryGetPropertyValue(key, out cur)) return null;
            }
            return ReadStringFrom(cur);
        }

        // 重要：不做��默�?customerId/vendorId”兜底（会��成静默误记账）�?        // 这里只支持��用户已经回�?clarification”的场景，将回答强制写入 payload，避免模型遗漏导致反复询�?step limit�?        var pendingField = context.PendingFieldName;
        var pendingValue = context.PendingFieldValue;
        if (!string.IsNullOrWhiteSpace(pendingField))
        {
            _logger.LogInformation("[AgentKit] create_voucher pendingField={PendingField} pendingValue='{PendingValue}'", pendingField, pendingValue ?? "");
        }

        const decimal Epsilon = 0.0001m;
        decimal debitTotal = 0m;
        decimal creditTotal = 0m;
        JsonObject? firstDebit = null;
        JsonObject? firstCredit = null;

        void RecalculateTotals()
        {
            debitTotal = 0m;
            creditTotal = 0m;
            firstDebit = null;
            firstCredit = null;

            foreach (var item in linesNode)
            {
                if (item is not JsonObject line) continue;
                var side = ReadSide(line);
                if (string.IsNullOrEmpty(side)) continue;

                var amount = ReadAmount(line);
                line["drcr"] = side;

                if (side == "DR")
                {
                    debitTotal += amount;
                    firstDebit ??= line;
                }
                else if (side == "CR")
                {
                    creditTotal += amount;
                    firstCredit ??= line;
                }
            }
        }

        RecalculateTotals();
        _logger.LogInformation("[AgentKit] create_voucher 初始借贷: DR={Debit} CR={Credit}", debitTotal, creditTotal);

        var accountCodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in linesNode)
        {
            if (item is not JsonObject line) continue;
            var code = ReadAccountCode(line);
            if (!string.IsNullOrWhiteSpace(code))
            {
                accountCodeSet.Add(code);
            }
        }
        await EnsureAccountCodesExistAsync(context.CompanyCode, accountCodeSet, ct);

        static string? ReadAccountCode(JsonObject line)
        {
            if (line.TryGetPropertyValue("accountCode", out var node) && node is JsonValue val && val.TryGetValue<string>(out var code))
            {
                return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
            }
            return null;
        }

        static decimal? TryGetDecimal(JsonObject obj, string property)
        {
            if (!obj.TryGetPropertyValue(property, out var node)) return null;
            if (node is JsonValue val)
            {
                if (val.TryGetValue<decimal>(out var dec)) return dec;
                if (val.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
                if (val.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
            }
            return null;
        }

        static string? TryGetString(JsonObject obj, string property)
        {
            if (!obj.TryGetPropertyValue(property, out var node)) return null;
            if (node is JsonValue val && val.TryGetValue<string>(out var str))
            {
                return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
            }
            return null;
        }

        decimal? expectedTotalAmount = null;
        JsonObject? invoiceObj = null;
        if (!string.IsNullOrWhiteSpace(context.DefaultFileId) && context.TryGetDocument(context.DefaultFileId, out var registeredDoc) && registeredDoc is JsonObject docObj)
        {
            invoiceObj = docObj;
        }

        var applyInvoiceDefaults = ShouldForceInvoiceDefaults(context) || invoiceObj is not null;
        if (applyInvoiceDefaults)
        {
            ApplyInvoiceHeaderDefaults(headerNode);
        }

        void ApplyInvoiceAdjustments()
        {
            if (invoiceObj is null) return;

            var totalAmount = TryGetDecimal(invoiceObj, "totalAmount");
            var taxAmount = TryGetDecimal(invoiceObj, "taxAmount");
            var taxRate = TryGetDecimal(invoiceObj, "taxRate");
            var invoiceNo = TryGetString(invoiceObj, "invoiceRegistrationNo");
            var partnerName = TryGetString(invoiceObj, "partnerName");
            var documentType = TryGetString(invoiceObj, "documentType");

            if (!string.IsNullOrEmpty(invoiceNo))
            {
                var normalized = new string(invoiceNo.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (!string.IsNullOrEmpty(normalized) && !headerNode.ContainsKey("invoiceRegistrationNo"))
                {
                    headerNode["invoiceRegistrationNo"] = normalized;
                }
            }
            if (!string.IsNullOrEmpty(partnerName) && !headerNode.ContainsKey("partnerName"))
            {
                headerNode["partnerName"] = partnerName;
            }
            if (!string.IsNullOrEmpty(documentType) && !string.IsNullOrEmpty(partnerName) && !headerNode.ContainsKey("summary"))
            {
                var summary = $"{documentType} | {partnerName}";
                headerNode["summary"] = summary;
            }

            if (taxAmount.HasValue && taxAmount.Value > 0.0001m && totalAmount.HasValue && totalAmount.Value >= taxAmount.Value)
            {
                var netAmount = Math.Max(0m, totalAmount.Value - taxAmount.Value);
                JsonObject? existingTaxLine = null;
                JsonObject? expenseLine = null;

                foreach (var item in linesNode)
                {
                    if (item is not JsonObject line) continue;
                    var side = ReadSide(line);
                    if (side != "DR") continue;
                    var code = ReadAccountCode(line);
                    if (string.Equals(code, inputTaxAccountCode, StringComparison.OrdinalIgnoreCase))
                    {
                        existingTaxLine ??= line;
                    }
                    else if (expenseLine is null)
                    {
                        expenseLine = line;
                    }
                }

                if (expenseLine is null)
                {
                    // 从场景配置中获取默认科目，如果没有配置则使用空字符串（让后续验证报错�?                    var scenario = context.Scenarios.FirstOrDefault();
                    var hints = scenario is not null ? ExtractExecutionHints(scenario) : null;
                    var defaultAccount = hints?.BelowOrEqualAccount ?? "";
                    
                    expenseLine = new JsonObject
                    {
                        ["accountCode"] = defaultAccount,
                        ["drcr"] = "DR"
                    };
                    linesNode.Add(expenseLine);
                }

                WriteAmount(expenseLine, Math.Round(netAmount, 2));

                // 使用 tax 嵌套对象格式，让 FinanceService 规范化时自动设置 baseLineNo
                // 而不是创建独立的消费税明细行
                var rateValue = taxRate.HasValue ? taxRate.Value : 10m;
                if (rateValue > 0 && rateValue < 1)
                {
                    rateValue *= 100;
                }

                var taxObj = new JsonObject
                {
                    ["accountCode"] = inputTaxAccountCode,
                    ["amount"] = Math.Round(taxAmount.Value, 2),
                    ["rate"] = Math.Round(rateValue, 0)
                };
                expenseLine["tax"] = taxObj;

                // 如果已有独立的消费税明细行，�?linesNode 中移除，避免重复
                if (existingTaxLine is not null)
                {
                    for (int i = linesNode.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(linesNode[i], existingTaxLine))
                        {
                            linesNode.RemoveAt(i);
                            break;
                        }
                    }
                }

                expectedTotalAmount = Math.Round(totalAmount.Value, 2);
            }
        }

        ApplyInvoiceAdjustments();
        
        RecalculateTotals();

        var creditAdjusted = false;

        if (firstCredit is null)
        {
            var creditLine = new JsonObject
            {
                ["accountCode"] = "1000",
                ["drcr"] = "CR",
                ["note"] = "現金支出"
            };
            linesNode.Add(creditLine);
            creditAdjusted = true;
            RecalculateTotals();
        }
        else
        {
            var code = ReadAccountCode(firstCredit);
            // 只有�?AI 没有指定贷方科目或指定的科目不存在时，才使用现金科目
            var shouldUseCash = string.IsNullOrWhiteSpace(code) || 
                                !await AccountExistsAsync(context.CompanyCode, code!, ct);
            if (shouldUseCash && !string.Equals(code, "1000", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[AgentKit] 贷方科目 {Code} 不存在或未指定，使用现金科目 1000", code ?? "(�?");
                firstCredit["accountCode"] = "1000";
                creditAdjusted = true;
            }
            if (!firstCredit.ContainsKey("note"))
            {
                firstCredit["note"] = "現金支出";
            }
        }

        if (expectedTotalAmount.HasValue && firstCredit is not null)
        {
            WriteAmount(firstCredit, expectedTotalAmount.Value);
            creditAdjusted = true;
        }

        if (creditAdjusted)
        {
            RecalculateTotals();
        }

        if (Math.Abs(debitTotal - creditTotal) >= 0.01m)
        {
            if (creditTotal <= Epsilon && debitTotal > Epsilon)
            {
                if (firstCredit is null)
                {
                    var creditLine = new JsonObject
                    {
                        ["accountCode"] = "1000",
                        ["drcr"] = "CR",
                        ["amount"] = Math.Round(debitTotal, 2)
                    };
                    linesNode.Add(creditLine);
                    firstCredit = creditLine;
                }
                else
                {
                    WriteAmount(firstCredit, Math.Round(debitTotal, 2));
                }
                creditTotal = debitTotal;
            }
            else if (Math.Abs(creditTotal) > Epsilon && debitTotal <= Epsilon)
            {
                // 只有贷方没有借方是异常情况，要求 AI 重新生成正确的传�?                throw new Exception(Localize(context.Language,
                    // sanitized
                    // sanitized
            }
            else
            {
                var diff = debitTotal - creditTotal;
                if (diff > 0.01m && firstCredit is not null)
                {
                    var current = ReadAmount(firstCredit);
                    WriteAmount(firstCredit, Math.Round(current + diff, 2));
                    creditTotal += diff;
                }
                else if (diff < -0.01m && firstDebit is not null)
                {
                    var current = ReadAmount(firstDebit);
                    WriteAmount(firstDebit, Math.Round(current + Math.Abs(diff), 2));
                    debitTotal += Math.Abs(diff);
                }
            }
        }

        var attachmentIds = new List<string>();
        if (args.TryGetProperty("attachments", out var attachEl))
        {
            if (attachEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in attachEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            attachmentIds.Add(id);
                        }
                    }
                }
            }
            else if (attachEl.ValueKind == JsonValueKind.String)
            {
                var id = attachEl.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    attachmentIds.Add(id);
                }
            }
        }
        if (attachmentIds.Count == 0)
        {
            attachmentIds.AddRange(sessionFileIds);
        }

        var normalizedAttachments = attachmentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var resolvedAttachments = new List<string>();
        foreach (var token in normalizedAttachments)
        {
            if (context.TryResolveAttachmentToken(token, out var resolved))
            {
                resolvedAttachments.Add(resolved);
            }
            else
            {
                _logger.LogInformation("log");
            }
        }
        if (resolvedAttachments.Count == 0)
        {
            resolvedAttachments.AddRange(sessionFileIds);
        }
        if (!string.IsNullOrWhiteSpace(context.DefaultFileId) &&
            !resolvedAttachments.Any(id => string.Equals(id, context.DefaultFileId, StringComparison.OrdinalIgnoreCase)))
        {
            resolvedAttachments.Add(context.DefaultFileId);
        }

        resolvedAttachments = resolvedAttachments
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalidAttachmentIds = resolvedAttachments
            .Where(id => !sessionFileIdSet.Contains(id))
            .ToArray();
        if (invalidAttachmentIds.Length > 0)
        {
            throw new Exception($"附件引用的文件不属于当前文档上下文：{string.Join(", ", invalidAttachmentIds)}");
        }

        var attachmentsNode = new JsonArray();
        foreach (var id in resolvedAttachments)
        {
            // 尝试获取文件完整信息
            var fileRecord = context.ResolveFile(id);
            if (fileRecord is not null)
            {
                var attObj = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = fileRecord.FileName,
                    ["contentType"] = fileRecord.ContentType,
                    ["size"] = fileRecord.Size
                };
                if (!string.IsNullOrWhiteSpace(fileRecord.BlobName))
                {
                    attObj["blobName"] = fileRecord.BlobName;
                    // 生成 URL（如果有 blob service�?                    try
                    {
                        var url = _blobService?.GetReadUri(fileRecord.BlobName);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            attObj["url"] = url;
                        }
                    }
                    catch { /* ignore */ }
                }
                attObj["uploadedAt"] = fileRecord.CreatedAt.ToString("O");
                attachmentsNode.Add(attObj);
            }
            else
            {
                // 回���：只�?ID（兼容旧逻辑�?                attachmentsNode.Add(id);
            }
        }

        // 自动填充 paymentDate：若 header �?postingDate/dueDate，但 lines 中的应付/应收科目没有 paymentDate，自动填充�?        // Booking 结算书场景里，��お支払い日”会作为 postingDate；但 Finance 校验要求 AR/AP 行上也要 paymentDate�?        string? headerPostingDate = null;
        if (headerNode.TryGetPropertyValue("postingDate", out var postingDateNode) && postingDateNode is JsonValue postingDateVal && postingDateVal.TryGetValue<string>(out var postingDate))
        {
            headerPostingDate = postingDate;
        }
        string? headerDueDate = null;
        if (headerNode.TryGetPropertyValue("dueDate", out var dueDateNode) && dueDateNode is JsonValue dueDateVal && dueDateVal.TryGetValue<string>(out var dueDate))
        {
            headerDueDate = dueDate;
        }
        var paymentDateFallback = !string.IsNullOrWhiteSpace(headerPostingDate) ? headerPostingDate : headerDueDate;
        if (!string.IsNullOrWhiteSpace(paymentDateFallback))
        {
            // 可能霢��?paymentDate 的账户代码（应付账款、应收账款等�?            var accountsNeedingPaymentDate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2100", "2110", "2120", "1100", "1110", "1120" };
            foreach (var item in linesNode)
            {
                if (item is not JsonObject lineObj) continue;
                var code = ReadAccountCode(lineObj);
                if (string.IsNullOrWhiteSpace(code)) continue;
                if (!accountsNeedingPaymentDate.Contains(code)) continue;
                // 棢�查是否已经有 paymentDate
                if (lineObj.TryGetPropertyValue("paymentDate", out var existingPd) && existingPd is JsonValue pdVal && pdVal.TryGetValue<string>(out var pdStr) && !string.IsNullOrWhiteSpace(pdStr))
                {
                    continue; // 已有 paymentDate，跳�?                }
                // 自动填充 paymentDate
                lineObj["paymentDate"] = paymentDateFallback;
                _logger.LogInformation("[AgentKit] 自动填充 paymentDate={PaymentDate} 到科�?{AccountCode}", paymentDateFallback, code);
            }
        }

        // 若用户已回答缺失字段（customerId/vendorId/paymentDate），在入库前强制注入到对应行
        if (!string.IsNullOrWhiteSpace(pendingField) && !string.IsNullOrWhiteSpace(pendingValue))
        {
            // postingDate is header-level: allow user to override to the correct period
            if (string.Equals(pendingField, "postingDate", StringComparison.OrdinalIgnoreCase))
            {
                if (!headerNode.TryGetPropertyValue("postingDate", out var pdNode) || string.IsNullOrWhiteSpace(ReadStringFrom(pdNode)))
                {
                    headerNode["postingDate"] = pendingValue;
                    _logger.LogInformation("log");
                }
                else
                {
                    // Even if already set, allow override (prevents wrong-year hallucinations)
                    headerNode["postingDate"] = pendingValue;
                    _logger.LogInformation("[AgentKit] 覆盖凭证抬头 postingDate={PostingDate}（来自用户回答）", pendingValue);
                }
            }

            // 常见：売掛金(1100�? �?customerId、買掛金(2100�? �?vendorId
            var accountsNeedingCustomerId = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1100", "1110", "1120" };
            var accountsNeedingVendorId = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2100", "2110", "2120" };
            foreach (var item in linesNode)
            {
                if (item is not JsonObject lineObj) continue;
                var code = ReadAccountCode(lineObj);
                if (string.IsNullOrWhiteSpace(code)) continue;

                if (string.Equals(pendingField, "customerId", StringComparison.OrdinalIgnoreCase) &&
                    accountsNeedingCustomerId.Contains(code))
                {
                    if (!lineObj.TryGetPropertyValue("customerId", out var cidNode) || string.IsNullOrWhiteSpace(ReadStringFrom(cidNode)))
                    {
                        lineObj["customerId"] = pendingValue;
                        _logger.LogInformation("[AgentKit] 注入用户回答 customerId={CustomerId} 到科�?{AccountCode}", pendingValue, code);
                    }
                }
                if (string.Equals(pendingField, "vendorId", StringComparison.OrdinalIgnoreCase) &&
                    accountsNeedingVendorId.Contains(code))
                {
                    if (!lineObj.TryGetPropertyValue("vendorId", out var vidNode) || string.IsNullOrWhiteSpace(ReadStringFrom(vidNode)))
                    {
                        lineObj["vendorId"] = pendingValue;
                        _logger.LogInformation("[AgentKit] 注入用户回答 vendorId={VendorId} 到科�?{AccountCode}", pendingValue, code);
                    }
                }
                if (string.Equals(pendingField, "paymentDate", StringComparison.OrdinalIgnoreCase))
                {
                    if (!lineObj.TryGetPropertyValue("paymentDate", out var pdNode) || string.IsNullOrWhiteSpace(ReadStringFrom(pdNode)))
                    {
                        lineObj["paymentDate"] = pendingValue;
                        _logger.LogInformation("[AgentKit] 注入用户回答 paymentDate={PaymentDate} 到科�?{AccountCode}", pendingValue, code);
                    }
                }
                if (string.Equals(pendingField, "postingDate", StringComparison.OrdinalIgnoreCase))
                {
                    // keep open-item paymentDate aligned with postingDate if missing
                    var accountsNeedingPaymentDate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1100", "1110", "1120", "2100", "2110", "2120" };
                    if (accountsNeedingPaymentDate.Contains(code))
                    {
                        if (!lineObj.TryGetPropertyValue("paymentDate", out var pdNode2) || string.IsNullOrWhiteSpace(ReadStringFrom(pdNode2)))
                        {
                            lineObj["paymentDate"] = pendingValue;
                            _logger.LogInformation("[AgentKit] 注入用户回答 paymentDate={PaymentDate}（来�?postingDate）到科目 {AccountCode}", pendingValue, code);
                        }
                    }
                }
            }
        }

        // 自动解析 customerId：若应收科目霢��?customerId 但未提供，尝试用场景/文件关键�?lookup_customer（唯丢�命中才自动填）�?        // 注意：PDF 里出�?“Booking.com�?并不等于系统里已经有 customerId，必须映射到 businesspartners.partner_code�?        {
            var accountsNeedingCustomerId = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1100", "1110", "1120" };
            var needsCustomerId = false;
            foreach (var item in linesNode)
            {
                if (item is not JsonObject lineObj) continue;
                var code = ReadAccountCode(lineObj);
                if (string.IsNullOrWhiteSpace(code) || !accountsNeedingCustomerId.Contains(code)) continue;
                if (!lineObj.TryGetPropertyValue("customerId", out var cidNode) || string.IsNullOrWhiteSpace(ReadStringFrom(cidNode)))
                {
                    needsCustomerId = true;
                    break;
                }
            }

            if (needsCustomerId)
            {
                var query = InferCustomerLookupQuery(context, attachmentsNode);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var resolved = await TryResolveCustomerCodeFromAnswerAsync(context.CompanyCode, query!, ct);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        foreach (var item in linesNode)
                        {
                            if (item is not JsonObject lineObj) continue;
                            var code = ReadAccountCode(lineObj);
                            if (string.IsNullOrWhiteSpace(code) || !accountsNeedingCustomerId.Contains(code)) continue;
                            if (!lineObj.TryGetPropertyValue("customerId", out var cidNode) || string.IsNullOrWhiteSpace(ReadStringFrom(cidNode)))
                            {
                                lineObj["customerId"] = resolved;
                                _logger.LogInformation("[AgentKit] auto-filled customerId={CustomerId} via lookup_customer('{Query}') for account {AccountCode}", resolved, query, code);
                            }
                        }
                    }
                }
            }
        }

        var root = new JsonObject
        {
            ["header"] = headerNode,
            ["lines"] = linesNode,
            ["attachments"] = attachmentsNode
        };

        var payloadJson = root.ToJsonString();
        _logger.LogInformation("[AgentKit] create_voucher payload: {Payload}", payloadJson);
        using var payloadDoc = JsonDocument.Parse(payloadJson);

        var table = Crud.TableFor("voucher");
        // Agent 场景：发票登记号无效时仍保存凭证，但在响应中警告用户
        var (insertedJson, invoiceWarning) = await _finance.CreateVoucher(
            context.CompanyCode, table, payloadDoc.RootElement, context.UserCtx,
            VoucherSource.Agent, sessionId: context.SessionId.ToString());
        using var insertedDoc = JsonDocument.Parse(insertedJson);
        var insertedRoot = insertedDoc.RootElement;
        var payload = insertedRoot.GetProperty("payload");
        var header = payload.GetProperty("header");
        var voucherNo = header.TryGetProperty("voucherNo", out var noEl) ? noEl.GetString() : null;
        Guid voucherId = Guid.Empty;
        if (insertedRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            Guid.TryParse(idEl.GetString(), out voucherId);
        }

        var tag = voucherNo is not null
            ? new
            {
                label = voucherNo,
                action = "openEmbed",
                key = "vouchers.list",
                payload = new
                {
                    voucherNo,
                    detailOnly = true
                }
            }
            : null;

        var content = voucherNo is not null
            // sanitized
            : Localize(context.Language, "会計伝票を作成しまし�?, "已创建会计凭�?);

        context.MarkVoucherCreated(voucherNo);
        if (context.TaskId.HasValue)
        {
            try
            {
                var metadata = new JsonObject
                {
                    ["voucherNo"] = voucherNo ?? string.Empty,
                    ["completedAt"] = DateTimeOffset.UtcNow.ToString("O")
                };
                await _invoiceTaskService.UpdateStatusAsync(context.TaskId.Value, "completed", metadata, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentKit] 更新票据任务状��失�?taskId={TaskId}", context.TaskId.Value);
            }
        }

        var messages = new List<AgentResultMessage> { new AgentResultMessage("assistant", content, "success", tag) };
        
        // Agent 场景：发票登记号无效时，添加警告消息但不阻止凭证创建
        if (invoiceWarning is { HasRegistrationNo: true, IsValid: false })
        {
            var warningContent = Localize(context.Language,
                $"⚠️ インボイス登録番�?{invoiceWarning.RegistrationNo} の検証に失敗しました: {invoiceWarning.Status}",
                $"⚠️ 发票登记�?{invoiceWarning.RegistrationNo} 验证失败: {invoiceWarning.Status}");
                messages.Add(new AgentResultMessage("assistant", "AI failed to create voucher. Please retry.", "warning", null));

                        // Skip header rows and empty rows
                        if (string.IsNullOrWhiteSpace(rowText)) continue;
                        if (ContainsGrossHeader(rowText) || ContainsCommHeader(rowText) || ContainsNetHeader(rowText)) continue;

                        // Collect numeric tokens per column by x position
                        string? tokGross = null, tokComm = null, tokFee = null, tokNet = null;
                        foreach (var w in rowWords)
                        {
                            var t = w.Text.Trim();
                            if (t.Length == 0) continue;
                            // Ignore page number like "1/2"
                            if (t.Contains('/')) continue;
                            // Only consider tokens that look like numbers
                            if (!char.IsDigit(t[0]) && t[0] != '-' && t[0] != '.') continue;

                            var x = w.BoundingBox.Left + w.BoundingBox.Width / 2.0;
                            if (x < b1) tokGross = t;
                            else if (x < b2) tokComm = t;
                            else if (x < b3) tokFee = t;
                            else tokNet = t;
                        }

                        if (tokGross is null || tokComm is null || tokFee is null || tokNet is null) continue;
                        if (!TryParseMoneyToken(tokGross, out var g)) continue;
                        if (!TryParseMoneyToken(tokComm, out var c)) continue;
                        if (!TryParseMoneyToken(tokFee, out var f)) continue;
                        if (!TryParseMoneyToken(tokNet, out var n)) continue;

                        gross += g;
                        commission += Math.Abs(c);
                        paymentFee += Math.Abs(f);
                        net += n;
                        matchedRows++;
                    }
                }
            }
            catch
            {
                return false;
            }

            return matchedRows >= 2 && gross > 0m && net > 0m;
        }

        static string NormalizeTextForBooking(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                // full-width digits �?ASCII digits
                if (ch >= '�? && ch <= '�?)
                {
                    sb.Append((char)('0' + (ch - '�?)));
                    continue;
                }
                // Kangxi radical variants (used in some PDF extractions) �?standard kanji
                // �?(U+2F40) �?�? �?(U+2F42) �?�? �?(U+2FA6) �?�? �?(U+2F26) �?�?                sb.Append(ch switch
                {
                    '\u2F40' => '�?, // �?�?�?                    '\u2F42' => '�?, // �?�?�?                    '\u2FA6' => '�?, // �?�?�?                    '\u2F26' => '�?, // �?�?�?                    '�? => '/',
                    '�? => '-',
                    '�? => '-',
                    '�? => '-',
                    '�? => '-',
                    '�? => '.',
                    '�? => ':',
                    '〢�' => ' ',
                    '\n' => ' ', // newlines to spaces for easier regex matching
                    '\r' => ' ',
                    _ => ch
                });
            }
            // Collapse multiple spaces
            return Regex.Replace(sb.ToString(), @"\s{2,}", " ");
        }

        sanitized = NormalizeTextForBooking(sanitized);

        // 0) If PDF, try geometry-based extraction first for stability.
        if (string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (TryComputeBookingTotalsFromPdfGeometry(file.StoredPath, out var g0, out var c0, out var f0, out var n0, out var rows0))
            {
                var paymentDateDet0 = ExtractPaymentDate(sanitized);
                var facilityIdDet0 = ExtractFacilityId(sanitized);
                var result0 = new JsonObject
                {
                    ["status"] = "ok",
                    ["platform"] = "BOOKING",
                    ["facilityId"] = facilityIdDet0,
                    ["paymentDate"] = paymentDateDet0,
                    ["currency"] = "JPY",
                    ["grossAmount"] = decimal.Round(g0, 0, MidpointRounding.AwayFromZero),
                    ["commissionAmount"] = decimal.Round(c0, 0, MidpointRounding.AwayFromZero),
                    ["paymentFeeAmount"] = decimal.Round(f0, 0, MidpointRounding.AwayFromZero),
                    ["netAmount"] = decimal.Round(n0, 0, MidpointRounding.AwayFromZero),
                    ["statementPeriod"] = ""
                };
                _logger.LogInformation("[AgentKit] booking settlement geometry totals rows={Rows} gross={Gross} commission={Comm} fee={Fee} net={Net}", rows0, result0["grossAmount"], result0["commissionAmount"], result0["paymentFeeAmount"], result0["netAmount"]);
                _logger.LogInformation("[AgentKit] booking settlement extracted json: {Json}", result0.ToJsonString());
                return result0;
            }
        }

        // 先尝试��确定��解析��：从明细行中按列汇总（避免 LLM 只看到部分行导致合计错误�?        // Booking 明细通常包含：��貨(JPY) + 金額 + コミッション + 決済サービスの手数料(或支払い手数�? + 純収�?        // 这些列在 PDF 文本中往徢�会出现在同一行或相邻 token，正则能稳定匹配�?        static bool TryComputeBookingTotals(string text, out decimal gross, out decimal commission, out decimal paymentFee, out decimal net, out int matchedRows, out DateTime? minCheckIn, out DateTime? maxCheckOut)
        {
            gross = commission = paymentFee = net = 0m;
            matchedRows = 0;
            minCheckIn = null;
            maxCheckOut = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // normalize minus variants
            text = text.Replace('�?, '-').Replace('�?, '-').Replace('�?, '-');
            var lines = text.Split('\n');

            static bool TryParseMoney(string raw, out decimal value)
            {
                value = 0m;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                var cleaned = raw.Trim()
                    .Replace(",", "")
                    .Replace("¥", "")
                    .Replace("JPY", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            }

            // Example row (from screenshot): "JPY 18900.00 -2835 -435 15630.00"
            // Some PDFs omit "JPY" on each detail row (it may only appear in header), so currency is optional here.
            // We match 4 numeric columns: 金額 / コミッション / 決済手数�?/ 純収�?            // Commission/Fee can be negative.
            // IMPORTANT: PdfPig's page.Text often has NO SPACES between values (e.g., "JPY18900.00-2835-43515630.00")
            // So we use \s* (optional whitespace) and rely on the negative sign "-" as implicit separator for comm/fee.
            // Pattern: JPY[space?]gross[space?](-comm)[space?](-fee)[space?]net
            var rowRe = new Regex(@"JPY\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*(-[0-9][0-9,]*)\s*(-[0-9][0-9,]*)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // Prefer explicit totals line if present (more stable than summing many detail rows)
            // Examples:
            // - 合計 JPY 635392.00 -xxxx.xx -yyyy.yy 525372.00
            // - 合計 ¥335,295 ¥-49,218 ¥-7,713 ¥279,437  (with yen symbol, possibly across lines)
            // - Total JPY ...
            // Note: ¥/�?symbols are common in Booking PDFs, and numbers may span lines.
            var totalRe = new Regex(@"(?:合計|Total)[\s:：\-]*(?:JPY\s+)?[¥￥]?([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*[¥￥]?(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*[¥￥]?(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*[¥￥]?(-?[0-9][0-9,]*(?:\.[0-9]{1,2})?)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            static bool TryParseJpDate(string raw, out DateTime dt)
            {
                dt = default;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                // sanitized
                if (m.Success)
                {
                    var y = int.Parse(m.Groups[1].Value);
                    var mm = int.Parse(m.Groups[2].Value);
                    var dd = int.Parse(m.Groups[3].Value);
                    dt = new DateTime(y, mm, dd);
                    return true;
                }
                // 2025-11-18
                if (DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                {
                    return true;
                }
                return false;
            }

            static void UpdatePeriodFromLine(string line, ref DateTime? minIn, ref DateTime? maxOut)
            {
                // 明細行にはチェックイ�?チェックゃ6�9��トが入っていることが多いので、同丢�行から日付を2つ拾�?                // �?つ目=チェックイン�?つ目=チェックゃ6�9��ト）として期間を更新する�?                var matches = Regex.Matches(line, @"[0-9]{4}\s*年\s*[0-9]{1,2}\s*月\s*[0-9]{1,2}\s*日|[0-9]{4}-[0-9]{2}-[0-9]{2}");
                if (matches.Count < 2) return;
                if (!TryParseJpDate(matches[0].Value, out var inDt)) return;
                if (!TryParseJpDate(matches[1].Value, out var outDt)) return;
                if (minIn is null || inDt.Date < minIn.Value.Date) minIn = inDt.Date;
                if (maxOut is null || outDt.Date > maxOut.Value.Date) maxOut = outDt.Date;
            }

            // Reduce false positives: try to locate the table header area first.
            // (Booking settlement tables usually contain these column labels)
            // sanitized
            if (tableStart < 0) tableStart = text.IndexOf("コミッション", StringComparison.OrdinalIgnoreCase);
            if (tableStart < 0) tableStart = text.IndexOf("決済サービス", StringComparison.OrdinalIgnoreCase);
            var scanText = tableStart >= 0 ? text.Substring(tableStart) : text;

            // 1) Prefer totals row if present (search in scanText; totals may wrap lines)
            var mtAll = totalRe.Match(scanText);
            if (mtAll.Success)
            {
                if (TryParseMoney(mtAll.Groups[1].Value, out var tg) &&
                    TryParseMoney(mtAll.Groups[2].Value, out var tc) &&
                    TryParseMoney(mtAll.Groups[3].Value, out var tf) &&
                    TryParseMoney(mtAll.Groups[4].Value, out var tn))
                {
                    gross = tg;
                    commission = Math.Abs(tc);
                    paymentFee = Math.Abs(tf);
                    net = tn;
                    matchedRows = 999; // marker: totals-based
                    // period still from detail lines (if any)
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0) continue;
                        UpdatePeriodFromLine(line, ref minCheckIn, ref maxCheckOut);
                    }
                    // Fixup if gross looks missing or equals net while fees exist (some PDFs' "合計" may show net only elsewhere)
                    var expectedGross = net + commission + paymentFee;
                    if ((gross <= 0m || Math.Abs(gross - net) <= 1m) && expectedGross > 0m)
                    {
                        gross = expectedGross;
                    }
                    return gross > 0m && net > 0m;
                }
            }

            // 2) Sum detail rows by scanning whole text.
            // Important: Pdf text extraction may split a single table row across line breaks.
            // \s+ matches newlines, so global matching is more robust than per-line.
            var matches = rowRe.Matches(scanText);
            foreach (Match m in matches)
            {
                if (!m.Success) continue;
                if (!TryParseMoney(m.Groups[1].Value, out var g)) continue;
                if (!TryParseMoney(m.Groups[2].Value, out var c)) continue;
                if (!TryParseMoney(m.Groups[3].Value, out var f)) continue;
                if (!TryParseMoney(m.Groups[4].Value, out var n)) continue;
                gross += g;
                commission += Math.Abs(c);
                paymentFee += Math.Abs(f);
                net += n;
                matchedRows++;
            }

            // period from detail lines (line-based is fine)
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                UpdatePeriodFromLine(line, ref minCheckIn, ref maxCheckOut);
            }

            // If we got enough rows, trust deterministic totals, with a final fixup to keep columns aligned:
            // - gross should represent 「金額��合�?            // - net should represent 「純収益」合�?            // In some extraction edge cases gross can become equal to net; if fees exist, reconstruct gross.
            if (matchedRows >= 2 && net > 0m)
            {
                var expectedGross = net + commission + paymentFee;
                if ((gross <= 0m || Math.Abs(gross - net) <= 1m) && expectedGross > 0m)
                {
                    gross = expectedGross;
                }
            }
            return matchedRows >= 2 && gross > 0m && net > 0m;
        }

        static string ExtractPaymentDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            // Normalize once more for safety (in case callers pass raw text)
            text = NormalizeTextForBooking(text);

            // 1) Japanese date: お支払い�?2025�?1�?0�?/ 支払予定�?/ お支払い予定�?/ 振込�?            var m = Regex.Match(
                text,
                // sanitized
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var y = int.Parse(m.Groups[1].Value);
                var mm = int.Parse(m.Groups[2].Value);
                var dd = int.Parse(m.Groups[3].Value);
                return new DateTime(y, mm, dd).ToString("yyyy-MM-dd");
            }

            // 2) Slash or hyphen: 2025/11/20 or 2025-11-20 near payment keywords
            m = Regex.Match(
                text,
                @"(?:お支払い日|支払日|支払予定日|お支払い予定日|振込�?\s*[:：]?\s*([0-9]{4})\s*[-/\.]\s*([0-9]{1,2})\s*[-/\.]\s*([0-9]{1,2})",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var y = int.Parse(m.Groups[1].Value);
                var mm = int.Parse(m.Groups[2].Value);
                var dd = int.Parse(m.Groups[3].Value);
                return new DateTime(y, mm, dd).ToString("yyyy-MM-dd");
            }

            // 3) Fallback: any yyyy-MM-dd / yyyy/MM/dd in the first part of document (but DO NOT invent)
            // Limit to head to avoid catching stay dates in line items.
            var head = text.Length > 6000 ? text.Substring(0, 6000) : text;
            m = Regex.Match(head, @"\b([0-9]{4})\s*[-/\.]\s*([0-9]{1,2})\s*[-/\.]\s*([0-9]{1,2})\b");
            if (m.Success)
            {
                var y = int.Parse(m.Groups[1].Value);
                var mm = int.Parse(m.Groups[2].Value);
                var dd = int.Parse(m.Groups[3].Value);
                // guard: plausible range
                if (y >= 2000 && y <= 2100)
                {
                    return new DateTime(y, mm, dd).ToString("yyyy-MM-dd");
                }
            }

            return string.Empty;
        }

        static string ExtractFacilityId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var m = Regex.Match(text, @"宿泊施設ID\s*([0-9]{5,})");
            if (!m.Success) m = Regex.Match(text, @"施設ID\s*([0-9]{5,})");
            return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        }

        if (TryComputeBookingTotals(sanitized, out var grossDet, out var commDet, out var feeDet, out var netDet, out var rows, out var minInDet, out var maxOutDet))
        {
            var paymentDateDet = ExtractPaymentDate(sanitized);
            var facilityIdDet = ExtractFacilityId(sanitized);
            var periodDet = (minInDet.HasValue && maxOutDet.HasValue)
                ? $"{minInDet.Value:yyyy-MM-dd}..{maxOutDet.Value:yyyy-MM-dd}"
                : "";
            var result = new JsonObject
            {
                ["status"] = "ok",
                ["platform"] = "BOOKING",
                ["facilityId"] = facilityIdDet,
                ["paymentDate"] = paymentDateDet,
                ["currency"] = "JPY",
                ["grossAmount"] = decimal.Round(grossDet, 0, MidpointRounding.AwayFromZero),
                ["commissionAmount"] = decimal.Round(commDet, 0, MidpointRounding.AwayFromZero),
                ["paymentFeeAmount"] = decimal.Round(feeDet, 0, MidpointRounding.AwayFromZero),
                ["netAmount"] = decimal.Round(netDet, 0, MidpointRounding.AwayFromZero),
                ["statementPeriod"] = periodDet
            };
            _logger.LogInformation("[AgentKit] booking settlement deterministic totals rows={Rows} gross={Gross} commission={Comm} fee={Fee} net={Net}", rows, result["grossAmount"], result["commissionAmount"], result["paymentFeeAmount"], result["netAmount"]);
            _logger.LogInformation("[AgentKit] booking settlement extracted json: {Json}", result.ToJsonString());
            return result;
        }
        
        // Booking の結算書は明細が長くなりがちで��モデル出力(JSON)が��中で切れる原因になる�?        // 记账只需要合计字段，因此先做“关键词聚焦”：只保留与合计/支付�?设施ID相关的片段，降低 token 与输出长度�?        static string FocusBookingSettlementText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var lines = text.Split('\n');
            var keywords = new[]
            {
                // sanitized
                "宿泊施設", "施設", "facility", "property", "施設ID", "宿泊施設ID",
                "純収�?, "純収�?, "net", "net amount",
                "金額", "売上", "gross", "amount",
                "コミッション", "commission",
                "決済サービス", "決済サービスの手数料",
                // sanitized
                "合計", "合算", "total", "total amount"
            };
            var keep = new List<string>(Math.Min(lines.Length, 400));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    // include small context window
                    var start = Math.Max(0, i - 2);
                    var end = Math.Min(lines.Length - 1, i + 2);
                    for (var j = start; j <= end; j++)
                    {
                        var l = lines[j].TrimEnd();
                        if (string.IsNullOrWhiteSpace(l)) continue;
                        if (keep.Count == 0 || !string.Equals(keep[^1], l, StringComparison.Ordinal))
                        {
                            keep.Add(l);
                        }
                    }
                    if (keep.Count >= 320) break;
                }
            }
            if (keep.Count < 20)
            {
                // fallback: head-only (still bounded)
                return string.Join('\n', lines.Take(200));
            }
            return string.Join('\n', keep);
        }
        
        var focused = FocusBookingSettlementText(sanitized);

        var http = _httpClientFactory.CreateClient("openai");
        OpenAiApiHelper.SetOpenAiHeaders(http, context.ApiKey);

        var metadata = new
        {
            fileId,
            file.FileName,
            file.ContentType,
            file.Size,
            companyCode = context.CompanyCode
        };

        var userContent = new List<object>
        {
            new { type = "text", text = JsonSerializer.Serialize(metadata, JsonOptions) }
        };
        if (!string.IsNullOrWhiteSpace(focused))
        {
            userContent.Add(new { type = "text", text = focused });
        }

        var sysPrompt = string.Equals(context.Language, "zh", StringComparison.OrdinalIgnoreCase)
            // sanitized
你是日本会计的结算单解析助手。请�?Booking.com 的��お支払い明細�?结算书中提取结构化数据，并严格输出一�?JSON�?
必须输出字段�?- platform: 固定�?"BOOKING"
- facilityId: 宿泊施設ID（如 9113577），没有则空字符�?- paymentDate: お支払い日，格式 YYYY-MM-DD，没有则空字符串
- currency: 货币，优�?JPY
- grossAmount: 表格中��金額��合计（数��，正数�?- commissionAmount: 表格中��コミッション��合计（数��，正数；原表可能为负数�?- paymentFeeAmount: 表格中��支払い手数料��合计（数��，正数；原表可能为负数�?- netAmount: 表格中��純収益」合计（数��，正数�?- statementPeriod: 可��，例如 "2025-09-18..2025-09-24"（根据チェックアウト/チェックイン推导，拿不到则空字符串）

重要：记账只霢�要��合计金�?支付�?设施ID”��为了稳定��，禁止输出明细列表�?- reservations: 必须省略（不要输出该字段）�?
// sanitized
            // sanitized
あなたは Booking.com の��お支払い明細��解析アシスタントです��与えられたテキストから、必ず単丢��?JSON を出力してください��?
必須フィールド：
- platform: 固定�?\"BOOKING\"
- facilityId: 宿泊施設ID（例 9113577）��見つからなければ空文字
- paymentDate: お支払い日（YYYY-MM-DD）��見つからなければ空文字
- currency: 通貨（��常 JPY�?- grossAmount: 表の「金額��合計（正の数）
- commissionAmount: 表の「コミッション��合計（正の数��表でマイナスでも絶対��）
- paymentFeeAmount: 表の「支払い手数料��合計（正の数��表でマイナスでも絶対��）
- netAmount: 表の「純収益」合計（正の数）
- statementPeriod: 任意（チェックアウト期間など）��取れなければ空文字

重要：会計処理には合計だけが必要です。安定��のため明細配列は絶対に出力しないでください�?- reservations: 必ず省略（出力禁止）�?
ルール：
// sanitized

        var messages = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user", content = userContent.ToArray() }
        };

        var resp = await OpenAiApiHelper.CallOpenAiAsync(
            http, context.ApiKey, "gpt-4o", messages, temperature: 0, maxTokens: 700, jsonMode: true, ct: ct);

        if (string.IsNullOrWhiteSpace(resp.Content))
        {
            return new JsonObject { ["status"] = "error", ["error"] = "empty response" };
        }

        static JsonObject? TryParseJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = CleanMarkdownJson(raw);
            try
            {
                return JsonNode.Parse(cleaned) as JsonObject;
            }
            catch
            {
                // 尝试截取第一�?{ 到最后一�?} 之间�?JSON（常见于被截�?夹杂文本的情况）
                var first = cleaned.IndexOf('{');
                var last = cleaned.LastIndexOf('}');
                if (first >= 0 && last > first)
                {
                    var slice = cleaned.Substring(first, last - first + 1);
                    try { return JsonNode.Parse(slice) as JsonObject; } catch { }
                }
                return null;
            }
        }

        var parsed = TryParseJsonObject(resp.Content!);
        if (parsed is not null)
        {
            parsed["status"] = "ok";
            _logger.LogInformation("[AgentKit] booking settlement extracted json: {Json}", parsed.ToJsonString());
            return parsed;
        }

        // JSON が壊れている場合�?回だけ��修正し�?JSON のみ出力」リトラ�?        try
        {
            var repairSys = string.Equals(context.Language, "zh", StringComparison.OrdinalIgnoreCase)
                // sanitized
                // sanitized
            var repairUser = new
            {
                note = "repair invalid json output",
                previousOutput = (resp.Content ?? string.Empty).Length > 4000 ? resp.Content!.Substring(0, 4000) : resp.Content,
                extractedText = focused.Length > 12000 ? focused.Substring(0, 12000) : focused
            };
            var repairMessages = new object[]
            {
                new { role = "system", content = sysPrompt + "\n\n" + repairSys },
                new { role = "user", content = JsonSerializer.Serialize(repairUser, JsonOptions) }
            };
            var repairResp = await OpenAiApiHelper.CallOpenAiAsync(
                http, context.ApiKey, "gpt-4o", repairMessages, temperature: 0, maxTokens: 600, jsonMode: true, ct: ct);
            var repaired = TryParseJsonObject(repairResp.Content ?? string.Empty);
            if (repaired is not null)
            {
                repaired["status"] = "ok";
                repaired["repaired"] = true;
                _logger.LogInformation("[AgentKit] booking settlement extracted json: {Json}", repaired.ToJsonString());
                return repaired;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] extract_booking_settlement_data repair attempt failed");
        }

        return new JsonObject { ["status"] = "error", ["error"] = "invalid json from model" };
    }

    private async Task<JsonObject> FindMoneytreeDepositForSettlementAsync(
        string companyCode,
        DateTime paymentDate,
        decimal netAmount,
        int daysTolerance,
        decimal amountTolerance,
        List<string> keywords,
        CancellationToken ct)
    {
        var start = paymentDate.AddDays(-Math.Abs(daysTolerance));
        var end = paymentDate.AddDays(Math.Abs(daysTolerance));

        if (keywords.Count == 0)
        {
            keywords.Add("BOOKING");
            keywords.Add("BOOKING.COM");
            // sanitized
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
SELECT id, transaction_date, deposit_amount, description, voucher_no, voucher_id
FROM moneytree_transactions
WHERE company_code = $1
  AND posting_status = 'posted'
  AND voucher_no IS NOT NULL
  AND transaction_date BETWEEN $2 AND $3
  AND COALESCE(deposit_amount, 0) > 0
  AND ABS(COALESCE(deposit_amount, 0) - $4) <= $5
ORDER BY ABS(EXTRACT(EPOCH FROM (transaction_date::timestamp - $6::timestamp))) ASC, imported_at ASC
// sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(start);
        cmd.Parameters.AddWithValue(end);
        cmd.Parameters.AddWithValue(netAmount);
        cmd.Parameters.AddWithValue(amountTolerance);
        cmd.Parameters.AddWithValue(paymentDate);

        var candidates = new List<(Guid Id, DateTime Date, decimal Amount, string? Desc, string VoucherNo, Guid? VoucherId)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var dt = reader.IsDBNull(1) ? paymentDate : reader.GetDateTime(1);
            var amt = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
            var desc = reader.IsDBNull(3) ? null : reader.GetString(3);
            var voucherNo = reader.GetString(4);
            Guid? voucherId = null;
            if (!reader.IsDBNull(5)) voucherId = reader.GetGuid(5);
            candidates.Add((id, dt, amt, desc, voucherNo, voucherId));
        }

        var filtered = candidates
            .Where(c =>
            {
                var d = c.Desc ?? string.Empty;
                return keywords.Any(k => !string.IsNullOrWhiteSpace(k) && d.Contains(k, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        var best = filtered.FirstOrDefault();
        if (best == default)
        {
            best = candidates.FirstOrDefault();
        }

        if (best == default || string.IsNullOrWhiteSpace(best.VoucherNo))
        {
            return new JsonObject
            {
                ["found"] = false,
                ["message"] = "no matching posted deposit voucher found"
            };
        }

        return new JsonObject
        {
            ["found"] = true,
            ["transactionId"] = best.Id.ToString(),
            ["transactionDate"] = best.Date.ToString("yyyy-MM-dd"),
            ["amount"] = best.Amount,
            ["description"] = best.Desc ?? string.Empty,
            ["voucherNo"] = best.VoucherNo,
            ["voucherId"] = best.VoucherId?.ToString() ?? string.Empty
        };
    }

    private sealed record ScenarioRoutingAlternative(string ScenarioKey, double Confidence);
    private sealed record ScenarioRoutingResult(string ScenarioKey, double Confidence, string? Reason, List<ScenarioRoutingAlternative> Alternatives);

    private async Task<ScenarioRoutingResult?> RouteScenarioForFileAsync(
        IReadOnlyList<AgentScenarioService.AgentScenario> activeScenarios,
        FileMatchContext file,
        string apiKey,
        string language,
        CancellationToken ct)
    {
        try
        {
            // 只提供启用场景清单（用户要求�?            var scenarioLines = new List<string>(activeScenarios.Count);
            foreach (var s in activeScenarios.OrderBy(s => s.Priority).ThenByDescending(s => s.UpdatedAt))
            {
                var title = string.IsNullOrWhiteSpace(s.Title) ? s.ScenarioKey : s.Title!;
                var desc = string.IsNullOrWhiteSpace(s.Description) ? string.Empty : s.Description!.Trim();
                var meta = ParseScenarioMetadata(s);
                var hints = new List<string>();
                if (meta.MimeTypes.Count > 0) hints.Add("mime=" + string.Join(",", meta.MimeTypes.Take(3)));
                if (meta.ContentContains.Count > 0) hints.Add("contains=" + string.Join(",", meta.ContentContains.Take(4)));
                if (meta.FileNameContains.Count > 0) hints.Add("fileName=" + string.Join(",", meta.FileNameContains.Take(4)));
                var hintText = hints.Count > 0 ? $" [{string.Join(";", hints)}]" : string.Empty;
                scenarioLines.Add($"- {s.ScenarioKey}: {title}{(string.IsNullOrWhiteSpace(desc) ? "" : " �?" + desc)}{hintText}");
            }

            var sys = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                // sanitized
你是“文件处理场景路由器”��给你一个文件的文本预览，以及系统中“启用��的场景清单。你必须从清单里选择朢�合��的丢��?scenario_key�?
输出必须是单�?JSON（不要加任何多余文本）：
{
  "scenarioKey": string,
  "confidence": number,   // 0~1
  "reason": string,       // 箢�短中文理�?  "alternatives": [ { "scenarioKey": string, "confidence": number } ]
}

// sanitized
                // sanitized
あなたは「ファイル処理シナリオのルーター」です��ファイルのテキストプレビューと有効シナリオ丢�覧から��最適な scenario_key �?つ選んでください�?
出力は単丢��?JSON のみ�?{
  "scenarioKey": string,
  "confidence": number, // 0~1
  "reason": string,
  "alternatives": [ { "scenarioKey": string, "confidence": number } ]
}

// sanitized

            var fileInfo = new
            {
                fileName = file.FileName,
                contentType = file.ContentType,
                preview = string.IsNullOrWhiteSpace(file.Preview) ? null : file.Preview,
                parsedData = file.ParsedData is null ? null : JsonSerializer.Deserialize<object>(file.ParsedData.ToJsonString(), JsonOptions)
            };

            var user = new
            {
                file = fileInfo,
                scenarios = scenarioLines
            };

            var http = _httpClientFactory.CreateClient("openai");
            OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);
            var messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = JsonSerializer.Serialize(user, JsonOptions) }
            };

            var resp = await OpenAiApiHelper.CallOpenAiAsync(http, apiKey, "gpt-4o", messages, temperature: 0, maxTokens: 1600, jsonMode: true);
            if (string.IsNullOrWhiteSpace(resp.Content)) return null;
            using var doc = JsonDocument.Parse(resp.Content!);
            var root = doc.RootElement;
            var scenarioKey = root.TryGetProperty("scenarioKey", out var sk) && sk.ValueKind == JsonValueKind.String ? sk.GetString() : null;
            var confidence = root.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number ? cf.GetDouble() : 0d;
            var reason = root.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
            var alts = new List<ScenarioRoutingAlternative>();
            if (root.TryGetProperty("alternatives", out var altEl) && altEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in altEl.EnumerateArray().Take(3))
                {
                    var k = a.TryGetProperty("scenarioKey", out var ak) && ak.ValueKind == JsonValueKind.String ? ak.GetString() : null;
                    var c = a.TryGetProperty("confidence", out var ac) && ac.ValueKind == JsonValueKind.Number ? ac.GetDouble() : 0d;
                    if (!string.IsNullOrWhiteSpace(k)) alts.Add(new ScenarioRoutingAlternative(k!, c));
                }
            }

            if (string.IsNullOrWhiteSpace(scenarioKey)) return null;
            // 校验 key 必须在启用列表中
            if (!activeScenarios.Any(s => string.Equals(s.ScenarioKey, scenarioKey, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            confidence = Math.Max(0d, Math.Min(1d, confidence));
            return new ScenarioRoutingResult(scenarioKey!, confidence, reason, alts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] GPT scenario routing failed");
            return null;
        }
    }

    /// <summary>
    /// 棢�查科目代码是否存在（独立获取数据库连接）�?    /// 注：FinanceService 中有类似方法但需要传入已有连接，此方法供 AI 工具调用使用�?    /// </summary>
    private async Task EnsureAccountCodesExistAsync(string companyCode, IReadOnlyCollection<string> accountCodes, CancellationToken ct)
    {
        if (accountCodes.Count == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_code FROM accounts WHERE company_code=@company AND account_code = ANY(@codes)";
        cmd.Parameters.Add(new NpgsqlParameter<string>("company", companyCode));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("codes", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = accountCodes.ToArray()
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            found.Add(reader.GetString(0));
        }
        var missing = accountCodes.Where(code => !found.Contains(code)).ToArray();
        if (missing.Length > 0)
        {
            throw new Exception($"存在しない勘定科目コード: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// 棢�查单个科目代码是否存在�?    /// </summary>
    private async Task<bool> AccountExistsAsync(string companyCode, string accountCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountCode)) return false;
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(accountCode.Trim());
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is not null && result is not DBNull;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从公司设置中获取进项税（仮払消費税）科目代码，默认为 1410�?    /// </summary>
    private async Task<string> GetInputTaxAccountCodeAsync(string companyCode, CancellationToken ct)
    {
        const string DefaultInputTaxAccount = "1410";
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload->>'inputTaxAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string code && !string.IsNullOrWhiteSpace(code))
            {
                return code.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] 获取进项税科目失败，使用默认�?{Default}", DefaultInputTaxAccount);
        }
        return DefaultInputTaxAccount;
    }

    private async Task<LookupAccountResult> LookupAccountAsync(string companyCode, string query, CancellationToken ct)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new LookupAccountResult(false, query, null, null, Array.Empty<string>());
        }

        static LookupAccountResult BuildResult(string resolvedQuery, string code, string payloadJson)
        {
            string accountName = string.Empty;
            List<string> aliases = new();
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
            return new LookupAccountResult(true, resolvedQuery, code, accountName, aliases);
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);

        async Task<LookupAccountResult?> QueryAsync(string sql, params object[] parameters)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue(companyCode);
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p);
            }
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0);
                var payloadJson = reader.GetString(1);
                return BuildResult(trimmed, code, payloadJson);
            }
            return null;
        }

        var exact = await QueryAsync("SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1", trimmed);
        if (exact is not null) return exact;

        var byName = await QueryAsync("SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND LOWER(payload->>'name') = LOWER($2) LIMIT 1", trimmed);
        if (byName is not null) return byName;

        // sanitized
                                           FROM accounts
                                           WHERE company_code=$1
                                             AND EXISTS (
                                               SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                                               WHERE LOWER(alias) = LOWER($2)
                                             )
                                           // sanitized
        if (byAlias is not null) return byAlias;

        // sanitized
                                       FROM accounts
                                       WHERE company_code=$1
                                         AND (
                                            payload->>'name' ILIKE $2
                                            OR EXISTS (
                                                SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                                                WHERE alias ILIKE $2
                                            )
                                         )
                                       ORDER BY account_code
                                       // sanitized
        if (fuzzy is not null) return fuzzy;

        return new LookupAccountResult(false, trimmed, null, null, Array.Empty<string>());
    }

    private async Task<IReadOnlyList<LookupAccountResult>> PrefetchScenarioAccountsAsync(string scenarioKey, AgentExecutionContext context, CancellationToken ct)
    {
        var normalizedKey = NormalizeScenarioKey(scenarioKey);
        if (!ScenarioAccountLookupHints.TryGetValue(normalizedKey, out var hints) || hints.Length == 0)
        {
            return Array.Empty<LookupAccountResult>();
        }

        var results = new List<LookupAccountResult>();
        var approvedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in hints)
        {
            try
            {
                var lookup = await LookupAccountAsync(context.CompanyCode, hint, ct);
                if (!lookup.Found || string.IsNullOrWhiteSpace(lookup.AccountCode))
                {
                    continue;
                }
                var normalizedCode = lookup.AccountCode.Trim();
                if (approvedCodes.Add(normalizedCode))
                {
                    context.RegisterApprovedAccount(normalizedCode, false);
                    results.Add(lookup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentKit] 预加载科�?{Hint} 失败", hint);
            }
        }

        if (approvedCodes.Count > 0)
        {
            context.EnableAccountWhitelist(approvedCodes);
        }

        return results;
    }

    private static string? BuildAccountHintMessage(string language, IReadOnlyList<LookupAccountResult> accounts)
    {
        if (accounts.Count == 0) return null;
        var parts = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.AccountCode))
            .Select(a =>
            {
                var name = string.IsNullOrWhiteSpace(a.AccountName) ? a.Query : a.AccountName;
                return $"{name}({a.AccountCode})";
            })
            .ToArray();
        if (parts.Length == 0) return null;
        var joined = string.Join(" / ", parts);
        return Localize(language, $"利用可能な会計科目��補: {joined}", $"可用的会计科目����：{joined}");
    }

    private async Task<object> CheckAccountingPeriodAsync(string companyCode, string postingDate, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_open FROM accounting_periods WHERE company_code=$1 AND period_start <= $2::date AND period_end >= $2::date ORDER BY period_end DESC LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(postingDate);
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is null)
        {
            return string.Empty;
        }
        var isOpen = Convert.ToBoolean(scalar, CultureInfo.InvariantCulture);
        return new { exists = true, isOpen, message = isOpen ? "会计期间弢��? : "会计期间已关�? };
    }

    private async Task<object> VerifyInvoiceRegistrationAsync(string regNo, CancellationToken ct)
    {
        var normalized = InvoiceRegistryService.Normalize(regNo);
        if (!InvoiceRegistryService.IsFormatValid(normalized))
        {
            return string.Empty;
        }
        var result = await _invoiceRegistry.VerifyAsync(normalized);
        var statusKey = InvoiceRegistryService.StatusKey(result.Status);
        string message = statusKey switch
        {
            // sanitized
            // sanitized
            // sanitized
            "expired" => "登记号已失效",
            _ => "查询完成"
        };
        return new
        {
            status = statusKey,
            message,
            issuerName = result.Name
        };
    }

    private async Task<JsonObject> LookupCustomerAsync(string companyCode, string query, int limit, CancellationToken ct)
    {
        limit = limit < 1 ? 1 : (limit > 50 ? 50 : limit);
        var pattern = $"%{query}%";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT partner_code, name, payload
            FROM businesspartners
            WHERE company_code = $1
              AND flag_customer IS TRUE
              AND (
                    partner_code ILIKE $2
                 OR name ILIKE $2
                 OR COALESCE(payload->>'kana','') ILIKE $2
              )
            ORDER BY CASE WHEN partner_code ILIKE $3 THEN 0 ELSE 1 END,
                     updated_at DESC
            LIMIT $4;
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(pattern);
        cmd.Parameters.AddWithValue($"{query}%");
        cmd.Parameters.AddWithValue(limit);

        var items = new JsonArray();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var payloadObj = ParseJsonObject(reader.IsDBNull(2) ? null : reader.GetString(2));

            var item = new JsonObject
            {
                ["code"] = code,
                ["name"] = name,
                ["kana"] = ReadJsonString(payloadObj, "kana") ?? string.Empty,
                ["status"] = ReadJsonString(payloadObj, "status") ?? string.Empty,
                ["addresses"] = ExtractCustomerAddresses(payloadObj)
            };

            if (payloadObj.TryGetPropertyValue("contact", out var contactNode) && contactNode is JsonObject contactObj)
            {
                item["contact"] = contactObj.DeepClone();
            }

            items.Add(item);
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["items"] = items
        };
    }

    /// <summary>
    /// 获取网页内容，用于从企业官网提取公司信息
    /// </summary>
    private async Task<string> FetchWebpageContentAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            http.Timeout = TimeSpan.FromSeconds(30);
            
            var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            
            var html = await response.Content.ReadAsStringAsync(ct);
            
            // 箢�单提取文本内容（移除HTML标签�?            var text = ExtractTextFromHtml(html);
            
            // 限制返回的文本长度，避免token过多
            if (text.Length > 15000)
            {
                text = text.Substring(0, 15000) + "... (截断)";
            }
            
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] 获取网页失败: {Url}", url);
            throw new Exception($"网页获取失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 从HTML中提取文本内�?    /// </summary>
    private static string ExtractTextFromHtml(string html)
    {
        // 移除script和style标签及其内容
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<!--[\s\S]*?-->", "");
        
        // 替换常见的HTML实体
        // sanitized
        
        // 移除扢�有HTML标签
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        
        // 清理多余空白
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ");
        
        return html.Trim();
    }

    /// <summary>
    /// 创建取引先（业务伙伴）主数据
    /// </summary>
    private async Task<object> CreateBusinessPartnerAsync(
        string companyCode, string name, string? nameKana, bool isCustomer, bool isVendor,
        string? postalCode, string? prefecture, string? address,
        string? phone, string? fax, string? email, string? contactPerson, string? note,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 生成取引先编�?        var partnerCode = await GeneratePartnerCodeAsync(conn, companyCode, ct);
        
        // 构建payload - 注意: businesspartners 表使�?payload JSONB 字段
        // partner_code, name, flag_customer, flag_vendor, status 是从 payload 自动生成的列
        var payload = new JsonObject
        {
            ["code"] = partnerCode,  // 映射�?partner_code 生成�?(payload->>'code')
            ["name"] = name,
            ["flags"] = new JsonObject
            {
                ["customer"] = isCustomer,
                ["vendor"] = isVendor
            },
            ["status"] = "active"
        };
        
        if (!string.IsNullOrWhiteSpace(nameKana))
            payload["nameKana"] = nameKana;
        
        // 地址信息
        var addressObj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(postalCode))
            addressObj["postalCode"] = postalCode;
        if (!string.IsNullOrWhiteSpace(prefecture))
            addressObj["prefecture"] = prefecture;
        if (!string.IsNullOrWhiteSpace(address))
            addressObj["address"] = address;
        if (addressObj.Count > 0)
            payload["address"] = addressObj;
        
        // 联系信息
        var contactObj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(phone))
            contactObj["phone"] = phone;
        if (!string.IsNullOrWhiteSpace(fax))
            contactObj["fax"] = fax;
        if (!string.IsNullOrWhiteSpace(email))
            contactObj["email"] = email;
        if (!string.IsNullOrWhiteSpace(contactPerson))
            contactObj["contactPerson"] = contactPerson;
        if (contactObj.Count > 0)
            payload["contact"] = contactObj;
        
        if (!string.IsNullOrWhiteSpace(note))
            payload["note"] = note;
        
        // 插入数据�?- 表名�?businesspartners (无下划线)
        // 只需要插�?company_code �?payload，其他字段是自动生成�?        await using var cmd = conn.CreateCommand();
        // sanitized
            INSERT INTO businesspartners (company_code, payload)
            VALUES ($1, $2::jsonb)
            RETURNING id
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        
        var id = await cmd.ExecuteScalarAsync(ct);
        
        _logger.LogInformation("[AgentKit] 创建取引先成�? {PartnerCode} {Name}", partnerCode, name);
        
        return new
        {
            status = "ok",
            message = $"取引先��{name}」を登録しました。取引先コードは「{partnerCode}」です：",
            partnerId = id?.ToString(),
            partnerCode,
            name,
            isCustomer,
            isVendor,
            // 用于前端生成可点击标�?            targetKey = "bp.list",
            identifier = partnerCode,
            targetPayload = new { partnerId = id?.ToString() }
        };
    }
    
    /// <summary>
    /// 生成取引先编码（流水号）
    /// </summary>
    private static async Task<string> GeneratePartnerCodeAsync(NpgsqlConnection conn, string companyCode, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT COALESCE(MAX(CAST(partner_code AS INTEGER)), 0) + 1
            FROM businesspartners
            WHERE company_code = $1
              AND partner_code ~ '^[0-9]+$'
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        var next = await cmd.ExecuteScalarAsync(ct);
        var num = Convert.ToInt32(next ?? 1);
        return num.ToString("D6"); // 6位数字，前面补零
    }

    private async Task<JsonObject> LookupVendorAsync(string companyCode, string query, int limit, CancellationToken ct)
    {
        limit = limit < 1 ? 1 : (limit > 50 ? 50 : limit);
        var pattern = $"%{query}%";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT id, partner_code, name, payload
            FROM businesspartners
            WHERE company_code = $1
              AND flag_vendor IS TRUE
              AND (
                    partner_code ILIKE $2
                 OR name ILIKE $2
                 OR COALESCE(payload->>'kana','') ILIKE $2
              )
            ORDER BY CASE WHEN partner_code ILIKE $3 THEN 0 ELSE 1 END,
                     updated_at DESC
            LIMIT $4;
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(pattern);
        cmd.Parameters.AddWithValue($"{query}%");
        cmd.Parameters.AddWithValue(limit);

        var items = new JsonArray();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.IsDBNull(0) ? string.Empty : reader.GetGuid(0).ToString();
            var code = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var payloadObj = ParseJsonObject(reader.IsDBNull(3) ? null : reader.GetString(3));

            var item = new JsonObject
            {
                ["id"] = id,
                ["code"] = code,
                ["name"] = name,
                ["kana"] = ReadJsonString(payloadObj, "kana") ?? string.Empty,
                ["status"] = ReadJsonString(payloadObj, "status") ?? string.Empty,
                ["paymentTerms"] = ReadJsonString(payloadObj, "paymentTerms") ?? string.Empty,
                ["preferredApAccountCode"] = ReadJsonString(payloadObj, "preferredApAccountCode") ?? string.Empty
            };

            if (payloadObj.TryGetPropertyValue("contact", out var contactNode) && contactNode is JsonObject contactObj)
            {
                item["contact"] = contactObj.DeepClone();
            }

            items.Add(item);
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["items"] = items
        };
    }

    /// <summary>
    /// 查询供应商的可匹配入库凭证（用于供应商请求书三单匹配�?    /// </summary>
    private async Task<object> SearchVendorReceiptsAsync(string companyCode, string vendorId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 先获取供应商�?partner_code
        string? vendorCode = null;
        await using (var codeCmd = conn.CreateCommand())
        {
            codeCmd.CommandText = "SELECT partner_code FROM businesspartners WHERE company_code = $1 AND (id::text = $2 OR partner_code = $2) LIMIT 1";
            codeCmd.Parameters.AddWithValue(companyCode);
            codeCmd.Parameters.AddWithValue(vendorId);
            vendorCode = (string?)await codeCmd.ExecuteScalarAsync(ct);
        }
        
        if (string.IsNullOrWhiteSpace(vendorCode))
        {
            return new { status = "error", message = "供应商不存在", receipts = Array.Empty<object>() };
        }
        
        // 查询该供应商的所有采购订单关联的入库记录（未完全请求的）
        var receipts = new List<object>();
        await using (var cmd = conn.CreateCommand())
        {
            // sanitized
                SELECT 
                    m.id,
                    m.movement_date,
                    m.reference_no as po_no,
                    m.payload,
                    po.partner_code
                FROM inventory_movements m
                JOIN purchase_orders po ON po.po_no = m.reference_no AND po.company_code = m.company_code
                WHERE m.company_code = $1 
                  AND m.movement_type = 'IN'
                  AND po.partner_code = $2
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(vendorCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var movementId = reader.GetGuid(0);
                var movementDate = reader.GetDateTime(1).ToString("yyyy-MM-dd");
                var poNo = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var payloadStr = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
                
                try
                {
                    using var payloadDoc = JsonDocument.Parse(payloadStr);
                    if (payloadDoc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                    {
                        var lineIndex = 0;
                        foreach (var line in lines.EnumerateArray())
                        {
                            var materialCode = line.TryGetProperty("materialCode", out var mc) ? mc.GetString() : null;
                            var materialName = line.TryGetProperty("materialName", out var mn) ? mn.GetString() : null;
                            var qty = line.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0;
                            var invoicedQty = line.TryGetProperty("invoicedQuantity", out var iq) ? iq.GetDecimal() : 0;
                            var unitPrice = line.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0;
                            var remainingQty = qty - invoicedQty;
                            
                            if (!string.IsNullOrWhiteSpace(materialCode) && remainingQty > 0)
                            {
                                receipts.Add(new {
                                    id = $"{movementId}_{lineIndex}",
                                    movementId = movementId.ToString(),
                                    lineIndex,
                                    materialCode,
                                    materialName = materialName ?? "",
                                    quantity = qty,
                                    invoicedQuantity = invoicedQty,
                                    remainingQuantity = remainingQty,
                                    unitPrice,
                                    estimatedAmount = remainingQty * unitPrice,
                                    poNo,
                                    receiptDate = movementDate,
                                    label = $"{poNo} / {movementDate} / {materialCode} / 残{remainingQty}"
                                });
                            }
                            lineIndex++;
                        }
                    }
                }
                catch { }
            }
        }
        
        return new { 
            status = "ok", 
            vendorCode,
            receipts,
            hasReceipts = receipts.Count > 0,
            message = receipts.Count > 0 
                // sanitized
                : "未找到可匹配的入库记录，可以选择直接创建会计凭证"
        };
    }

    /// <summary>
    /// 获取可��的借方费用/库存科目（用于供应商请求书直接记账）
    /// 返回库存科目、外注费、仕入等常用科目供用户��择
    /// </summary>
    private async Task<object> GetExpenseAccountOptionsAsync(string companyCode, string? vendorId, CancellationToken ct)
    {
        var options = new List<object>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 1. 查询库存科目（BS资产�?+ 非清账管理）
        await using (var cmd = conn.CreateCommand())
        {
            // sanitized
                SELECT account_code, payload->>'name' as name, 'inventory' as category
                FROM accounts 
                WHERE company_code = $1
                  AND payload->>'category' = 'BS'
                  AND payload->>'accountType' = 'asset'
                  AND (payload->>'openItem' IS NULL OR (payload->>'openItem')::boolean = false)
                  AND (payload->>'name' LIKE '%在庫%' OR payload->>'name' LIKE '%商品%' OR payload->>'name' LIKE '%製品%' OR payload->>'name' LIKE '%原材�?' OR payload->>'name' LIKE '%仕掛%')
                ORDER BY account_code
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                options.Add(new {
                    code = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    category = "inventory",
                    description = "在庫科目（棚卸資産）"
                });
            }
        }
        
        // 2. 查询费用科目（PL科目中的仕入、外注费等）
        await using (var cmd = conn.CreateCommand())
        {
            // sanitized
                SELECT account_code, payload->>'name' as name, 'expense' as category
                FROM accounts 
                WHERE company_code = $1
                  AND payload->>'category' = 'PL'
                  AND (
                    payload->>'name' LIKE '%仕入%' 
                    OR payload->>'name' LIKE '%外注%' 
                    OR payload->>'name' LIKE '%購入%'
                    OR payload->>'name' LIKE '%材料�?'
                    OR payload->>'name' LIKE '%消��品%'
                    OR account_code LIKE '5%'
                  )
                ORDER BY 
                  CASE 
                    WHEN payload->>'name' LIKE '%外注%' THEN 1
                    WHEN payload->>'name' LIKE '%仕入%' THEN 2
                    ELSE 3
                  END,
                  account_code
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                options.Add(new {
                    code = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    category = "expense",
                    // sanitized
                });
            }
        }
        
        // 3. 如果有供应商ID，查询该供应商历史使用的科目
        string? recentAccountCode = null;
        string? recentAccountName = null;
        if (!string.IsNullOrWhiteSpace(vendorId))
        {
            await using var cmd = conn.CreateCommand();
            // sanitized
                SELECT auh.account_code, a.payload->>'name'
                FROM account_usage_history auh
                JOIN accounts a ON a.company_code = auh.company_code AND a.account_code = auh.account_code
                WHERE auh.company_code = $1 
                  AND auh.usage_type = 'dr_vendor'
                  AND auh.context_value = $2
                ORDER BY auh.usage_count DESC, auh.last_used_at DESC
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(vendorId);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                recentAccountCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                recentAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        
        // 4. 获取应付账款科目（贷方，霢��?vendorId�?        string? apAccountCode = null;
        string? apAccountName = null;
        await using (var cmd = conn.CreateCommand())
        {
            // sanitized
                SELECT account_code, payload->>'name'
                FROM accounts 
                WHERE company_code = $1
                  AND payload->>'category' = 'BS'
                  AND payload->>'accountType' = 'liability'
                  AND (payload->>'openItem')::boolean = true
                  AND payload->>'openItemBaseline' = 'VENDOR'
                ORDER BY account_code
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                apAccountCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                apAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        
        // 5. 获取箢�易应付科目（贷方，不霢��?vendorId - 用于箢�易记账模式）
        string? simpleApAccountCode = null;
        string? simpleApAccountName = null;
        await using (var cmd = conn.CreateCommand())
        {
            // sanitized
                SELECT account_code, payload->>'name'
                FROM accounts 
                WHERE company_code = $1
                  AND payload->>'category' = 'BS'
                  AND payload->>'accountType' = 'liability'
                  AND (payload->>'openItem' IS NULL OR (payload->>'openItem')::boolean = false)
                  AND (
                    payload->>'name' LIKE '%未払%'
                    OR account_code LIKE '22%'
                  )
                ORDER BY 
                  CASE 
                    WHEN payload->>'name' LIKE '%未払�?' THEN 1
                    WHEN payload->>'name' LIKE '%未払費用%' THEN 2
                    ELSE 3
                  END,
                  account_code
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                simpleApAccountCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                simpleApAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        
        // 6. 获取进项税科�?        string? inputTaxAccountCode = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT payload->>'inputTaxAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string code && !string.IsNullOrWhiteSpace(code))
            {
                inputTaxAccountCode = code.Trim();
            }
        }
        
        // 7. 查询常用费用科目（外注費、支払手数料等）�?AI 根据内容判断
        var commonExpenseAccounts = new List<object>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT account_code, payload->>'name' as name, 'other' as usage_type
                FROM accounts
                WHERE company_code = $1
                  AND payload->>'category' = 'PL'
                ORDER BY account_code
                LIMIT 50";
            cmd.Parameters.AddWithValue(companyCode);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                commonExpenseAccounts.Add(new {
                    code = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    usageType = reader.IsDBNull(2) ? "other" : reader.GetString(2)
                });
            }
        }
        
        return new {
            status = "ok",
            options,
            commonExpenseAccounts,
            accountSelectionGuide = new {
                // sanitized
                commission = "支払手数�? 振込手数料��代行手数料、仲介手数料など",
                // sanitized
                // sanitized
            },
            recentAccount = recentAccountCode is not null ? new { code = recentAccountCode, name = recentAccountName, isRecent = true } : null,
            apAccount = apAccountCode is not null ? new { code = apAccountCode, name = apAccountName, requiresVendorId = true } : null,
            simpleApAccount = simpleApAccountCode is not null ? new { code = simpleApAccountCode, name = simpleApAccountName, requiresVendorId = false } : null,
            inputTaxAccountCode,
            message = "請求書の内容から適切な科目を選択してください"
        };
    }

    /// <summary>
    /// 创建供应商请求书（三单匹配流程）
    /// </summary>
    private async Task<ToolExecutionResult> CreateVendorInvoiceAsync(AgentExecutionContext context, JsonElement args, CancellationToken ct)
    {
        // 解析参数
        var vendorId = args.TryGetProperty("vendor_id", out var vidEl) ? vidEl.GetString() : null;
        var invoiceDate = args.TryGetProperty("invoice_date", out var idEl) ? idEl.GetString() : DateTime.Today.ToString("yyyy-MM-dd");
        var dueDate = args.TryGetProperty("due_date", out var ddEl) ? ddEl.GetString() : null;
        var totalAmount = args.TryGetProperty("total_amount", out var taEl) && taEl.TryGetDecimal(out var ta) ? ta : 0m;
        var taxAmount = args.TryGetProperty("tax_amount", out var txEl) && txEl.TryGetDecimal(out var txVal) ? txVal : 0m;
        var summary = args.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() : null;
        var documentSessionId = args.TryGetProperty("documentSessionId", out var dsiEl) ? dsiEl.GetString() : null;
        
        if (string.IsNullOrWhiteSpace(vendorId))
            throw new Exception("error");
        
        // 解析入库匹配明细
        var matchedReceipts = new List<(string receiptId, string materialCode, decimal quantity, decimal unitPrice, string? poNo)>();
        if (args.TryGetProperty("matched_receipts", out var mrEl) && mrEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in mrEl.EnumerateArray())
            {
                var receiptId = item.TryGetProperty("receipt_id", out var ridEl) ? ridEl.GetString() : null;
                var materialCode = item.TryGetProperty("material_code", out var mcEl) ? mcEl.GetString() : null;
                var quantity = item.TryGetProperty("quantity", out var qEl) && qEl.TryGetDecimal(out var q) ? q : 0m;
                var unitPrice = item.TryGetProperty("unit_price", out var upEl) && upEl.TryGetDecimal(out var up) ? up : 0m;
                var poNo = item.TryGetProperty("po_no", out var pnEl) ? pnEl.GetString() : null;
                
                if (!string.IsNullOrWhiteSpace(receiptId) && !string.IsNullOrWhiteSpace(materialCode) && quantity > 0)
                {
                    matchedReceipts.Add((receiptId, materialCode, quantity, unitPrice, poNo));
                }
            }
        }
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 获取供应商信�?        string? vendorCode = null;
        string? vendorName = null;
        await using (var vCmd = conn.CreateCommand())
        {
            vCmd.CommandText = "SELECT partner_code, name FROM businesspartners WHERE company_code = $1 AND (id::text = $2 OR partner_code = $2) LIMIT 1";
            vCmd.Parameters.AddWithValue(context.CompanyCode);
            vCmd.Parameters.AddWithValue(vendorId);
            await using var vRd = await vCmd.ExecuteReaderAsync(ct);
            if (await vRd.ReadAsync(ct))
            {
                vendorCode = vRd.IsDBNull(0) ? null : vRd.GetString(0);
                vendorName = vRd.IsDBNull(1) ? null : vRd.GetString(1);
            }
        }
        
        if (string.IsNullOrWhiteSpace(vendorCode))
            throw new Exception("error");
        
        // 生成请求书编�?        var invoiceNo = await GenerateVendorInvoiceNoAsync(context.CompanyCode, ct);
        
        // 构建请求书明�?        var invoiceLines = new JsonArray();
        var lineNo = 1;
        foreach (var (receiptId, materialCode, quantity, unitPrice, poNo) in matchedReceipts)
        {
            var lineAmount = quantity * unitPrice;
            invoiceLines.Add(new JsonObject
            {
                ["lineNo"] = lineNo++,
                ["materialCode"] = materialCode,
                ["quantity"] = quantity,
                ["unitPrice"] = unitPrice,
                ["amount"] = lineAmount,
                ["matchedReceiptId"] = receiptId,
                ["matchedPoNo"] = poNo ?? ""
            });
        }
        
        // 如果没有匹配的入库记录但有金额，创建丢�个汇总行
        if (invoiceLines.Count == 0 && totalAmount > 0)
        {
            invoiceLines.Add(new JsonObject
            {
                ["lineNo"] = 1,
                ["description"] = summary ?? "供应商请求书",
                ["quantity"] = 1,
                ["unitPrice"] = totalAmount - taxAmount,
                ["amount"] = totalAmount - taxAmount
            });
        }
        
        // 计算金额
        decimal calculatedNetAmount = 0m;
        foreach (var lineNode in invoiceLines)
        {
            if (lineNode is JsonObject lineObj && lineObj.TryGetPropertyValue("amount", out var amtNode) && amtNode is JsonValue amtVal)
            {
                calculatedNetAmount += amtVal.GetValue<decimal>();
            }
        }
        
        var netAmount = totalAmount > 0 ? (totalAmount - taxAmount) : calculatedNetAmount;
        var finalTaxAmount = taxAmount > 0 ? taxAmount : Math.Round(netAmount * 0.1m, 0);
        var grandTotal = netAmount + finalTaxAmount;
        
        // 构建请求�?payload
        var invoicePayload = new JsonObject
        {
            ["invoiceNo"] = invoiceNo,
            ["vendorCode"] = vendorCode,
            ["vendorName"] = vendorName ?? "",
            ["invoiceDate"] = invoiceDate,
            ["dueDate"] = dueDate ?? "",
            ["netAmount"] = netAmount,
            ["taxAmount"] = finalTaxAmount,
            ["grandTotal"] = grandTotal,
            ["currency"] = "JPY",
            ["status"] = "pending",
            ["lines"] = invoiceLines,
            ["createdBy"] = context.UserCtx.UserName ?? context.UserCtx.UserId ?? "agent",
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["source"] = "agent"
        };
        
        // 关联文档
        if (!string.IsNullOrWhiteSpace(documentSessionId))
        {
            invoicePayload["documentSessionId"] = documentSessionId;
        }
        
        // 获取附件信息
        var fileId = context.DefaultFileId;
        if (!string.IsNullOrWhiteSpace(fileId))
        {
            var fileInfo = context.ResolveFile(fileId);
            if (fileInfo is not null && !string.IsNullOrWhiteSpace(fileInfo.BlobName))
            {
                invoicePayload["attachments"] = new JsonArray { fileInfo.BlobName };
            }
        }
        
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            // 插入供应商请求书
            Guid invoiceId;
            await using (var insCmd = conn.CreateCommand())
            {
                insCmd.Transaction = tx;
                // sanitized
                    INSERT INTO vendor_invoices (company_code, payload)
                    VALUES ($1, $2::jsonb)
                    // sanitized
                insCmd.Parameters.AddWithValue(context.CompanyCode);
                insCmd.Parameters.AddWithValue(invoicePayload.ToJsonString());
                var result = await insCmd.ExecuteScalarAsync(ct);
                invoiceId = result is Guid g ? g : Guid.Empty;
            }
            
            if (invoiceId == Guid.Empty)
            {
                await tx.RollbackAsync(ct);
                throw new Exception(Localize(context.Language, "請求書の作成に失敗しました��?, "创建请求书失�?));
            }
            
            // 更新入库记录的已请求数量
            foreach (var (receiptId, materialCode, quantity, _, _) in matchedReceipts)
            {
                var parts = receiptId.Split('_');
                if (parts.Length == 2 && Guid.TryParse(parts[0], out var movementId) && int.TryParse(parts[1], out var lineIndex))
                {
                    await UpdateReceiptInvoicedQuantityAsync(conn, tx, context.CompanyCode, movementId, lineIndex, quantity, ct);
                }
            }
            
            await tx.CommitAsync(ct);
            
            // 构建成功消息
            var successMsg = Localize(context.Language,
                $"仕入先請求書 {invoiceNo} を作成しました��\n" +
                $"仕入�? {vendorName}\n" +
                $"請求�? {invoiceDate}\n" +
                // sanitized
                $"税抜金額: {netAmount:#,0} 円\n" +
                $"消費�? {finalTaxAmount:#,0} 円\n" +
                $"合計金額: {grandTotal:#,0} 円\n" +
                // sanitized
                $"已创建供应商请求�?{invoiceNo}\n" +
                $"供应�? {vendorName}\n" +
                $"请求日期: {invoiceDate}\n" +
                // sanitized
                $"税前金额: {netAmount:#,0} 円\n" +
                $"消费�? {finalTaxAmount:#,0} 円\n" +
                $"合计金额: {grandTotal:#,0} 円\n" +
                // sanitized
            
            var resultMessage = new AgentResultMessage("assistant", successMsg, "info", new { 
                kind = "vendorInvoice", 
                invoiceId = invoiceId.ToString(), 
                invoiceNo,
                hasMatching = matchedReceipts.Count > 0
            });
            
            return ToolExecutionResult.FromModel(new { 
                status = "ok", 
                invoiceId = invoiceId.ToString(), 
                invoiceNo,
                vendorCode,
                vendorName,
                grandTotal,
                matchedReceiptsCount = matchedReceipts.Count
            }, new List<AgentResultMessage> { resultMessage });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<string> GenerateVendorInvoiceNoAsync(string companyCode, CancellationToken ct)
    {
        var datePrefix = DateTime.Today.ToString("yyyyMMdd");
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT COALESCE(MAX(CAST(SUBSTRING(invoice_no FROM 13) AS INTEGER)), 0) + 1
            FROM vendor_invoices
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue($"VI-{datePrefix}-%");
        var seq = await cmd.ExecuteScalarAsync(ct);
        var seqNo = seq is int i ? i : (seq is long l ? (int)l : 1);
        return $"VI-{datePrefix}-{seqNo:D5}";
    }

    private static async Task UpdateReceiptInvoicedQuantityAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string companyCode, Guid movementId, int lineIndex, decimal quantity, CancellationToken ct)
    {
        // 获取当前入库记录�?payload
        string? currentPayload = null;
        await using (var getCmd = conn.CreateCommand())
        {
            getCmd.Transaction = tx;
            getCmd.CommandText = "SELECT payload FROM inventory_movements WHERE id = $1 AND company_code = $2";
            getCmd.Parameters.AddWithValue(movementId);
            getCmd.Parameters.AddWithValue(companyCode);
            currentPayload = (string?)await getCmd.ExecuteScalarAsync(ct);
        }
        
        if (string.IsNullOrEmpty(currentPayload)) return;
        
        try
        {
            var payloadNode = JsonNode.Parse(currentPayload) as JsonObject;
            if (payloadNode?.TryGetPropertyValue("lines", out var linesNode) == true && linesNode is JsonArray linesArr && lineIndex < linesArr.Count)
            {
                var lineObj = linesArr[lineIndex] as JsonObject;
                if (lineObj is not null)
                {
                    var currentInvoiced = lineObj.TryGetPropertyValue("invoicedQuantity", out var iqNode) && iqNode is JsonValue iqVal
                        ? iqVal.GetValue<decimal>()
                        : 0m;
                    lineObj["invoicedQuantity"] = currentInvoiced + quantity;
                    
                    await using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = "UPDATE inventory_movements SET payload = $1::jsonb, updated_at = now() WHERE id = $2 AND company_code = $3";
                    updateCmd.Parameters.AddWithValue(payloadNode.ToJsonString());
                    updateCmd.Parameters.AddWithValue(movementId);
                    updateCmd.Parameters.AddWithValue(companyCode);
                    await updateCmd.ExecuteNonQueryAsync(ct);
                }
            }
        }
        catch { }
    }

    private async Task<JsonObject> LookupMaterialAsync(string companyCode, string query, int limit, CancellationToken ct)
    {
        limit = limit < 1 ? 1 : (limit > 50 ? 50 : limit);
        var pattern = $"%{query}%";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT material_code, payload
            FROM materials
            WHERE company_code = $1
              AND (
                    material_code ILIKE $2
                 OR COALESCE(payload->>'name','') ILIKE $2
                 OR COALESCE(payload->>'kana','') ILIKE $2
              )
            ORDER BY CASE WHEN material_code ILIKE $3 THEN 0 ELSE 1 END,
                     updated_at DESC
            LIMIT $4;
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(pattern);
        cmd.Parameters.AddWithValue($"{query}%");
        cmd.Parameters.AddWithValue(limit);

        var items = new JsonArray();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var payloadObj = ParseJsonObject(reader.IsDBNull(1) ? null : reader.GetString(1));

            var unitPrice = ReadJsonDecimal(payloadObj, "salesPrice");
            if (unitPrice <= 0m)
            {
                unitPrice = ReadJsonDecimal(payloadObj, "unitPrice");
            }

            var item = new JsonObject
            {
                ["code"] = code,
                ["name"] = ReadJsonString(payloadObj, "name") ?? string.Empty,
                ["uom"] = ReadJsonString(payloadObj, "baseUom") ?? ReadJsonString(payloadObj, "uom") ?? string.Empty,
                ["unitPrice"] = unitPrice,
                ["description"] = ReadJsonString(payloadObj, "description") ?? ReadJsonString(payloadObj, "spec") ?? string.Empty,
                ["raw"] = payloadObj.DeepClone()
            };
            items.Add(item);
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["items"] = items
        };
    }

    private async Task<ToolExecutionResult> CreateSalesOrderAsync(AgentExecutionContext context, JsonElement args, CancellationToken ct)
    {
        string? rawArgs = null;
        try
        {
            rawArgs = args.GetRawText();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentKit] create_sales_order args 获取失败");
            rawArgs = null;
        }
        if (!string.IsNullOrWhiteSpace(rawArgs))
        {
            _logger.LogInformation("[AgentKit] create_sales_order raw args: {Args}", rawArgs);
        }

        var root = JsonNode.Parse(rawArgs ?? args.GetRawText())?.AsObject() ?? new JsonObject();
        var customerNode = root.TryGetPropertyValue("customer", out var customerRaw) && customerRaw is JsonObject customerObj
            ? customerObj
            : null;

        var customerCode = ReadJsonString(root, "customerCode") ?? ReadJsonString(customerNode, "code");
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            throw new Exception("error");
        }
        customerCode = customerCode.Trim();

        var customerName = ReadJsonString(root, "customerName") ?? ReadJsonString(customerNode, "name") ?? customerCode;
        var now = DateTimeOffset.UtcNow;
        var orderDateRaw = ReadJsonString(root, "orderDate");
        var orderDate = DetermineOrderDate(orderDateRaw, now);
        var deliveryDateRaw = ReadJsonString(root, "deliveryDate");
        var deliveryDate = NormalizeDateString(deliveryDateRaw, now);
        _logger.LogInformation("[AgentKit] create_sales_order dates resolved orderDateRaw={Raw} resolved={Resolved}", orderDateRaw, orderDate);

        if (root.TryGetPropertyValue("delivery", out var deliveryRaw) && deliveryRaw is JsonObject deliveryObj)
        {
            deliveryDate ??= NormalizeDateString(ReadJsonString(deliveryObj, "date"), now);
        }
        deliveryDate = DetermineDeliveryDate(deliveryDate, now);
        _logger.LogInformation("[AgentKit] create_sales_order deliveryDateRaw={Raw} resolved={Resolved}", deliveryDateRaw, deliveryDate);

        var currency = ReadJsonString(root, "currency") ?? "JPY";
        currency = currency.ToUpperInvariant();
        var note = ReadJsonString(root, "note");

        if (!root.TryGetPropertyValue("lines", out var linesNode) || linesNode is not JsonArray lineArray || lineArray.Count == 0)
        {
            throw new Exception("error");
        }

        var materialCache = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);
        var lines = new JsonArray();
        var responseLines = new JsonArray();
        decimal totalAmount = 0m;
        var lineNo = 1;

        foreach (var item in lineArray)
        {
            if (item is not JsonObject lineObj) continue;
            var materialCode = ReadJsonString(lineObj, "materialCode")
                               ?? ReadJsonString(lineObj, "code")
                               ?? ReadJsonString(lineObj, "item");
            if (string.IsNullOrWhiteSpace(materialCode))
            {
                throw new Exception("error");
            }
            materialCode = materialCode.Trim();

            var qty = ReadJsonDecimal(lineObj, "quantity");
            if (qty <= 0m)
            {
                throw new Exception("error");
            }

            var uom = ReadJsonString(lineObj, "uom") ?? ReadJsonString(lineObj, "unit") ?? "EA";
            var materialName = ReadJsonString(lineObj, "materialName") ?? ReadJsonString(lineObj, "name") ?? string.Empty;
            var description = ReadJsonString(lineObj, "description") ?? ReadJsonString(lineObj, "memo") ?? string.Empty;
            var unitPrice = ReadJsonDecimal(lineObj, "unitPrice");
            var amount = ReadJsonDecimal(lineObj, "amount");
            if (unitPrice <= 0m)
            {
                if (!materialCache.TryGetValue(materialCode, out var materialInfo))
                {
                    materialInfo = await LoadMaterialInfoAsync(context.CompanyCode, materialCode, ct) ?? MaterialInfo.Empty;
                    materialCache[materialCode] = materialInfo;
                }
                if (materialInfo.UnitPrice > 0m)
                {
                    unitPrice = materialInfo.UnitPrice;
                }
                if (string.IsNullOrWhiteSpace(materialName) && !string.IsNullOrWhiteSpace(materialInfo.Name))
                {
                    materialName = materialInfo.Name;
                }
                if ((string.IsNullOrWhiteSpace(uom) || string.Equals(uom, "EA", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(materialInfo.Uom))
                {
                    uom = materialInfo.Uom!;
                }
                if (string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(materialInfo.Description))
                {
                    description = materialInfo.Description!;
                }
            }
            if (amount <= 0m && unitPrice > 0m)
            {
                amount = Math.Round(qty * unitPrice, 2, MidpointRounding.AwayFromZero);
            }
            if (amount < 0m) amount = 0m;
            totalAmount += amount;

            var noteValue = ReadJsonString(lineObj, "note") ?? ReadJsonString(lineObj, "remark");

            var normalizedLine = new JsonObject
            {
                ["lineNo"] = lineNo,
                ["materialCode"] = materialCode,
                ["materialName"] = materialName,
                ["description"] = description,
                ["quantity"] = qty,
                ["uom"] = uom,
                ["unitPrice"] = unitPrice,
                ["amount"] = amount
            };
            if (!string.IsNullOrWhiteSpace(noteValue))
            {
                normalizedLine["note"] = noteValue;
            }
            lines.Add(normalizedLine);

            var responseLine = new JsonObject
            {
                ["lineNo"] = lineNo,
                ["materialCode"] = materialCode,
                ["materialName"] = materialName,
                ["quantity"] = qty,
                ["uom"] = uom,
                ["unitPrice"] = unitPrice,
                ["amount"] = amount
            };
            if (!string.IsNullOrWhiteSpace(noteValue))
            {
                responseLine["note"] = noteValue;
            }
            responseLines.Add(responseLine);

            lineNo++;
        }

        if (lines.Count == 0)
        {
            throw new Exception("error");
        }

        var soNo = GenerateSalesOrderNumber(context.CompanyCode);
        var payload = new JsonObject
        {
            ["soNo"] = soNo,
            ["orderDate"] = orderDate,
            ["status"] = "confirmed",
            ["partnerCode"] = customerCode,
            ["partnerName"] = customerName ?? customerCode,
            ["currency"] = currency,
            ["amountTotal"] = totalAmount,
            ["lines"] = lines,
            ["source"] = "agent"
        };
        if (!string.IsNullOrWhiteSpace(note))
        {
            payload["note"] = note;
        }
        if (!string.IsNullOrWhiteSpace(deliveryDate))
        {
            payload["requestedDeliveryDate"] = deliveryDate;
        }
        if (customerNode is not null)
        {
            payload["customer"] = customerNode.DeepClone();
        }
        if (root.TryGetPropertyValue("requestedBy", out var requestedByNode) && requestedByNode is JsonValue requestedValue && requestedValue.TryGetValue<string>(out var requestedBy) && !string.IsNullOrWhiteSpace(requestedBy))
        {
            payload["requestedBy"] = requestedBy.Trim();
        }

        var shipTo = BuildShipToNode(root, customerNode);
        if (shipTo is not null)
        {
            payload["shipTo"] = shipTo;
        }

        using var payloadDoc = JsonDocument.Parse(payload.ToJsonString());
        var insertedJson = await Crud.InsertRawJson(_ds, "sales_orders", context.CompanyCode, payloadDoc.RootElement.GetRawText());
        if (string.IsNullOrWhiteSpace(insertedJson))
        {
            throw new Exception(Localize(context.Language, "受注の登録に失敗しました�?, "创建受注订单失败�?));
        }

        using var insertedDoc = JsonDocument.Parse(insertedJson);
        var insertedRoot = insertedDoc.RootElement;
        Guid salesOrderId = Guid.Empty;
        if (insertedRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            Guid.TryParse(idEl.GetString(), out salesOrderId);
        }
        var insertedPayload = insertedRoot.GetProperty("payload");
        var insertedPayloadObj = JsonNode.Parse(insertedPayload.GetRawText())?.AsObject() ?? new JsonObject();

        var summary = BuildSalesOrderSummary(context.Language, customerName ?? customerCode, deliveryDate, totalAmount, currency);

        SalesOrderTaskService.SalesOrderTask? task = null;
        if (context.TaskId.HasValue)
        {
            task = await _salesOrderTaskService.GetAsync(context.TaskId.Value, ct);
        }
        if (task is null && root.TryGetPropertyValue("taskId", out var taskIdNode) && taskIdNode is JsonValue taskIdValue && taskIdValue.TryGetValue<string>(out var taskIdStr) && Guid.TryParse(taskIdStr, out var providedTaskId))
        {
            task = await _salesOrderTaskService.GetAsync(providedTaskId, ct);
        }
        if (task is null)
        {
            var metadata = new JsonObject
            {
                ["scenario"] = context.Scenarios.FirstOrDefault()?.ScenarioKey ?? string.Empty,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            task = await _salesOrderTaskService.CreateAsync(
                context.SessionId,
                context.CompanyCode,
                context.UserCtx.UserId,
                "in_progress",
                summary,
                null,
                metadata,
                ct);
        }
        context.SetTaskId(task.Id);

        var metadataUpdate = new JsonObject
        {
            ["salesOrderNo"] = soNo,
            ["orderDate"] = orderDate,
            ["deliveryDate"] = deliveryDate ?? string.Empty,
            ["totalAmount"] = totalAmount,
            ["currency"] = currency,
            ["lineCount"] = lines.Count,
            ["completedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        await _salesOrderTaskService.UpdateAsync(
            task.Id,
            "completed",
            insertedPayloadObj,
            metadataUpdate,
            salesOrderId == Guid.Empty ? (Guid?)null : salesOrderId,
            soNo,
            customerCode,
            customerName,
            summary,
            markCompleted: true,
            ct);

        var messageContent = string.IsNullOrWhiteSpace(soNo)
            ? Localize(context.Language, "受注を登録しました��?, "已创建受注订单�?)
            : Localize(context.Language, $"受注 {soNo} を登録しました��?, $"已创建受注订�?{soNo}�?);
        if (!string.IsNullOrWhiteSpace(deliveryDate))
        {
            messageContent += Localize(context.Language, $" 納期: {deliveryDate}", $" 交期: {deliveryDate}");
        }

        var tag = new
        {
            label = soNo,
            action = "openEmbed",
            key = "crm.salesOrders",
            payload = new
            {
                salesOrderNo = soNo,
                detailOnly = true
            }
        };
        var agentMessage = new AgentResultMessage("assistant", messageContent.Trim(), "success", tag);

        var responseModel = new
        {
            status = "success",
            taskId = task.Id,
            salesOrderId = salesOrderId == Guid.Empty ? (Guid?)null : salesOrderId,
            salesOrderNo = soNo,
            customerCode,
            customerName,
            orderDate,
            deliveryDate,
            currency,
            totalAmount,
            lines = JsonSerializer.Deserialize<object>(responseLines.ToJsonString(), JsonOptions),
            payload = JsonSerializer.Deserialize<object>(insertedPayloadObj.ToJsonString(), JsonOptions)
        };

        return ToolExecutionResult.FromModel(responseModel, new List<AgentResultMessage> { agentMessage });
    }

    private sealed record MaterialInfo(string? Name, string? Uom, string? Description, decimal UnitPrice)
    {
        public static readonly MaterialInfo Empty = new(null, null, null, 0m);
    }

    private async Task<MaterialInfo?> LoadMaterialInfoAsync(string companyCode, string materialCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // sanitized
            SELECT payload
            FROM materials
            WHERE company_code = $1
              AND material_code = $2
            LIMIT 1;
            // sanitized
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(materialCode);
        var payloadJson = (string?)await cmd.ExecuteScalarAsync(ct);
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        var payload = ParseJsonObject(payloadJson);
        var price = ReadJsonDecimal(payload, "salesPrice");
        if (price <= 0m)
        {
            price = ReadJsonDecimal(payload, "unitPrice");
        }
        if (price <= 0m)
        {
            price = ReadJsonDecimal(payload, "price");
        }
        if (price <= 0m && payload.TryGetPropertyValue("pricing", out var pricingNode) && pricingNode is JsonObject pricingObj)
        {
            price = ReadJsonDecimal(pricingObj, "sales");
            if (price <= 0m) price = ReadJsonDecimal(pricingObj, "standard");
            if (price <= 0m) price = ReadJsonDecimal(pricingObj, "default");
        }
        var name = ReadJsonString(payload, "name");
        var uom = ReadJsonString(payload, "baseUom") ?? ReadJsonString(payload, "uom");
        var description = ReadJsonString(payload, "description") ?? ReadJsonString(payload, "spec");
        return new MaterialInfo(name, uom, description, price);
    }

    private static JsonObject? BuildShipToNode(JsonObject root, JsonObject? customerNode)
    {
        if (root.TryGetPropertyValue("delivery", out var deliveryRaw) && deliveryRaw is JsonObject deliveryObj)
        {
            var ship = deliveryObj.DeepClone().AsObject();
            if (!ship.ContainsKey("addressText"))
            {
                if (ship.TryGetPropertyValue("address", out var addrNode) && addrNode is JsonObject addrObj)
                {
                    var text = BuildAddressText(addrObj);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        ship["addressText"] = text;
                    }
                }
            }
            return ship;
        }

        if (customerNode is not null)
        {
            JsonObject? addressObj = null;
            if (customerNode.TryGetPropertyValue("address", out var addrNode) && addrNode is JsonObject cAddr)
            {
                addressObj = cAddr;
            }
            else if (customerNode.TryGetPropertyValue("addresses", out var addressesNode) && addressesNode is JsonArray addressesArr && addressesArr.Count > 0 && addressesArr[0] is JsonObject firstAddr)
            {
                addressObj = firstAddr;
            }
            if (addressObj is not null)
            {
                var ship = new JsonObject
                {
                    ["address"] = addressObj.DeepClone()
                };
                var text = BuildAddressText(addressObj);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ship["addressText"] = text;
                }
                if (customerNode.TryGetPropertyValue("addressCode", out var codeNode) && codeNode is JsonValue codeVal && codeVal.TryGetValue<string>(out var addressCode) && !string.IsNullOrWhiteSpace(addressCode))
                {
                    ship["addressCode"] = addressCode.Trim();
                }
                return ship;
            }
        }

        return null;
    }

    private static JsonArray ExtractCustomerAddresses(JsonObject payload)
    {
        var collection = new JsonArray();

        void AddAddress(JsonObject source)
        {
            var address = new JsonObject
            {
                ["id"] = ReadJsonString(source, "id") ?? ReadJsonString(source, "code") ?? string.Empty,
                ["label"] = ReadJsonString(source, "label") ?? ReadJsonString(source, "name") ?? string.Empty,
                ["type"] = ReadJsonString(source, "type") ?? string.Empty
            };
            if (source.TryGetPropertyValue("address", out var addrNode) && addrNode is JsonObject addrObj)
            {
                address["address"] = addrObj.DeepClone();
                var text = BuildAddressText(addrObj);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    address["addressText"] = text;
                }
            }
            else
            {
                var text = BuildAddressText(source);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    address["addressText"] = text;
                }
            }
            if (source.TryGetPropertyValue("contact", out var contactNode) && contactNode is JsonObject contactObj)
            {
                address["contact"] = contactObj.DeepClone();
            }
            if (source.TryGetPropertyValue("phone", out var phoneNode) && phoneNode is JsonValue phoneVal && phoneVal.TryGetValue<string>(out var phone) && !string.IsNullOrWhiteSpace(phone))
            {
                address["phone"] = phone.Trim();
            }
            address["raw"] = source.DeepClone();
            collection.Add(address);
        }

        if (payload.TryGetPropertyValue("addresses", out var addressesNode) && addressesNode is JsonArray addressesArr)
        {
            foreach (var entry in addressesArr)
            {
                if (entry is JsonObject addressObj)
                {
                    AddAddress(addressObj);
                }
            }
        }
        else if (payload.TryGetPropertyValue("shippingAddresses", out var shippingNode) && shippingNode is JsonArray shippingArr)
        {
            foreach (var entry in shippingArr)
            {
                if (entry is JsonObject shippingObj)
                {
                    AddAddress(shippingObj);
                }
            }
        }
        else if (payload.TryGetPropertyValue("address", out var singleNode) && singleNode is JsonObject singleObj)
        {
            AddAddress(singleObj);
        }

        return collection;
    }

    private static string? BuildAddressText(JsonObject obj)
    {
        var parts = new List<string>();
        void Append(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        Append(ReadJsonString(obj, "postalCode") ?? ReadJsonString(obj, "zip"));
        Append(ReadJsonString(obj, "country"));
        Append(ReadJsonString(obj, "state") ?? ReadJsonString(obj, "prefecture"));
        Append(ReadJsonString(obj, "city"));
        Append(ReadJsonString(obj, "district"));
        Append(ReadJsonString(obj, "address1") ?? ReadJsonString(obj, "line1"));
        Append(ReadJsonString(obj, "address2") ?? ReadJsonString(obj, "line2"));
        Append(ReadJsonString(obj, "address3") ?? ReadJsonString(obj, "line3"));
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static JsonObject ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string? ReadJsonString(JsonObject? obj, string property)
    {
        if (obj is null) return null;
        if (!obj.TryGetPropertyValue(property, out var node)) return null;
        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str) => str.Trim(),
            _ => null
        };
    }

    private static decimal ReadJsonDecimal(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var node)) return 0m;
        return ReadJsonDecimal(node);
    }

    private static decimal ReadJsonDecimal(JsonNode? node)
    {
        if (node is null) return 0m;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<decimal>(out var dec)) return dec;
            if (value.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl, CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var lng)) return Convert.ToDecimal(lng, CultureInfo.InvariantCulture);
            if (value.TryGetValue<string>(out var str) && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return 0m;
    }

    private static string GenerateSalesOrderNumber(string companyCode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        return $"SO-{DateTime.UtcNow:yyyyMMddHHmmss}-{suffix}";
    }

    private string BuildSalesOrderSummary(string language, string? customerName, string? deliveryDate, decimal totalAmount, string currency)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(customerName))
        {
            parts.Add(customerName.Trim());
        }
        if (!string.IsNullOrWhiteSpace(deliveryDate))
        {
            parts.Add(Localize(language, $"納期 {deliveryDate}", $"交期 {deliveryDate}"));
        }
        if (totalAmount > 0m)
        {
            var amountText = $"{currency.ToUpperInvariant()} {totalAmount:0.##}";
            parts.Add(Localize(language, $"金額 {amountText}", $"金额 {amountText}"));
        }
        return parts.Count == 0 ? Localize(language, "受注", "受注订单") : string.Join(" / ", parts);
    }

    private async Task<ToolExecutionResult> GetVoucherByNumberAsync(string companyCode, string voucherNo, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM vouchers WHERE company_code=$1 AND payload->'header'->>'voucherNo' = $2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(voucherNo);
        var payload = await cmd.ExecuteScalarAsync(ct) as string;
        if (payload is null)
        {
            return ToolExecutionResult.FromModel(new { found = false, voucherNo });
        }
        var msg = new AgentResultMessage("assistant", $"已找到凭�?{voucherNo}", "info", new
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
        return ToolExecutionResult.FromModel(model, new List<AgentResultMessage> { msg });
    }

    /// <summary>
    /// 调用 AI 锢�售分析服务（两阶段架构）
    /// 阶段1: AI 识别意图 �?阶段2: 代码生成安全 SQL
    /// </summary>
    private async Task<Analytics.SalesAnalyticsService.AnalysisResult> AnalyzeSalesAsync(
        string companyCode, 
        string query,
        string userId,
        string? dateFrom, 
        string? dateTo, 
        string apiKey, 
        CancellationToken ct)
    {
        // 创建配置
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = apiKey,
                ["OpenAI:ChatModel"] = _configuration["OpenAI:ChatModel"] ?? "gpt-4"
            })
            .Build();
        
        // 创建用户安全上下文（TODO: 从数据库加载用户角色和权限）
        var userContext = await LoadUserSecurityContextAsync(companyCode, userId, ct);
        
        var salesAnalyticsService = new Analytics.SalesAnalyticsService(_ds, _httpClientFactory, config);
        return await salesAnalyticsService.AnalyzeAsync(companyCode, query, userContext, dateFrom, dateTo, ct);
    }
    
    /// <summary>
    /// 加载用户安全上下�?    /// </summary>
    private async Task<Analytics.UserSecurityContext> LoadUserSecurityContextAsync(
        string companyCode, 
        string userId, 
        CancellationToken ct)
    {
        var context = new Analytics.UserSecurityContext
        {
            UserId = userId,
            CompanyCode = companyCode,
            Roles = new[] { "owner" },  // 默认：��板权限（可以看扢�有数据）
            IsAdmin = false
        };
        
        // TODO: 从数据库加载用户的实际角色和权限
        // 例如：查�?user_roles 表获取角�?        // 查询 user_dept 获取部门
        // 查询 user_region 获取区域
        
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            
            // 查询用户信息
            // sanitized
                SELECT 
                    payload->>'roles' as roles,
                    payload->>'deptCode' as dept_code,
                    payload->>'regionCode' as region_code,
                    payload->>'isAdmin' as is_admin
                FROM users 
                WHERE company_code = $1 AND user_id = $2
                // sanitized
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(userId);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var rolesJson = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!string.IsNullOrEmpty(rolesJson))
                {
                    try
                    {
                        context.Roles = System.Text.Json.JsonSerializer.Deserialize<string[]>(rolesJson) 
                            ?? new[] { "owner" };
                    }
                    catch { }
                }
                
                context.DeptCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                context.RegionCode = reader.IsDBNull(2) ? null : reader.GetString(2);
                context.IsAdmin = !reader.IsDBNull(3) && reader.GetString(3) == "true";
            }
        }
        catch
        {
            // 如果查询失败，使用默认权�?        }
        
        return context;
    }

    private async Task PersistAssistantMessagesAsync(Guid sessionId, Guid? taskId, IReadOnlyList<AgentResultMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        foreach (var message in messages)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO ai_messages(id, session_id, role, content, payload, task_id) VALUES (gen_random_uuid(), $1,$2,$3,$4::jsonb,$5)";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(message.Role);
            cmd.Parameters.AddWithValue(message.Content ?? string.Empty);
            if (message.Status is null && message.Tag is null)
            {
                cmd.Parameters.AddWithValue(DBNull.Value);
            }
            else
            {
                var payload = new JsonObject
                {
                    ["kind"] = "event"
                };
                if (!string.IsNullOrWhiteSpace(message.Status))
                    payload["status"] = message.Status;
                if (message.Tag is not null)
                {
                    payload["tag"] = JsonSerializer.Deserialize<JsonNode>(JsonSerializer.Serialize(message.Tag, JsonOptions));
                }
                cmd.Parameters.AddWithValue(payload.ToJsonString());
            }
            cmd.Parameters.AddWithValue(taskId.HasValue ? taskId.Value : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await UpdateSessionTimestampAsync(sessionId, conn, ct);
    }

    private async Task<Guid> PersistMessageAsync(Guid sessionId, string role, string content, JsonObject? payload, object? tag, Guid? taskId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ai_messages(id, session_id, role, content, payload, task_id) VALUES (gen_random_uuid(),$1,$2,$3,$4::jsonb,$5) RETURNING id";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(role);
        cmd.Parameters.AddWithValue(content ?? string.Empty);
        if (payload is null && tag is null)
        {
            cmd.Parameters.AddWithValue(DBNull.Value);
        }
        else
        {
            var node = payload ?? new JsonObject();
            if (tag is not null)
            {
                node["tag"] = JsonSerializer.Deserialize<JsonNode>(JsonSerializer.Serialize(tag, JsonOptions));
            }
            cmd.Parameters.AddWithValue(node.ToJsonString());
        }
        cmd.Parameters.AddWithValue(taskId.HasValue ? taskId.Value : DBNull.Value);
        var insertedIdObj = await cmd.ExecuteScalarAsync(ct);
        var messageId = insertedIdObj is Guid guid ? guid : Guid.Parse(insertedIdObj?.ToString() ?? throw new InvalidOperationException("Failed to insert message id"));
        await UpdateSessionTimestampAsync(sessionId, conn, ct);
        return messageId;
    }

    private async Task UpdateMessageTaskAsync(Guid messageId, Guid sessionId, Guid taskId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ai_messages SET task_id = $2 WHERE id = $1";
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue(taskId);
        await cmd.ExecuteNonQueryAsync(ct);
        await UpdateSessionTimestampAsync(sessionId, conn, ct);
    }

    private static async Task UpdateSessionTimestampAsync(Guid sessionId, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ai_sessions SET updated_at = now() WHERE id=$1";
        cmd.Parameters.AddWithValue(sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement message)
    {
        var dict = new Dictionary<string, object?>
        {
            ["role"] = message.GetProperty("role").GetString()
        };
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            dict["content"] = content.GetString();
        }
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            dict["tool_calls"] = JsonSerializer.Deserialize<object>(toolCalls.GetRawText(), JsonOptions);
        }
        return dict;
    }

    private static object[] BuildToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "extract_invoice_data",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            file_id = new { type = "string", description = "上传时返回的 fileId" }
                        },
                        required = new[] { "file_id" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "extract_booking_settlement_data",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            file_id = new { type = "string", description = "上传时返回的 fileId" }
                        },
                        required = new[] { "file_id" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "find_moneytree_deposit_for_settlement",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            payment_date = new { type = "string", description = "结算单入金日 YYYY-MM-DD" },
                            // sanitized
                            keywords = new { type = "array", items = new { type = "string" }, description = "用于匹配摘要的关键词数组（如 BOOKING, ドイツギンコ�?等）" },
                            // sanitized
                            // sanitized
                        },
                        required = new[] { "payment_date", "net_amount" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "bulk_register_moneytree_rule",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            rules = new
                            {
                                type = "array",
                                // sanitized
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        title = new { type = "string" },
                                        description = new { type = "string" },
                                        priority = new { type = "integer" },
                                        matcher = new { type = "object", additionalProperties = true },
                                        action = new { type = "object", additionalProperties = true },
                                        isActive = new { type = "boolean" }
                                    },
                                    required = new[] { "title", "matcher", "action" }
                                }
                            }
                        },
                        required = new[] { "rules" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "lookup_account",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "科目名称、别名或编码" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "check_accounting_period",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            posting_date = new { type = "string", description = "YYYY-MM-DD 格式" }
                        },
                        required = new[] { "posting_date" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "verify_invoice_registration",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            registration_no = new { type = "string", description = "例如 T1234567890123" }
                        },
                        required = new[] { "registration_no" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "lookup_customer",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            limit = new { type = "integer", description = "返回的最大条数，默认 10" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "lookup_vendor",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            limit = new { type = "integer", description = "返回的最大条数，默认 10" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "search_vendor_receipts",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                        },
                        required = new[] { "vendor_id" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_expense_account_options",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            vendor_id = new { type = "string", description = "供应商ID（可选），用于查询该供应商历史使用的科目" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_vendor_invoice",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            // sanitized
                            due_date = new { type = "string", description = "支付期限 YYYY-MM-DD" },
                            // sanitized
                            // sanitized
                            summary = new { type = "string", description = "摘要说明" },
                            matched_receipts = new
                            {
                                type = "array",
                                // sanitized
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        receipt_id = new { type = "string", description = "入库记录ID" },
                                        material_code = new { type = "string", description = "品目编码" },
                                        quantity = new { type = "number", description = "数量" },
                                        unit_price = new { type = "number", description = "单价" },
                                        // sanitized
                                    },
                                    required = new[] { "receipt_id", "material_code", "quantity" }
                                }
                            },
                            // sanitized
                        },
                        required = new[] { "vendor_id" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "lookup_material",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            limit = new { type = "integer", description = "返回的最大条数，默认 10" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "register_moneytree_rule",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            description = new { type = "string", description = "可��的详细说明" },
                            priority = new { type = "integer", description = "优先级，数��越小越优先，默�?100" },
                            matcher = new
                            {
                                type = "object",
                                // sanitized
                                additionalProperties = true
                            },
                            action = new
                            {
                                type = "object",
                                // sanitized
                                additionalProperties = true
                            },
                            isActive = new { type = "boolean", description = "是否启用规则，默�?true" }
                        },
                        required = new[] { "title", "matcher", "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "request_clarification",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            document_id = new { type = "string", description = "相关文件�?fileId" },
                            // sanitized
                            detail = new { type = "string", description = "可��的补充说明" }
                        },
                        required = new[] { "document_id", "question" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_voucher",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            header = new
                            {
                                type = "object",
                                properties = new
                                {
                                    postingDate = new { type = "string", description = "YYYY-MM-DD" },
                                    summary = new { type = "string" },
                                    currency = new { type = "string", description = "默认�?JPY" },
                                    partnerName = new { type = "string", description = "供应�?客户名称" },
                                    vendorId = new { type = "string", description = "供应商ID（��过 lookup_vendor 获取），用于应付账款凭证" },
                                    // sanitized
                                    customerId = new { type = "string", description = "客户ID（��过 lookup_customer 获取），用于应收账款凭证" },
                                    customerCode = new { type = "string", description = "客户编码" },
                                    invoiceRegistrationNo = new { type = "string" },
                                    dueDate = new { type = "string", description = "支付期限 YYYY-MM-DD" }
                                },
                                required = new[] { "postingDate", "summary" }
                            },
                            lines = new
                            {
                                type = "array",
                                // sanitized
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        accountCode = new { type = "string" },
                                        amount = new { type = "number" },
                                        side = new { type = "string", description = "DR/CR �?debit/credit" },
                                        note = new { type = "string" },
                                        // sanitized
                                        tax = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                amount = new { type = "number" },
                                                accountCode = new { type = "string" },
                                                side = new { type = "string" }
                                            }
                                        }
                                    },
                                    required = new[] { "accountCode", "amount", "side" }
                                },
                                minItems = 2
                            },
                            attachments = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "关联的文�?ID 列表"
                            },
                            documentSessionId = new { type = "string", description = "当前文档上下文标识，必须匹配已注册的文件会话" }
                        },
                        required = new[] { "documentSessionId", "header", "lines" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_sales_order",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            customerCode = new { type = "string", description = "客户编码（优先提供）" },
                            customerName = new { type = "string", description = "客户名称（可选）" },
                            customer = new
                            {
                                type = "object",
                                // sanitized
                                properties = new
                                {
                                    code = new { type = "string" },
                                    name = new { type = "string" },
                                    addressCode = new { type = "string" },
                                    address = new { type = "object" },
                                    contact = new { type = "string" },
                                    phone = new { type = "string" }
                                }
                            },
                            // sanitized
                            deliveryDate = new { type = "string", description = "希望納期 YYYY-MM-DD" },
                            delivery = new
                            {
                                type = "object",
                                description = "送货信息",
                                properties = new
                                {
                                    addressCode = new { type = "string" },
                                    address = new { type = "object" },
                                    date = new { type = "string" },
                                    note = new { type = "string" }
                                }
                            },
                            currency = new { type = "string", description = "币种，默�?JPY" },
                            note = new { type = "string", description = "订单备注" },
                            lines = new
                            {
                                type = "array",
                                minItems = 1,
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        materialCode = new { type = "string" },
                                        materialName = new { type = "string" },
                                        description = new { type = "string" },
                                        quantity = new { type = "number" },
                                        uom = new { type = "string" },
                                        unitPrice = new { type = "number" },
                                        amount = new { type = "number" },
                                        note = new { type = "string" }
                                    },
                                    required = new[] { "materialCode", "quantity" }
                                }
                            }
                        },
                        required = new[] { "lines" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_voucher_by_number",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            voucher_no = new { type = "string" }
                        },
                        required = new[] { "voucher_no" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "analyze_sales",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            date_from = new { type = "string", description = "弢�始日�?YYYY-MM-DD（可选）" },
                            date_to = new { type = "string", description = "结束日期 YYYY-MM-DD（可选）" }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "fetch_webpage",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            url = new { type = "string", description = "要获取的网页URL" }
                        },
                        required = new[] { "url" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_business_partner",
                    // sanitized
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            // sanitized
                            // sanitized
                            // sanitized
                            // sanitized
                            postalCode = new { type = "string", description = "郵便番号" },
                            prefecture = new { type = "string", description = "都道府県" },
                            // sanitized
                            phone = new { type = "string", description = "電話番号" },
                            fax = new { type = "string", description = "FAX番号" },
                            // sanitized
                            contactPerson = new { type = "string", description = "担当者名" },
                            // sanitized
                        },
                        required = new[] { "name" }
                    }
                }
            }
        };
    }

    internal Task<string?> GetActiveDocumentSessionIdAsync(Guid sessionId, CancellationToken ct)
        => GetSessionActiveDocumentAsync(sessionId, ct);

    internal Task SetActiveDocumentSessionIdAsync(Guid sessionId, string? documentSessionId, CancellationToken ct)
        => SetSessionActiveDocumentAsync(sessionId, documentSessionId, ct);

    private async Task<string?> GetSessionActiveDocumentAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state->>'activeDocumentSessionId' FROM ai_sessions WHERE id=$1";
        cmd.Parameters.AddWithValue(sessionId);
        var result = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private async Task SetSessionActiveDocumentAsync(Guid sessionId, string? documentSessionId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(documentSessionId))
        {
            cmd.CommandText = "UPDATE ai_sessions SET state = COALESCE(state,'{}'::jsonb) - 'activeDocumentSessionId', updated_at = now() WHERE id=$1";
            cmd.Parameters.AddWithValue(sessionId);
        }
        else
        {
            cmd.CommandText = "UPDATE ai_sessions SET state = jsonb_set(COALESCE(state,'{}'::jsonb), '{activeDocumentSessionId}', to_jsonb($2::text), true), updated_at = now() WHERE id=$1";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(documentSessionId);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<ClarificationInfo?> LoadClarificationAsync(Guid sessionId, string questionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(questionId)) return null;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload::text FROM ai_messages WHERE session_id=$1 AND payload->>'status' = 'clarify' AND payload->'tag'->>'questionId' = $2 ORDER BY created_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(questionId);
        var payloadText = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(payloadText)) return null;
        JsonObject? payloadNode;
        try
        {
            payloadNode = JsonNode.Parse(payloadText)?.AsObject();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentKit] clarification payload JSON 解析失败");
            return null;
        }
        if (payloadNode is null) return null;
        if (!payloadNode.TryGetPropertyValue("tag", out var tagNode) || tagNode is null)
        {
            return null;
        }
        ClarificationTag? tag;
        try
        {
            tag = tagNode.Deserialize<ClarificationTag>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AgentKit] clarification tag 反序列化失败");
            return null;
        }
        if (tag is null) return null;
        JsonObject? analysisClone = null;
        if (tag.DocumentAnalysis is not null)
        {
            analysisClone = tag.DocumentAnalysis.DeepClone().AsObject();
        }
        var finalQuestionId = string.IsNullOrWhiteSpace(tag.QuestionId) ? questionId : tag.QuestionId!;
        var finalQuestion = string.IsNullOrWhiteSpace(tag.Question) ? string.Empty : tag.Question!;
        var documentSessionId = string.IsNullOrWhiteSpace(tag.DocumentSessionId) ? null : tag.DocumentSessionId;
        return new ClarificationInfo(finalQuestionId, documentSessionId, tag.DocumentId, finalQuestion, tag.Detail, analysisClone, tag.DocumentName, tag.BlobName, tag.DocumentLabel, tag.ScenarioKey, tag.MissingField, tag.AccountCode, tag.DraftVoucher);
    }

    private async Task MarkClarificationAnsweredAsync(Guid sessionId, string questionId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ai_messages SET payload = jsonb_set(COALESCE(payload,'{}'::jsonb), '{answeredAt}', to_jsonb(now())) WHERE session_id=$1 AND payload->>'status' = 'clarify' AND payload->'tag'->>'questionId' = $2";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(questionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<ToolExecutionResult?> RetryDraftVoucherWithAnswerAsync(AgentExecutionContext context, ClarificationInfo clarification, string answer, CancellationToken ct)
    {
        try
        {
            if (clarification.DraftVoucher is null) return null;
            if (string.IsNullOrWhiteSpace(clarification.MissingField)) return null;
            if (string.IsNullOrWhiteSpace(answer)) return null;

            // Booking 结算单：如果这次是��缺�?customerId”的回答，重试前确保我们手里有结算解�?JSON（commission/payment fee 等）�?            // 否则草�6�0传票只有丢��?5500 合计，无法拆分为「コミッション����決済サービスの手数料��两条明细�?            if (string.Equals(clarification.MissingField, "customerId", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(clarification.ScenarioKey) &&
                clarification.ScenarioKey!.Contains("voucher.ota.booking.settlement", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                try
                {
                    var docId = clarification.DocumentId!.Trim();
                    var hasBreakdown = false;
                    if (context.TryGetDocument(docId, out var existing) && existing is JsonObject obj)
                    {
                        var comm = TryGetJsonDecimal(obj, "commissionAmount");
                        var fee = TryGetJsonDecimal(obj, "paymentFeeAmount");
                        if (comm.HasValue && fee.HasValue && (comm.Value + fee.Value) > 0m) hasBreakdown = true;
                    }
                    if (!hasBreakdown)
                    {
                        var file = context.ResolveFile(docId);
                        if (file is not null)
                        {
                            var data = await ExtractBookingSettlementDataAsync(docId, file, context, ct);
                            if (data is not null)
                            {
                                context.RegisterDocument(docId, data);
                                _logger.LogInformation("[AgentKit] retry: ensured booking settlement doc breakdown before create_voucher docId={DocId}", docId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AgentKit] retry: failed to ensure booking settlement breakdown doc before create_voucher");
                }
            }

            // 若用户对 customerId 的回答是“名�?关键字��，尝试�?lookup_customer 解析为客户代码（唯一命中才自动替换）
            var resolvedAnswer = answer;
            if (string.Equals(clarification.MissingField, "customerId", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = await TryResolveCustomerCodeFromAnswerAsync(context.CompanyCode, answer, ct);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    resolvedAnswer = resolved!;
                    _logger.LogInformation("[AgentKit] resolved customerId answer '{Raw}' -> '{Resolved}'", answer, resolvedAnswer);
                }
            }
            else if (string.Equals(clarification.MissingField, "postingDate", StringComparison.OrdinalIgnoreCase))
            {
                var existingPosting = TryReadHeaderPostingDate(clarification.DraftVoucher);
                var normalized = NormalizePostingDateAnswer(answer, existingPosting);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    resolvedAnswer = normalized!;
                }
            }

            var draft = clarification.DraftVoucher.DeepClone().AsObject();
            InjectAnswerIntoDraftVoucher(draft, clarification.MissingField!, resolvedAnswer, clarification.AccountCode);

            using var doc = JsonDocument.Parse(draft.ToJsonString());
            _logger.LogInformation("[AgentKit] retry draft voucher with {Field}='{Answer}' accountCode={AccountCode}", clarification.MissingField, answer, clarification.AccountCode ?? "(null)");
            var exec = await CreateVoucherAsync(context, doc.RootElement, ct);
            return exec;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] retry draft voucher failed");
            return null;
        }
    }

    private async Task<string?> TryResolveCustomerCodeFromAnswerAsync(string companyCode, string answer, CancellationToken ct)
    {
        try
        {
            // 先按关键字查客户主数据（businesspartners.partner_code�?            var payload = await LookupCustomerAsync(companyCode, answer.Trim(), 5, ct);
            if (!payload.TryGetPropertyValue("items", out var itemsNode) || itemsNode is not JsonArray itemsArr) return null;
            if (itemsArr.Count != 1) return null;
            if (itemsArr[0] is not JsonObject first) return null;
            if (!first.TryGetPropertyValue("code", out var codeNode) || codeNode is not JsonValue codeVal || !codeVal.TryGetValue<string>(out var code)) return null;
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void InjectAnswerIntoDraftVoucher(JsonObject draft, string fieldName, string fieldValue, string? accountCode)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(fieldValue)) return;

        var oldPostingDate = TryReadHeaderPostingDate(draft);

        // postingDate is a header field (not a line field)
        if (string.Equals(fieldName, "postingDate", StringComparison.OrdinalIgnoreCase))
        {
            if (!draft.TryGetPropertyValue("header", out var headerNode) || headerNode is not JsonObject headerObj)
            {
                headerObj = new JsonObject();
                draft["header"] = headerObj;
            }
            headerObj["postingDate"] = fieldValue.Trim();

            // keep open-item paymentDate consistent if it was derived from old postingDate
            if (draft.TryGetPropertyValue("lines", out var linesNode2) && linesNode2 is JsonArray linesArr2)
            {
                foreach (var item in linesArr2)
                {
                    if (item is not JsonObject lineObj) continue;
                    if (!lineObj.TryGetPropertyValue("paymentDate", out var pdNode) || pdNode is null)
                    {
                        EnsurePaymentDate(lineObj, fieldValue.Trim());
                        continue;
                    }
                    var current = pdNode is JsonValue pdVal && pdVal.TryGetValue<string>(out var s) ? s?.Trim() : null;
                    if (!string.IsNullOrWhiteSpace(oldPostingDate) && string.Equals(current, oldPostingDate, StringComparison.OrdinalIgnoreCase))
                    {
                        lineObj["paymentDate"] = fieldValue.Trim();
                    }
                }
            }
            return;
        }

        var postingDate = oldPostingDate;

        // 优先注入到指定科目行；若未指�?accountCode，则注入到第丢�条行
        if (draft.TryGetPropertyValue("lines", out var linesNode) && linesNode is JsonArray linesArr)
        {
            JsonObject? firstLine = null;
            foreach (var item in linesArr)
            {
                if (item is not JsonObject lineObj) continue;
                firstLine ??= lineObj;
                var code = TryReadAccountCode(lineObj);
                if (!string.IsNullOrWhiteSpace(accountCode) &&
                    !string.Equals(code, accountCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!lineObj.ContainsKey(fieldName))
                {
                    lineObj[fieldName] = fieldValue.Trim();
                    EnsurePaymentDate(lineObj, postingDate);
                    return;
                }
            }
            if (firstLine is not null && !firstLine.ContainsKey(fieldName))
            {
                firstLine[fieldName] = fieldValue.Trim();
                EnsurePaymentDate(firstLine, postingDate);
            }
        }
    }

    private static string? TryReadHeaderPostingDate(JsonObject draft)
    {
        if (draft.TryGetPropertyValue("header", out var headerNode) && headerNode is JsonObject headerObj)
        {
            if (headerObj.TryGetPropertyValue("postingDate", out var node) && node is JsonValue val && val.TryGetValue<string>(out var dateStr))
            {
                return string.IsNullOrWhiteSpace(dateStr) ? null : dateStr.Trim();
            }
        }
        return null;
    }

    private static void EnsurePaymentDate(JsonObject lineObj, string? postingDate)
    {
        if (string.IsNullOrWhiteSpace(postingDate)) return;
        // Finance 校验：某�?open-item 科目（如 1100 売掛金）要求行上�?paymentDate
        if (!lineObj.ContainsKey("paymentDate"))
        {
            lineObj["paymentDate"] = postingDate;
        }
    }

    private static string? TryReadAccountCode(JsonObject line)
    {
        if (line.TryGetPropertyValue("accountCode", out var node) && node is JsonValue val && val.TryGetValue<string>(out var code))
        {
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        }
        return null;
    }

    private async Task<IReadOnlyList<string>> GetRecentUserMessagesAsync(Guid sessionId, int limit, CancellationToken ct)
    {
        var result = new List<string>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM ai_messages WHERE session_id=$1 AND role='user' ORDER BY created_at DESC LIMIT $2";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var content = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(content))
            {
                result.Add(content);
            }
        }
        return result;
    }

    private static string NormalizeScenarioKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var lower = input.ToLowerInvariant();
        var normalized = Regex.Replace(lower, "[^a-z0-9\\.-]+", ".");
        normalized = Regex.Replace(normalized, @"\.\.+", ".");
        normalized = normalized.Trim('.');
        return normalized;
    }

    private sealed record AgentExecutionResult(IReadOnlyList<AgentResultMessage> Messages);

    internal sealed record ClarificationInfo(
        string QuestionId,
        string? DocumentSessionId,
        string? DocumentId,
        string Question,
        string? Detail,
        JsonObject? DocumentAnalysis,
        string? DocumentName,
        string? BlobName,
        string? DocumentLabel,
        string? ScenarioKey,
        string? MissingField = null,
        string? AccountCode = null,
        JsonObject? DraftVoucher = null);

    private sealed record ClarificationTag(
        string? QuestionId,
        string? DocumentSessionId,
        string? DocumentId,
        string? Question,
        string? Detail,
        JsonObject? DocumentAnalysis,
        string? DocumentName,
        string? BlobName,
        string? DocumentLabel,
        string? ScenarioKey,
        string? MissingField = null,
        string? AccountCode = null,
        JsonObject? DraftVoucher = null);

    public sealed record AgentRunResult(Guid SessionId, IReadOnlyList<AgentResultMessage> Messages);

    public sealed record ScenarioPreviewResult(IReadOnlyList<string> MatchedScenarioKeys, string SystemPrompt, IReadOnlyList<object> ContextMessages);

    internal sealed record GeneratedAgentScenario(
        string ScenarioKey,
        string Title,
        string Description,
        string? Instructions,
        IReadOnlyList<string> ToolHints,
        JsonNode? Metadata,
        JsonNode? Context,
        int Priority,
        bool IsActive);

    internal sealed record AgentMessageRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string Message, string ApiKey, string Language, Func<string, UploadedFileRecord?>? FileResolver, string? ScenarioKey, string? AnswerTo, Guid? TaskId);

    internal sealed record AgentFileRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string FileId, string FileName, string ContentType, long Size, string ApiKey, string Language, Func<string, UploadedFileRecord?>? FileResolver, string? ScenarioKey, string BlobName, string? UserMessage = null, JsonObject? ParsedData = null, string? AnswerTo = null);

    private sealed record UploadContext(string Kind, DateTimeOffset CreatedAt, IReadOnlyList<UploadContextDoc> Documents, string? ActiveDocumentSessionId);
    private sealed record UploadContextDoc(string DocumentSessionId, string? FileId, string? FileName, string? BlobName, JsonObject? Analysis);
    internal sealed record AgentTaskDocument(string DocumentSessionId, string FileId, string FileName, string ContentType, long Size, string BlobName, string StoredPath, JsonObject? Data, string? FileNameNormalized, string? DocumentLabel);
    internal sealed record AgentTaskRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string? Message, string ApiKey, string Language, string? ScenarioKeyOverride, string? AnswerTo, Func<string, UploadedFileRecord?>? FileResolver = null);
    internal sealed record AgentTaskPlanningRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string? Message, string ApiKey, string Language, string? ScenarioKeyOverride, ClarificationInfo? Clarification, string? ActiveDocumentSessionId);
    internal sealed record TaskGroupPlan(string ScenarioKey, string DocumentSessionId, IReadOnlyList<string> DocumentIds, string Reason, string? UserMessageOverride);
    internal sealed record AgentTaskPreparationResult(Guid SessionId, IReadOnlyList<AgentTaskDocument> Documents, ClarificationInfo? Clarification, string? ActiveDocumentSessionId, IReadOnlyList<InvoiceTaskInfo> Tasks);
    internal sealed record AgentTaskPlanningResult(Guid SessionId, IReadOnlyList<TaskGroupPlan> Plans, IReadOnlyList<string> UnassignedDocuments);
    internal sealed record UploadedFileEnvelope(string FileId, UploadedFileRecord Record);
    internal sealed record AgentTaskExecutionRequest(Guid SessionId, string CompanyCode, Auth.UserCtx UserCtx, string ApiKey, string Language, IReadOnlyList<TaskGroupPlan> Plans, IReadOnlyList<AgentTaskDocument> Documents, ClarificationInfo? Clarification, string? ActiveDocumentSessionId, IReadOnlyList<InvoiceTaskInfo> Tasks);
    internal sealed record InvoiceTaskInfo(Guid TaskId, string FileId, string DocumentSessionId, string FileName, string? ContentType, long Size, string Status, string? DocumentLabel, string? Summary, JsonObject? Analysis, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    internal async Task LogAssistantMessageAsync(Guid sessionId, AgentResultMessage message, Guid? taskId, CancellationToken ct)
    {
        await PersistAssistantMessagesAsync(sessionId, taskId, new[] { message }, ct);
    }
}
}


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
using Server.Infrastructure;
using Server.Modules.AgentKit;
using Server.Modules.AgentKit.Tools;

namespace Server.Modules;

public sealed class AgentKitService
{
    private readonly NpgsqlDataSource _ds;
    private readonly FinanceService _finance;
    private readonly InvoiceRegistryService _invoiceRegistry;
    private readonly InvoiceTaskService _invoiceTaskService;
    private readonly SalesOrderTaskService _salesOrderTaskService;
    private readonly AgentScenarioService _scenarioService;
    private readonly AgentAccountingRuleService _ruleService;
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

    private static string NormalizeLanguage(string? language)
    {
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) return "zh";
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) return "en";
        return "ja";
    }

    private static string Localize(string language, string ja, string zh, string? en = null) =>
        language switch
        {
            "zh" => zh,
            "en" => en ?? ja,
            _ => ja
        };

    public AgentKitService(
        NpgsqlDataSource ds,
        FinanceService finance,
        InvoiceRegistryService invoiceRegistry,
        InvoiceTaskService invoiceTaskService,
        SalesOrderTaskService salesOrderTaskService,
        AgentScenarioService scenarioService,
        AgentAccountingRuleService ruleService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AgentKitService> logger,
        AgentToolRegistry toolRegistry,
        // 注入各个工具（与 BuildToolDefinitions() 保持一致）
        CheckAccountingPeriodTool checkAccountingPeriodTool,
        VerifyInvoiceRegistrationTool verifyInvoiceRegistrationTool,
        LookupCustomerTool lookupCustomerTool,
        LookupMaterialTool lookupMaterialTool,
        LookupAccountTool lookupAccountTool,
        LookupVendorTool lookupVendorTool,
        SearchVendorReceiptsTool searchVendorReceiptsTool,
        GetExpenseAccountOptionsTool getExpenseAccountOptionsTool,
        CreateVendorInvoiceTool createVendorInvoiceTool,
        GetVoucherByNumberTool getVoucherByNumberTool,
        ExtractBookingSettlementDataTool extractBookingSettlementDataTool,
        FindMoneytreeDepositForSettlementTool findMoneytreeDepositForSettlementTool,
        PreflightCheckTool preflightCheckTool,
        CalculatePayrollTool calculatePayrollTool,
        SavePayrollTool savePayrollTool,
        GetPayrollHistoryTool getPayrollHistoryTool,
        GetMyPayrollTool getMyPayrollTool,
        GetPayrollComparisonTool getPayrollComparisonTool,
        GetDepartmentSummaryTool getDepartmentSummaryTool)
    {
        _ds = ds;
        _finance = finance;
        _invoiceRegistry = invoiceRegistry;
        _invoiceTaskService = invoiceTaskService;
        _salesOrderTaskService = salesOrderTaskService;
        _scenarioService = scenarioService;
        _ruleService = ruleService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _toolRegistry = toolRegistry;
        
        // 注册工具到 Registry
        _toolRegistry.Register(checkAccountingPeriodTool);
        _toolRegistry.Register(verifyInvoiceRegistrationTool);
        _toolRegistry.Register(lookupCustomerTool);
        _toolRegistry.Register(lookupMaterialTool);
        _toolRegistry.Register(lookupAccountTool);
        _toolRegistry.Register(lookupVendorTool);
        _toolRegistry.Register(searchVendorReceiptsTool);
        _toolRegistry.Register(getExpenseAccountOptionsTool);
        _toolRegistry.Register(createVendorInvoiceTool);
        _toolRegistry.Register(getVoucherByNumberTool);
        _toolRegistry.Register(extractBookingSettlementDataTool);
        _toolRegistry.Register(findMoneytreeDepositForSettlementTool);
        _toolRegistry.Register(preflightCheckTool);
        _toolRegistry.Register(calculatePayrollTool);
        _toolRegistry.Register(savePayrollTool);
        _toolRegistry.Register(getPayrollHistoryTool);
        _toolRegistry.Register(getMyPayrollTool);
        _toolRegistry.Register(getPayrollComparisonTool);
        _toolRegistry.Register(getDepartmentSummaryTool);
    }

    internal async Task<AgentRunResult> ProcessUserMessageAsync(AgentMessageRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException(Localize(language, "OpenAI API キーが設定されていません。", "OpenAI API Key 未配置", "OpenAI API Key is not configured"));

        if (request.TaskId.HasValue)
        {
            return await ProcessTaskMessageAsync(request, ct);
        }
        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var latestUpload = await GetLatestUploadAsync(sessionId, ct);
        var storedDocumentSessionId = await GetSessionActiveDocumentAsync(sessionId, ct);
        var historySince = latestUpload?.CreatedAt;
        var history = await LoadHistoryAsync(sessionId, historySince, 20, request.TaskId, ct);
        var allScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();
        var selectedScenarios = SelectScenariosForMessage(allScenarios, request.ScenarioKey, request.Message);

        var documentLabelEntries = latestUpload is not null
            ? BuildDocumentLabelEntries(latestUpload.Documents, d => d.DocumentSessionId, d => d.FileName ?? d.FileId ?? d.DocumentSessionId, d => d.Analysis, d => d.FileId, language: language)
            : new List<DocumentLabelEntry>();
        var documentLabelMap = documentLabelEntries.ToDictionary(e => e.SessionId, e => e, StringComparer.OrdinalIgnoreCase);

        var tokens = new Dictionary<string, string?> { ["input"] = request.Message };
        if (documentLabelEntries.Count > 0)
        {
            tokens["documentGroups"] = string.Join("；", documentLabelEntries.Select(entry =>
            {
                var summary = new StringBuilder();
                summary.Append(entry.Label).Append('：').Append(entry.DisplayName);
                summary.Append("（fileId=").Append(entry.PrimaryFileId);
                summary.Append("；docSessionId=").Append(entry.SessionId);
                if (!string.IsNullOrWhiteSpace(entry.Highlight))
                {
                    summary.Append("；").Append(entry.Highlight);
                }
                summary.Append('）');
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
        if (!string.IsNullOrWhiteSpace(request.AnswerTo))
        {
            clarification = await LoadClarificationAsync(sessionId, request.AnswerTo, ct);
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
            }
            else
            {
                _logger.LogWarning("[AgentKit] 未找到待回答的问题 questionId={QuestionId}", request.AnswerTo);
            }
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
            groupSummary.AppendLine(Localize(language, "現在の証憑グループ：", "当前票据分组：", "Current document groups:"));
            foreach (var entry in documentLabelEntries)
            {
                var highlightJa = string.IsNullOrWhiteSpace(entry.Highlight) ? string.Empty : $"；ハイライト：{entry.Highlight}";
                var highlightZh = string.IsNullOrWhiteSpace(entry.Highlight) ? string.Empty : $"；{entry.Highlight}";
                var highlightEn = string.IsNullOrWhiteSpace(entry.Highlight) ? string.Empty : $"; highlight: {entry.Highlight}";
                var line = Localize(language,
                    $"{entry.Label}：{entry.DisplayName}（fileId={entry.PrimaryFileId}；docSessionId={entry.SessionId}{highlightJa}）",
                    $"{entry.Label}：{entry.DisplayName}（fileId={entry.PrimaryFileId}；docSessionId={entry.SessionId}{highlightZh}）",
                    $"{entry.Label}: {entry.DisplayName} (fileId={entry.PrimaryFileId}; docSessionId={entry.SessionId}{highlightEn})");
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
            clarifyNote.Append(Localize(language, "ユーザーは以前の質問に回答しています：", "当前用户正在回答此前的问题：", "User is answering a previous question: "));
            if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
            {
                clarifyNote.Append('[').Append(clarification.DocumentLabel).Append(']');
            }
            clarifyNote.Append(clarification.Question);
            var docLabel = !string.IsNullOrWhiteSpace(clarification.DocumentName) ? clarification.DocumentName : clarification.DocumentId;
            if (!string.IsNullOrWhiteSpace(docLabel))
            {
                clarifyNote.Append(Localize(language, "（ファイル：", "（文件：", " (File: ")).Append(docLabel).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                clarifyNote.Append(Localize(language, "。補足：", "。补充说明：", ". Note: ")).Append(clarification.Detail);
            }
            if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
            {
                clarifyNote.Append(Localize(language, "。関連コンテキスト：", "。关联上下文：", ". Related context: ")).Append(clarification.DocumentSessionId);
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
            hint.Append(Localize(language, "直近にアップロードした証憑を続けて処理できます。", "最近一次上传的票据可继续处理。", "Recently uploaded documents can be processed."));
            if (latestUpload.Documents.Count > 1)
            {
                hint.Append(Localize(language, $" 合計 {latestUpload.Documents.Count} 件のファイル。", $"共 {latestUpload.Documents.Count} 个文件。", $" Total {latestUpload.Documents.Count} file(s)."));
            }
            if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
            {
                if (documentLabelMap.TryGetValue(activeDocumentSessionId, out var activeEntryInfo))
                {
                    hint.Append(Localize(language,
                        $" 現在のコンテキスト：{activeEntryInfo.Label}（{activeEntryInfo.DisplayName}）。",
                        $" 当前上下文：{activeEntryInfo.Label}（{activeEntryInfo.DisplayName}）。",
                        $" Current context: {activeEntryInfo.Label} ({activeEntryInfo.DisplayName})."));
                }
                else
                {
                    hint.Append(Localize(language,
                        $" 現在のドキュメントセッション：{activeDocumentSessionId}。",
                        $" 当前上下文文档会话：{activeDocumentSessionId}。",
                        $" Current document session: {activeDocumentSessionId}."));
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
                if (!string.IsNullOrWhiteSpace(primaryDoc.FileId)) hint.Append(Localize(language, $" デフォルト fileId={primaryDoc.FileId}。", $" 默认文件ID={primaryDoc.FileId}。", $" Default fileId={primaryDoc.FileId}."));
                if (!string.IsNullOrWhiteSpace(partner)) hint.Append(Localize(language, $" 取引先：{partner}。", $" 供应方：{partner}。", $" Partner: {partner}."));
                if (totalAmount.HasValue) hint.Append(Localize(language, $" 税込金額：{totalAmount.Value:0.##}。", $" 含税金额：{totalAmount.Value:0.##}。", $" Total amount (tax incl.): {totalAmount.Value:0.##}."));
            }
            hint.Append(Localize(language, " ユーザーが「この一枚」「さっきのもの」などと指す場合は、今回のアップロードを継続して処理してください。", " 若用户提及「这张」「上一张」等指代，请延续本次上传的票据继续执行。", " If user refers to 'this one' or 'the previous one', continue processing the current upload."));
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = hint.ToString().Trim()
            });
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = request.Message
        });

        var context = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, selectedScenarios, request.FileResolver ?? (_ => null));
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

        try
        {
            if (request.TaskId.HasValue)
            {
                await _invoiceTaskService.UpdateStatusAsync(request.TaskId.Value, "in_progress", null, ct);
            }

            // 保存用户消息时包含 answerTo，以便前端能正确关联回答到问题
            JsonObject? userPayload = null;
            if (!string.IsNullOrWhiteSpace(request.AnswerTo))
            {
                userPayload = new JsonObject { ["answerTo"] = request.AnswerTo };
            }
            await PersistMessageAsync(sessionId, "user", request.Message, userPayload, null, request.TaskId, ct);

            if (clarification is not null)
            {
                await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
            }

            var agentResult = await RunAgentAsync(messages, context, ct);

            await PersistAssistantMessagesAsync(sessionId, context.TaskId, agentResult.Messages, ct);

            // 如果有关联的任务，在执行完成后更新任务状态，避免卡在“处理中”
            if (context.TaskId.HasValue)
            {
                var hasError = agentResult.Messages.Any(m => string.Equals(m.Status, "error", StringComparison.OrdinalIgnoreCase));
                var needsClarification = agentResult.Messages.Any(m => string.Equals(m.Status, "clarify", StringComparison.OrdinalIgnoreCase));
                
                var nextStatus = hasError ? "error" : (needsClarification ? "pending" : "completed");
                _logger.LogInformation("[AgentKit] 任务执行完毕，自动更新状态: taskId={TaskId}, status={Status}", context.TaskId.Value, nextStatus);
                await _invoiceTaskService.UpdateStatusAsync(context.TaskId.Value, nextStatus, null, ct);
            }

            return new AgentRunResult(sessionId, agentResult.Messages);
        }
        catch (Exception ex)
        {
            if (request.TaskId.HasValue)
            {
                _logger.LogError(ex, "[AgentKit] 任务执行出错: taskId={TaskId}", request.TaskId.Value);
                await _invoiceTaskService.UpdateStatusAsync(request.TaskId.Value, "error", new JsonObject { ["error"] = ex.Message }, ct);
            }
            throw;
        }
    }

    private async Task<AgentRunResult> ProcessTaskMessageAsync(AgentMessageRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (!request.TaskId.HasValue)
            throw new InvalidOperationException(Localize(language, "taskId が指定されていません。", "taskId 未提供", "taskId is not specified"));

        var taskId = request.TaskId.Value;
        var task = await _invoiceTaskService.GetAsync(taskId, ct);
        if (task is null)
            throw new InvalidOperationException(Localize(language, "該当する証憑タスクが見つかりません。", "未找到对应的票据任务", "Document task not found"));
        if (!string.Equals(task.CompanyCode, request.CompanyCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localize(language, "この証憑タスクは現在の会社に属していません。", "票据任务不属于当前公司", "This document task does not belong to the current company"));
        if (!string.IsNullOrWhiteSpace(task.UserId) &&
            !string.IsNullOrWhiteSpace(request.UserCtx.UserId) &&
            !string.Equals(task.UserId, request.UserCtx.UserId, StringComparison.Ordinal))
            throw new InvalidOperationException(Localize(language, "この票据タスクにアクセスする権限がありません。", "无权访问该票据任务", "You do not have permission to access this document task"));

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
                // 强制同步：确保 AI 看到的 DocumentId 永远与当前 Task 的 FileId 一致，防止因历史 ID 不一致导致的解析失败
                else if (!string.Equals(clarification.DocumentId, task.FileId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[AgentKit] 纠正澄清信息中的 DocumentId: {Old} -> {New}", clarification.DocumentId, task.FileId);
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
            }
            else
            {
                _logger.LogWarning("[AgentKit] 未找到待回答的问题 questionId={QuestionId}", request.AnswerTo);
            }
        }

        var messages = BuildInitialMessages(request.CompanyCode, selectedScenarios, accountingRules, history, tokens, language);

        // 如果票据中已经识别出人数，则无需再询问人数，仅询问姓名并直接计算人均
        if (clarification is null && task.Analysis is JsonObject autoAnalysis)
        {
            var netAmount = TryGetJsonDecimal(autoAnalysis, "totalAmount") - (TryGetJsonDecimal(autoAnalysis, "taxAmount") ?? 0m);
            if (netAmount >= 20000)
            {
                var detectedCount = TryGetPersonCount(autoAnalysis);
                if (detectedCount is not null && detectedCount.Value > 0)
                {
                    var perPerson = netAmount.Value / detectedCount.Value;
                    var suggestedAccount = perPerson > 10000 ? "交際費" : "会議費";
                    var partnerName = ReadString(autoAnalysis, "partnerName") ?? task.FileName;
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] =
                            $"[SYSTEM CALCULATION] 票据中已识别用餐人数 = {detectedCount.Value}人。人均金额 = {perPerson:N0} JPY ({netAmount:N0} / {detectedCount.Value}人)。根据 10,000 JPY 规则，科目判定应为：【{suggestedAccount}】。" +
                            "请不要再询问人数，只需向用户确认“参加者氏名”。" +
                            "请调用 lookup_account 获取该科目的最新代码，并在凭证摘要中体现人数与参加者信息。" +
                            $" 凭证摘要建议：{suggestedAccount} | {partnerName} ({detectedCount.Value}人, 参加者: 未确认)"
                    });
                }
            }
        }

        // 如果是回答澄清问题的流程，且金额较大，后端计算好科目建议注入 AI，防止其选错
        if (clarification is not null && task.Analysis is JsonObject analysis)
        {
            var netAmount = TryGetJsonDecimal(analysis, "totalAmount") - (TryGetJsonDecimal(analysis, "taxAmount") ?? 0m);
            if (netAmount >= 20000)
            {
                // 尝试从回答中提取数字
                var match = Regex.Match(request.Message, @"(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var personCount) && personCount > 0)
                {
                    var perPerson = netAmount.Value / personCount;
                    var suggestedAccount = perPerson > 10000 ? "交際費" : "会議費";
                    
                    // 尝试获取商户名
                    var partnerName = ReadString(analysis, "partnerName") ?? task.FileName;

                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "system",
                        ["content"] = $"[SYSTEM INFO] 当前正在处理的文件 ID 为：\"{task.FileId}\" (标签: {task.DocumentLabel ?? "无"})。如果需要再次提取数据，请务必使用此 ID。\n" + 
                                     $"[SYSTEM CALCULATION] 人均金额 = {perPerson:N0} JPY ({netAmount:N0} / {personCount}人)。根据 10,000 JPY 规则，科目判定应为：【{suggestedAccount}】。请调用 lookup_account 获取该科目的最新代码。同时，请将凭证摘要（header.summary）更新为：「{suggestedAccount} | {partnerName} ({request.Message})」。"
                    });
                }
            }
        }

        var summaryBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(task.DocumentLabel))
        {
            summaryBuilder.AppendLine(Localize(language, $"現在の証憑：{task.DocumentLabel}（{task.FileName}）", $"当前票据：{task.DocumentLabel}（{task.FileName}）", $"Current document: {task.DocumentLabel} ({task.FileName})"));
        }
        else
        {
            summaryBuilder.AppendLine(Localize(language, $"現在の証憑：{task.FileName}", $"当前票据：{task.FileName}", $"Current document: {task.FileName}"));
        }
        summaryBuilder.AppendLine(Localize(language, $"fileId={task.FileId}；docSessionId={task.DocumentSessionId}", $"fileId={task.FileId}；docSessionId={task.DocumentSessionId}", $"fileId={task.FileId}; docSessionId={task.DocumentSessionId}"));
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
            clarifyNote.Append(Localize(language, "ユーザーは以前の質問に回答しています：", "当前用户正在回答此前的问题：", "User is answering a previous question: "));
            if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
            {
                clarifyNote.Append('[').Append(clarification.DocumentLabel).Append(']');
            }
            clarifyNote.Append(clarification.Question);
            if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                clarifyNote.Append(Localize(language, "（fileId：", "（fileId：", " (fileId: ")).Append(clarification.DocumentId).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                clarifyNote.Append(Localize(language, "。補足：", "。补充说明：", ". Note: ")).Append(clarification.Detail);
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

        try
        {
            await _invoiceTaskService.UpdateStatusAsync(task.Id, "in_progress", null, ct);
            // 保存用户消息时包含 answerTo，以便前端能正确关联回答到问题
            JsonObject? taskUserPayload = null;
            if (!string.IsNullOrWhiteSpace(request.AnswerTo))
            {
                taskUserPayload = new JsonObject { ["answerTo"] = request.AnswerTo };
            }
            await PersistMessageAsync(sessionId, "user", request.Message, taskUserPayload, null, request.TaskId, ct);
            if (clarification is not null)
            {
                await MarkClarificationAnsweredAsync(sessionId, clarification.QuestionId, ct);
            }

            var agentResult = await RunAgentAsync(messages, context, ct);
            await PersistAssistantMessagesAsync(sessionId, context.TaskId, agentResult.Messages, ct);

            // 任务执行完成后更新任务状态
            var hasError = agentResult.Messages.Any(m => string.Equals(m.Status, "error", StringComparison.OrdinalIgnoreCase));
            var needsClarification = agentResult.Messages.Any(m => string.Equals(m.Status, "clarify", StringComparison.OrdinalIgnoreCase));
            
            var nextStatus = hasError ? "error" : (needsClarification ? "pending" : "completed");
            _logger.LogInformation("[AgentKit] 票据任务执行完毕，自动更新状态: taskId={TaskId}, status={Status}", task.Id, nextStatus);
            await _invoiceTaskService.UpdateStatusAsync(task.Id, nextStatus, null, ct);

            return new AgentRunResult(sessionId, agentResult.Messages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentKit] 票据任务执行出错: taskId={TaskId}", task.Id);
            await _invoiceTaskService.UpdateStatusAsync(task.Id, "error", new JsonObject { ["error"] = ex.Message }, ct);
            throw;
        }
    }
    internal async Task<AgentRunResult> ProcessFileAsync(AgentFileRequest request, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException(Localize(language, "OpenAI API キーが設定されていません。", "OpenAI API Key 未配置", "OpenAI API Key is not configured"));

        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);
        var history = Array.Empty<(string Role, string Content)>();
        var allScenarios = await _scenarioService.ListActiveAsync(request.CompanyCode, ct);
        var accountingRules = (await _ruleService.ListAsync(request.CompanyCode, includeInactive: false, ct)).Take(20).ToArray();

        var fileRecord = request.FileResolver?.Invoke(request.FileId);

        JsonObject? parsedData = request.ParsedData;
        var documentSessionId = $"doc_{Guid.NewGuid():N}";
        const string documentLabel = "#1";
        if (parsedData is null && fileRecord is not null)
        {
            try
            {
                var preContext = new AgentExecutionContext(sessionId, request.CompanyCode, request.UserCtx, request.ApiKey, language, allScenarios, request.FileResolver ?? (_ => null));
                parsedData = await ExtractInvoiceDataAsync(request.FileId, fileRecord, preContext, ct);
                if (parsedData is not null)
                {
                    preContext.RegisterDocument(request.FileId, parsedData, documentSessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentKit] 预解析发票失败: {FileId}", request.FileId);
            }
        }

        var prompt = Localize(language,
            $"ユーザーがファイルをアップロードしました：{request.FileName}（ID: {request.FileId}、種類: {request.ContentType}、サイズ: {request.Size} バイト）。必要に応じて解析し、関連する会計処理を完了してください。",
            $"用户上传了文件：{request.FileName}（ID: {request.FileId}，类型: {request.ContentType}，大小: {request.Size} 字节）。请根据需要分析此文件，并完成相关会计处理。",
            $"User uploaded a file: {request.FileName} (ID: {request.FileId}, Type: {request.ContentType}, Size: {request.Size} bytes). Please analyze and complete the related accounting process.");

        var uploadPayload = new JsonObject
        {
            ["kind"] = "user.upload",
            ["fileId"] = request.FileId,
            ["fileName"] = request.FileName,
            ["documentSessionId"] = documentSessionId,
            ["activeDocumentSessionId"] = documentSessionId,
            ["documentLabel"] = documentLabel
        };

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

        await PersistMessageAsync(sessionId, "user", prompt, uploadPayload, null, null, ct);

        var fileContext = new FileMatchContext(request.FileName, request.ContentType, null, parsedData);
        var selectedScenarios = SelectScenariosForFile(allScenarios, request.ScenarioKey, fileContext);

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
            summaryBuilder.Append("（").Append(highlight).Append('）');
        }
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "system",
            ["content"] = "当前票据分组：" + summaryBuilder.ToString()
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
            selected = SelectScenariosForFile(scenarios, scenarioKey, fileContext);
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
            throw new ArgumentException(Localize(language, "prompt は必須です。", "prompt 必填", "prompt is required"), nameof(prompt));

        var http = _httpClientFactory.CreateClient("openai");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var existingSummary = existingScenarios.Count == 0
            ? Localize(language, "（現在シナリオは未設定）", "(当前尚未配置场景)", "(No scenarios configured)")
            : string.Join('\n', existingScenarios
                .OrderBy(s => s.Priority)
                .Select(s => $"- {s.ScenarioKey}: {s.Title}"));

        var sbPrompt = new StringBuilder();
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            sbPrompt.AppendLine("你是一个企业 ERP AgentKit 的场景设计专家，需要根据用户提供的自然语言需求生成一个新的智能场景配置。");
            sbPrompt.AppendLine($"公司代码: {companyCode}");
            sbPrompt.AppendLine("当前已存在的场景:");
            sbPrompt.AppendLine(existingSummary);
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("请只输出一个 JSON 对象，结构如下：");
            sbPrompt.AppendLine("{");
            sbPrompt.AppendLine("  \"scenarioKey\": string,          // 唯一键，建议小写字母、数字、点号或短横线");
            sbPrompt.AppendLine("  \"title\": string,               // 人类可读标题");
            sbPrompt.AppendLine("  \"description\": string,         // 场景说明");
            sbPrompt.AppendLine("  \"instructions\": string,        // 给 Agent 的执行指引");
            sbPrompt.AppendLine("  \"toolHints\": string[],         // 建议使用的工具名称列表");
            sbPrompt.AppendLine("  \"priority\": number,            // 越小优先级越高，默认 100");
            sbPrompt.AppendLine("  \"isActive\": boolean,           // 是否启用");
            sbPrompt.AppendLine("  \"metadata\": object,            // matcher、contextMessages 等元数据");
            sbPrompt.AppendLine("  \"context\": object              // 用于 Agent 执行时注入的上下文，可为空对象");
            sbPrompt.AppendLine("}");
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("要求：");
            sbPrompt.AppendLine("- metadata.matcher 中至少包含 appliesTo/messageContains 等信息，以便匹配。");
            sbPrompt.AppendLine("- 若用户需求未说明，toolHints 可为空数组。");
            sbPrompt.AppendLine("- 如果无法确定字段，请给出合理的默认值。");
        }
        else
        {
            sbPrompt.AppendLine("あなたは企業向け ERP AgentKit のシナリオ設計エキスパートです。ユーザーが提供する自然言語の要望に基づき、新しいインテリジェントシナリオ設定を生成してください。");
            sbPrompt.AppendLine($"会社コード: {companyCode}");
            sbPrompt.AppendLine("既存のシナリオ一覧:");
            sbPrompt.AppendLine(existingSummary);
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("JSON オブジェクトのみを出力してください。構造は次の通りです：");
            sbPrompt.AppendLine("{");
            sbPrompt.AppendLine("  \"scenarioKey\": string,          // 一意キー。小文字・数字・ドット・ハイフン推奨");
            sbPrompt.AppendLine("  \"title\": string,               // 人が読めるタイトル");
            sbPrompt.AppendLine("  \"description\": string,         // シナリオの説明");
            sbPrompt.AppendLine("  \"instructions\": string,        // Agent への実行ガイドライン");
            sbPrompt.AppendLine("  \"toolHints\": string[],         // 推奨ツール名リスト");
            sbPrompt.AppendLine("  \"priority\": number,            // 小さいほど優先度が高い。既定 100");
            sbPrompt.AppendLine("  \"isActive\": boolean,           // 有効化フラグ");
            sbPrompt.AppendLine("  \"metadata\": object,            // matcher・contextMessages などのメタデータ");
            sbPrompt.AppendLine("  \"context\": object              // Agent 実行時に注入するコンテキスト。空オブジェクトでも可");
            sbPrompt.AppendLine("}");
            sbPrompt.AppendLine();
            sbPrompt.AppendLine("要件：");
            sbPrompt.AppendLine("- metadata.matcher には appliesTo や messageContains などの情報を含め、マッチング可能にしてください。");
            sbPrompt.AppendLine("- ユーザー要望に記載がなければ toolHints は空配列でも構いません。");
            sbPrompt.AppendLine("- 判別できない項目は合理的な既定値を設定してください。");
        }
        var systemPrompt = sbPrompt.ToString();

        var requestBody = new
        {
            model = "gpt-4o",
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            }
        };

        using var response = await http.PostAsync("chat/completions",
            new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"), ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[AgentKit] 生成场景失败: {Status} {Body}", response.StatusCode, responseText);
            throw new Exception(Localize(language, "プロンプトからシナリオ設定を生成できませんでした。", "无法根据提示生成场景配置", "Failed to generate scenario from prompt"));
        }

        using var rootDoc = JsonDocument.Parse(responseText);
        var choice = rootDoc.RootElement.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new Exception(Localize(language, "モデルがシナリオ内容を返しませんでした。", "模型未返回任何场景内容", "Model did not return scenario content"));

        using var scenarioDoc = JsonDocument.Parse(content);
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
            catch
            {
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
            catch
            {
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
        throw new Exception("创建会话失败");
    }

    private async Task<List<(string Role, string Content)>> LoadHistoryAsync(Guid sessionId, DateTimeOffset? since, int maxMessages, Guid? taskId, CancellationToken ct)
    {
        if (maxMessages <= 0 && !since.HasValue) return new List<(string, string)>();

        var list = new List<(string, string)>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (taskId.HasValue)
        {
            if (since.HasValue)
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
        }
        else if (since.HasValue)
        {
            cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 AND created_at >= $2 ORDER BY created_at";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(since.Value.UtcDateTime);
        }
        else
        {
            cmd.CommandText = "SELECT role, COALESCE(content,'') FROM ai_messages WHERE session_id=$1 ORDER BY created_at DESC LIMIT $2";
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(maxMessages);
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

        return list;
    }

    internal async Task<AgentTaskPreparationResult> PrepareAgentTaskAsync(AgentTaskRequest request, IReadOnlyList<UploadedFileEnvelope> files, CancellationToken ct)
    {
        var language = NormalizeLanguage(request.Language);
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException(Localize(language, "OpenAI API キーが設定されていません。", "OpenAI API Key 未配置", "OpenAI API Key is not configured"));

        if (files.Count == 0)
            throw new InvalidOperationException(Localize(language, "少なくとも1件のファイルが必要です。", "至少需要一个文件", "At least one file is required"));

        var sessionId = await EnsureSessionAsync(request.SessionId, request.CompanyCode, request.UserCtx, ct);

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

        foreach (var file in files)
        {
            _logger.LogInformation("[AgentKit] 开始处理上传文件 {FileId} ({FileName})", file.FileId, file.Record.FileName);
            JsonObject? parsed = null;
            var documentSessionId = $"doc_{Guid.NewGuid():N}";
            try
            {
                parsed = await ExtractInvoiceDataAsync(file.FileId, file.Record, context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentKit] 解析文件 {FileId} 失败", file.FileId);
            }

            if (parsed is not null)
            {
                context.RegisterDocument(file.FileId, parsed, documentSessionId);
                _logger.LogInformation("[AgentKit] 文件 {FileId} 解析成功", file.FileId);
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
            ? Localize(language, $"ユーザーがファイル {documents[0].FileName} をアップロードし、システムが初期解析を完了しました。", $"用户上传了文件 {documents[0].FileName}，系统已完成初步解析。", $"User uploaded file {documents[0].FileName}, initial parsing complete.")
            : Localize(language, $"ユーザーが {documents.Count} 件のファイルをアップロードし、システムが初期解析を完了しました。", $"用户上传了 {documents.Count} 个文件，系统已完成初步解析。", $"User uploaded {documents.Count} file(s), initial parsing complete.");

        await PersistMessageAsync(sessionId, "user", content, uploadPayload, null, null, ct);
        if (!string.IsNullOrWhiteSpace(activeDocumentSessionId))
        {
            await SetSessionActiveDocumentAsync(sessionId, activeDocumentSessionId, ct);
        }

        return new AgentTaskPreparationResult(sessionId, documents, clarification, activeDocumentSessionId, taskInfos);
    }

    internal async Task<AgentTaskPlanningResult> PlanTaskGroupsAsync(AgentTaskPlanningRequest request, IReadOnlyList<AgentTaskDocument> documents, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            throw new InvalidOperationException("OpenAI API Key 未配置");

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
                _logger.LogInformation("[AgentKit] 任务规划聚焦 {Note}，匹配文档 {Count}", note, filtered.Length);
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
                _logger.LogWarning("[AgentKit] 指定的 documentSessionId={DocumentSessionId} 未匹配任何待处理文件", focusDocumentSessionId);
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
        if (!string.IsNullOrWhiteSpace(request.ScenarioKeyOverride))
        {
            var overrideKey = NormalizeScenarioKey(request.ScenarioKeyOverride);
            var match = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, overrideKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(Localize(language, $"指定されたシナリオ {request.ScenarioKeyOverride} は存在しないか無効です。", $"指定场景 {request.ScenarioKeyOverride} 不存在或未启用", $"Specified scenario {request.ScenarioKeyOverride} does not exist or is disabled"));
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
                    ? Localize(language, "ユーザー指定シナリオ（文書ごとに分割）", "用户指定场景（按文档拆分）", "User-specified scenario (split by document)")
                    : Localize(language, "ユーザー指定シナリオ", "用户指定场景", "User-specified scenario");
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
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

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
            return input[..maxLen] + "…";
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
            "あなたは ERP のタスク編成コーディネーターです。ユーザーがアップロードした複数のファイルやメッセージ内容に基づき、どのようにグループ化し、最適な Agent シナリオを選択するかを判断してください。",
            "以下の原則に従ってください：",
            "1. ファイル同士が同一の業務タスクに属するか（同一契約の複数ページ、領収書の表裏など）を判断し、同一タスクであればまとめて処理します。完全に独立している場合は複数タスクに分割してください。",
            "2. 各ファイルの解析情報（analysis）とユーザーからの入力を組み合わせ、最も適合するシナリオを判断してください。",
            "3. シナリオ一覧（優先順位の高い順）は次の通りです：",
            scenarioSummary,
            "4. 高い確度で判断できる場合のみシナリオを選択してください。適切なシナリオが見つからない場合はファイルを unassignedDocuments に追加します。",
            "5. 追加の注意事項やユーザーに確認してほしい事項がある場合は message フィールドに記載してください。",
            "6. すべてのファイルには documentSessionId が含まれており、同一の業務コンテキストを表します。1 つのタスク内のファイルはすべて同じ documentSessionId でなければなりません。複数のコンテキストを扱う必要がある場合はタスクを分割してください。",
            "7. 出力する各タスクには documentSessionId を必ず含め、タスクに含まれるファイルの documentSessionId と一致させてください。"
        };
        var plannerLinesZh = new[]
        {
            "你是一名 ERP 智能编排协调器，需要根据用户上传的多个文件与消息内容，决定如何分组并选择最合适的 Agent 场景。",
            "请遵循以下原则：",
            "1. 先判断文件之间是否属于同一业务任务（例如同一合同多页、同一票据的正反面），需要合并在一起执行；若完全独立，则应拆分为多个任务。",
            "2. 结合每个文件的解析信息（analysis）以及用户输入的文本，判断与哪个场景最匹配。",
            "3. 场景列表如下（按优先级从高到低）：",
            scenarioSummary,
            "4. 仅在高度确定的情况下选择场景；若没有合适的场景，请将文件列入 unassignedDocuments。",
            "5. 若能给出额外提醒或需要用户补充的信息，可在 message 字段说明。",
            "6. 每个文件都包含 documentSessionId，用于标识同一业务上下文。一个任务内的所有文件必须具有完全一致的 documentSessionId；如需处理多个上下文，请拆分为多条任务。",
            "7. 输出的每个任务必须包含 documentSessionId 字段，并与任务中文件实际所属的 documentSessionId 一致。"
        };
        var plannerLinesEn = new[]
        {
            "You are an ERP task orchestration coordinator. Based on multiple files uploaded by the user and the message content, determine how to group them and select the most appropriate Agent scenario.",
            "Follow these principles:",
            "1. Determine whether files belong to the same business task (e.g., multiple pages of the same contract, front and back of the same receipt). If so, process them together; if completely independent, split into multiple tasks.",
            "2. Combine each file's analysis information with user input text to determine the best matching scenario.",
            "3. The scenario list (in order of priority from high to low) is:",
            scenarioSummary,
            "4. Only select a scenario when highly confident; if no suitable scenario is found, add files to unassignedDocuments.",
            "5. If there are additional notes or information needed from the user, include them in the message field.",
            "6. Each file contains a documentSessionId representing the same business context. All files within a task must have the exact same documentSessionId; if multiple contexts need to be processed, split into multiple tasks.",
            "7. Each output task must include the documentSessionId field, matching the actual documentSessionId of the files in the task."
        };
        var plannerLines = language switch
        {
            "zh" => plannerLinesZh,
            "en" => plannerLinesEn,
            _ => plannerLinesJa
        };
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
        systemPrompt.AppendLine(Localize(language, "confidence の範囲は 0~1（任意項目）です。", "其中 confidence 范围 0~1，可选。", "confidence range is 0~1 (optional)."));
        if (focusNotes.Count > 0)
        {
            systemPrompt.AppendLine();
            systemPrompt.AppendLine(Localize(language, "注意：以下の対象のみ処理し、それ以外のファイルは保留してください。", "注意：当前只需处理以下目标范围，其他文件暂缓：", "Note: Only process the following targets, other files are on hold:"));
            foreach (var note in focusNotes.Distinct())
            {
                systemPrompt.AppendLine($"- {note}");
            }
        }
        if (clarification is not null)
        {
            systemPrompt.AppendLine();
            systemPrompt.AppendLine(Localize(language, "補足: ユーザーは次の質問に回答しています。関連するファイルを優先し、計画理由に対応関係を明示してください。", "补充说明：用户正在回应以下问题，请优先关注相关文件并在计划理由中标记对应关系。", "Note: User is responding to the following question. Prioritize related files and indicate the relationship in the planning reason."));
            systemPrompt.AppendLine(Localize(language, $"質問：{clarification.Question}", $"问题：{clarification.Question}", $"Question: {clarification.Question}"));
            if (!string.IsNullOrWhiteSpace(clarification.DocumentId))
            {
                systemPrompt.AppendLine(Localize(language, $"関連ファイルID：{clarification.DocumentId}", $"关联文件ID：{clarification.DocumentId}", $"Related fileId: {clarification.DocumentId}"));
            }
            if (!string.IsNullOrWhiteSpace(clarification.Detail))
            {
                systemPrompt.AppendLine(Localize(language, $"備考：{clarification.Detail}", $"备注：{clarification.Detail}", $"Remarks: {clarification.Detail}"));
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

        var requestBody = new
        {
            model = "gpt-4o",
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = systemPrompt.ToString() },
                new { role = "user", content = JsonSerializer.Serialize(userPayload, JsonOptions) }
            }
        };

        using var response = await http.PostAsync("chat/completions",
            new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"), ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[AgentKit] 任务分组模型调用失败: {Status} {Body}", response.StatusCode, responseText);
            throw new Exception(Localize(language, "タスクのグルーピングを完了できませんでした。しばらくしてから再試行してください。", "无法完成任务分组，请稍后重试", "Unable to complete task grouping. Please try again later."));
        }

        using var rootDoc = JsonDocument.Parse(responseText);
        var content = rootDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new Exception(Localize(language, "モデルがタスク分割の結果を返しませんでした。", "模型未返回任务分组结果", "Model did not return task grouping result"));
        }

        using var planDoc = JsonDocument.Parse(content);
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
                        _logger.LogWarning("[AgentKit] 模型建议的上下文 {Suggested} 与实际 {Actual} 不一致，已采用实际值", requestedSessionId, resolvedSessionId);
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
                    _logger.LogWarning("[AgentKit] 模型计划包含多个文档上下文，已拆分执行 scenario={ScenarioKey}", scenario.ScenarioKey);
                    foreach (var sessionGroup in groupedBySession)
                    {
                        var sessionKey = string.IsNullOrWhiteSpace(sessionGroup.Key)
                            ? $"legacy_{sessionGroup.First().FileId}"
                            : sessionGroup.Key;
                        var sessionDocs = sessionGroup.Select(d => d.FileId).ToArray();
                        var localizedReason = string.IsNullOrWhiteSpace(reason)
                            ? Localize(language, "ファイルごとに自動分割しました。", "按文档自动拆分", "Auto-split by document")
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
        catch
        {
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
        var jaRules = new[]
        {
            "あなたは企業 ERP システムの財務アシスタントです。ユーザーの自然言語指示を理解し、アップロードされた証憑を解析し、提供されたツールで会計処理を実行してください。",
            $"会社コード: {companyCode}",
            "業務ルール：",
            "1. 請求書／領収書の画像を受け取ったら、まず extract_invoice_data を呼び出して構造化データを取得すること。",
            "2. 勘定科目を特定する必要がある場合は lookup_account を利用し、名称や別名から社内コードを検索すること。",
            "3. 伝票起票前に check_accounting_period で会計期間が開いているか確認し、必要に応じて verify_invoice_registration で適格請求書登録番号を検証すること。",
            "4. create_voucher を呼び出す際は documentSessionId を必ず指定し、借方・貸方が一致するよう調整すること。情報が不足または不明な場合はユーザーへ確認すること。",
            "5. ツールがエラーを返した場合は、欠落している項目や次のステップをユーザーへ明確に伝えること。",
            "6. すべての回答は簡潔な日本語で記載し、実行結果や伝票番号などの重要情報を明示すること。",
            "7. 追加情報が必要な場合は request_clarification ツールで questionId 付きカードを生成し、テキストだけで質問しないこと。",
            "8. 証憑や質問を参照する際は必ず票据グループ番号（例: #1）を示し、ツール引数にも document_id と documentSessionId を渡すこと。",
            "9. ファイルを扱うツールでは必ずシステムが提供する fileId（32 桁 GUID など）を使用し、元のファイル名を使わないこと。"
        };
        var zhRules = new[]
        {
            "你是企业 ERP 系统中的财务智能助手，负责理解用户的自然语言指令、解析上传的票据，并通过提供的工具完成会计相关操作。",
            $"公司代码: {companyCode}",
            "工作守则：",
            "1. 对于发票/收据类图片，先调用 extract_invoice_data 获取结构化信息。",
            "2. 需要确定会计科目时，使用 lookup_account 以名称或别名检索内部科目编码。",
            "3. 创建会计凭证前，务必调用 check_accounting_period 确认会计期间处于打开状态，必要时调用 verify_invoice_registration 校验发票登记号。",
            "4. 调用 create_voucher 时必须带上 documentSessionId，并确保借贷金额一致；若信息缺失或不确定，应先向用户确认。",
            "5. 工具返回错误时要及时反馈用户，并说明缺失的字段或下一步建议。",
            "6. 所有回复请使用简洁的中文，明确列出操作结果、凭证编号等关键信息。",
            "7. 需要向用户确认信息时，必须调用 request_clarification 工具生成 questionId 卡片，禁止仅输出纯文本提问。",
            "8. 提及票据或提问时，务必引用票据分组编号（例如 #1），并在工具参数中携带 document_id 和 documentSessionId。",
            "9. 调用任何需要文件的工具时，document_id 必须使用系统提供的 fileId（如 32 位 GUID），禁止使用文件原始名称。"
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
                sb.AppendLine("公司自定义票据指引（按优先级排序，需优先参考）：");
            }
            else
            {
                sb.AppendLine("会社固有の仕訳ガイド（優先度順・該当する場合は優先的に参照してください）：");
            }
            var index = 1;
            foreach (var rule in rules.Where(r => r.IsActive).Take(20))
            {
                var line = new StringBuilder();
                line.Append(index++).Append('.').Append(' ').Append(rule.Title);
                var keywords = rule.Keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToArray();
                if (keywords is { Length: > 0 })
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "；关键词：" : "；キーワード：").Append(string.Join(" / ", keywords));
                }
                if (!string.IsNullOrWhiteSpace(rule.AccountCode) || !string.IsNullOrWhiteSpace(rule.AccountName))
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "；推荐借方：" : "；推奨借方：");
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
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "；建议备注：" : "；備考候補：").Append(rule.Note!.Trim());
                }
                if (!string.IsNullOrWhiteSpace(rule.Description))
                {
                    line.Append(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "；说明：" : "；説明：").Append(rule.Description!.Trim());
                }
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
                ? "处理票据时若匹配上述模式，请优先调用 lookup_account 等工具确认科目编码；若仍不确定，应向用户确认而不是擅自假设。"
                : "証憑が上記パターンに該当する場合は、lookup_account などのツールで科目コードを確認し、それでも不明なときは勝手に推測せず利用者へ確認してください。");
        }

        if (scenarios.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "当前可用场景：" : "利用可能なシナリオ：");
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
            parts.Add(Localize(language, $"取引先：{partner}", $"供应方：{partner}", $"Partner: {partner}"));
        }
        if (TryGetJsonString(analysis, "issueDate", out var issueDate) && !string.IsNullOrWhiteSpace(issueDate))
        {
            parts.Add(Localize(language, $"日付：{issueDate}", $"日期：{issueDate}", $"Date: {issueDate}"));
        }
        var amount = TryGetJsonDecimal(analysis, "totalAmount");
        if (amount.HasValue && amount.Value > 0)
        {
            parts.Add(Localize(language, $"税込金額：{amount.Value:0.##}", $"含税金额：{amount.Value:0.##}", $"Total (tax incl.): {amount.Value:0.##}"));
        }
        if (parts.Count == 0) return null;
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)
            ? string.Join("，", parts)
            : string.Join("／", parts);
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

    private static int? TryGetPersonCount(JsonObject obj)
    {
        var keys = new[]
        {
            "personCount", "peopleCount", "partySize", "numberOfPeople", "numPeople",
            "attendeeCount", "participants", "attendees"
        };
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue jsonValue) continue;
            if (jsonValue.TryGetValue<int>(out var intVal) && intVal > 0) return intVal;
            if (jsonValue.TryGetValue<decimal>(out var decVal) && decVal > 0) return (int)Math.Round(decVal, MidpointRounding.AwayFromZero);
            if (jsonValue.TryGetValue<double>(out var dblVal) && dblVal > 0) return (int)Math.Round(dblVal, MidpointRounding.AwayFromZero);
            if (jsonValue.TryGetValue<string>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
            {
                var match = Regex.Match(strVal, @"(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0) return parsed;
            }
        }
        return null;
    }

    private static bool IsPlaceholderRegistrationNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return string.Equals(normalized, "T1234567890123", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "1234567890123", StringComparison.OrdinalIgnoreCase);
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
                if (string.IsNullOrWhiteSpace(fileId)) return null;
                
                var fid = fileId.Trim();
                // 1. 完全匹配
                if (string.Equals(fid, task.FileId, StringComparison.OrdinalIgnoreCase)) goto match;
                
                // 2. 匹配标签（如 "#1"）
                if (fid.StartsWith("#") && !string.IsNullOrWhiteSpace(task.DocumentLabel) && 
                    string.Equals(fid, task.DocumentLabel, StringComparison.OrdinalIgnoreCase)) goto match;
                
                // 3. 匹配去除前缀后的 ID（处理 tmp_ 或 file_ 前缀不一致的情况）
                var cleanId = fid.Contains('_') ? fid.Split('_', 2)[1] : fid;
                var cleanTaskId = task.FileId.Contains('_') ? task.FileId.Split('_', 2)[1] : task.FileId;
                if (string.Equals(cleanId, cleanTaskId, StringComparison.OrdinalIgnoreCase)) goto match;

                // 4. 模糊匹配：如果输入 ID 包含在任务 ID 中，或反之（处理某些哈希截断或前缀丢失）
                if (task.FileId.Contains(cleanId, StringComparison.OrdinalIgnoreCase) || 
                    fid.Contains(cleanTaskId, StringComparison.OrdinalIgnoreCase)) goto match;

                return null;

                match:
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
            return forcedScenario;
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

    // 兜底：历史版本里有强制“受注/订单”场景的逻辑；如果当前代码缺失该实现，
    // 这里提供一个保守实现以保证编译通过（默认不强制任何场景）。
    private static bool TryForceSalesOrderScenario(
        string? message,
        IReadOnlyList<AgentScenarioService.AgentScenario> scenarios,
        out AgentScenarioService.AgentScenario[]? forced)
    {
        forced = null;
        return false;
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
                "text" => ScenarioTarget.MessageOnly,
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

        if (threshold is null && string.IsNullOrWhiteSpace(lowMessage) && string.IsNullOrWhiteSpace(highMessage))
        {
            return null;
        }

        return new ScenarioExecutionHints(threshold, lowMessage, highMessage);
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
        string? HighAmountSystemMessage);

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
                    _logger.LogInformation("[AgentKit] 跳过任务 {ScenarioKey}，因为 documentSessionId={PlanDocumentSessionId} 不在当前焦点 {FocusDocumentSessionId}", plan.ScenarioKey, plan.DocumentSessionId, focusDocumentSessionId);
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) && processedDocumentSessions.Contains(plan.DocumentSessionId))
            {
                var duplicateText = Localize(language, $"ドキュメントコンテキスト {plan.DocumentSessionId} は既に処理済みのため、タスク {plan.ScenarioKey} をスキップしました。", $"文档上下文 {plan.DocumentSessionId} 已处理，跳过重复任务 {plan.ScenarioKey}。", $"Document context {plan.DocumentSessionId} already processed, skipping duplicate task {plan.ScenarioKey}.");
                var dupMsg = new AgentResultMessage("assistant", duplicateText, "info", null);
                await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { dupMsg }, ct);
                allMessages.Add(dupMsg);
                continue;
            }

            var scenarioDef = scenarios.FirstOrDefault(s => string.Equals(s.ScenarioKey, plan.ScenarioKey, StringComparison.OrdinalIgnoreCase));
            if (scenarioDef is null)
            {
                var scenarioMissing = Localize(language, $"シナリオ {plan.ScenarioKey} が見つからないか、無効化されています。", $"场景 {plan.ScenarioKey} 未找到或未启用。", $"Scenario {plan.ScenarioKey} not found or disabled.");
                allMessages.Add(new AgentResultMessage("assistant", scenarioMissing, "warning", null));
                continue;
            }

            var tokens = new Dictionary<string, string?>
            {
                ["groupReason"] = plan.Reason,
                ["groupMessage"] = plan.UserMessageOverride
            };
            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId))
            {
                tokens["documentSessionId"] = plan.DocumentSessionId;
            }
            if (!string.IsNullOrWhiteSpace(focusDocumentSessionId))
            {
                tokens["focusDocumentSessionId"] = focusDocumentSessionId;
            }
            if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) && documentLabelMap.TryGetValue(plan.DocumentSessionId, out var planLabel))
            {
                tokens["documentLabel"] = planLabel.Label;
                tokens["documentName"] = planLabel.DisplayName;
            }
            if (clarification is not null)
            {
                tokens["answerQuestionId"] = clarification.QuestionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId)) tokens["answerDocumentSessionId"] = clarification.DocumentSessionId;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentId)) tokens["answerDocumentId"] = clarification.DocumentId;
                if (!string.IsNullOrWhiteSpace(clarification.Question)) tokens["answerQuestion"] = clarification.Question;
                if (!string.IsNullOrWhiteSpace(clarification.Detail)) tokens["answerDetail"] = clarification.Detail;
                if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel)) tokens["answerDocumentLabel"] = clarification.DocumentLabel;
            }

            var messages = BuildInitialMessages(request.CompanyCode, new[] { scenarioDef }, accountingRules, Array.Empty<(string Role, string Content)>(), tokens, language);

            var summaryBuilder = new StringBuilder();
            var includeClarification = clarification is not null &&
                (string.IsNullOrWhiteSpace(clarification.DocumentSessionId) ||
                 string.IsNullOrWhiteSpace(plan.DocumentSessionId) ||
                 string.Equals(clarification.DocumentSessionId, plan.DocumentSessionId, StringComparison.OrdinalIgnoreCase));
            if (includeClarification && clarification is not null)
            {
                if (!string.IsNullOrWhiteSpace(clarification.Question))
                {
                    summaryBuilder.AppendLine(Localize(language, "質問への回答：" + clarification.Question, "问题答复：" + clarification.Question, "Answer to question: " + clarification.Question));
                }
                if (!string.IsNullOrWhiteSpace(clarification.Detail))
                {
                    summaryBuilder.AppendLine(Localize(language, "補足説明：" + clarification.Detail, "补充说明：" + clarification.Detail, "Additional info: " + clarification.Detail));
                }
                var docLabel = clarification.DocumentName ?? clarification.DocumentId;
                if (!string.IsNullOrWhiteSpace(docLabel))
                {
                    summaryBuilder.AppendLine(Localize(language, "関連ファイル：" + docLabel, "关联文件：" + docLabel, "Related file: " + docLabel));
                }
                if (!string.IsNullOrWhiteSpace(clarification.DocumentSessionId))
                {
                    summaryBuilder.AppendLine(Localize(language, "関連コンテキスト：" + clarification.DocumentSessionId, "关联上下文：" + clarification.DocumentSessionId, "Related context: " + clarification.DocumentSessionId));
                }
                if (!string.IsNullOrWhiteSpace(clarification.DocumentLabel))
                {
                    summaryBuilder.AppendLine(Localize(language, "関連グループ：" + clarification.DocumentLabel, "关联分组：" + clarification.DocumentLabel, "Related group: " + clarification.DocumentLabel));
                }
            }
            decimal? planNetAmount = null;
            string? planCurrency = null;

            foreach (var docId in plan.DocumentIds)
            {
                var doc = request.Documents.FirstOrDefault(d => string.Equals(d.FileId, docId, StringComparison.OrdinalIgnoreCase));
                if (doc is null)
                {
                    var warnText = Localize(language, $"タスク {plan.ScenarioKey} は不明なファイル {docId} を参照しているためスキップしました。", $"任务 {plan.ScenarioKey} 引用了未知文件 {docId}，已跳过。", $"Task {plan.ScenarioKey} references unknown file {docId}, skipped.");
                    var warnMsg = new AgentResultMessage("assistant", warnText, "warning", null);
                    await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { warnMsg }, ct);
                    allMessages.Add(warnMsg);
                    continue;
                }

                if (processedFiles.Contains(doc.FileId))
                {
                    var infoText = Localize(language, $"ファイル {doc.FileName ?? doc.FileId} は別のタスクで処理済みのためスキップしました。", $"文件 {doc.FileName ?? doc.FileId} 已在其他任务中执行，已跳过。", $"File {doc.FileName ?? doc.FileId} already processed in another task, skipped.");
                    var infoMsg = new AgentResultMessage("assistant", infoText, "info", null);
                    await PersistAssistantMessagesAsync(request.SessionId, taskInfo?.TaskId, new[] { infoMsg }, ct);
                    allMessages.Add(infoMsg);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(plan.DocumentSessionId) &&
                    !string.Equals(doc.DocumentSessionId, plan.DocumentSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    var mismatchText = Localize(language, $"タスク {plan.ScenarioKey} のファイル {doc.FileName ?? doc.FileId} は文書コンテキストが一致しないためスキップしました。", $"任务 {plan.ScenarioKey} 的文件 {doc.FileName ?? doc.FileId} 上下文不匹配，已跳过。", $"File {doc.FileName ?? doc.FileId} in task {plan.ScenarioKey} has mismatched context, skipped.");
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
                summaryBuilder.AppendLine(Localize(language, $"ファイル：{labelPrefix}{doc.FileName} ({doc.ContentType}, {doc.Size} bytes)", $"文件：{labelPrefix}{doc.FileName} ({doc.ContentType}, {doc.Size} bytes)", $"File: {labelPrefix}{doc.FileName} ({doc.ContentType}, {doc.Size} bytes)"));
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
                summaryBuilder.AppendLine(Localize(language, "ドキュメントコンテキスト：" + plan.DocumentSessionId, "文档上下文：" + plan.DocumentSessionId, "Document context: " + plan.DocumentSessionId));
            }

            if (!string.IsNullOrWhiteSpace(plan.UserMessageOverride))
            {
                summaryBuilder.AppendLine();
                summaryBuilder.AppendLine(Localize(language, "ユーザー備考：" + plan.UserMessageOverride, "用户备注：" + plan.UserMessageOverride, "User note: " + plan.UserMessageOverride));
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = summaryBuilder.ToString()
            });

            Func<string, UploadedFileRecord?> resolver = id =>
            {
                var match = request.Documents.FirstOrDefault(d => string.Equals(d.FileId, id, StringComparison.OrdinalIgnoreCase));
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
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

        var tools = BuildToolDefinitions();

        for (var step = 0; step < 8; step++)
        {
            var requestBody = new
            {
                model = "gpt-4o",
                temperature = 0.1,
                tool_choice = "auto",
                messages = openAiMessages,
                tools
            };

            using var response = await http.PostAsync("chat/completions",
                new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"), ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[AgentKit] OpenAI 调用失败：{Status} {Body}", response.StatusCode, responseText);
                var errorContent = Localize(context.Language, $"LLM 呼び出しに失敗しました：{response.StatusCode}", $"调用语言模型失败：{response.StatusCode}", $"LLM call failed: {response.StatusCode}");
                aggregated.Add(new AgentResultMessage("assistant", errorContent, "error", null));
                return new AgentExecutionResult(aggregated);
            }

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            openAiMessages.Add(ToDictionary(message));

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var toolName = toolCall.GetProperty("function").GetProperty("name").GetString() ?? string.Empty;
                    var rawArgs = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                    JsonDocument argsDoc;
                    try
                    {
                        argsDoc = JsonDocument.Parse(rawArgs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AgentKit] 解析工具参数失败: {Name} Args={Args}", toolName, rawArgs);
                        argsDoc = JsonDocument.Parse("{}");
                    }

                    var execution = await ExecuteToolAsync(toolName, argsDoc.RootElement, context, ct);
                    var toolMessage = new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolCall.GetProperty("id").GetString(),
                        ["content"] = execution.ContentForModel
                    };
                    openAiMessages.Add(toolMessage);
                    if (execution.Messages is { Count: > 0 })
                    {
                        aggregated.AddRange(execution.Messages);
                    }
                }
                continue;
            }

            var content = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString() ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(content))
            {
                aggregated.Add(new AgentResultMessage("assistant", content.Trim(), null, null));
            }
            var finalized = FinalizeMessages(aggregated, context);
            return new AgentExecutionResult(finalized);
        }

        aggregated.Add(new AgentResultMessage("assistant", Localize(context.Language, "処理ステップが多すぎるため停止しました。", "执行步骤过多，已停止。", "Too many processing steps, stopped."), "error", null));
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
                      && (m.Content.Contains("已创建会计凭证", StringComparison.OrdinalIgnoreCase)
                          || m.Content.Contains("会計伝票を作成しました", StringComparison.OrdinalIgnoreCase))))
                .ToList();
            if (messages.Count == 0)
            {
                messages.Add(new AgentResultMessage("assistant", Localize(context.Language, "AI が会計伝票を作成できませんでした。証憑内容をご確認いただくか、しばらくしてから再試行してください。", "AI 未能创建会计凭证，请确认票据内容或稍后重试。", "AI could not create accounting voucher. Please verify document content or try again later."), "warning", null));
            }
        }
        return messages;
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(string name, JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        // 优先使用注册表中的工具
        if (_toolRegistry.Contains(name))
        {
            _logger.LogInformation("[AgentKit] 使用注册表执行工具: {ToolName}", name);
            return await _toolRegistry.ExecuteAsync(name, args, context, ct);
        }

        // 回退到内置工具实现
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
                        throw new Exception(Localize(context.Language, "file_id が指定されていません。", "file_id 缺失", "file_id is not specified"));
                    if (context.TryGetDocument(fileId!, out var cached) && cached is JsonObject cachedObj)
                    {
                        _logger.LogInformation("[AgentKit] 使用缓存的发票解析结果 fileId={FileId}", fileId);
                        return ToolExecutionResult.FromModel(cachedObj);
                    }

                    _logger.LogInformation("[AgentKit] 调用工具 extract_invoice_data，fileId={FileId}", fileId);
                    if (context.TryResolveAttachmentToken(fileId, out var resolvedFileId))
                    {
                        fileId = resolvedFileId;
                    }
                    var file = context.ResolveFile(fileId!);
                    if (file is null && !string.IsNullOrWhiteSpace(context.DefaultFileId) &&
                        !string.Equals(fileId, context.DefaultFileId, StringComparison.OrdinalIgnoreCase))
                    {
                        var fallbackId = context.DefaultFileId!;
                        var fallbackFile = context.ResolveFile(fallbackId);
                        if (fallbackFile is not null)
                        {
                            _logger.LogWarning("[AgentKit] fileId={FileId} 未解析到文件，回退使用默认文件 {FallbackId}", fileId, fallbackId);
                            fileId = fallbackId;
                            file = fallbackFile;
                        }
                    }
                    if (file is null)
                        throw new Exception(Localize(context.Language, $"ファイル {fileId} が見つからないか期限切れです。", $"文件 {fileId} 未找到或已过期", $"File {fileId} not found or expired"));
                    var data = await ExtractInvoiceDataAsync(fileId!, file, context, ct);
                    context.RegisterDocument(fileId, data);
                    return ToolExecutionResult.FromModel(data ?? new JsonObject { ["status"] = "error" });
                }
                case "lookup_account":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception(Localize(context.Language, "query を指定してください。", "query 不能为空", "query is required"));
                    _logger.LogInformation("[AgentKit] 调用工具 lookup_account，query={Query}", query);
                    var match = await LookupAccountAsync(context.CompanyCode, query!, ct);
                    return ToolExecutionResult.FromModel(match);
                }
                case "check_accounting_period":
                {
                    var posting = args.TryGetProperty("posting_date", out var pdEl) ? pdEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(posting))
                        throw new Exception(Localize(context.Language, "posting_date を指定してください。", "posting_date 不能为空", "posting_date is required"));
                    _logger.LogInformation("[AgentKit] 调用工具 check_accounting_period，postingDate={Posting}", posting);
                    var status = await CheckAccountingPeriodAsync(context.CompanyCode, posting!, ct);
                    return ToolExecutionResult.FromModel(status);
                }
                case "verify_invoice_registration":
                {
                    var regNo = args.TryGetProperty("registration_no", out var regEl) ? regEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(regNo))
                        throw new Exception(Localize(context.Language, "registration_no を指定してください。", "registration_no 不能为空", "registration_no is required"));
                    var trimmedRegNo = regNo.Trim();
                    if (IsPlaceholderRegistrationNo(trimmedRegNo))
                    {
                        var resolved = ResolveRegistrationNoFromContext(context);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            _logger.LogInformation("[AgentKit] verify_invoice_registration 收到占位登记号，占位值={Placeholder}，使用上下文登记号={Resolved}", trimmedRegNo, resolved);
                            regNo = resolved;
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
                        throw new Exception(Localize(context.Language, "query を指定してください。", "query 不能为空", "query is required"));
                    var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
                    _logger.LogInformation("[AgentKit] 调用工具 lookup_customer，query={Query} limit={Limit}", query, limit);
                    var payload = await LookupCustomerAsync(context.CompanyCode, query!.Trim(), limit, ct);
                    return ToolExecutionResult.FromModel(JsonSerializer.Deserialize<object>(payload.ToJsonString(), JsonOptions) ?? new { status = "ok", items = Array.Empty<object>() });
                }
                case "lookup_material":
                {
                    var query = args.TryGetProperty("query", out var qEl) ? qEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(query))
                        throw new Exception(Localize(context.Language, "query を指定してください。", "query 不能为空", "query is required"));
                    var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 10;
                    _logger.LogInformation("[AgentKit] 调用工具 lookup_material，query={Query} limit={Limit}", query, limit);
                    var payload = await LookupMaterialAsync(context.CompanyCode, query!.Trim(), limit, ct);
                    return ToolExecutionResult.FromModel(JsonSerializer.Deserialize<object>(payload.ToJsonString(), JsonOptions) ?? new { status = "ok", items = Array.Empty<object>() });
                }
                case "request_clarification":
                {
                    var docId = args.TryGetProperty("document_id", out var docIdEl) ? docIdEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(docId))
                        throw new Exception(Localize(context.Language, "document_id が指定されていません。", "document_id 缺失", "document_id is not specified"));
                    var question = args.TryGetProperty("question", out var questionEl) ? questionEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(question))
                        throw new Exception(Localize(context.Language, "question が指定されていません。", "question 缺失", "question is not specified"));
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
                    var tag = new ClarificationTag(questionId, documentSessionId, docId, question.Trim(), detail?.Trim(), analysisClone, file?.FileName, file?.BlobName, documentLabel);
                    var clarifyMessage = new AgentResultMessage("assistant", trimmedQuestion, "clarify", tag);
                    if (!string.IsNullOrWhiteSpace(documentSessionId))
                    {
                        await SetSessionActiveDocumentAsync(context.SessionId, documentSessionId, ct);
                    }
                    return ToolExecutionResult.FromModel(new { status = "clarify", questionId, documentId = docId, documentSessionId, documentLabel }, new List<AgentResultMessage> { clarifyMessage });
                }
                case "create_voucher":
                {
                    _logger.LogInformation("[AgentKit] 调用工具 create_voucher 开始");
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
                        throw new Exception(Localize(context.Language, "voucher_no を指定してください。", "voucher_no 不能为空", "voucher_no is required"));
                    _logger.LogInformation("[AgentKit] 调用工具 get_voucher_by_number，voucherNo={VoucherNo}", voucherNo);
                    var payload = await GetVoucherByNumberAsync(context.CompanyCode, voucherNo!, ct);
                    return ToolExecutionResult.FromModel(payload);
                }
                case "create_business_partner":
                {
                    var partnerName = args.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(partnerName))
                        throw new Exception(Localize(context.Language, "取引先名を指定してください。", "取引先名不能为空", "partner_name is required"));
                    _logger.LogInformation("[AgentKit] 调用工具 create_business_partner，name={Name}", partnerName);
                    var result = await CreateBusinessPartnerAsync(context, args, ct);
                    return result;
                }
                case "fetch_webpage":
                {
                    var url = args.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url))
                        throw new Exception(Localize(context.Language, "url を指定してください。", "url 不能为空", "url is required"));
                    _logger.LogInformation("[AgentKit] 调用工具 fetch_webpage，url={Url}", url);
                    var content = await FetchWebpageContentAsync(url!, ct);
                    return ToolExecutionResult.FromModel(new { status = "ok", url, content });
                }
                default:
                    throw new Exception(Localize(context.Language, $"未登録のツールです：{name}", $"未知的工具：{name}", $"Unknown tool: {name}"));
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
            var failMessage = new AgentResultMessage("assistant", Localize(context.Language, $"ツール {name} の実行に失敗しました：{ex.Message}", $"工具 {name} 执行失败：{ex.Message}", $"Tool {name} execution failed: {ex.Message}"), "error", null);
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
            throw new Exception("header 字段缺失或不是对象");
        if (!args.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
            throw new Exception("lines 字段缺失或不是数组");

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
            throw new Exception("documentSessionId 缺失");
        documentSessionId = documentSessionId.Trim();

        var sessionFileIds = context.GetFileIdsByDocumentSession(documentSessionId).ToArray();
        if (sessionFileIds.Length == 0)
        {
            throw new Exception($"documentSessionId {documentSessionId} 未在当前上下文注册");
        }

        context.SetActiveDocumentSession(documentSessionId);
        if (!string.Equals(context.ActiveDocumentSessionId, documentSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"无法切换到指定的 documentSessionId {documentSessionId}");
        }

        var sessionFileIdSet = new HashSet<string>(sessionFileIds, StringComparer.OrdinalIgnoreCase);

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
                    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();
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
        var linesNode = JsonNode.Parse(linesEl.GetRawText())?.AsArray() ?? new JsonArray();

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

        // 获取公司设定的进项税科目
        var inputTaxAccountCode = await GetInputTaxAccountCodeAsync(context.CompanyCode, ct);
        if (string.IsNullOrWhiteSpace(inputTaxAccountCode))
        {
            _logger.LogWarning("[AgentKit] 未能找到有效的进项税科目（仮払消費税），将无法自动生成税金行。");
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

            // 查找已存在的税金分录和费用分录
            // 同时检查是否有嵌套的 tax 属性（LLM 可能用这种方式生成税金）
            JsonObject? existingTaxLine = null;
            JsonObject? existingExpenseLine = null;
            decimal detectedTaxAmount = 0m; 

            foreach (var item in linesNode)
            {
                if (item is not JsonObject line) continue;
                var side = ReadSide(line);
                if (side != "DR") continue;
                
                // 检查是否有嵌套的 tax 属性
                if (line.TryGetPropertyValue("tax", out var taxNode) && taxNode is JsonObject taxObj)
                {
                    // 提取嵌套税金金额
                    if (taxObj.TryGetPropertyValue("amount", out var taxAmtNode))
                    {
                        if (taxAmtNode is JsonValue taxAmtVal)
                        {
                            if (taxAmtVal.TryGetValue<decimal>(out var taxAmt)) detectedTaxAmount += taxAmt;
                            else if (taxAmtVal.TryGetValue<double>(out var taxAmtDbl)) detectedTaxAmount += (decimal)taxAmtDbl;
                        }
                    }
                    // 重要：处理完嵌套税金后，从 Json 对象中将其移除，避免 FinanceService 重复计算或处理不当
                    line.Remove("tax");
                    _logger.LogInformation("[AgentKit] 移除嵌套 tax 属性，准备扁平化为独立分录");
                }
                
                var code = ReadAccountCode(line);
                if (string.Equals(code, inputTaxAccountCode, StringComparison.OrdinalIgnoreCase))
                {
                    existingTaxLine ??= line;
                    detectedTaxAmount += ReadAmount(line);
                }
                else if (existingExpenseLine is null)
                {
                    existingExpenseLine = line;
                }
            }

            // 优先使用发票识别出的税额，如果没有则使用从分录中检测到的税额
            var finalTaxAmount = taxAmount ?? detectedTaxAmount;

            if (finalTaxAmount > 0.0001m && totalAmount.HasValue && totalAmount.Value >= finalTaxAmount)
            {
                // 标准价税分离流程
                var netAmount = Math.Max(0m, totalAmount.Value - finalTaxAmount);

                if (existingExpenseLine is null)
                {
                    // 不再硬编码科目代码，让 LLM 根据场景规则来决定科目（如 6200 或 6250）
                    // 仅在已有分录时才进行价税分离调整
                    _logger.LogInformation("[AgentKit] 未找到借方费用分录，跳过后端自动调整，交由 AI 处理");
                    return; 
                }

                if (!existingExpenseLine.ContainsKey("note"))
                {
                    existingExpenseLine["note"] = "飲食費";
                }

                WriteAmount(existingExpenseLine, Math.Round(netAmount, 2));

                // 统一扁平化生成税金分录
                if (existingTaxLine is null && !string.IsNullOrWhiteSpace(inputTaxAccountCode))
                {
                    existingTaxLine = new JsonObject
                    {
                        ["accountCode"] = inputTaxAccountCode,
                        ["drcr"] = "DR",
                        ["note"] = "仮払消費税"
                    };
                    linesNode.Add(existingTaxLine);
                }
                
                if (existingTaxLine is not null)
                {
                    WriteAmount(existingTaxLine, Math.Round(finalTaxAmount, 2));
                    
                    if (taxRate.HasValue)
                    {
                        var rateValue = taxRate.Value;
                        if (rateValue > 0 && rateValue < 1) rateValue *= 100;
                        existingTaxLine["taxRate"] = Math.Round(rateValue, 0);
                    }
                }

                expectedTotalAmount = Math.Round(totalAmount.Value, 2);
                _logger.LogInformation("[AgentKit] 已完成价税分离: 费用={Net}, 税金={Tax}", netAmount, finalTaxAmount);
            }
            else if (totalAmount.HasValue && existingTaxLine is not null && detectedTaxAmount > 0.0001m && existingExpenseLine is not null)
            {
                // taxAmount 为空，但 LLM 已经生成了税金分录
                // 需要调整费用分录金额 = totalAmount - 已有税金金额
                // 这样可以避免税额被重复计算导致借贷不平衡
                var currentExpenseAmount = ReadAmount(existingExpenseLine);
                var expectedExpenseAmount = Math.Max(0m, totalAmount.Value - detectedTaxAmount);
                
                // 只有当费用分录金额等于含税总额时才调整（说明 LLM 误用了含税金额）
                if (Math.Abs(currentExpenseAmount - totalAmount.Value) < 0.01m)
                {
                    WriteAmount(existingExpenseLine, Math.Round(expectedExpenseAmount, 2));
                    _logger.LogInformation("[AgentKit] 自动调整费用分录金额: {Original} -> {Adjusted} (扣除税金 {Tax})", 
                        currentExpenseAmount, expectedExpenseAmount, detectedTaxAmount);
                }

                expectedTotalAmount = Math.Round(totalAmount.Value, 2);
            }
        }

        ApplyInvoiceAdjustments();
        RecalculateTotals();

        // 查询系统中默认的现金科目（不再硬编码 1000）
        var defaultCashAccount = await GetDefaultCashAccountCodeAsync(context.CompanyCode, ct);
        
        var creditAdjusted = false;

        if (firstCredit is null)
        {
            // 只有当存在默认现金科目时才自动创建贷方分录
            if (!string.IsNullOrWhiteSpace(defaultCashAccount))
            {
                var creditLine = new JsonObject
                {
                    ["accountCode"] = defaultCashAccount,
                    ["drcr"] = "CR",
                    ["note"] = "現金支出"
                };
                linesNode.Add(creditLine);
                creditAdjusted = true;
                RecalculateTotals();
            }
            // 如果没有默认现金科目，让 LLM 处理（不自动创建）
        }
        else
        {
            // 保留 LLM 提供的贷方科目代码，不再强制覆盖为硬编码值
            // 只有当贷方科目为空时才使用默认现金科目
            var code = ReadAccountCode(firstCredit);
            if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(defaultCashAccount))
            {
                firstCredit["accountCode"] = defaultCashAccount;
                creditAdjusted = true;
            }
            if (!firstCredit.ContainsKey("note"))
            {
                firstCredit["note"] = "現金支出";
            }
        }

        // 5. 最终平衡校验：计算当前所有行的借贷总计
        RecalculateTotals();
        
        // 如果借贷不平衡，尝试自动修复
        if (Math.Abs(debitTotal - creditTotal) >= 0.01m)
        {
            _logger.LogInformation("[AgentKit] 借贷不平衡，尝试修复: DR={Debit} CR={Credit}, 预期总额={Expected}", debitTotal, creditTotal, expectedTotalAmount);
            
            if (creditTotal <= Epsilon && debitTotal > Epsilon)
            {
                // 情况 A: 完全没有贷方，添加默认现金科目
                var cashAccount = !string.IsNullOrWhiteSpace(defaultCashAccount) ? defaultCashAccount : "1000";
                var creditLine = new JsonObject
                {
                    ["accountCode"] = cashAccount,
                    ["drcr"] = "CR",
                    ["amount"] = Math.Round(debitTotal, 2),
                    ["note"] = "現金支出"
                };
                linesNode.Add(creditLine);
            }
            else if (debitTotal > Epsilon && creditTotal > Epsilon)
            {
                // 情况 B: 借贷都有金额但不相等（这是最常见的情况，如 30371 vs 27610）
                // 计算差额并补在第一个贷方行上
                var diff = debitTotal - creditTotal;
                if (firstCredit is not null)
                {
                    var currentAmt = ReadAmount(firstCredit);
                    WriteAmount(firstCredit, Math.Round(currentAmt + diff, 2));
                    _logger.LogInformation("[AgentKit] 补齐贷方差额: {Diff}，更新后金额: {NewAmt}", diff, currentAmt + diff);
                }
                else if (firstDebit is not null)
                {
                    // 万一只有贷方没有借方（极少见）
                    var diffRev = creditTotal - debitTotal;
                    var currentAmt = ReadAmount(firstDebit);
                    WriteAmount(firstDebit, Math.Round(currentAmt + diffRev, 2));
                }
            }
            
            // 修复后最后一次重计
            RecalculateTotals();
        }

        // 移除多余的旧覆盖逻辑，全部交由上面的差额补齐逻辑处理


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
                _logger.LogInformation("[AgentKit] 附件标识 {AttachmentToken} 未匹配文件，将忽略该项", token);
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
            // Resolve file record to get full attachment info including blobName
            var fileRecord = context.ResolveFile(id);
            if (fileRecord is not null && !string.IsNullOrWhiteSpace(fileRecord.BlobName))
            {
                attachmentsNode.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = fileRecord.FileName ?? "ファイル",
                    ["contentType"] = fileRecord.ContentType ?? "application/octet-stream",
                    ["size"] = fileRecord.Size,
                    ["blobName"] = fileRecord.BlobName,
                    ["source"] = "agent" // Mark as agent-uploaded, so it won't be deleted when voucher is deleted
                });
            }
            else
            {
                // Fallback: use id as-is (legacy behavior, may not work for preview)
                _logger.LogWarning("[AgentKit] 无法解析附件 {FileId} 的 blobName，使用旧行为", id);
                attachmentsNode.Add(id);
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
        var (insertedJson, _) = await _finance.CreateVoucher(
            context.CompanyCode,
            table,
            payloadDoc.RootElement,
            context.UserCtx,
            VoucherSource.Agent,
            sessionId: context.SessionId.ToString());
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
            ? Localize(context.Language, $"会計伝票 {voucherNo} を作成しました。", $"已创建会计凭证 {voucherNo}", $"Created accounting voucher {voucherNo}")
            : Localize(context.Language, "会計伝票を作成しました。", "已创建会计凭证", "Created accounting voucher");

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
                _logger.LogWarning(ex, "[AgentKit] 更新票据任务状态失败 taskId={TaskId}", context.TaskId.Value);
            }
        }

        var message = new AgentResultMessage("assistant", content, "success", tag);

        var modelPayload = new
        {
            status = "success",
            voucherNo,
            voucherId,
            payload = JsonSerializer.Deserialize<object>(payload.GetRawText(), JsonOptions)
        };
        return ToolExecutionResult.FromModel(modelPayload, new List<AgentResultMessage> { message });
    }

    /// <summary>
    /// 查询系统中默认的现金科目代码（isCash=true 的第一个科目）
    /// </summary>
    private async Task<string?> GetDefaultCashAccountCodeAsync(string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 1. 优先查找标记为 isCash 的科目
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT account_code FROM accounts 
                                WHERE company_code = $1 
                                  AND COALESCE((payload->>'isCash')::boolean, false) = true 
                                ORDER BY account_code LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        // 2. 如果没找到，尝试按名称模糊查找
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT account_code FROM accounts 
                                WHERE company_code = $1 
                                  AND (payload->>'name' LIKE '%現金%' OR payload->>'name' ILIKE '%cash%')
                                ORDER BY account_code LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        return null;
    }

    private async Task<string?> GetInputTaxAccountCodeAsync(string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 1. 尝试从公司设置中获取
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT payload->>'inputTaxAccountCode' FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        // 2. 如果设置中没有，通过 taxType 或名称查找科目
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT account_code FROM accounts 
                                WHERE company_code = $1 
                                  AND (payload->>'taxType' = 'INPUT_TAX' 
                                       OR payload->>'name' LIKE '%仮払消費税%')
                                ORDER BY account_code LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        return null;
    }

    private async Task<JsonObject?> ExtractInvoiceDataAsync(string fileId, UploadedFileRecord file, AgentExecutionContext context, CancellationToken ct)
    {
        var base64 = await AiFileHelpers.ReadFileAsBase64Async(file.StoredPath, ct);
        var preview = AiFileHelpers.ExtractTextPreview(file.StoredPath, file.ContentType, 4000);
        var sanitized = AiFileHelpers.SanitizePreview(preview, 4000);

        var http = _httpClientFactory.CreateClient("openai");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

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
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            userContent.Add(new { type = "text", text = sanitized });
        }
        if (!string.IsNullOrWhiteSpace(base64))
        {
            var ctType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType;
            userContent.Add(new { type = "image_url", image_url = new { url = $"data:{ctType};base64,{base64}" } });
        }

        var extractPrompt = string.Equals(context.Language, "zh", StringComparison.OrdinalIgnoreCase)
            ? @"你是会计票据解析助手。根据用户提供的票据（可能是图片或文字），请输出一个 JSON，字段包括：
- documentType: 文档类型，诸如 'invoice'、'receipt'；
- category: 发票类别（必须从 'dining'、'transportation'、'misc' 中选择其一）。请基于票据内容判断：餐饮/会食相关取 'dining'，交通费（乘车券、出租车、高速费、停车等）取 'transportation'，其余杂费取 'misc'；
- issueDate: 开票或消费日期，格式 YYYY-MM-DD；
- partnerName: 供应商或收款方名称；
- totalAmount: 含税总额，数字；
- taxAmount: 税额，数字；
- currency: 货币代码，默认为 JPY；
- taxRate: 税率（百分数，整数）；
- items: 明细数组，每项含 description、amount；
- invoiceRegistrationNo: 如果看到符合 ^T\d{13}$ 的号码请注明；
- headerSummarySuggestion: 若能生成合理的凭证抬头摘要（例如“交通費 | 手段/会社名 | 起点→終点”或“会議費 | 店名 | 用途”），请给出。若缺乏必要信息则返回空字符串。
- lineMemoSuggestion: 若能为主要会计分录提供简洁备注（例如“タクシー料金 8/9 墨田→上野”），请给出，缺少信息则留空。
- memo: 其他补充说明。
若无法识别某字段，请返回空字符串或 0，不要编造。category 一定要给出上述枚举值之一，不能留下空值。"
            : @"あなたは会計証憑の解析アシスタントです。ユーザーが提供する証憑（画像またはテキスト）に基づき、次の JSON を出力してください：
- documentType: ドキュメント種別（例: 'invoice'、'receipt'）
- category: 証憑カテゴリ。'dining'、'transportation'、'misc' のいずれかを必ず選択し、内容に基づいて判断すること（会食関連は 'dining'、交通費は 'transportation'、その他は 'misc'）。
- issueDate: 発行日または利用日（YYYY-MM-DD）
- partnerName: 取引先／支払先名
- totalAmount: 税込金額（数値）
- taxAmount: 税額（数値）
- currency: 通貨コード。既定は JPY
- taxRate: 税率（パーセンテージ、整数）
- items: 明細配列。各要素は description と amount を含む
- invoiceRegistrationNo: ^T\d{13}$ に一致する番号があれば記載
- headerSummarySuggestion: 伝票ヘッダーに適したサマリー（例：「交通費 | 手段/会社名 | 起点→終点」「会議費 | 店名 | 用途」）。情報不足なら空文字
- lineMemoSuggestion: 主要仕訳行の簡潔なメモ（例：「タクシー料金 8/9 墨田→上野」）。情報不足なら空文字
- memo: その他の補足
判別できない項目は空文字または 0 を返し、決して推測で値を作らないこと。category は必ず上記のいずれかを設定してください。";

        var body = new
        {
            model = string.IsNullOrWhiteSpace(base64) ? "gpt-4o-mini" : "gpt-4o",
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = extractPrompt },
                new { role = "user", content = userContent.ToArray() }
            }
        };

        using var response = await http.PostAsync("chat/completions",
            new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[AgentKit] 发票解析失败：{Status} {Body}", response.StatusCode, text);
            throw new Exception("无法解析发票内容");
        }
        using var doc = JsonDocument.Parse(text);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new Exception("模型未返回解析结果");
        _logger.LogInformation("[AgentKit] extract_invoice_data response: {Content}", content);
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(content)?.AsObject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] 解析发票 JSON 失败: {Content}", content);
            throw new Exception("模型返回的票据结构无效");
        }

        context.RegisterDocument(fileId, node);
        _logger.LogInformation("[AgentKit] 发票解析成功 fileId={FileId}", fileId);
        return node;
    }

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

    private async Task<object> LookupAccountAsync(string companyCode, string query, CancellationToken ct)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new { found = false, query };
        }

        static object BuildResult(string code, string payloadJson)
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
            return new { found = true, accountCode = code, accountName, aliases };
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);

        async Task<object?> QueryAsync(string sql, params object[] parameters)
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
                return BuildResult(code, payloadJson);
            }
            return null;
        }

        var exact = await QueryAsync("SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1", trimmed);
        if (exact is not null) return exact;

        var byName = await QueryAsync("SELECT account_code, payload::text FROM accounts WHERE company_code=$1 AND LOWER(payload->>'name') = LOWER($2) LIMIT 1", trimmed);
        if (byName is not null) return byName;

        var byAlias = await QueryAsync(@"SELECT account_code, payload::text
                                           FROM accounts
                                           WHERE company_code=$1
                                             AND EXISTS (
                                               SELECT 1 FROM jsonb_array_elements_text(COALESCE(payload->'aliases','[]'::jsonb)) AS alias
                                               WHERE LOWER(alias) = LOWER($2)
                                             )
                                           LIMIT 1", trimmed);
        if (byAlias is not null) return byAlias;

        var fuzzy = await QueryAsync(@"SELECT account_code, payload::text
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
                                       LIMIT 1", $"%{trimmed}%");
        if (fuzzy is not null) return fuzzy;

        return new { found = false, query };
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
            return new { exists = false, isOpen = true, message = "未找到对应期间，视为开放" };
        }
        var isOpen = Convert.ToBoolean(scalar, CultureInfo.InvariantCulture);
        return new { exists = true, isOpen, message = isOpen ? "会计期间开放" : "会计期间已关闭" };
    }

    private async Task<object> VerifyInvoiceRegistrationAsync(string regNo, CancellationToken ct)
    {
        var normalized = InvoiceRegistryService.Normalize(regNo);
        if (!InvoiceRegistryService.IsFormatValid(normalized))
        {
            return new { status = "invalid", message = "番号格式不正确" };
        }
        var result = await _invoiceRegistry.VerifyAsync(normalized);
        var statusKey = InvoiceRegistryService.StatusKey(result.Status);
        string message = statusKey switch
        {
            "matched" => "发票登记号有效",
            "not_found" => "未找到该登记号",
            "inactive" => "登记号尚未生效",
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
        cmd.CommandText = """
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
            """;
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

    private async Task<JsonObject> LookupMaterialAsync(string companyCode, string query, int limit, CancellationToken ct)
    {
        limit = limit < 1 ? 1 : (limit > 50 ? 50 : limit);
        var pattern = $"%{query}%";
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            """;
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
        var root = JsonNode.Parse(args.GetRawText())?.AsObject() ?? new JsonObject();
        var customerNode = root.TryGetPropertyValue("customer", out var customerRaw) && customerRaw is JsonObject customerObj
            ? customerObj
            : null;

        var customerCode = ReadJsonString(root, "customerCode") ?? ReadJsonString(customerNode, "code");
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            throw new Exception(Localize(context.Language, "顧客コードが必須です。", "customerCode 必须指定", "customerCode is required"));
        }
        customerCode = customerCode.Trim();

        var customerName = ReadJsonString(root, "customerName") ?? ReadJsonString(customerNode, "name") ?? customerCode;
        var orderDate = ReadJsonString(root, "orderDate") ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        var deliveryDate = ReadJsonString(root, "deliveryDate");

        if (root.TryGetPropertyValue("delivery", out var deliveryRaw) && deliveryRaw is JsonObject deliveryObj)
        {
            deliveryDate ??= ReadJsonString(deliveryObj, "date");
        }

        var currency = ReadJsonString(root, "currency") ?? "JPY";
        currency = currency.ToUpperInvariant();
        var note = ReadJsonString(root, "note");

        if (!root.TryGetPropertyValue("lines", out var linesNode) || linesNode is not JsonArray lineArray || lineArray.Count == 0)
        {
            throw new Exception(Localize(context.Language, "品目明細が不足しています。", "缺少品目明细", "Item details are missing"));
        }

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
                throw new Exception(Localize(context.Language, $"明細 {lineNo} に品目コードがありません。", $"明细 {lineNo} 缺少 materialCode", $"Line {lineNo} is missing materialCode"));
            }
            materialCode = materialCode.Trim();

            var qty = ReadJsonDecimal(lineObj, "quantity");
            if (qty <= 0m)
            {
                throw new Exception(Localize(context.Language, $"明細 {lineNo} の数量が不正です。", $"明细 {lineNo} 的数量必须大于 0", $"Line {lineNo} has invalid quantity"));
            }

            var uom = ReadJsonString(lineObj, "uom") ?? ReadJsonString(lineObj, "unit") ?? "EA";
            var materialName = ReadJsonString(lineObj, "materialName") ?? ReadJsonString(lineObj, "name") ?? string.Empty;
            var description = ReadJsonString(lineObj, "description") ?? ReadJsonString(lineObj, "memo") ?? string.Empty;
            var unitPrice = ReadJsonDecimal(lineObj, "unitPrice");
            var amount = ReadJsonDecimal(lineObj, "amount");
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
            throw new Exception(Localize(context.Language, "品目明細が不足しています。", "缺少品目明细", "Item details are missing"));
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
            throw new Exception(Localize(context.Language, "受注の登録に失敗しました。", "创建受注订单失败。", "Failed to create sales order."));
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
            salesOrderId == Guid.Empty ? null : salesOrderId,
            soNo,
            customerCode,
            customerName,
            summary,
            markCompleted: true,
            ct);

        var messageContent = string.IsNullOrWhiteSpace(soNo)
            ? Localize(context.Language, "受注を登録しました。", "已创建受注订单。", "Sales order created.")
            : Localize(context.Language, $"受注 {soNo} を登録しました。", $"已创建受注订单 {soNo}。", $"Sales order {soNo} created.");
        if (!string.IsNullOrWhiteSpace(deliveryDate))
        {
            messageContent += Localize(context.Language, $" 納期: {deliveryDate}", $" 交期: {deliveryDate}", $" Delivery: {deliveryDate}");
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
            parts.Add(Localize(language, $"納期 {deliveryDate}", $"交期 {deliveryDate}", $"Delivery: {deliveryDate}"));
        }
        if (totalAmount > 0m)
        {
            var amountText = $"{currency.ToUpperInvariant()} {totalAmount:0.##}";
            parts.Add(Localize(language, $"金額 {amountText}", $"金额 {amountText}", $"Amount: {amountText}"));
        }
        return parts.Count == 0 ? Localize(language, "受注", "受注订单", "Sales Order") : string.Join(" / ", parts);
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
        return ToolExecutionResult.FromModel(model, new List<AgentResultMessage> { msg });
    }

    /// <summary>
    /// 创建取引先（业务伙伴）主数据，并自动匹配インボイス登録番号
    /// </summary>
    private async Task<ToolExecutionResult> CreateBusinessPartnerAsync(AgentExecutionContext context, JsonElement args, CancellationToken ct)
    {
        var partnerName = args.TryGetProperty("name", out var nameEl) ? nameEl.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(partnerName))
            throw new Exception(Localize(context.Language, "取引先名を指定してください。", "取引先名不能为空", "partner_name is required"));
        
        var nameKana = args.TryGetProperty("nameKana", out var kanaEl) ? kanaEl.GetString()?.Trim() : null;
        var isCustomer = args.TryGetProperty("isCustomer", out var custEl) && custEl.GetBoolean();
        var isVendor = args.TryGetProperty("isVendor", out var vendEl) && vendEl.GetBoolean();
        var postalCode = args.TryGetProperty("postalCode", out var pcEl) ? pcEl.GetString()?.Trim() : null;
        var prefecture = args.TryGetProperty("prefecture", out var prefEl) ? prefEl.GetString()?.Trim() : null;
        var address = args.TryGetProperty("address", out var addrEl) ? addrEl.GetString()?.Trim() : null;
        var phone = args.TryGetProperty("phone", out var phoneEl) ? phoneEl.GetString()?.Trim() : null;
        var fax = args.TryGetProperty("fax", out var faxEl) ? faxEl.GetString()?.Trim() : null;
        var email = args.TryGetProperty("email", out var emailEl) ? emailEl.GetString()?.Trim() : null;
        var contactPerson = args.TryGetProperty("contactPerson", out var cpEl) ? cpEl.GetString()?.Trim() : null;

        // 自动从 invoice_issuers 表中匹配 T 番号
        string? invoiceRegNo = null;
        DateOnly? invoiceStartDate = null;
        string? matchedIssuerName = null;
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 查找匹配的インボイス登録番号（基于公司名称模糊匹配）
        await using (var searchCmd = conn.CreateCommand())
        {
            searchCmd.CommandText = @"
                SELECT registration_no, name, effective_from
                FROM invoice_issuers
                WHERE (name ILIKE '%' || $1 || '%' OR name_kana ILIKE '%' || $1 || '%')
                  AND (effective_to IS NULL OR effective_to >= CURRENT_DATE)
                ORDER BY 
                    CASE WHEN name = $1 THEN 0 ELSE 1 END,
                    CASE WHEN name ILIKE $1 || '%' THEN 0 ELSE 1 END
                LIMIT 1";
            searchCmd.Parameters.AddWithValue(partnerName);
            
            await using var reader = await searchCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                invoiceRegNo = reader.GetString(0);
                matchedIssuerName = reader.IsDBNull(1) ? null : reader.GetString(1);
                invoiceStartDate = reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2));
                _logger.LogInformation("[AgentKit] 自动匹配到インボイス登録番号 {RegNo}，匹配名称={MatchedName}", invoiceRegNo, matchedIssuerName);
            }
        }

        // 生成取引先编号
        string partnerCode;
        await using (var seqCmd = conn.CreateCommand())
        {
            seqCmd.CommandText = @"
                INSERT INTO partner_sequences (company_code, last_number, updated_at)
                VALUES ($1, 1, now())
                ON CONFLICT (company_code) DO UPDATE SET last_number = partner_sequences.last_number + 1, updated_at = now()
                RETURNING last_number";
            seqCmd.Parameters.AddWithValue(context.CompanyCode);
            var seqNo = (int)(await seqCmd.ExecuteScalarAsync(ct) ?? 1);
            partnerCode = $"BP{seqNo:D6}";
        }

        // 构建 payload
        var payloadObj = new JsonObject
        {
            ["name"] = partnerName,
            ["status"] = "active",
            ["flags"] = new JsonObject { ["customer"] = isCustomer, ["vendor"] = isVendor }
        };
        if (!string.IsNullOrWhiteSpace(nameKana)) payloadObj["nameKana"] = nameKana;
        if (!string.IsNullOrWhiteSpace(invoiceRegNo)) payloadObj["invoiceRegistrationNumber"] = invoiceRegNo;
        if (invoiceStartDate.HasValue) payloadObj["invoiceRegistrationStartDate"] = invoiceStartDate.Value.ToString("yyyy-MM-dd");
        
        // 住所信息
        if (!string.IsNullOrWhiteSpace(postalCode) || !string.IsNullOrWhiteSpace(prefecture) || !string.IsNullOrWhiteSpace(address))
        {
            var addrObj = new JsonObject();
            if (!string.IsNullOrWhiteSpace(postalCode)) addrObj["postalCode"] = postalCode;
            if (!string.IsNullOrWhiteSpace(prefecture)) addrObj["prefecture"] = prefecture;
            if (!string.IsNullOrWhiteSpace(address)) addrObj["address"] = address;
            payloadObj["address"] = addrObj;
        }
        
        // 联系方式
        if (!string.IsNullOrWhiteSpace(phone) || !string.IsNullOrWhiteSpace(fax) || !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(contactPerson))
        {
            var contactObj = new JsonObject();
            if (!string.IsNullOrWhiteSpace(phone)) contactObj["phone"] = phone;
            if (!string.IsNullOrWhiteSpace(fax)) contactObj["fax"] = fax;
            if (!string.IsNullOrWhiteSpace(email)) contactObj["email"] = email;
            if (!string.IsNullOrWhiteSpace(contactPerson)) contactObj["contactPerson"] = contactPerson;
            payloadObj["contact"] = contactObj;
        }

        // 插入数据库
        Guid newId;
        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = @"
                INSERT INTO businesspartners (company_code, payload)
                VALUES ($1, $2)
                RETURNING id";
            insertCmd.Parameters.AddWithValue(context.CompanyCode);
            insertCmd.Parameters.AddWithValue(payloadObj.ToJsonString());
            newId = (Guid)(await insertCmd.ExecuteScalarAsync(ct))!;
        }

        var resultMsg = Localize(context.Language,
            invoiceRegNo != null 
                ? $"取引先「{partnerName}」を登録しました（コード：{partnerCode}）。インボイス登録番号 {invoiceRegNo} を自動設定しました。" 
                : $"取引先「{partnerName}」を登録しました（コード：{partnerCode}）。",
            invoiceRegNo != null
                ? $"已创建取引先「{partnerName}」（编码：{partnerCode}）。已自动匹配 T 番号 {invoiceRegNo}。"
                : $"已创建取引先「{partnerName}」（编码：{partnerCode}）。",
            invoiceRegNo != null
                ? $"Created partner \"{partnerName}\" (code: {partnerCode}). Invoice registration number {invoiceRegNo} was auto-assigned."
                : $"Created partner \"{partnerName}\" (code: {partnerCode}).");

        var msg = new AgentResultMessage("assistant", resultMsg, "success", new
        {
            label = partnerCode,
            action = "openEmbed",
            key = "bp.list",
            payload = new { partnerId = newId.ToString(), partnerCode }
        });

        return ToolExecutionResult.FromModel(new
        {
            status = "ok",
            partnerId = newId,
            partnerCode,
            name = partnerName,
            invoiceRegistrationNumber = invoiceRegNo,
            invoiceRegistrationStartDate = invoiceStartDate?.ToString("yyyy-MM-dd"),
            matchedIssuerName
        }, new List<AgentResultMessage> { msg });
    }

    /// <summary>
    /// 获取网页内容（用于从URL提取公司信息）
    /// </summary>
    private async Task<string> FetchWebpageContentAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; YanxiaBot/1.0)");
        http.Timeout = TimeSpan.FromSeconds(30);
        
        try
        {
            var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);
            
            // 简单的 HTML 清理：移除脚本、样式等，保留文本内容
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", " ");
            
            // 限制返回内容长度
            if (html.Length > 15000)
            {
                html = html.Substring(0, 15000) + "...(truncated)";
            }
            
            return html.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentKit] fetch_webpage 失败，url={Url}", url);
            throw new Exception($"无法获取网页内容：{ex.Message}");
        }
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

    private async Task PersistMessageAsync(Guid sessionId, string role, string content, JsonObject? payload, object? tag, Guid? taskId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ai_messages(id, session_id, role, content, payload, task_id) VALUES (gen_random_uuid(),$1,$2,$3,$4::jsonb,$5)";
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
                    description = "解析上传的发票或收据，提取结构化字段。",
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
                    name = "lookup_account",
                    description = "根据科目名称、别名或编码查询公司内部会计科目。",
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
                    description = "检查指定日期所属会计期间是否打开。",
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
                    description = "验证日本发票登记号，确认是否有效。",
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
                    description = "根据客户名称、编码或别名查询客户主数据，并返回送货地址等详细信息。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "客户名称、编码或关键字" },
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
                    name = "lookup_material",
                    description = "根据品目名称或编码查询物料主数据，返回基本单位、价格等信息。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "品目名称、编码或关键字" },
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
                    description = "根据供应商名称或编码查询供应商主数据。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "供应商名称或编码" }
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
                    description = "搜索供应商请求书或入库匹配记录。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            vendor_code = new { type = "string" },
                            date_from = new { type = "string", description = "YYYY-MM-DD" },
                            date_to = new { type = "string", description = "YYYY-MM-DD" },
                            status = new { type = "string" },
                            limit = new { type = "integer" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_expense_account_options",
                    description = "获取简易记账时的费用科目选项。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string" },
                            vendor_code = new { type = "string" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "create_vendor_invoice",
                    description = "创建供应商请求书（请款单）。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            vendor_id = new { type = "string", description = "供应商ID或编码" },
                            invoice_date = new { type = "string", description = "YYYY-MM-DD" },
                            due_date = new { type = "string", description = "YYYY-MM-DD" },
                            total_amount = new { type = "number" },
                            tax_amount = new { type = "number" },
                            summary = new { type = "string" },
                            lines = new { 
                                type = "array",
                                items = new {
                                    type = "object",
                                    properties = new {
                                        description = new { type = "string" },
                                        amount = new { type = "number" },
                                        quantity = new { type = "number" },
                                        unit_price = new { type = "number" }
                                    }
                                }
                            }
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
                    name = "extract_booking_settlement_data",
                    description = "解析 Booking.com 结算单，提取金额与日期信息。",
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
                    description = "根据支付日期和净额查找对应的银行入金记录。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            payment_date = new { type = "string", description = "YYYY-MM-DD" },
                            net_amount = new { type = "number" },
                            days_tolerance = new { type = "integer" },
                            amount_tolerance = new { type = "number" },
                            keywords = new { type = "array", items = new { type = "string" } }
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
                    name = "preflight_check",
                    description = "工资计算前置检查。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            month = new { type = "string", description = "YYYY-MM" },
                            employee_ids = new { type = "array", items = new { type = "string" } },
                            config = new { type = "object" }
                        },
                        required = new[] { "month" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "calculate_payroll",
                    description = "工资计算预览（不保存）。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            month = new { type = "string", description = "YYYY-MM" },
                            employee_ids = new { type = "array", items = new { type = "string" } },
                            policy_id = new { type = "string" },
                            debug = new { type = "boolean" }
                        },
                        required = new[] { "month" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "save_payroll",
                    description = "保存工资计算结果。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            month = new { type = "string", description = "YYYY-MM" },
                            overwrite = new { type = "boolean" },
                            entries = new { type = "array", items = new { type = "object" } }
                        },
                        required = new[] { "month", "entries" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_payroll_history",
                    description = "查询工资历史（按月）。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            month = new { type = "string", description = "YYYY-MM" },
                            limit = new { type = "integer" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_my_payroll",
                    description = "查询当前用户的工资明细。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            year_month = new { type = "string", description = "YYYY-MM" }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_payroll_comparison",
                    description = "工资对比（按月汇总）。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            months = new { type = "array", items = new { type = "string" } }
                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "get_department_summary",
                    description = "部门工资汇总。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            month = new { type = "string", description = "YYYY-MM" }
                        },
                        required = new[] { "month" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "request_clarification",
                    description = "当需要用户补充信息时调用，生成一个带引用的提问卡片。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            document_id = new { type = "string", description = "相关文件的 fileId" },
                            question = new { type = "string", description = "要向用户提出的问题" },
                            detail = new { type = "string", description = "可选的补充说明" }
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
                    description = "创建会计凭证，需保证借贷平衡。",
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
                                    currency = new { type = "string", description = "默认为 JPY" },
                                    partnerName = new { type = "string", description = "供应商/客户名称" },
                                    invoiceRegistrationNo = new { type = "string" }
                                },
                                required = new[] { "postingDate", "summary" }
                            },
                            lines = new
                            {
                                type = "array",
                                description = "按借贷方向列出分录，需至少一借一贷，金额相等。",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        accountCode = new { type = "string" },
                                        amount = new { type = "number" },
                                        side = new { type = "string", description = "DR/CR 或 debit/credit" },
                                        note = new { type = "string" },
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
                                description = "关联的文件 ID 列表"
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
                    description = "创建受注订单。必须包含客户、送货地址、品目明细和交期等信息。",
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
                                description = "客户详情，包含 code/name/address 等字段",
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
                            orderDate = new { type = "string", description = "受注日，YYYY-MM-DD，默认今天" },
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
                            currency = new { type = "string", description = "币种，默认 JPY" },
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
                    description = "根据凭证号查询凭证详情。",
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
                    name = "create_business_partner",
                    description = "創建取引先（業務夥伴）マスタ。可以是顧客（客戶）或仕入先（供應商）。系統會自動從インボイス登録番號數據庫中匹配T番號。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "取引先名稱（正式名稱）- 必須" },
                            nameKana = new { type = "string", description = "取引先名稱假名" },
                            isCustomer = new { type = "boolean", description = "是否為顧客（客戶）" },
                            isVendor = new { type = "boolean", description = "是否為仕入先（供應商）" },
                            postalCode = new { type = "string", description = "郵便番號" },
                            prefecture = new { type = "string", description = "都道府縣" },
                            address = new { type = "string", description = "住所（市區町村・番地・建物名）" },
                            phone = new { type = "string", description = "電話番號" },
                            fax = new { type = "string", description = "FAX番號" },
                            email = new { type = "string", description = "電子郵件" },
                            contactPerson = new { type = "string", description = "擔當者名" }
                        },
                        required = new[] { "name" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "fetch_webpage",
                    description = "獲取指定URL的網頁內容。用於從企業官網提取公司信息。",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            url = new { type = "string", description = "要獲取的網頁URL" }
                        },
                        required = new[] { "url" }
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
        catch
        {
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
        catch
        {
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
        return new ClarificationInfo(finalQuestionId, documentSessionId, tag.DocumentId, finalQuestion, tag.Detail, analysisClone, tag.DocumentName, tag.BlobName, tag.DocumentLabel);
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

    public sealed record ToolExecutionResult(string ContentForModel, IReadOnlyList<AgentResultMessage> Messages, bool ShouldBreakLoop = false)
    {
        public static ToolExecutionResult FromModel(object model, IReadOnlyList<AgentResultMessage>? messages = null, bool shouldBreakLoop = false)
        {
            var json = JsonSerializer.Serialize(model, JsonOptions);
            return new ToolExecutionResult(json, messages ?? Array.Empty<AgentResultMessage>(), shouldBreakLoop);
        }
    }

    internal sealed record ClarificationInfo(string QuestionId, string? DocumentSessionId, string? DocumentId, string Question, string? Detail, JsonObject? DocumentAnalysis, string? DocumentName, string? BlobName, string? DocumentLabel);

    private sealed record ClarificationTag(string? QuestionId, string? DocumentSessionId, string? DocumentId, string? Question, string? Detail, JsonObject? DocumentAnalysis, string? DocumentName, string? BlobName, string? DocumentLabel);

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

    public sealed record AgentResultMessage(string Role, string Content, string? Status, object? Tag);

    internal sealed record AgentMessageRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string Message, string ApiKey, string Language, Func<string, UploadedFileRecord?>? FileResolver, string? ScenarioKey, string? AnswerTo, Guid? TaskId);

    internal sealed record AgentFileRequest(Guid? SessionId, string CompanyCode, Auth.UserCtx UserCtx, string FileId, string FileName, string ContentType, long Size, string ApiKey, string Language, Func<string, UploadedFileRecord?>? FileResolver, string? ScenarioKey, string BlobName, string? UserMessage = null, JsonObject? ParsedData = null, string? AnswerTo = null);

    public sealed class AgentExecutionContext
    {
        private readonly Func<string, UploadedFileRecord?> _fileResolver;
        private readonly Dictionary<string, JsonObject> _parsedDocuments = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _documentSessionByFileId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _fileIdsByDocumentSession = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _documentSessionLabels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _knownFileIds = new(StringComparer.OrdinalIgnoreCase);
        private Guid? _taskId;
        private string? _defaultFileId;
        private string? _activeDocumentSessionId;
        private bool _voucherCreated;
        private readonly List<string> _createdVoucherNos = new();

        public AgentExecutionContext(Guid sessionId, string companyCode, Auth.UserCtx userCtx, string apiKey, string language, IReadOnlyList<AgentScenarioService.AgentScenario> scenarios, Func<string, UploadedFileRecord?> fileResolver, Guid? taskId = null)
        {
            SessionId = sessionId;
            CompanyCode = companyCode;
            UserCtx = userCtx;
            ApiKey = apiKey;
            Language = NormalizeLanguage(language);
            Scenarios = scenarios;
            _fileResolver = fileResolver;
            _taskId = taskId;
        }

        public Guid SessionId { get; }
        public string CompanyCode { get; }
        public Auth.UserCtx UserCtx { get; }
        public string ApiKey { get; }
        public string Language { get; }
        public IReadOnlyList<AgentScenarioService.AgentScenario> Scenarios { get; }

        public string? DefaultFileId => _defaultFileId;
        public string? ActiveDocumentSessionId => _activeDocumentSessionId;
        public bool HasVoucherCreated => _voucherCreated;
        public IReadOnlyList<string> CreatedVoucherNos => _createdVoucherNos;
        public IReadOnlyDictionary<string, string> DocumentSessionLabels => _documentSessionLabels;
        public Guid? TaskId => _taskId;

        public UploadedFileRecord? ResolveFile(string fileId) => _fileResolver(fileId);

        public void RegisterDocument(string? fileId, JsonObject? parsedData, string? documentSessionId = null)
        {
            if (string.IsNullOrWhiteSpace(fileId)) return;
            if (parsedData is not null)
            {
                _parsedDocuments[fileId] = parsedData;
            }
            _knownFileIds.Add(fileId);
            if (string.IsNullOrWhiteSpace(documentSessionId))
            {
                if (_documentSessionByFileId.TryGetValue(fileId, out var existing))
                {
                    documentSessionId = existing;
                }
                else if (!string.IsNullOrWhiteSpace(_activeDocumentSessionId))
                {
                    documentSessionId = _activeDocumentSessionId;
                }
            }
            if (!string.IsNullOrWhiteSpace(documentSessionId))
            {
                _documentSessionByFileId[fileId] = documentSessionId;
                if (!_fileIdsByDocumentSession.TryGetValue(documentSessionId, out var list))
                {
                    list = new List<string>();
                    _fileIdsByDocumentSession[documentSessionId] = list;
                }
                if (!list.Any(existing => string.Equals(existing, fileId, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(fileId);
                }
            }
        }

        public void AssignDocumentLabel(string? documentSessionId, string? label)
        {
            if (string.IsNullOrWhiteSpace(documentSessionId)) return;
            if (string.IsNullOrWhiteSpace(label)) return;
            _documentSessionLabels[documentSessionId] = label;
        }

        public string? GetDocumentSessionLabel(string? documentSessionId)
        {
            if (string.IsNullOrWhiteSpace(documentSessionId)) return null;
            return _documentSessionLabels.TryGetValue(documentSessionId, out var label) ? label : null;
        }

        public bool TryGetDocument(string fileId, out JsonObject? parsedData)
        {
            if (_parsedDocuments.TryGetValue(fileId, out parsedData)) return true;
            
            // 如果直接找找不到，尝试解析 token 再找
            if (TryResolveAttachmentToken(fileId, out var resolvedId))
            {
                return _parsedDocuments.TryGetValue(resolvedId, out parsedData);
            }
            
            parsedData = null;
            return false;
        }

        public void SetDefaultFileId(string? fileId)
        {
            _defaultFileId = string.IsNullOrWhiteSpace(fileId) ? null : fileId;
            if (_defaultFileId is not null && _documentSessionByFileId.TryGetValue(_defaultFileId, out var docSession))
            {
                _activeDocumentSessionId = docSession;
            }
        }

        public void ClearDefaultFileId()
        {
            _defaultFileId = null;
            _activeDocumentSessionId = null;
        }

        public void SetActiveDocumentSession(string? documentSessionId)
        {
            _activeDocumentSessionId = string.IsNullOrWhiteSpace(documentSessionId) ? null : documentSessionId;
            if (_activeDocumentSessionId is not null &&
                _fileIdsByDocumentSession.TryGetValue(_activeDocumentSessionId, out var files) &&
                files.Count > 0)
            {
                _defaultFileId = files[0];
            }
        }

        public string? GetDocumentSessionIdByFileId(string fileId)
        {
            return _documentSessionByFileId.TryGetValue(fileId, out var sessionId) ? sessionId : null;
        }

        public IReadOnlyList<string> GetFileIdsByDocumentSession(string documentSessionId)
        {
            if (_fileIdsByDocumentSession.TryGetValue(documentSessionId, out var files))
            {
                return files;
            }
            return Array.Empty<string>();
        }

        public IReadOnlyCollection<string> GetRegisteredFileIds()
        {
            return _knownFileIds;
        }

        public bool TryResolveAttachmentToken(string? token, out string resolvedFileId)
        {
            resolvedFileId = string.Empty;
            if (string.IsNullOrWhiteSpace(token)) return false;
            var normalized = token.Trim();
            
            // 1. 如果本身就是已知的 FileId
            if (_knownFileIds.Contains(normalized))
            {
                resolvedFileId = normalized;
                return true;
            }

            // 2. 如果是 DocumentSessionId (doc_xxx 或 xxx)
            var sessionKey = normalized.StartsWith("doc_") ? normalized : "doc_" + normalized;
            if (_fileIdsByDocumentSession.TryGetValue(sessionKey, out var sessionFiles) && sessionFiles.Count > 0)
            {
                resolvedFileId = sessionFiles[0];
                return true;
            }
            if (_documentSessionByFileId.ContainsKey(normalized))
            {
                resolvedFileId = normalized;
                return true;
            }

            // 3. 标签匹配 (#1)
            foreach (var kvp in _documentSessionLabels)
            {
                if (string.Equals(kvp.Value, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (_fileIdsByDocumentSession.TryGetValue(kvp.Key, out var labelFiles) && labelFiles.Count > 0)
                    {
                        resolvedFileId = labelFiles[0];
                        return true;
                    }
                }
            }

            // 4. 模糊匹配 (处理哈希值不完整的情况)
            var bestMatch = _knownFileIds.FirstOrDefault(fid => 
                fid.Contains(normalized, StringComparison.OrdinalIgnoreCase) || 
                normalized.Contains(fid, StringComparison.OrdinalIgnoreCase));
            if (bestMatch != null)
            {
                resolvedFileId = bestMatch;
                return true;
            }

            return false;
        }

        public void MarkVoucherCreated(string? voucherNo)
        {
            _voucherCreated = true;
            if (!string.IsNullOrWhiteSpace(voucherNo))
            {
                _createdVoucherNos.Add(voucherNo);
            }
        }

        public void SetTaskId(Guid? taskId)
        {
            _taskId = taskId;
        }

        /// <summary>
        /// 注册科目查询结果（用于科目白名单验证）
        /// </summary>
        public void RegisterLookupAccountResult(string? accountCode)
        {
            // 此方法用于工具回调，当前版本不实现白名单逻辑
            // 保留接口以兼容工具调用
        }
    }

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



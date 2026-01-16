using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Infrastructure;
using UglyToad.PdfPig;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 发票数据提取工具
/// </summary>
public sealed class ExtractInvoiceDataTool : AgentToolBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ExtractInvoiceDataTool(IHttpClientFactory httpClientFactory, ILogger<ExtractInvoiceDataTool> logger) : base(logger)
    {
        _httpClientFactory = httpClientFactory;
    }

    public override string Name => "extract_invoice_data";

    public override async Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var fileId = GetString(args, "file_id") ?? GetString(args, "fileId") ?? context.DefaultFileId;
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return ErrorResult(Localize(context.Language, "file_id が指定されていません", "file_id 缺失"));
        }

        // 检查缓存
        if (context.TryGetDocument(fileId!, out var cached) && cached is JsonObject cachedObj)
        {
            Logger.LogInformation("[ExtractInvoiceDataTool] 使用缓存的发票解析结果 fileId={FileId}", fileId);
            return ToolExecutionResult.FromModel(cachedObj);
        }

        // 解析附件令牌
        if (context.TryResolveAttachmentToken(fileId, out var resolvedFileId))
        {
            fileId = resolvedFileId;
        }

        var file = context.ResolveFile(fileId!);
        if (file is null)
        {
            // 尝试回退到默认文件
            if (!string.IsNullOrWhiteSpace(context.DefaultFileId) &&
                !string.Equals(fileId, context.DefaultFileId, StringComparison.OrdinalIgnoreCase))
            {
                var fallbackFile = context.ResolveFile(context.DefaultFileId!);
                if (fallbackFile is not null)
                {
                    fileId = context.DefaultFileId;
                    file = fallbackFile;
                }
            }
        }

        if (file is null)
        {
            return ErrorResult(Localize(context.Language, 
                $"ファイル {fileId} が見つからないか期限切れです", 
                $"文件 {fileId} 未找到或已过期"));
        }

        Logger.LogInformation("[ExtractInvoiceDataTool] 调用工具 extract_invoice_data，fileId={FileId}", fileId);

        try
        {
            var data = await ExtractInvoiceDataAsync(fileId!, file, context, ct);
            context.RegisterDocument(fileId, data);
            return ToolExecutionResult.FromModel(data ?? new JsonObject { ["status"] = "error" });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ExtractInvoiceDataTool] 发票解析失败");
            return ErrorResult(ex.Message);
        }
    }

    private async Task<JsonObject?> ExtractInvoiceDataAsync(string fileId, UploadedFileRecord file, AgentExecutionContext context, CancellationToken ct)
    {
        var base64 = await AiFileHelpers.ReadFileAsBase64Async(file.StoredPath, ct);
        var preview = AiFileHelpers.ExtractTextPreview(file.StoredPath, file.ContentType, 4000);
        var sanitized = AiFileHelpers.SanitizePreview(preview, 4000);

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
        
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            userContent.Add(new { type = "text", text = sanitized });
        }

        var isPdf = !string.IsNullOrWhiteSpace(file.ContentType) &&
                    file.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(base64) && !isPdf)
        {
            var ctType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/png" : file.ContentType;
            userContent.Add(new { type = "image_url", image_url = new { url = $"data:{ctType};base64,{base64}" } });
        }

        if (isPdf && string.IsNullOrWhiteSpace(sanitized))
        {
            Logger.LogWarning("[ExtractInvoiceDataTool] PDF 文件无法提取文本内容: {FileId}", fileId);
        }

        var extractPrompt = GetExtractPrompt(context.Language);

        var messages = new object[]
        {
            new { role = "system", content = extractPrompt },
            new { role = "user", content = userContent.ToArray() }
        };

        var openAiResponse = await OpenAiApiHelper.CallOpenAiAsync(
            http, context.ApiKey, "gpt-4o", messages,
            temperature: 0.1, maxTokens: 4096, jsonMode: true, ct: ct);

        var content = openAiResponse.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            Logger.LogWarning("[ExtractInvoiceDataTool] 发票解析失败：响应为空");
            throw new Exception("請求書の内容を解析できませんでした");
        }

        Logger.LogInformation("[ExtractInvoiceDataTool] extract_invoice_data response: {Content}", content);

        var cleanedContent = CleanMarkdownJson(content);

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(cleanedContent)?.AsObject();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[ExtractInvoiceDataTool] 解析发票 JSON 失败: {Content}", cleanedContent);
            throw new Exception("モデルから返された請求書構造が無効です");
        }

        return node;
    }

    private static string GetExtractPrompt(string language)
    {
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return @"你是会计票据解析助手。根据用户提供的票据（可能是图片或文字），请输出一个 JSON，字段包括：
- documentType: 文档类型（'invoice'/'receipt'/'contract'/'request'/'other'）
- category: 发票类别（'dining'/'transportation'/'vendor_invoice'/'misc'）
- suggestedScenario: 建议的处理场景键
- issueDate: 开票日期（YYYY-MM-DD）
- dueDate: 支付期限
- partnerName: 供应商名称
- totalAmount: 含税总额
- taxAmount: 税额
- currency: 货币代码（默认 JPY）
- taxRate: 税率
- items: 明细数组
- invoiceRegistrationNo: T开头的13位登记号
- purchaseCategory: 采购类别
- headerSummarySuggestion: 凭证摘要建议
- lineMemoSuggestion: 分录备注建议
- memo: 其他说明
若无法识别某字段，返回空字符串或0，不要编造。";
        }

        return @"あなたは会計証憑の解析アシスタントです。ユーザーが提供する証憑に基づき、次の JSON を出力してください：
- documentType: ドキュメント種別（'invoice'/'receipt'/'contract'/'request'/'other'）
- category: 証憑カテゴリ（'dining'/'transportation'/'vendor_invoice'/'misc'）
- suggestedScenario: 推奨される処理シナリオキー
- issueDate: 発行日（YYYY-MM-DD）
- dueDate: 支払期限
- partnerName: 取引先名
- totalAmount: 税込金額
- taxAmount: 税額
- currency: 通貨コード（既定は JPY）
- taxRate: 税率
- items: 明細配列
- invoiceRegistrationNo: T開始の13桁登録番号
- purchaseCategory: 購入カテゴリ
- headerSummarySuggestion: 伝票サマリー提案
- lineMemoSuggestion: 仕訳メモ提案
- memo: その他補足
判別できない項目は空文字または0を返し、推測で値を作らないこと。";
    }

    private static string CleanMarkdownJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed.Substring(firstNewline + 1);
            else
                trimmed = trimmed.Substring(3);
        }
        if (trimmed.EndsWith("```"))
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        return trimmed.Trim();
    }
}





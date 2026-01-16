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
/// 鍙戠エ鏁版嵁鎻愬彇宸ュ叿
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
            return ErrorResult(Localize(context.Language, "file_id 銇屾寚瀹氥仌銈屻仸銇勩伨銇涖倱", "file_id 缂哄け"));
        }

        // 妫€鏌ョ紦瀛?        if (context.TryGetDocument(fileId!, out var cached) && cached is JsonObject cachedObj)
        {
            Logger.LogInformation("[ExtractInvoiceDataTool] 浣跨敤缂撳瓨鐨勫彂绁ㄨВ鏋愮粨鏋?fileId={FileId}", fileId);
            return ToolExecutionResult.FromModel(cachedObj);
        }

        // 瑙ｆ瀽闄勪欢浠ょ墝
        if (context.TryResolveAttachmentToken(fileId, out var resolvedFileId))
        {
            fileId = resolvedFileId;
        }

        var file = context.ResolveFile(fileId!);
        if (file is null)
        {
            // 灏濊瘯鍥為€€鍒伴粯璁ゆ枃浠?            if (!string.IsNullOrWhiteSpace(context.DefaultFileId) &&
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
                $"銉曘偂銈ゃ儷 {fileId} 銇岃銇ゃ亱銈夈仾銇勩亱鏈熼檺鍒囥倢銇с仚", 
                $"鏂囦欢 {fileId} 鏈壘鍒版垨宸茶繃鏈?));
        }

        Logger.LogInformation("[ExtractInvoiceDataTool] 璋冪敤宸ュ叿 extract_invoice_data锛宖ileId={FileId}", fileId);

        try
        {
            var data = await ExtractInvoiceDataAsync(fileId!, file, context, ct);
            context.RegisterDocument(fileId, data);
            return ToolExecutionResult.FromModel(data ?? new JsonObject { ["status"] = "error" });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[ExtractInvoiceDataTool] 鍙戠エ瑙ｆ瀽澶辫触");
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
            Logger.LogWarning("[ExtractInvoiceDataTool] PDF 鏂囦欢鏃犳硶鎻愬彇鏂囨湰鍐呭: {FileId}", fileId);
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
            Logger.LogWarning("[ExtractInvoiceDataTool] 鍙戠エ瑙ｆ瀽澶辫触锛氬搷搴斾负绌?);
            throw new Exception("璜嬫眰鏇搞伄鍐呭銈掕В鏋愩仹銇嶃伨銇涖倱銇с仐銇?);
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
            Logger.LogWarning(ex, "[ExtractInvoiceDataTool] 瑙ｆ瀽鍙戠エ JSON 澶辫触: {Content}", cleanedContent);
            throw new Exception("銉儑銉亱銈夎繑銇曘倢銇熻珛姹傛浉妲嬮€犮亴鐒″姽銇с仚");
        }

        return node;
    }

    private static string GetExtractPrompt(string language)
    {
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return @"浣犳槸浼氳绁ㄦ嵁瑙ｆ瀽鍔╂墜銆傛牴鎹敤鎴锋彁渚涚殑绁ㄦ嵁锛堝彲鑳芥槸鍥剧墖鎴栨枃瀛楋級锛岃杈撳嚭涓€涓?JSON锛屽瓧娈靛寘鎷細
- documentType: 鏂囨。绫诲瀷锛?invoice'/'receipt'/'contract'/'request'/'other'锛?- category: 鍙戠エ绫诲埆锛?dining'/'transportation'/'vendor_invoice'/'misc'锛?- suggestedScenario: 寤鸿鐨勫鐞嗗満鏅敭
- issueDate: 寮€绁ㄦ棩鏈燂紙YYYY-MM-DD锛?- dueDate: 鏀粯鏈熼檺
- partnerName: 渚涘簲鍟嗗悕绉?- totalAmount: 鍚◣鎬婚
- taxAmount: 绋庨
- currency: 璐у竵浠ｇ爜锛堥粯璁?JPY锛?- taxRate: 绋庣巼
- items: 鏄庣粏鏁扮粍
- invoiceRegistrationNo: T寮€澶寸殑13浣嶇櫥璁板彿
- purchaseCategory: 閲囪喘绫诲埆
- headerSummarySuggestion: 鍑瘉鎽樿寤鸿
- lineMemoSuggestion: 鍒嗗綍澶囨敞寤鸿
- memo: 鍏朵粬璇存槑
鑻ユ棤娉曡瘑鍒煇瀛楁锛岃繑鍥炵┖瀛楃涓叉垨0锛屼笉瑕佺紪閫犮€?;
        }

        return @"銇傘仾銇熴伅浼氳▓瑷兼啈銇В鏋愩偄銈枫偣銈裤兂銉堛仹銇欍€傘儲銉笺偠銉笺亴鎻愪緵銇欍倠瑷兼啈銇熀銇ャ亶銆佹銇?JSON 銈掑嚭鍔涖仐銇︺亸銇犮仌銇勶細
- documentType: 銉夈偔銉ャ儭銉炽儓绋垾锛?invoice'/'receipt'/'contract'/'request'/'other'锛?- category: 瑷兼啈銈儐銈淬儶锛?dining'/'transportation'/'vendor_invoice'/'misc'锛?- suggestedScenario: 鎺ㄥエ銇曘倢銈嬪嚘鐞嗐偡銉娿儶銈偔銉?- issueDate: 鐧鸿鏃ワ紙YYYY-MM-DD锛?- dueDate: 鏀墪鏈熼檺
- partnerName: 鍙栧紩鍏堝悕
- totalAmount: 绋庤炯閲戦
- taxAmount: 绋庨
- currency: 閫氳波銈炽兗銉夛紙鏃㈠畾銇?JPY锛?- taxRate: 绋庣巼
- items: 鏄庣窗閰嶅垪
- invoiceRegistrationNo: T闁嬪銇?3妗佺櫥閷茬暘鍙?- purchaseCategory: 璩煎叆銈儐銈淬儶
- headerSummarySuggestion: 浼濈エ銈点優銉兗鎻愭
- lineMemoSuggestion: 浠曡ǔ銉°儮鎻愭
- memo: 銇濄伄浠栬瓒?鍒ゅ垾銇с亶銇亜闋呯洰銇┖鏂囧瓧銇俱仧銇?銈掕繑銇椼€佹帹娓仹鍊ゃ倰浣溿倝銇亜銇撱仺銆?;
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



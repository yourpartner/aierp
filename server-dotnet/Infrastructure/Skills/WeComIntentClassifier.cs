using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 企业微信员工消息意图分类引擎。
/// 使用 LLM（轻量模型）对员工消息进行意图分类和实体提取。
/// 对于高频、明确的意图使用规则引擎快速匹配，减少 LLM 调用。
/// </summary>
public sealed class WeComIntentClassifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WeComIntentClassifier> _logger;

    public WeComIntentClassifier(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<WeComIntentClassifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>意图分类结果</summary>
    public sealed record IntentResult(
        string Intent,                    // 意图 ID (如 timesheet.entry, payroll.query)
        decimal Confidence,               // 置信度 0~1
        Dictionary<string, string> Entities, // 提取的实体 (日期、时间、金额等)
        string? Suggestion = null         // 建议的回复模板（规则匹配时直接返回）
    );

    /// <summary>
    /// 对员工消息进行意图分类。
    /// 优先使用规则引擎快速匹配，无法匹配时回退到 LLM。
    /// </summary>
    public async Task<IntentResult> ClassifyAsync(
        string message, string? messageType = "text", string? contextIntent = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message) && string.Equals(messageType, "text", StringComparison.OrdinalIgnoreCase))
            return new IntentResult("unknown", 0, new Dictionary<string, string>());

        // 文件消息（图片/文件）→ 根据上下文判断是工时上传还是发票识别
        if (!string.Equals(messageType, "text", StringComparison.OrdinalIgnoreCase))
        {
            // 如果在 timesheet 上下文中，直接判定为工时文件上传
            if (string.Equals(contextIntent, "timesheet.entry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(contextIntent, "timesheet.upload", StringComparison.OrdinalIgnoreCase))
            {
                return new IntentResult("timesheet.upload", 0.95m, new Dictionary<string, string>
                {
                    ["messageType"] = messageType ?? "file"
                });
            }
            // 图片消息 → 默认视为发票识别（最常见的图片使用场景）
            if (string.Equals(messageType, "image", StringComparison.OrdinalIgnoreCase))
            {
                return new IntentResult("invoice.recognize", 0.85m, new Dictionary<string, string>
                {
                    ["messageType"] = "image"
                });
            }
            // 其他文件（PDF等）→ 也尝试发票识别
            return new IntentResult("invoice.recognize", 0.7m, new Dictionary<string, string>
            {
                ["messageType"] = messageType ?? "file"
            });
        }

        // 规则引擎快速匹配
        var ruleResult = MatchByRules(message, contextIntent);
        if (ruleResult is not null && ruleResult.Confidence >= 0.8m)
        {
            _logger.LogDebug("[IntentClassifier] 规则匹配: intent={Intent}, confidence={Confidence:F2}",
                ruleResult.Intent, ruleResult.Confidence);
            return ruleResult;
        }

        // LLM 意图分类
        try
        {
            var llmResult = await ClassifyWithLlmAsync(message, contextIntent, ct);
            if (llmResult is not null)
            {
                _logger.LogInformation("[IntentClassifier] LLM 分类: intent={Intent}, confidence={Confidence:F2}",
                    llmResult.Intent, llmResult.Confidence);
                return llmResult;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IntentClassifier] LLM 分类失败，使用规则匹配结果");
        }

        // 回退到规则匹配结果（即使低置信度）或 unknown
        return ruleResult ?? new IntentResult("general.question", 0.3m, new Dictionary<string, string>());
    }

    /// <summary>规则引擎快速匹配</summary>
    private static IntentResult? MatchByRules(string message, string? contextIntent)
    {
        var msg = message.Trim();
        var entities = new Dictionary<string, string>();

        // ===== Timesheet 录入 =====
        // "今天 9点到18点" / "今日 9:00-18:00" / "本日 9時～18時"
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(今[日天]|本日|きょう).*([\d]{1,2})[時时点:：]([\d]{0,2}).*([\d]{1,2})[時时点:：]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("timesheet.entry", 0.9m, new Dictionary<string, string>
            {
                ["scope"] = "today",
                ["rawInput"] = msg
            });
        }

        // "本周/今週 ..." / "周一到周五..."
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(本周|今週|这周|この週|周[一二三四五六日]|月曜|火曜|水曜|木曜|金曜)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("timesheet.entry", 0.85m, new Dictionary<string, string>
            {
                ["scope"] = "week",
                ["rawInput"] = msg
            });
        }

        // "录入/填写/登録/入力 timesheet/勤怠/工时/出勤"
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(录入|填写|登録|入力|提出|記入|填|登记).*(timesheet|勤怠|工时|出勤|タイムシート|勤務)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("timesheet.entry", 0.9m, new Dictionary<string, string>
            {
                ["rawInput"] = msg
            });
        }

        // ===== Timesheet 查询 =====
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(查|確認|见|看|表示).*(工时|勤怠|timesheet|タイムシート|出勤|労働時間)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(这个月|今月|上个月|先月).*(工时|时间|勤怠|小时|時間)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("timesheet.query", 0.9m, new Dictionary<string, string>
            {
                ["rawInput"] = msg
            });
        }

        // ===== Timesheet 提交 =====
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(提交|提出|submit).*(timesheet|勤怠|工时|タイムシート|本月|今月)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("timesheet.submit", 0.9m, new Dictionary<string, string>());
        }

        // ===== 工资查询 =====
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(工资|给料|給料|給与|薪水|salary|payslip|工资条|明細|明细|手取り|振込)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("payroll.query", 0.9m, new Dictionary<string, string>
            {
                ["rawInput"] = msg
            });
        }

        // ===== 证明书申请 =====
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(证明|証明|certificate).*(书|書|在职|在職|收入|離職|退職|income)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(在[职職]証明|収入証明|退[职職]証明|就[业業]証明)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("certificate.apply", 0.9m, new Dictionary<string, string>
            {
                ["rawInput"] = msg
            });
        }

        // ===== 请假 =====
        if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                @"(请假|休暇|休み|有休|有給|年休|病假|欠勤|leave|vacation|休日)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return new IntentResult("leave.query", 0.85m, new Dictionary<string, string>
            {
                ["rawInput"] = msg
            });
        }

        // ===== 多轮对话中的确认/否认 =====
        if (!string.IsNullOrWhiteSpace(contextIntent))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"^(对|是|ok|好|确认|確認|はい|うん|そう|correct|yes|没问题)\s*[。！!]?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return new IntentResult("confirm", 0.95m, new Dictionary<string, string>
                {
                    ["contextIntent"] = contextIntent
                });
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(msg,
                    @"^(不|否|no|不对|不是|いいえ|違う|cancel|取消)\s*[。！!]?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return new IntentResult("deny", 0.95m, new Dictionary<string, string>
                {
                    ["contextIntent"] = contextIntent
                });
            }
        }

        return null;
    }

    /// <summary>使用 LLM 进行意图分类</summary>
    private async Task<IntentResult?> ClassifyWithLlmAsync(string message, string? contextIntent, CancellationToken ct)
    {
        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var systemPrompt = @"你是一个意图分类器，负责分析员工在企业微信中发送的消息意图。

可选意图：
- timesheet.entry: 录入工时（包含具体的时间信息）
- timesheet.upload: 上传工时文件
- timesheet.query: 查询工时记录
- timesheet.submit: 提交工时审批
- payroll.query: 查询工资/薪资信息
- certificate.apply: 申请证明书（在职证明、收入证明等）
- leave.query: 查询/申请休假
- general.question: 一般性问题

请以 JSON 格式返回：{""intent"":""..._..."",""confidence"":0.9,""entities"":{""key"":""value""}}
entities 中可包含：scope(today/week/month), date, startTime, endTime, month 等。
仅返回 JSON，不要其他文字。";

        if (!string.IsNullOrWhiteSpace(contextIntent))
        {
            systemPrompt += $"\n\n当前对话上下文意图: {contextIntent}，请考虑多轮对话的连贯性。";
        }

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = message }
            },
            temperature = 0.1,
            max_tokens = 200
        };

        var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var content = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(content)) return null;

        // 解析 JSON 结果
        content = content.Trim();
        if (content.StartsWith("```")) content = content.Split('\n', 3).Length > 1 ? content.Split('\n', 3)[1] : content;
        if (content.EndsWith("```")) content = content[..^3];
        content = content.Trim();

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var intent = root.GetProperty("intent").GetString() ?? "general.question";
        var confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDecimal() : 0.7m;
        var entities = new Dictionary<string, string>();
        if (root.TryGetProperty("entities", out var ent) && ent.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ent.EnumerateObject())
            {
                entities[prop.Name] = prop.Value.ToString();
            }
        }

        return new IntentResult(intent, confidence, entities);
    }
}

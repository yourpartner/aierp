using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// Timesheet 文件 AI 解析器
/// 
/// 支持解析以下格式的工时表文件：
/// - Excel (.xlsx / .xls) — 通过 OpenAI Vision 或结构化提取
/// - CSV — 直接解析
/// - 图片 (拍照上传的纸质工时表) — 通过 Vision API
/// - PDF — 通过 Vision API
/// 
/// 工作流程：
/// 1. 根据文件类型选择解析策略
/// 2. 使用 LLM 提取结构化的工时数据
/// 3. 返回解析结果 + 置信度
/// </summary>
public sealed class TimesheetAiParser
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TimesheetAiParser> _logger;

    public TimesheetAiParser(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<TimesheetAiParser> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>解析结果</summary>
    public sealed class ParseResult
    {
        public bool Success { get; set; }
        public List<DailyEntry> Entries { get; set; } = new();
        public decimal Confidence { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> Warnings { get; set; } = new();
        public string? Summary { get; set; }
    }

    /// <summary>每日工时条目</summary>
    public sealed class DailyEntry
    {
        public string Date { get; set; } = "";           // YYYY-MM-DD
        public string? StartTime { get; set; }            // HH:mm
        public string? EndTime { get; set; }              // HH:mm
        public int BreakMinutes { get; set; } = 60;
        public decimal RegularHours { get; set; }
        public decimal OvertimeHours { get; set; }
        public bool IsHoliday { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// 从 CSV 文本解析工时数据
    /// </summary>
    public async Task<ParseResult> ParseCsvAsync(string csvContent, CancellationToken ct)
    {
        _logger.LogInformation("[TimesheetParser] 开始解析 CSV，内容长度={Length}", csvContent.Length);

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ParseResult { Success = false, ErrorMessage = "AI service not configured" };

        return await ParseWithLlmAsync(csvContent, "csv", ct);
    }

    /// <summary>
    /// 从图片解析工时数据（OCR + 结构化提取）
    /// </summary>
    public async Task<ParseResult> ParseImageAsync(byte[] imageData, string mimeType, CancellationToken ct)
    {
        _logger.LogInformation("[TimesheetParser] 开始解析图片，size={Size}, type={Type}", imageData.Length, mimeType);

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ParseResult { Success = false, ErrorMessage = "AI service not configured" };

        var base64 = Convert.ToBase64String(imageData);
        return await ParseImageWithVisionAsync(base64, mimeType, ct);
    }

    /// <summary>
    /// 从 Excel 文本内容解析（预先转为 CSV 或文本后调用）
    /// </summary>
    public async Task<ParseResult> ParseExcelTextAsync(string textContent, CancellationToken ct)
    {
        _logger.LogInformation("[TimesheetParser] 开始解析 Excel 文本，内容长度={Length}", textContent.Length);
        return await ParseWithLlmAsync(textContent, "excel", ct);
    }

    /// <summary>使用 LLM 从文本提取工时数据</summary>
    private async Task<ParseResult> ParseWithLlmAsync(string content, string sourceType, CancellationToken ct)
    {
        var apiKey = _configuration["OpenAI:ApiKey"]!;
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var systemPrompt = @"你是一个工时表解析专家。从给定的文本内容中提取每日工时数据。

请以 JSON 格式返回：
{
  ""success"": true,
  ""entries"": [
    {
      ""date"": ""2026-01-15"",
      ""startTime"": ""09:00"",
      ""endTime"": ""18:00"",
      ""breakMinutes"": 60,
      ""regularHours"": 8.0,
      ""overtimeHours"": 0,
      ""isHoliday"": false,
      ""notes"": """"
    }
  ],
  ""confidence"": 0.95,
  ""warnings"": [],
  ""summary"": ""1月工时表，共22天，176h""
}

规则：
1. 正常工作时间8小时，超出部分为加班
2. 休息时间默认60分钟（如无特殊标注）
3. 周六日标记为 isHoliday
4. 如果只有工时数没有具体时间，设 startTime/endTime 为 null
5. confidence 取值 0~1，表示解析可靠程度
6. 如有异常数据（如连续加班超24h），添加到 warnings

仅返回 JSON，不要其他文字。";

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = $"以下是{sourceType}格式的工时表内容，请解析：\n\n{content}" }
            },
            temperature = 0.1,
            max_tokens = 3000
        };

        try
        {
            var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var responseContent = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(responseContent))
                return new ParseResult { Success = false, ErrorMessage = "Empty AI response" };

            return DeserializeParseResult(responseContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TimesheetParser] LLM 解析失败");
            return new ParseResult { Success = false, ErrorMessage = $"AI parse failed: {ex.Message}" };
        }
    }

    /// <summary>使用 Vision API 从图片提取工时数据</summary>
    private async Task<ParseResult> ParseImageWithVisionAsync(string base64Image, string mimeType, CancellationToken ct)
    {
        var apiKey = _configuration["OpenAI:ApiKey"]!;
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var systemPrompt = @"你是一个工时表OCR解析专家。从给定的工时表图片中提取每日工时数据。

请以 JSON 格式返回：
{
  ""success"": true,
  ""entries"": [
    {
      ""date"": ""2026-01-15"",
      ""startTime"": ""09:00"",
      ""endTime"": ""18:00"",
      ""breakMinutes"": 60,
      ""regularHours"": 8.0,
      ""overtimeHours"": 0,
      ""isHoliday"": false,
      ""notes"": """"
    }
  ],
  ""confidence"": 0.85,
  ""warnings"": [],
  ""summary"": ""1月工时表""
}

规则：
1. 正常工作时间8小时，超出部分为加班
2. 如无法识别某些数据，降低 confidence 并在 warnings 中说明
3. 仅返回 JSON，不要其他文字";

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "请解析这张工时表图片中的工时数据：" },
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:{mimeType};base64,{base64Image}" }
                        }
                    }
                }
            },
            temperature = 0.1,
            max_tokens = 3000
        };

        try
        {
            var response = await client.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var responseContent = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(responseContent))
                return new ParseResult { Success = false, ErrorMessage = "Empty Vision API response" };

            return DeserializeParseResult(responseContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TimesheetParser] Vision 解析失败");
            return new ParseResult { Success = false, ErrorMessage = $"Vision parse failed: {ex.Message}" };
        }
    }

    /// <summary>反序列化 LLM 返回的 JSON 结果</summary>
    private ParseResult DeserializeParseResult(string json)
    {
        try
        {
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                var lines = json.Split('\n');
                json = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.TrimStart().StartsWith("```")));
            }
            json = json.Trim();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new ParseResult
            {
                Success = root.TryGetProperty("success", out var s) && s.GetBoolean(),
                Confidence = root.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0.5m,
                Summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() : null,
                ErrorMessage = root.TryGetProperty("error", out var err) ? err.GetString() : null
            };

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    result.Entries.Add(new DailyEntry
                    {
                        Date = entry.GetProperty("date").GetString() ?? "",
                        StartTime = entry.TryGetProperty("startTime", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null,
                        EndTime = entry.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null,
                        BreakMinutes = entry.TryGetProperty("breakMinutes", out var bm) && bm.ValueKind == JsonValueKind.Number ? bm.GetInt32() : 60,
                        RegularHours = entry.TryGetProperty("regularHours", out var rh) && rh.ValueKind == JsonValueKind.Number ? rh.GetDecimal() : 0,
                        OvertimeHours = entry.TryGetProperty("overtimeHours", out var oh) && oh.ValueKind == JsonValueKind.Number ? oh.GetDecimal() : 0,
                        IsHoliday = entry.TryGetProperty("isHoliday", out var ih) && ih.GetBoolean(),
                        Notes = entry.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null
                    });
                }
            }

            if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in warnings.EnumerateArray())
                {
                    if (w.ValueKind == JsonValueKind.String) result.Warnings.Add(w.GetString()!);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TimesheetParser] JSON 反序列化失败");
            return new ParseResult { Success = false, ErrorMessage = $"Parse result deserialization failed: {ex.Message}" };
        }
    }
}

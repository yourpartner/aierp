using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Server.Infrastructure;

/// <summary>
/// OpenAI API 调用帮助类
/// </summary>
public static class OpenAiApiHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// 设置 OpenAI 请求头
    /// </summary>
    public static void SetOpenAiHeaders(HttpClient http, string apiKey)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// 调用 OpenAI Chat Completion API
    /// </summary>
    public static async Task<OpenAiResponse> CallOpenAiAsync(
        HttpClient http,
        string apiKey,
        string model,
        IEnumerable<object> messages,
        double temperature = 0.7,
        int maxTokens = 4096,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        SetOpenAiHeaders(http, apiKey);

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens
        };

        if (jsonMode)
        {
            requestBody["response_format"] = new { type = "json_object" };
        }

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("chat/completions", content, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI API error: {response.StatusCode} - {responseText}");
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new Exception("OpenAI returned no choices");
        }

        var message = choices[0].GetProperty("message");
        var textContent = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? ""
            : "";

        return new OpenAiResponse(textContent, null);
    }

    /// <summary>
    /// 调用 OpenAI Chat Completion API with Tools (Function Calling)
    /// </summary>
    public static async Task<OpenAiResponse> CallOpenAiWithToolsAsync(
        HttpClient http,
        string apiKey,
        string model,
        IEnumerable<object> messages,
        IEnumerable<object> tools,
        double temperature = 0.7,
        int maxTokens = 4096,
        CancellationToken ct = default)
    {
        SetOpenAiHeaders(http, apiKey);

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await http.PostAsync("chat/completions", content, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI API error: {response.StatusCode} - {responseText}");
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new Exception("OpenAI returned no choices");
        }

        var message = choices[0].GetProperty("message");
        var textContent = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString() ?? ""
            : "";

        List<ToolCall>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
        {
            toolCalls = new List<ToolCall>();
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var id = tc.GetProperty("id").GetString() ?? "";
                var function = tc.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? "";
                var arguments = function.GetProperty("arguments").GetString() ?? "{}";
                toolCalls.Add(new ToolCall(id, name, arguments));
            }
        }

        return new OpenAiResponse(textContent, toolCalls);
    }

    /// <summary>
    /// 构建 assistant 消息（带 tool_calls）
    /// </summary>
    public static Dictionary<string, object?> BuildAssistantMessageWithToolUse(string? textContent, IReadOnlyList<ToolCall>? toolCalls)
    {
        var msg = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = textContent
        };

        if (toolCalls is { Count: > 0 })
        {
            msg["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object>
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = tc.Name,
                    ["arguments"] = tc.Arguments
                }
            }).ToList();
        }

        return msg;
    }

    /// <summary>
    /// 构建 tool 结果消息
    /// </summary>
    public static Dictionary<string, object?> BuildToolResultMessage(string toolCallId, string content)
    {
        return new Dictionary<string, object?>
        {
            ["role"] = "tool",
            ["tool_call_id"] = toolCallId,
            ["content"] = content
        };
    }

    /// <summary>
    /// 将工具定义转换为 OpenAI 格式
    /// </summary>
    public static List<object> ConvertToolsToOpenAiFormat(IEnumerable<object> tools)
    {
        var result = new List<object>();
        foreach (var tool in tools)
        {
            if (tool is JsonObject jo)
            {
                // 已经是 OpenAI 格式
                if (jo.ContainsKey("type") && jo["type"]?.ToString() == "function")
                {
                    result.Add(tool);
                }
                // Claude 格式转 OpenAI 格式
                else if (jo.ContainsKey("name"))
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = jo["name"]?.ToString(),
                            ["description"] = jo["description"]?.ToString(),
                            ["parameters"] = jo["input_schema"]?.DeepClone() ?? new JsonObject()
                        }
                    });
                }
            }
            else
            {
                result.Add(tool);
            }
        }
        return result;
    }

    public sealed record OpenAiResponse(string Content, IReadOnlyList<ToolCall>? ToolCalls);
    public sealed record ToolCall(string Id, string Name, string Arguments);
}


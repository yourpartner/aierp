using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Infrastructure;

/// <summary>
/// Claude API 调用帮助类，提供与 OpenAI 兼容的接口
/// </summary>
public static class ClaudeApiHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Claude 模型映射
    private static readonly Dictionary<string, string> ModelMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = "claude-sonnet-4-20250514",
        ["gpt-4o-mini"] = "claude-sonnet-4-20250514",  // 统一使用 Sonnet 4
        ["gpt-4"] = "claude-sonnet-4-20250514",
        ["gpt-3.5-turbo"] = "claude-3-5-haiku-20241022"
    };

    /// <summary>
    /// 将 OpenAI 模型名称映射到 Claude 模型
    /// </summary>
    public static string MapModel(string openAiModel)
    {
        if (ModelMapping.TryGetValue(openAiModel, out var claudeModel))
            return claudeModel;
        // 如果已经是 Claude 模型名，直接返回
        if (openAiModel.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            return openAiModel;
        return "claude-sonnet-4-20250514"; // 默认
    }

    /// <summary>
    /// 设置 Claude API 请求头
    /// </summary>
    public static void SetClaudeHeaders(HttpClient http, string apiKey)
    {
        http.DefaultRequestHeaders.Remove("Authorization");
        if (!http.DefaultRequestHeaders.Contains("x-api-key"))
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        else
        {
            http.DefaultRequestHeaders.Remove("x-api-key");
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }
    }

    /// <summary>
    /// 调用 Claude API（简单消息，无工具调用）
    /// </summary>
    public static async Task<ClaudeResponse> CallClaudeAsync(
        HttpClient http,
        string apiKey,
        string model,
        IEnumerable<object> messages,
        double temperature = 0.2,
        int maxTokens = 4096,
        bool jsonMode = false,
        CancellationToken ct = default)
    {
        SetClaudeHeaders(http, apiKey);

        var (systemPrompt, claudeMessages) = ConvertMessages(messages);

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = MapModel(model),
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = claudeMessages
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            requestBody["system"] = systemPrompt;
        }

        var response = await http.PostAsync("v1/messages",
            new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"), ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new ClaudeResponse(false, null, null, $"Claude API error: {response.StatusCode} - {responseText}");
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;

        // 提取文本内容
        string? textContent = null;
        if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentArr.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text")
                {
                    textContent = block.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                    break;
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

        return new ClaudeResponse(true, textContent, stopReason, null);
    }

    /// <summary>
    /// 调用 Claude API（带工具调用支持）
    /// </summary>
    public static async Task<ClaudeToolResponse> CallClaudeWithToolsAsync(
        HttpClient http,
        string apiKey,
        string model,
        IEnumerable<object> messages,
        IEnumerable<object>? tools,
        double temperature = 0.1,
        int maxTokens = 4096,
        CancellationToken ct = default)
    {
        SetClaudeHeaders(http, apiKey);

        var (systemPrompt, claudeMessages) = ConvertMessages(messages);
        var claudeTools = tools != null ? ConvertTools(tools) : null;

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = MapModel(model),
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = claudeMessages
        };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            requestBody["system"] = systemPrompt;
        }

        if (claudeTools != null && claudeTools.Count > 0)
        {
            requestBody["tools"] = claudeTools;
        }

        var response = await http.PostAsync("v1/messages",
            new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json"), ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new ClaudeToolResponse(false, null, null, null, $"Claude API error: {response.StatusCode} - {responseText}", responseText);
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;

        string? textContent = null;
        var toolCalls = new List<ClaudeToolCall>();

        if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentArr.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                if (blockType == "text")
                {
                    textContent = block.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
                }
                else if (blockType == "tool_use")
                {
                    var toolId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString();
                    var toolName = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
                    var toolInput = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";
                    toolCalls.Add(new ClaudeToolCall(toolId ?? "", toolName ?? "", toolInput));
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

        return new ClaudeToolResponse(true, textContent, stopReason, toolCalls.Count > 0 ? toolCalls : null, null, responseText);
    }

    /// <summary>
    /// 构建工具调用结果消息（用于追加到消息列表）
    /// </summary>
    public static Dictionary<string, object> BuildToolResultMessage(string toolUseId, string content, bool isError = false)
    {
        return new Dictionary<string, object>
        {
            ["role"] = "user",
            ["content"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = content,
                    ["is_error"] = isError
                }
            }
        };
    }

    /// <summary>
    /// 构建助手消息（包含工具调用）
    /// </summary>
    public static Dictionary<string, object> BuildAssistantMessageWithToolUse(string? text, IEnumerable<ClaudeToolCall> toolCalls)
    {
        var content = new List<object>();
        
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(new Dictionary<string, object>
            {
                ["type"] = "text",
                ["text"] = text
            });
        }

        foreach (var tc in toolCalls)
        {
            content.Add(new Dictionary<string, object>
            {
                ["type"] = "tool_use",
                ["id"] = tc.Id,
                ["name"] = tc.Name,
                ["input"] = JsonSerializer.Deserialize<object>(tc.InputJson) ?? new { }
            });
        }

        return new Dictionary<string, object>
        {
            ["role"] = "assistant",
            ["content"] = content
        };
    }

    /// <summary>
    /// 将 OpenAI 格式的消息转换为 Claude 格式
    /// </summary>
    private static (string? SystemPrompt, List<Dictionary<string, object>> Messages) ConvertMessages(IEnumerable<object> messages)
    {
        string? systemPrompt = null;
        var claudeMessages = new List<Dictionary<string, object>>();

        foreach (var msg in messages)
        {
            var json = JsonSerializer.Serialize(msg, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var role = root.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;
            
            if (role == "system")
            {
                // Claude 的 system 消息放在顶层
                systemPrompt = root.TryGetProperty("content", out var contentEl) ? contentEl.GetString() : null;
                continue;
            }

            if (role == "tool")
            {
                // OpenAI 的 tool 响应转换为 Claude 的 tool_result
                var toolCallId = root.TryGetProperty("tool_call_id", out var tcidEl) ? tcidEl.GetString() : "";
                var content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() : "";
                claudeMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolCallId ?? "",
                            ["content"] = content ?? ""
                        }
                    }
                });
                continue;
            }

            // 处理 assistant 消息（可能包含 tool_calls）
            if (role == "assistant" && root.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
            {
                var content = new List<object>();
                
                // 添加文本内容（如果有）
                if (root.TryGetProperty("content", out var textContentEl) && textContentEl.ValueKind == JsonValueKind.String)
                {
                    var textStr = textContentEl.GetString();
                    if (!string.IsNullOrEmpty(textStr))
                    {
                        content.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = textStr
                        });
                    }
                }

                // 转换 tool_calls
                foreach (var tc in toolCallsEl.EnumerateArray())
                {
                    var tcId = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString();
                    var funcName = tc.TryGetProperty("function", out var funcEl) && funcEl.TryGetProperty("name", out var fnEl) 
                        ? fnEl.GetString() : "";
                    var funcArgs = tc.TryGetProperty("function", out var funcEl2) && funcEl2.TryGetProperty("arguments", out var argsEl)
                        ? argsEl.GetString() : "{}";

                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "tool_use",
                        ["id"] = tcId ?? "",
                        ["name"] = funcName ?? "",
                        ["input"] = JsonSerializer.Deserialize<object>(funcArgs ?? "{}") ?? new { }
                    });
                }

                claudeMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = content
                });
                continue;
            }

            // 普通用户/助手消息
            var msgContent = root.TryGetProperty("content", out var mc) ? mc : default;
            object? convertedContent = null;

            if (msgContent.ValueKind == JsonValueKind.String)
            {
                convertedContent = msgContent.GetString();
            }
            else if (msgContent.ValueKind == JsonValueKind.Array)
            {
                // 处理多模态内容（图片等）
                var parts = new List<object>();
                foreach (var part in msgContent.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var ptEl) ? ptEl.GetString() : null;
                    if (partType == "text")
                    {
                        parts.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = part.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : ""
                        });
                    }
                    else if (partType == "image_url")
                    {
                        // 转换图片格式
                        var imageUrl = part.TryGetProperty("image_url", out var iuEl) && iuEl.TryGetProperty("url", out var urlEl)
                            ? urlEl.GetString() : null;
                        if (!string.IsNullOrEmpty(imageUrl) && imageUrl.StartsWith("data:"))
                        {
                            // data:image/jpeg;base64,xxxx
                            var commaIdx = imageUrl.IndexOf(',');
                            var mediaType = "image/jpeg";
                            var base64Data = imageUrl;
                            if (commaIdx > 0)
                            {
                                var prefix = imageUrl.Substring(0, commaIdx);
                                if (prefix.Contains("image/png")) mediaType = "image/png";
                                else if (prefix.Contains("image/gif")) mediaType = "image/gif";
                                else if (prefix.Contains("image/webp")) mediaType = "image/webp";
                                base64Data = imageUrl.Substring(commaIdx + 1);
                            }
                            parts.Add(new Dictionary<string, object>
                            {
                                ["type"] = "image",
                                ["source"] = new Dictionary<string, object>
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = mediaType,
                                    ["data"] = base64Data
                                }
                            });
                        }
                    }
                }
                convertedContent = parts;
            }

            // 跳过空内容的消息（Claude API 不接受空内容）
            if (convertedContent == null)
                continue;
            
            // 检查字符串内容是否为空
            if (convertedContent is string strContent && string.IsNullOrWhiteSpace(strContent))
                continue;
            
            // 检查数组内容是否为空
            if (convertedContent is List<object> listContent && listContent.Count == 0)
                continue;

            claudeMessages.Add(new Dictionary<string, object>
            {
                ["role"] = role == "assistant" ? "assistant" : "user",
                ["content"] = convertedContent
            });
        }

        return (systemPrompt, claudeMessages);
    }

    /// <summary>
    /// 将 OpenAI 格式的工具定义转换为 Claude 格式
    /// </summary>
    private static List<Dictionary<string, object>> ConvertTools(IEnumerable<object> tools)
    {
        var claudeTools = new List<Dictionary<string, object>>();

        foreach (var tool in tools)
        {
            var json = JsonSerializer.Serialize(tool, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // OpenAI 格式: { type: "function", function: { name, description, parameters } }
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "function"
                && root.TryGetProperty("function", out var funcEl))
            {
                var name = funcEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "";
                var desc = funcEl.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";
                var parameters = funcEl.TryGetProperty("parameters", out var paramsEl) 
                    ? JsonSerializer.Deserialize<object>(paramsEl.GetRawText()) 
                    : new { type = "object", properties = new { } };

                claudeTools.Add(new Dictionary<string, object>
                {
                    ["name"] = name ?? "",
                    ["description"] = desc ?? "",
                    ["input_schema"] = parameters ?? new { type = "object" }
                });
            }
        }

        return claudeTools;
    }
}

/// <summary>
/// Claude API 响应
/// </summary>
public record ClaudeResponse(
    bool Success,
    string? Content,
    string? StopReason,
    string? Error
);

/// <summary>
/// Claude API 工具调用响应
/// </summary>
public record ClaudeToolResponse(
    bool Success,
    string? TextContent,
    string? StopReason,
    List<ClaudeToolCall>? ToolCalls,
    string? Error,
    string? RawResponse
);

/// <summary>
/// Claude 工具调用信息
/// </summary>
public record ClaudeToolCall(
    string Id,
    string Name,
    string InputJson
);


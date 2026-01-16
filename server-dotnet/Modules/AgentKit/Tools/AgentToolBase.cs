using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 工具基类 - 提供共享功能
/// </summary>
public abstract class AgentToolBase : IAgentTool
{
    protected readonly ILogger Logger;
    
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    protected AgentToolBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string Name { get; }

    public abstract System.Threading.Tasks.Task<ToolExecutionResult> ExecuteAsync(
        JsonElement args, 
        AgentExecutionContext context, 
        System.Threading.CancellationToken ct);

    /// <summary>
    /// 本地化辅助方法
    /// </summary>
    protected static string Localize(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;

    /// <summary>
    /// 从 JSON 参数获取字符串
    /// </summary>
    protected static string? GetString(JsonElement args, string propertyName)
    {
        return args.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    /// <summary>
    /// 从 JSON 参数获取整数
    /// </summary>
    protected static int? GetInt(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var el)) return null;
        
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(el.GetString(), out var i) => i,
            _ => null
        };
    }

    /// <summary>
    /// 从 JSON 参数获取小数
    /// </summary>
    protected static decimal? GetDecimal(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var el)) return null;
        
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(el.GetString(), out var d) => d,
            _ => null
        };
    }

    /// <summary>
    /// 从 JSON 参数获取布尔值
    /// </summary>
    protected static bool? GetBool(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var el)) return null;
        
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : null,
            _ => null
        };
    }

    /// <summary>
    /// 创建错误结果
    /// </summary>
    protected static ToolExecutionResult ErrorResult(string errorMessage)
    {
        return new ToolExecutionResult(
            JsonSerializer.Serialize(new { error = errorMessage }, JsonOptions),
            Array.Empty<AgentResultMessage>());
    }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    protected static ToolExecutionResult SuccessResult(object data, AgentResultMessage[]? messages = null)
    {
        return ToolExecutionResult.FromModel(data, messages);
    }
}





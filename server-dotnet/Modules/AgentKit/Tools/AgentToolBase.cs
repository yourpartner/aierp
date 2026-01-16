using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 宸ュ叿鍩虹被 - 鎻愪緵鍏变韩鍔熻兘
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
    /// 鏈湴鍖栬緟鍔╂柟娉?    /// </summary>
    protected static string Localize(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;

    /// <summary>
    /// 浠?JSON 鍙傛暟鑾峰彇瀛楃涓?    /// </summary>
    protected static string? GetString(JsonElement args, string propertyName)
    {
        return args.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    /// <summary>
    /// 浠?JSON 鍙傛暟鑾峰彇鏁存暟
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
    /// 浠?JSON 鍙傛暟鑾峰彇灏忔暟
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
    /// 浠?JSON 鍙傛暟鑾峰彇甯冨皵鍊?    /// </summary>
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
    /// 鍒涘缓閿欒缁撴灉
    /// </summary>
    protected static ToolExecutionResult ErrorResult(string errorMessage)
    {
        return new ToolExecutionResult(
            JsonSerializer.Serialize(new { error = errorMessage }, JsonOptions),
            Array.Empty<AgentResultMessage>());
    }

    /// <summary>
    /// 鍒涘缓鎴愬姛缁撴灉
    /// </summary>
    protected static ToolExecutionResult SuccessResult(object data, AgentResultMessage[]? messages = null)
    {
        return ToolExecutionResult.FromModel(data, messages);
    }
}



using Microsoft.Extensions.Logging;



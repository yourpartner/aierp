using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 宸ュ叿娉ㄥ唽琛?- 绠＄悊鍜屽垎鍙戝伐鍏锋墽琛?/// </summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AgentToolRegistry> _logger;

    public AgentToolRegistry(ILogger<AgentToolRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 娉ㄥ唽宸ュ叿
    /// </summary>
    public void Register(IAgentTool tool)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name cannot be empty", nameof(tool));
        
        _tools[tool.Name] = tool;
        _logger.LogDebug("[AgentToolRegistry] 娉ㄥ唽宸ュ叿: {ToolName}", tool.Name);
    }

    /// <summary>
    /// 鎵归噺娉ㄥ唽宸ュ叿
    /// </summary>
    public void RegisterAll(IEnumerable<IAgentTool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    /// <summary>
    /// 妫€鏌ュ伐鍏锋槸鍚﹀凡娉ㄥ唽
    /// </summary>
    public bool Contains(string name) => _tools.ContainsKey(name);

    /// <summary>
    /// 宸叉敞鍐岀殑宸ュ叿鏁伴噺
    /// </summary>
    public int Count => _tools.Count;

    /// <summary>
    /// 鑾峰彇鎵€鏈夊凡娉ㄥ唽鐨勫伐鍏峰悕绉?    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredToolNames() => _tools.Keys;

    /// <summary>
    /// 鎵ц宸ュ叿
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        string name, 
        JsonElement args, 
        AgentExecutionContext context, 
        CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            var errorMsg = Localize(context.Language, 
                $"鏈煡銇儎銉笺儷: {name}", 
                $"鏈煡宸ュ叿: {name}");
            _logger.LogWarning("[AgentToolRegistry] {Error}", errorMsg);
            return new ToolExecutionResult(
                JsonSerializer.Serialize(new { error = errorMsg }), 
                Array.Empty<AgentResultMessage>());
        }

        try
        {
            _logger.LogInformation("[AgentToolRegistry] 鎵ц宸ュ叿: {ToolName}", name);
            return await tool.ExecuteAsync(args, context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentToolRegistry] 宸ュ叿鎵ц澶辫触: {ToolName}", name);
            var errorMsg = Localize(context.Language,
                $"銉勩兗銉疅琛屻偍銉┿兗: {ex.Message}",
                $"宸ュ叿鎵ц閿欒: {ex.Message}");
            return new ToolExecutionResult(
                JsonSerializer.Serialize(new { error = errorMsg }), 
                Array.Empty<AgentResultMessage>());
        }
    }

    private static string Localize(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;
}



using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;



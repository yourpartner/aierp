using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 工具注册表 - 管理和分发工具执行
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AgentToolRegistry> _logger;

    public AgentToolRegistry(ILogger<AgentToolRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册工具
    /// </summary>
    public void Register(IAgentTool tool)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("Tool name cannot be empty", nameof(tool));
        
        _tools[tool.Name] = tool;
        _logger.LogDebug("[AgentToolRegistry] 注册工具: {ToolName}", tool.Name);
    }

    /// <summary>
    /// 批量注册工具
    /// </summary>
    public void RegisterAll(IEnumerable<IAgentTool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    /// <summary>
    /// 检查工具是否已注册
    /// </summary>
    public bool Contains(string name) => _tools.ContainsKey(name);

    /// <summary>
    /// 已注册的工具数量
    /// </summary>
    public int Count => _tools.Count;

    /// <summary>
    /// 获取所有已注册的工具名称
    /// </summary>
    public IReadOnlyCollection<string> GetRegisteredToolNames() => _tools.Keys;

    /// <summary>
    /// 执行工具
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
                $"未知のツール: {name}", 
                $"未知工具: {name}");
            _logger.LogWarning("[AgentToolRegistry] {Error}", errorMsg);
            return new ToolExecutionResult(
                JsonSerializer.Serialize(new { error = errorMsg }), 
                Array.Empty<AgentResultMessage>());
        }

        try
        {
            _logger.LogInformation("[AgentToolRegistry] 执行工具: {ToolName}", name);
            return await tool.ExecuteAsync(args, context, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentToolRegistry] 工具执行失败: {ToolName}", name);
            var errorMsg = Localize(context.Language,
                $"ツール実行エラー: {ex.Message}",
                $"工具执行错误: {ex.Message}");
            return new ToolExecutionResult(
                JsonSerializer.Serialize(new { error = errorMsg }), 
                Array.Empty<AgentResultMessage>());
        }
    }

    private static string Localize(string language, string ja, string zh) =>
        string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? zh : ja;
}





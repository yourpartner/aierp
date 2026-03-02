using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 工具接口
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// 工具名称（用于路由）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 执行工具
    /// </summary>
    Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct);
}





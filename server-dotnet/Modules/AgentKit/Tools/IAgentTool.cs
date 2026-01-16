using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server.Modules.AgentKit;

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
    Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct);
}





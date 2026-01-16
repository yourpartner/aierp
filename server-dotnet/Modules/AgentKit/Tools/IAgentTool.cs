using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Server.Modules.AgentKit;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// Agent 宸ュ叿鎺ュ彛
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// 宸ュ叿鍚嶇О锛堢敤浜庤矾鐢憋級
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 鎵ц宸ュ叿
    /// </summary>
    Task<ToolExecutionResult> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct);
}



using System.Threading.Tasks;



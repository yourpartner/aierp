using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// Skill Router — 技能路由器
/// 
/// 核心职责：
/// 1. 加载/保存 ConversationContext（从 ai_sessions.state）
/// 2. 判断新消息是否为跟进（follow-up）
/// 3. 如果是跟进 → 路由到活跃 Skill 的 HandleFollowUp
/// 4. 如果不是 → 返回 null，由原有逻辑处理
/// 
/// 设计原则：最小侵入，不改变现有流程，只在前面加一层判断
/// </summary>
public class SkillRouter
{
    private readonly ILogger<SkillRouter> _logger;
    private readonly NpgsqlDataSource _ds;
    private readonly Dictionary<string, IAgentSkill> _skills = new();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // 通用修改意图关键词（跨语言）
    private static readonly Regex ModifyIntentPattern = new(
        @"(改|修改|変更|更新|update|change|modify|edit|纠正|订正|訂正|直す|やり直|移到|移す|切替|替换|换成|改成|设为|設定|调整|変える|差し替)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 明确切换话题的关键词
    private static readonly Regex TopicSwitchPattern = new(
        @"^(另外|别的|其他|另一个|新的|換えて|別の|新しい|btw|by the way|new topic|next)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SkillRouter(ILogger<SkillRouter> logger, NpgsqlDataSource ds)
    {
        _logger = logger;
        _ds = ds;
    }

    /// <summary>注册技能</summary>
    public void RegisterSkill(IAgentSkill skill)
    {
        _skills[skill.Name] = skill;
        _logger.LogInformation("[SkillRouter] Registered skill: {Name}", skill.Name);
    }

    /// <summary>
    /// 尝试将消息路由为跟进操作。
    /// 返回 SkillRouteResult 表示成功路由，null 表示不是跟进（交给原有逻辑）
    /// </summary>
    public async Task<SkillRouteResult?> TryRouteAsFollowUpAsync(
        Guid sessionId, string companyCode, string userId,
        string message, string msgType, string channel,
        CancellationToken ct)
    {
        // 1. 加载上下文
        var ctx = await LoadContextAsync(sessionId, companyCode, userId, channel, ct);
        if (ctx == null)
        {
            _logger.LogDebug("[SkillRouter] No context for session {SessionId}", sessionId);
            return null;
        }

        // 2. 检查是否明确切换话题
        if (TopicSwitchPattern.IsMatch(message))
        {
            _logger.LogInformation("[SkillRouter] Topic switch detected, deactivating skill");
            ctx.DeactivateSkill();
            await SaveContextAsync(ctx, ct);
            return null;
        }

        // 3. 检查是否在跟进窗口内
        if (!ctx.IsWithinFollowUpWindow())
        {
            _logger.LogDebug("[SkillRouter] Outside follow-up window");
            return null;
        }

        // 4. 检查是否有活跃技能
        if (string.IsNullOrEmpty(ctx.ActiveSkillName) || !_skills.TryGetValue(ctx.ActiveSkillName, out var skill))
        {
            _logger.LogDebug("[SkillRouter] No active skill or skill not found: {Skill}", ctx.ActiveSkillName);
            return null;
        }

        // 5. 询问活跃技能能否处理为跟进
        var check = await skill.CanHandleFollowUpAsync(ctx, message, msgType, ct);
        if (!check.CanHandle)
        {
            _logger.LogDebug("[SkillRouter] Active skill {Skill} declined follow-up: {Reason}",
                skill.Name, check.Reason);
            return null;
        }

        _logger.LogInformation("[SkillRouter] Follow-up routed to skill {Skill}, taskId={TaskId}, reason={Reason}",
            skill.Name, check.TargetTaskId, check.Reason);

        // 6. 执行跟进
        var result = await skill.HandleFollowUpAsync(ctx, message, msgType, ct);

        // 7. 更新上下文
        ctx.LastActionTime = DateTimeOffset.UtcNow;
        if (result.AffectedObject != null)
        {
            ctx.PushObject(result.AffectedObject);
        }
        await SaveContextAsync(ctx, ct);

        return new SkillRouteResult(
            SkillName: skill.Name,
            Result: result,
            TaskId: check.TargetTaskId,
            IsFollowUp: true
        );
    }

    /// <summary>
    /// 记录技能执行结果到上下文（由 AgentKitService 在完成操作后调用）
    /// </summary>
    public async Task RecordActionAsync(
        Guid sessionId, string companyCode, string userId, string channel,
        string skillName, Guid? taskId, BusinessObjectRef? createdObject,
        CancellationToken ct)
    {
        var ctx = await LoadContextAsync(sessionId, companyCode, userId, channel, ct)
                  ?? new ConversationContext
                  {
                      SessionId = sessionId,
                      CompanyCode = companyCode,
                      UserId = userId,
                      Channel = channel
                  };

        ctx.ActivateSkill(skillName, taskId);

        if (createdObject != null)
        {
            ctx.PushObject(createdObject);
        }

        await SaveContextAsync(ctx, ct);

        _logger.LogInformation("[SkillRouter] Recorded action: skill={Skill}, task={TaskId}, object={Object}",
            skillName, taskId, createdObject?.Type + ":" + createdObject?.Id);
    }

    /// <summary>
    /// 判断消息是否包含修改意图
    /// </summary>
    public static bool HasModifyIntent(string message)
    {
        return ModifyIntentPattern.IsMatch(message);
    }

    // ==================== 上下文持久化 ====================

    /// <summary>从 ai_sessions.state 加载上下文</summary>
    public async Task<ConversationContext?> LoadContextAsync(
        Guid sessionId, string companyCode, string userId, string channel,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT state FROM ai_sessions WHERE id = $1";
        cmd.Parameters.AddWithValue(sessionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is not string stateJson || string.IsNullOrEmpty(stateJson))
            return null;

        try
        {
            var state = JsonNode.Parse(stateJson) as JsonObject;
            if (state == null) return null;

            var ctxNode = state["skillContext"];
            if (ctxNode == null) return null;

            var ctx = JsonSerializer.Deserialize<ConversationContext>(ctxNode.ToJsonString(), JsonOpts);
            if (ctx != null)
            {
                ctx.SessionId = sessionId;
                ctx.CompanyCode = companyCode;
                ctx.UserId = userId;
                ctx.Channel = channel;
            }
            return ctx;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SkillRouter] Failed to parse context for session {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>保存上下文到 ai_sessions.state</summary>
    public async Task SaveContextAsync(ConversationContext ctx, CancellationToken ct)
    {
        var ctxJson = JsonSerializer.Serialize(ctx, JsonOpts);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // 使用 jsonb_set 只更新 state 中的 skillContext 字段，不影响其他 state 内容
        cmd.CommandText = @"
            UPDATE ai_sessions 
            SET state = COALESCE(state, '{}'::jsonb) || jsonb_build_object('skillContext', $2::jsonb),
                updated_at = now()
            WHERE id = $1";
        cmd.Parameters.AddWithValue(ctx.SessionId);
        cmd.Parameters.AddWithValue(ctxJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>获取已注册的所有技能</summary>
    public IReadOnlyDictionary<string, IAgentSkill> GetRegisteredSkills() => _skills;
}

/// <summary>技能路由结果</summary>
public record SkillRouteResult(
    string SkillName,
    SkillResult Result,
    Guid? TaskId,
    bool IsFollowUp
);

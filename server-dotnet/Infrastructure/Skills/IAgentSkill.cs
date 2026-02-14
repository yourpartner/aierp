using System.Text.Json.Nodes;

namespace Server.Infrastructure.Skills;

/// <summary>
/// Agent Skill 统一接口
/// 
/// 每个 Skill 是一个独立的业务能力单元，负责：
/// 1. 声明自己能处理的意图
/// 2. 判断某条消息是否为跟进（解决任务隔离问题）
/// 3. 执行主逻辑
/// 4. 处理跟进消息（修改已创建的业务对象）
/// 5. 提供快捷操作建议
/// </summary>
public interface IAgentSkill
{
    /// <summary>技能名称，如 "invoice.booking"</summary>
    string Name { get; }

    /// <summary>技能描述</summary>
    string Description { get; }

    /// <summary>匹配的意图模式，如 ["invoice.*", "voucher.modify"]</summary>
    string[] IntentPatterns { get; }

    /// <summary>所需权限 cap，如 ["ai.invoice.recognize"]</summary>
    string[] RequiredCaps { get; }

    /// <summary>支持的渠道，如 ["web", "wecom", "line"]</summary>
    string[] SupportedChannels { get; }

    /// <summary>
    /// 判断某条消息是否可以作为当前技能的跟进消息处理
    /// （核心：解决任务隔离问题）
    /// </summary>
    Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct);

    /// <summary>
    /// 处理跟进消息（修改凭证、补充信息等）
    /// </summary>
    Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct);
}

/// <summary>跟进检查结果</summary>
public record FollowUpCheck(bool CanHandle, string? Reason = null, Guid? TargetTaskId = null);

/// <summary>技能执行结果</summary>
public class SkillResult
{
    /// <summary>纯文字回复</summary>
    public string Text { get; set; } = "";

    /// <summary>快捷操作按钮</summary>
    public List<QuickAction>? QuickActions { get; set; }

    /// <summary>创建或修改的业务对象</summary>
    public BusinessObjectRef? AffectedObject { get; set; }

    /// <summary>是否需要跳转到 Web 页面</summary>
    public string? LinkUrl { get; set; }

    /// <summary>执行是否成功</summary>
    public bool Success { get; set; } = true;

    /// <summary>使用的技能名称</summary>
    public string? SkillName { get; set; }
}

/// <summary>快捷操作</summary>
public record QuickAction(string Label, string Action, JsonObject? Params = null);

/// <summary>业务对象引用</summary>
public record BusinessObjectRef(string Type, string Id, string? Label = null);

/// <summary>
/// 对话上下文 — 跟踪用户当前正在操作的业务对象和技能状态
/// 序列化后存储在 ai_sessions.state 中
/// </summary>
public class ConversationContext
{
    /// <summary>会话 ID</summary>
    public Guid SessionId { get; set; }

    /// <summary>公司代码</summary>
    public string CompanyCode { get; set; } = "";

    /// <summary>用户 ID</summary>
    public string UserId { get; set; } = "";

    /// <summary>渠道：web / wecom / line</summary>
    public string Channel { get; set; } = "web";

    /// <summary>当前激活的技能名称</summary>
    public string? ActiveSkillName { get; set; }

    /// <summary>当前活跃的业务对象栈（最近的在前）</summary>
    public List<BusinessObjectRef> ActiveObjects { get; set; } = new();

    /// <summary>最近一次有效操作的时间</summary>
    public DateTimeOffset? LastActionTime { get; set; }

    /// <summary>跟进窗口（分钟），默认 15 分钟内的消息视为跟进</summary>
    public int FollowUpWindowMinutes { get; set; } = 15;

    /// <summary>最近操作关联的 Task ID</summary>
    public Guid? LastTaskId { get; set; }

    /// <summary>扩展数据（技能特定的状态）</summary>
    public JsonObject? SkillState { get; set; }

    // ========== 便捷方法 ==========

    /// <summary>是否在跟进窗口内</summary>
    public bool IsWithinFollowUpWindow()
    {
        if (LastActionTime == null || ActiveSkillName == null) return false;
        return DateTimeOffset.UtcNow - LastActionTime.Value < TimeSpan.FromMinutes(FollowUpWindowMinutes);
    }

    /// <summary>注册新的业务对象（推到栈顶）</summary>
    public void PushObject(BusinessObjectRef obj)
    {
        // 如果已存在则移到栈顶
        ActiveObjects.RemoveAll(o => o.Type == obj.Type && o.Id == obj.Id);
        ActiveObjects.Insert(0, obj);

        // 最多保留 10 个
        if (ActiveObjects.Count > 10)
            ActiveObjects.RemoveRange(10, ActiveObjects.Count - 10);
    }

    /// <summary>获取最近的指定类型对象</summary>
    public BusinessObjectRef? GetLatestObject(string type)
    {
        return ActiveObjects.FirstOrDefault(o => o.Type == type);
    }

    /// <summary>设置当前活跃技能并记录时间</summary>
    public void ActivateSkill(string skillName, Guid? taskId = null)
    {
        ActiveSkillName = skillName;
        LastActionTime = DateTimeOffset.UtcNow;
        if (taskId.HasValue) LastTaskId = taskId;
    }

    /// <summary>清除活跃技能（用户切换了话题）</summary>
    public void DeactivateSkill()
    {
        ActiveSkillName = null;
        LastTaskId = null;
        SkillState = null;
    }
}

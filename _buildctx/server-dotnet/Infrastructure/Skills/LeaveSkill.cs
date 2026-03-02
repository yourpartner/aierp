using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 休假申请/审批技能
/// </summary>
public class LeaveSkill : IAgentSkill
{
    private readonly ILogger<LeaveSkill> _logger;

    public string Name => "leave";
    public string Description => "休假申请、余额查询、审批";
    public string[] IntentPatterns => new[] { "leave.*" };
    public string[] RequiredCaps => new[] { "ai.leave.apply" };
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    private static readonly Regex LeaveEntityPattern = new(
        @"(请假|休假|有給|有给|年休|年假|病假|事假|産休|育休|休暇|leave|vacation|day off|休み)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ModifyPattern = new(
        @"(改|修改|変更|调整|取消|キャンセル|cancel|延长|延長|缩短)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LeaveSkill(ILogger<LeaveSkill> logger)
    {
        _logger = logger;
    }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text" || string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new FollowUpCheck(false));

        // 修改/取消休假
        if (ModifyPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(10))
                return Task.FromResult(new FollowUpCheck(true, "修改意图+10分钟内"));
        }

        // 休假关键词 + 短窗口（追问余额等）
        if (LeaveEntityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(5))
                return Task.FromResult(new FollowUpCheck(true, "休假关键词+5分钟内"));
        }

        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult
        {
            Text = "正在处理休假相关请求...",
            Success = true,
            SkillName = Name
        });
    }
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 工时管理技能 — 录入、查询、提交、修改工时
/// </summary>
public class TimesheetSkill : IAgentSkill
{
    private readonly ILogger<TimesheetSkill> _logger;
    private readonly NpgsqlDataSource _ds;

    public string Name => "timesheet";
    public string Description => "工时录入、查询、提交、修改";
    public string[] IntentPatterns => new[] { "timesheet.*" };
    public string[] RequiredCaps => new[] { "ai.timesheet.entry", "ai.timesheet.query" };
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    private static readonly Regex ModifyPattern = new(
        @"(改|修改|変更|更新|调整|直す|やり直|update|change|modify)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimesheetEntityPattern = new(
        @"(工时|工時|timesheet|タイムシート|出勤|勤怠|上班|下班|加班|残業|出退勤|休憩|午休)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public TimesheetSkill(ILogger<TimesheetSkill> logger, NpgsqlDataSource ds)
    {
        _logger = logger;
        _ds = ds;
    }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text" || string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new FollowUpCheck(false));

        // 修改意图 + 工时关键词
        if (ModifyPattern.IsMatch(message) && TimesheetEntityPattern.IsMatch(message))
            return Task.FromResult(new FollowUpCheck(true, "修改意图+工时关键词", ctx.LastTaskId));

        // 修改意图 + 5分钟内
        if (ModifyPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(5))
                return Task.FromResult(new FollowUpCheck(true, "修改意图+5分钟内", ctx.LastTaskId));
        }

        // 工时实体关键词 + 3分钟内（可能是追加信息）
        if (TimesheetEntityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(3))
                return Task.FromResult(new FollowUpCheck(true, "工时关键词+3分钟内", ctx.LastTaskId));
        }

        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        // 跟进处理由 WeComEmployeeGateway 的现有 Handler 执行
        return Task.FromResult(new SkillResult
        {
            Text = "正在处理工时修改...",
            Success = true,
            SkillName = Name
        });
    }
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 薪资查询/报表技能
/// </summary>
public class PayrollSkill : IAgentSkill
{
    private readonly ILogger<PayrollSkill> _logger;

    public string Name => "payroll";
    public string Description => "薪资查询、工资明细、薪资报表";
    public string[] IntentPatterns => new[] { "payroll.*" };
    public string[] RequiredCaps => new[] { "ai.payroll.query" };
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    private static readonly Regex PayrollEntityPattern = new(
        @"(工资|薪资|给料|給料|salary|payroll|pay|手取|手取り|明细|明細|工资条|給与明細|社保|年金|源泉|住民税)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ComparePattern = new(
        @"(对比|比较|比べ|比較|上月|先月|去年|昨年|同比|環比|compare|vs)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PayrollSkill(ILogger<PayrollSkill> logger)
    {
        _logger = logger;
    }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text" || string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new FollowUpCheck(false));

        // 薪资相关关键词 + 短窗口（追问细节）
        if (PayrollEntityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(5))
                return Task.FromResult(new FollowUpCheck(true, "薪资关键词+5分钟内"));
        }

        // 对比类请求
        if (ComparePattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(10))
                return Task.FromResult(new FollowUpCheck(true, "对比请求+10分钟内"));
        }

        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult
        {
            Text = "正在查询薪资信息...",
            Success = true,
            SkillName = Name
        });
    }
}

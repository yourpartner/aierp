using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 证明书申请/审批技能
/// </summary>
public class CertificateSkill : IAgentSkill
{
    private readonly ILogger<CertificateSkill> _logger;

    public string Name => "certificate";
    public string Description => "证明书申请、进度查询、审批";
    public string[] IntentPatterns => new[] { "certificate.*" };
    public string[] RequiredCaps => new[] { "ai.certificate.apply" };
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    private static readonly Regex CertEntityPattern = new(
        @"(证明|証明|certificate|在职|在籍|退職|収入|年収|就労|在留|資格)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StatusPattern = new(
        @"(进度|進捗|状态|状況|ステータス|status|结果|結果|审批|承認|通过|却下)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CertificateSkill(ILogger<CertificateSkill> logger)
    {
        _logger = logger;
    }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text" || string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new FollowUpCheck(false));

        // 证明书相关 + 短窗口
        if (CertEntityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(10))
                return Task.FromResult(new FollowUpCheck(true, "证明书关键词+10分钟内"));
        }

        // 查进度
        if (StatusPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "进度查询+15分钟内"));
        }

        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult
        {
            Text = "正在处理证明书相关请求...",
            Success = true,
            SkillName = Name
        });
    }
}

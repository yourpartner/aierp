using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

// ==================== Phase 4: 高级技能 ====================

/// <summary>
/// 简历分析/人才匹配技能
/// </summary>
public class ResumeAnalysisSkill : IAgentSkill
{
    private readonly ILogger<ResumeAnalysisSkill> _logger;

    public string Name => "resume.analysis";
    public string Description => "简历分析、技能洞察、人才匹配";
    public string[] IntentPatterns => new[] { "resume.*", "candidate.*", "matching.*" };
    public string[] RequiredCaps => new[] { "ai.order.manage" }; // 销售+管理者可用
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    private static readonly Regex ResumePattern = new(
        @"(简历|履歴|スキルシート|resume|CV|经历|経歴|技能|スキル|skill|候选|候補|人選|人材)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MatchPattern = new(
        @"(匹配|マッチ|match|推荐|おすすめ|推薦|适合|適合|提案|propose)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ResumeAnalysisSkill(ILogger<ResumeAnalysisSkill> logger) { _logger = logger; }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text") return Task.FromResult(new FollowUpCheck(false));

        if ((ResumePattern.IsMatch(message) || MatchPattern.IsMatch(message)) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "简历/匹配关键词+15分钟内"));
        }
        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult { Text = "正在分析...", Success = true, SkillName = Name });
    }
}

/// <summary>
/// 请求书（Billing Invoice）生成技能
/// </summary>
public class BillingSkill : IAgentSkill
{
    private readonly ILogger<BillingSkill> _logger;

    public string Name => "billing";
    public string Description => "请求书生成、发送、确认";
    public string[] IntentPatterns => new[] { "billing.*", "invoice.generate" };
    public string[] RequiredCaps => new[] { "ai.report.financial" };
    public string[] SupportedChannels => new[] { "web", "wecom" };

    private static readonly Regex BillingPattern = new(
        @"(请求书|請求書|billing|invoice|请款|入金|支払|回収)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public BillingSkill(ILogger<BillingSkill> logger) { _logger = logger; }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text") return Task.FromResult(new FollowUpCheck(false));
        if (BillingPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "请求书关键词+15分钟内"));
        }
        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult { Text = "正在处理请求书...", Success = true, SkillName = Name });
    }
}

/// <summary>
/// 注文书（Purchase Order）识别录入技能
/// </summary>
public class PurchaseOrderSkill : IAgentSkill
{
    private readonly ILogger<PurchaseOrderSkill> _logger;

    public string Name => "purchase_order";
    public string Description => "注文书识别、录入、确认";
    public string[] IntentPatterns => new[] { "order.*", "purchase_order.*" };
    public string[] RequiredCaps => new[] { "ai.order.manage" };
    public string[] SupportedChannels => new[] { "web", "wecom" };

    private static readonly Regex POPattern = new(
        @"(注文|発注|purchase.?order|PO|订单|订购|受注|発注書|注文書)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PurchaseOrderSkill(ILogger<PurchaseOrderSkill> logger) { _logger = logger; }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text") return Task.FromResult(new FollowUpCheck(false));
        if (POPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "注文书关键词+15分钟内"));
        }
        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult { Text = "正在处理注文书...", Success = true, SkillName = Name });
    }
}

/// <summary>
/// 商机管理技能
/// </summary>
public class OpportunitySkill : IAgentSkill
{
    private readonly ILogger<OpportunitySkill> _logger;

    public string Name => "opportunity";
    public string Description => "商机管理、需求匹配、候选人推荐";
    public string[] IntentPatterns => new[] { "opportunity.*", "deal.*" };
    public string[] RequiredCaps => new[] { "ai.order.manage" };
    public string[] SupportedChannels => new[] { "web", "wecom" };

    private static readonly Regex OpportunityPattern = new(
        @"(商机|案件|案件情報|opportunity|deal|需求|ニーズ|募集|案件紹介|提案)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public OpportunitySkill(ILogger<OpportunitySkill> logger) { _logger = logger; }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text") return Task.FromResult(new FollowUpCheck(false));
        if (OpportunityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "商机关键词+15分钟内"));
        }
        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult { Text = "正在处理商机...", Success = true, SkillName = Name });
    }
}

/// <summary>
/// 财务报表/分析技能
/// </summary>
public class FinancialReportSkill : IAgentSkill
{
    private readonly ILogger<FinancialReportSkill> _logger;

    public string Name => "financial.report";
    public string Description => "财务报表查询、数据分析、月结辅助";
    public string[] IntentPatterns => new[] { "report.*", "analysis.*" };
    public string[] RequiredCaps => new[] { "ai.report.financial" };
    public string[] SupportedChannels => new[] { "web", "wecom" };

    private static readonly Regex ReportPattern = new(
        @"(报表|報表|レポート|report|利润|利益|profit|损益|損益|资产|資産|balance|试算|試算|月结|月締|close)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FinancialReportSkill(ILogger<FinancialReportSkill> logger) { _logger = logger; }

    public Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        if (msgType != "text") return Task.FromResult(new FollowUpCheck(false));
        if (ReportPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(15))
                return Task.FromResult(new FollowUpCheck(true, "财务报表关键词+15分钟内"));
        }
        return Task.FromResult(new FollowUpCheck(false));
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        return Task.FromResult(new SkillResult { Text = "正在查询报表...", Success = true, SkillName = Name });
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 发票记账技能 — 第一个标准 Skill 实现
/// 
/// 职责：
/// 1. 判断跟进消息是否与已创建的凭证相关
/// 2. 将跟进消息路由到正确的 Task，交由 AgentKitService 处理
/// 3. 记录创建的凭证到 ConversationContext
/// 
/// 设计原则：
/// - 不重写任何 AgentKitService 逻辑
/// - 只做"跟进判断 + 路由"，实际执行仍由 AgentKit 完成
/// </summary>
public class InvoiceBookingSkill : IAgentSkill
{
    private readonly ILogger<InvoiceBookingSkill> _logger;
    private readonly NpgsqlDataSource _ds;

    public string Name => "invoice.booking";
    public string Description => "发票识别、记账、凭证修改";
    public string[] IntentPatterns => new[] { "invoice.*", "voucher.*" };
    public string[] RequiredCaps => new[] { "ai.invoice.recognize" };
    public string[] SupportedChannels => new[] { "web", "wecom", "line" };

    // 修改意图关键词
    private static readonly Regex ModifyPattern = new(
        @"(改|修改|変更|更新|update|change|modify|edit|纠正|订正|訂正|直す|やり直|移到|移す|切替|替换|换成|改成|设为|設定|调整|変える|差し替)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 凭证相关实体关键词
    private static readonly Regex VoucherEntityPattern = new(
        @"(科目|勘定|account|摘要|summary|日期|日付|date|金额|金額|amount|税|tax|借方|贷方|debit|credit|貸方|借方|传票|伝票|voucher|凭证|記帳|记账)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 凭证号匹配
    private static readonly Regex VoucherNoPattern = new(@"\b(\d{10})\b", RegexOptions.Compiled);

    public InvoiceBookingSkill(ILogger<InvoiceBookingSkill> logger, NpgsqlDataSource ds)
    {
        _logger = logger;
        _ds = ds;
    }

    public async Task<FollowUpCheck> CanHandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        // 只处理文本消息的跟进
        if (msgType != "text" || string.IsNullOrWhiteSpace(message))
            return new FollowUpCheck(false, "非文本消息");

        // 检查是否有活跃的凭证对象
        var latestVoucher = ctx.GetLatestObject("voucher");
        var latestTask = ctx.LastTaskId;

        if (latestVoucher == null && latestTask == null)
            return new FollowUpCheck(false, "无活跃凭证或任务");

        // 策略 1: 消息中包含凭证号 → 精确匹配
        var voucherNoMatch = VoucherNoPattern.Match(message);
        if (voucherNoMatch.Success)
        {
            var voucherNo = voucherNoMatch.Groups[1].Value;
            // 查找该凭证对应的 Task
            var taskId = await FindTaskByVoucherNoAsync(ctx.SessionId, ctx.CompanyCode, voucherNo, ct);
            if (taskId != null)
            {
                return new FollowUpCheck(true, $"消息包含凭证号 {voucherNo}", taskId);
            }
        }

        // 策略 2: 修改意图 + 凭证实体关键词 → 大概率是在修改上一张凭证
        if (ModifyPattern.IsMatch(message) && VoucherEntityPattern.IsMatch(message))
        {
            _logger.LogInformation("[InvoiceSkill] 检测到修改意图+凭证关键词，路由到最近的 Task {TaskId}",
                latestTask);
            return new FollowUpCheck(true, "修改意图+凭证关键词", latestTask);
        }

        // 策略 3: 修改意图（不含凭证关键词，但时间窗口内只有一个活跃任务）
        if (ModifyPattern.IsMatch(message) && latestTask != null)
        {
            return new FollowUpCheck(true, "修改意图+活跃任务", latestTask);
        }

        // 策略 4: 凭证实体关键词 + 在5分钟内（更短的窗口要求更强的关联性）
        if (VoucherEntityPattern.IsMatch(message) && ctx.LastActionTime.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - ctx.LastActionTime.Value;
            if (elapsed < TimeSpan.FromMinutes(5) && latestTask != null)
            {
                return new FollowUpCheck(true, "凭证关键词+5分钟内", latestTask);
            }
        }

        return new FollowUpCheck(false, "未匹配跟进条件");
    }

    public Task<SkillResult> HandleFollowUpAsync(
        ConversationContext ctx, string message, string msgType, CancellationToken ct)
    {
        // 这个方法不直接执行 — SkillRouter 检测到跟进后，
        // 会通过 TaskId 将消息路由到 AgentKitService.ProcessTaskMessageAsync。
        // 这里返回的结果不会被直接使用，因为实际执行发生在 AgentKitService 中。
        //
        // 真正的跟进流程：
        // 1. SkillRouter.TryRouteAsFollowUpAsync 检测到跟进
        // 2. 返回 SkillRouteResult(TaskId = xxx)
        // 3. AgentKitService 用这个 TaskId 调用 ProcessTaskMessageAsync
        // 4. AgentKitService 内部的 voucher modification guard 会处理修改逻辑

        // 此方法作为后备，在 SkillRouter 直接调用时提供基本回复
        return Task.FromResult(new SkillResult
        {
            Text = "正在处理您的修改请求...",
            Success = true,
            SkillName = Name
        });
    }

    // ==================== 辅助方法 ====================

    /// <summary>通过凭证号查找对应的 Task ID</summary>
    private async Task<Guid?> FindTaskByVoucherNoAsync(
        Guid sessionId, string companyCode, string voucherNo, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id FROM ai_tasks 
            WHERE session_id = $1 AND company_code = $2 
              AND task_type = 'invoice'
              AND metadata->>'voucherNo' = $3
            ORDER BY updated_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(voucherNo);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : null;
    }
}

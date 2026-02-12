using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 通用消息→任务路由器。
/// 当用户发送消息时，自动判断该消息应归属到哪个任务。
/// 
/// 路由优先级：
/// 1. 前端显式指定 taskId → 直接使用
/// 2. 消息中提到凭证号（如 2511000073）→ 找到创建该凭证的任务
/// 3. 消息中提到票据标签（如 #5）→ 找到对应标签的任务
/// 4. 消息中包含修改意图（改、修改、変更等）+ 只有一个最近活跃任务 → 路由到该任务
/// 5. 当前 session 只有一个未完成/最近完成的任务 → 路由到该任务
/// 6. 无法判定 → 返回 null，走通用处理
/// </summary>
public sealed class MessageTaskRouter
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<MessageTaskRouter> _logger;

    public MessageTaskRouter(NpgsqlDataSource ds, ILogger<MessageTaskRouter> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    /// <summary>路由结果</summary>
    public sealed record RouteResult(
        Guid TaskId,
        string Reason,           // 路由原因（用于日志/调试）
        string? VoucherNo = null  // 如果是通过凭证号匹配的
    );

    /// <summary>任务摘要（用于路由判断）</summary>
    private sealed record TaskSummary(
        Guid Id,
        string Status,
        string? DocumentLabel,
        string FileName,
        string? VoucherNo,      // 从 metadata 中提取
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt
    );

    // 修改意图关键词
    private static readonly Regex ModifyIntentPattern = new(
        @"(改|修改|変更|更新|update|change|modify|edit|纠正|订正|訂正|直す|やり直|削除|delete|取消|cancel|キャンセル|移到|移す|切替)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 凭证号模式 (例: 2511000073, 2512000062)
    private static readonly Regex VoucherNoPattern = new(
        @"\b(\d{10})\b",
        RegexOptions.Compiled);

    // 票据标签模式 (例: #5, #1, ＃3)
    private static readonly Regex DocumentLabelPattern = new(
        @"[#＃](\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 根据用户消息内容和会话上下文，判断消息应归属到哪个任务。
    /// </summary>
    /// <param name="sessionId">当前会话 ID</param>
    /// <param name="companyCode">公司代码</param>
    /// <param name="message">用户消息内容</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>路由结果，null 表示无法判定归属</returns>
    public async Task<RouteResult?> ResolveTaskAsync(
        Guid sessionId, string companyCode, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        try
        {
            // 加载当前 session 的所有任务
            var tasks = await LoadSessionTasksAsync(sessionId, companyCode, ct);
            if (tasks.Count == 0) return null;

            // 策略 1: 消息中包含凭证号 → 找到创建该凭证的任务
            var voucherMatch = VoucherNoPattern.Match(message);
            if (voucherMatch.Success)
            {
                var voucherNo = voucherMatch.Groups[1].Value;
                var matchedTask = tasks.FirstOrDefault(t =>
                    !string.IsNullOrWhiteSpace(t.VoucherNo) &&
                    t.VoucherNo.Contains(voucherNo, StringComparison.OrdinalIgnoreCase));
                if (matchedTask is not null)
                {
                    _logger.LogInformation("[MessageRouter] 通过凭证号 {VoucherNo} 匹配到任务 {TaskId}", voucherNo, matchedTask.Id);
                    return new RouteResult(matchedTask.Id, $"voucher_no_match:{voucherNo}", matchedTask.VoucherNo);
                }
            }

            // 策略 2: 消息中包含票据标签 → 找到对应标签的任务
            var labelMatch = DocumentLabelPattern.Match(message);
            if (labelMatch.Success)
            {
                var label = $"#{labelMatch.Groups[1].Value}";
                var matchedTask = tasks.FirstOrDefault(t =>
                    !string.IsNullOrWhiteSpace(t.DocumentLabel) &&
                    string.Equals(t.DocumentLabel.Trim(), label, StringComparison.OrdinalIgnoreCase));
                if (matchedTask is not null)
                {
                    _logger.LogInformation("[MessageRouter] 通过标签 {Label} 匹配到任务 {TaskId}", label, matchedTask.Id);
                    return new RouteResult(matchedTask.Id, $"label_match:{label}", matchedTask.VoucherNo);
                }
            }

            // 策略 3: 消息包含修改意图 + 只有一个最近活跃的任务 → 路由到该任务
            if (ModifyIntentPattern.IsMatch(message))
            {
                // 找最近 10 分钟内更新过的任务，或者状态为 completed/pending 的最近任务
                var recentTasks = tasks
                    .Where(t => t.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-30))
                    .OrderByDescending(t => t.UpdatedAt)
                    .ToList();

                if (recentTasks.Count == 1)
                {
                    var target = recentTasks[0];
                    _logger.LogInformation("[MessageRouter] 修改意图 + 唯一最近任务 → 匹配到任务 {TaskId} ({FileName})",
                        target.Id, target.FileName);
                    return new RouteResult(target.Id, "modify_intent_single_recent", target.VoucherNo);
                }

                // 如果有多个最近任务，优先选最后一个完成的
                if (recentTasks.Count > 1)
                {
                    var lastCompleted = recentTasks.FirstOrDefault(t =>
                        string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
                    if (lastCompleted is not null)
                    {
                        _logger.LogInformation("[MessageRouter] 修改意图 + 最近完成的任务 → 匹配到任务 {TaskId} ({FileName})",
                            lastCompleted.Id, lastCompleted.FileName);
                        return new RouteResult(lastCompleted.Id, "modify_intent_last_completed", lastCompleted.VoucherNo);
                    }
                }
            }

            // 策略 4: 只有一个任务 → 直接路由（高概率场景：用户只上传了一张发票）
            if (tasks.Count == 1)
            {
                var only = tasks[0];
                _logger.LogInformation("[MessageRouter] 唯一任务 → 匹配到任务 {TaskId} ({FileName})", only.Id, only.FileName);
                return new RouteResult(only.Id, "single_task", only.VoucherNo);
            }

            // 策略 5: 有 pending 状态的任务 → 优先路由到 pending 任务
            var pendingTask = tasks
                .Where(t => string.Equals(t.Status, "pending", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.UpdatedAt)
                .FirstOrDefault();
            if (pendingTask is not null)
            {
                _logger.LogInformation("[MessageRouter] 存在 pending 任务 → 匹配到任务 {TaskId} ({FileName})",
                    pendingTask.Id, pendingTask.FileName);
                return new RouteResult(pendingTask.Id, "pending_task_priority", pendingTask.VoucherNo);
            }

            // 无法判定
            _logger.LogDebug("[MessageRouter] 无法确定消息归属任务，共 {Count} 个任务", tasks.Count);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MessageRouter] 路由判断异常，回退到通用处理");
            return null;
        }
    }

    /// <summary>
    /// 加载会话中的所有任务摘要
    /// </summary>
    private async Task<IReadOnlyList<TaskSummary>> LoadSessionTasksAsync(
        Guid sessionId, string companyCode, CancellationToken ct)
    {
        var results = new List<TaskSummary>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, status, payload->>'documentLabel', title,
       metadata->>'voucherNo', created_at, updated_at
FROM ai_tasks
WHERE session_id = $1 AND company_code = $2 AND task_type = 'invoice'
ORDER BY created_at DESC
LIMIT 20";
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            results.Add(new TaskSummary(
                rd.GetGuid(0),
                rd.IsDBNull(1) ? "pending" : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? "" : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.GetDateTime(5),
                rd.GetDateTime(6)
            ));
        }
        return results;
    }
}

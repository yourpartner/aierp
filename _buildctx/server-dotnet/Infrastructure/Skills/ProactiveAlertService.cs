using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 主动提醒框架 — 检测各类业务异常并推送提醒
/// 
/// 异常检测场景：
/// 1. Timesheet 未提交提醒
/// 2. 请求金额异常（与历史平均偏差过大）
/// 3. 销售金额显著下降
/// 4. 证明书审批超时
/// 5. 发票到期未回收
/// </summary>
public class ProactiveAlertService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<ProactiveAlertService> _logger;

    public ProactiveAlertService(NpgsqlDataSource ds, ILogger<ProactiveAlertService> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    // ==================== 检测引擎 ====================

    /// <summary>
    /// 执行所有异常检测（由后台定时任务调用）
    /// </summary>
    public async Task<List<AlertItem>> RunAllChecksAsync(string companyCode, CancellationToken ct)
    {
        var alerts = new List<AlertItem>();

        try { alerts.AddRange(await CheckTimesheetNotSubmittedAsync(companyCode, ct)); } catch (Exception ex) { _logger.LogWarning(ex, "[Alert] Timesheet check failed"); }
        try { alerts.AddRange(await CheckBillingAmountAnomalyAsync(companyCode, ct)); } catch (Exception ex) { _logger.LogWarning(ex, "[Alert] Billing anomaly check failed"); }
        try { alerts.AddRange(await CheckOverdueApprovalsAsync(companyCode, ct)); } catch (Exception ex) { _logger.LogWarning(ex, "[Alert] Approval check failed"); }
        try { alerts.AddRange(await CheckRevenueDropAsync(companyCode, ct)); } catch (Exception ex) { _logger.LogWarning(ex, "[Alert] Revenue check failed"); }

        _logger.LogInformation("[Alert] 异常检测完成: company={Company}, alerts={Count}", companyCode, alerts.Count);
        return alerts;
    }

    /// <summary>
    /// 检测 Timesheet 未提交
    /// 规则：当月已过 X 天但员工尚未提交 timesheet
    /// </summary>
    public async Task<List<AlertItem>> CheckTimesheetNotSubmittedAsync(
        string companyCode, CancellationToken ct)
    {
        var alerts = new List<AlertItem>();
        var now = DateTimeOffset.UtcNow;

        // 每月5号后检测上月未提交
        if (now.Day < 5) return alerts;

        var lastMonth = now.AddMonths(-1);
        var periodStart = new DateTimeOffset(lastMonth.Year, lastMonth.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT r.id, r.employee_name, r.employee_code
FROM stf_resources r
WHERE r.company_code = $1
  AND r.status = 'active'
  AND NOT EXISTS (
    SELECT 1 FROM stf_timesheets t
    WHERE t.resource_id = r.id
      AND t.period_start = $2
      AND t.status IN ('submitted', 'approved')
  )
ORDER BY r.employee_name";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(periodStart.DateTime);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var resourceId = reader.GetGuid(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? "" : reader.GetString(2);

            alerts.Add(new AlertItem
            {
                Type = AlertType.TimesheetNotSubmitted,
                Severity = AlertSeverity.Warning,
                TargetUserId = resourceId.ToString(),
                TargetUserName = name,
                Title = "工时未提交",
                Message = $"员工 {name}({code}) 的 {lastMonth:yyyy年M月} 工时尚未提交，请尽快提醒。",
                Data = new JsonObject
                {
                    ["resourceId"] = resourceId.ToString(),
                    ["period"] = $"{lastMonth:yyyy-MM}"
                }
            });
        }

        return alerts;
    }

    /// <summary>
    /// 检测请求金额异常（与最近6个月平均偏差>50%）
    /// </summary>
    public async Task<List<AlertItem>> CheckBillingAmountAnomalyAsync(
        string companyCode, CancellationToken ct)
    {
        var alerts = new List<AlertItem>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH recent_avg AS (
    SELECT 
        client_id,
        AVG(total_amount) as avg_amount,
        STDDEV(total_amount) as std_amount,
        COUNT(*) as cnt
    FROM stf_invoices
    WHERE company_code = $1
      AND invoice_date > now() - interval '180 days'
      AND status != 'cancelled'
    GROUP BY client_id
    HAVING COUNT(*) >= 3
)
SELECT 
    i.id, i.invoice_no, i.total_amount,
    ra.avg_amount, ra.std_amount,
    c.name as client_name
FROM stf_invoices i
JOIN recent_avg ra ON ra.client_id = i.client_id
LEFT JOIN clients c ON c.id = i.client_id
WHERE i.company_code = $1
  AND i.created_at > now() - interval '7 days'
  AND i.status != 'cancelled'
  AND ABS(i.total_amount - ra.avg_amount) > ra.avg_amount * 0.5";
        cmd.Parameters.AddWithValue(companyCode);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var invoiceId = reader.GetGuid(0);
                var invoiceNo = reader.GetString(1);
                var amount = reader.GetDecimal(2);
                var avg = reader.GetDecimal(3);
                var clientName = reader.IsDBNull(5) ? "unknown" : reader.GetString(5);
                var deviation = Math.Abs((double)(amount - avg) / (double)avg) * 100;

                alerts.Add(new AlertItem
                {
                    Type = AlertType.BillingAmountAnomaly,
                    Severity = deviation > 100 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Title = "请求金额异常",
                    Message = $"客户 {clientName} 的请求书 {invoiceNo} 金额 {amount:N0} 与最近平均 {avg:N0} 偏差 {deviation:F0}%。",
                    Data = new JsonObject
                    {
                        ["invoiceId"] = invoiceId.ToString(),
                        ["amount"] = (double)amount,
                        ["average"] = (double)avg,
                        ["deviationPercent"] = deviation
                    }
                });
            }
        }
        catch (Exception)
        {
            // 表不存在时忽略
        }

        return alerts;
    }

    /// <summary>
    /// 检测审批超时（证明书/timesheet等）
    /// </summary>
    public async Task<List<AlertItem>> CheckOverdueApprovalsAsync(
        string companyCode, CancellationToken ct)
    {
        var alerts = new List<AlertItem>();

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Timesheet 审批超过3天未处理
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT t.id, r.employee_name, t.period_start, t.submitted_at
FROM stf_timesheets t
JOIN stf_resources r ON r.id = t.resource_id
WHERE t.company_code = $1
  AND t.status = 'submitted'
  AND t.submitted_at < now() - interval '3 days'
ORDER BY t.submitted_at ASC
LIMIT 50";
        cmd.Parameters.AddWithValue(companyCode);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var tsId = reader.GetGuid(0);
                var name = reader.GetString(1);
                var period = reader.GetDateTime(2);
                var submittedAt = reader.GetDateTime(3);
                var waitDays = (DateTime.Now - submittedAt).Days;

                alerts.Add(new AlertItem
                {
                    Type = AlertType.ApprovalOverdue,
                    Severity = waitDays > 7 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    Title = "工时审批超时",
                    Message = $"{name} 的 {period:yyyy年M月} 工时已提交 {waitDays} 天未审批。",
                    Data = new JsonObject
                    {
                        ["timesheetId"] = tsId.ToString(),
                        ["waitDays"] = waitDays
                    }
                });
            }
        }
        catch (Exception)
        {
            // 表不存在时忽略
        }

        return alerts;
    }

    /// <summary>
    /// 检测销售额显著下降（与上月同期比）
    /// </summary>
    public async Task<List<AlertItem>> CheckRevenueDropAsync(
        string companyCode, CancellationToken ct)
    {
        var alerts = new List<AlertItem>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH monthly_revenue AS (
    SELECT 
        date_trunc('month', invoice_date) as month,
        SUM(total_amount) as revenue
    FROM stf_invoices
    WHERE company_code = $1
      AND status NOT IN ('cancelled', 'draft')
      AND invoice_date > now() - interval '3 months'
    GROUP BY 1
    ORDER BY 1 DESC
    LIMIT 3
)
SELECT * FROM monthly_revenue ORDER BY month DESC";
        cmd.Parameters.AddWithValue(companyCode);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var revenues = new List<(DateTime month, decimal revenue)>();
            while (await reader.ReadAsync(ct))
            {
                revenues.Add((reader.GetDateTime(0), reader.GetDecimal(1)));
            }

            if (revenues.Count >= 2)
            {
                var current = revenues[0].revenue;
                var previous = revenues[1].revenue;
                if (previous > 0)
                {
                    var dropPercent = (double)((previous - current) / previous) * 100;
                    if (dropPercent > 20)
                    {
                        alerts.Add(new AlertItem
                        {
                            Type = AlertType.RevenueSignificantDrop,
                            Severity = dropPercent > 50 ? AlertSeverity.Critical : AlertSeverity.Warning,
                            Title = "销售额显著下降",
                            Message = $"{revenues[0].month:yyyy年M月} 销售额 {current:N0} 较上月 {previous:N0} 下降 {dropPercent:F0}%。",
                            Data = new JsonObject
                            {
                                ["currentMonth"] = revenues[0].month.ToString("yyyy-MM"),
                                ["currentRevenue"] = (double)current,
                                ["previousRevenue"] = (double)previous,
                                ["dropPercent"] = dropPercent
                            }
                        });
                    }
                }
            }
        }
        catch (Exception)
        {
            // 表不存在时忽略
        }

        return alerts;
    }

    // ==================== 推送逻辑 ====================

    /// <summary>
    /// 将检测到的异常推送到指定渠道
    /// </summary>
    public async Task DispatchAlertsAsync(
        string companyCode,
        List<AlertItem> alerts,
        IChannelAdapter? wecomAdapter,
        IChannelAdapter? lineAdapter,
        CancellationToken ct)
    {
        if (alerts.Count == 0) return;

        // 查找应接收通知的管理者
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT b.channel, b.channel_user_id, u.display_name
FROM employee_channel_bindings b
JOIN users u ON u.id = b.user_id
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
JOIN role_caps rc ON rc.role_id = r.id AND rc.capability = 'ai.admin.bind'
WHERE b.company_code = $1
  AND b.status = 'active'";
        cmd.Parameters.AddWithValue(companyCode);

        var managers = new List<(string channel, string channelUserId, string name)>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                managers.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        catch (Exception)
        {
            // 绑定表可能不存在
            return;
        }

        foreach (var alert in alerts.Where(a => a.Severity >= AlertSeverity.Warning))
        {
            var severityIcon = alert.Severity switch
            {
                AlertSeverity.Critical => "🚨",
                AlertSeverity.Warning => "⚠️",
                _ => "ℹ️"
            };

            var reply = new UnifiedReply
            {
                Text = $"{severityIcon} {alert.Title}\n\n{alert.Message}"
            };

            foreach (var (channel, channelUserId, _) in managers)
            {
                try
                {
                    IChannelAdapter? adapter = channel switch
                    {
                        "wecom" => wecomAdapter,
                        "line" => lineAdapter,
                        _ => null
                    };

                    if (adapter != null)
                    {
                        await adapter.PushMessageAsync(channelUserId, reply, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Alert] 推送失败: channel={Channel}, user={User}",
                        channel, channelUserId);
                }
            }
        }

        // 记录已推送
        await RecordAlertsSentAsync(companyCode, alerts, ct);
    }

    /// <summary>记录已发送的提醒到数据库</summary>
    private async Task RecordAlertsSentAsync(
        string companyCode, List<AlertItem> alerts, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        foreach (var alert in alerts)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO ai_learning_events (company_code, event_type, context, outcome)
VALUES ($1, $2, $3::jsonb, 'sent')";
                cmd.Parameters.AddWithValue(companyCode);
                cmd.Parameters.AddWithValue($"alert.{alert.Type}");
                var context = new JsonObject
                {
                    ["title"] = alert.Title,
                    ["message"] = alert.Message,
                    ["severity"] = alert.Severity.ToString(),
                    ["data"] = alert.Data
                };
                cmd.Parameters.AddWithValue(context.ToJsonString());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Alert] 记录推送记录失败");
            }
        }
    }
}

// ==================== 数据模型 ====================

public class AlertItem
{
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? TargetUserId { get; set; }
    public string? TargetUserName { get; set; }
    public JsonObject? Data { get; set; }
}

public enum AlertType
{
    TimesheetNotSubmitted,
    BillingAmountAnomaly,
    ApprovalOverdue,
    RevenueSignificantDrop,
    InvoiceOverdue,
    ContractExpiring,
    Custom
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

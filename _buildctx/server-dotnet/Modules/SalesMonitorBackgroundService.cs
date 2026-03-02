using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 销售监控后台服务 - 定期检查销售异常并生成告警
/// </summary>
public class SalesMonitorBackgroundService : BackgroundService
{
    private readonly ILogger<SalesMonitorBackgroundService> _logger;
    private readonly NpgsqlDataSource _ds;
    private readonly WeComNotificationService _wecomService;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(30); // 每30分钟检查一次

    public SalesMonitorBackgroundService(
        ILogger<SalesMonitorBackgroundService> logger,
        NpgsqlDataSource ds,
        WeComNotificationService wecomService)
    {
        _logger = logger;
        _ds = ds;
        _wecomService = wecomService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SalesMonitor] Background service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAllChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SalesMonitor] Error during monitoring cycle");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("[SalesMonitor] Background service stopping");
    }

    private async Task RunAllChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("[SalesMonitor] Starting monitoring cycle at {Time}", DateTime.UtcNow);

        // 获取所有公司的监控规则
        var rules = await GetActiveRulesAsync(ct);

        foreach (var rule in rules)
        {
            try
            {
                var alerts = rule.RuleType switch
                {
                    "overdue_delivery" => await CheckOverdueDeliveryAsync(rule, ct),
                    "overdue_payment" => await CheckOverduePaymentAsync(rule, ct),
                    "customer_churn" => await CheckCustomerChurnAsync(rule, ct),
                    "inventory_shortage" => await CheckInventoryShortageAsync(rule, ct),
                    _ => new List<AlertInfo>()
                };

                foreach (var alert in alerts)
                {
                    await CreateAlertAndNotifyAsync(rule, alert, ct);
                }

                _logger.LogInformation("[SalesMonitor] Rule {RuleType} for {Company} generated {Count} alerts",
                    rule.RuleType, rule.CompanyCode, alerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SalesMonitor] Error checking rule {RuleType} for {Company}",
                    rule.RuleType, rule.CompanyCode);
            }
        }
    }

    private async Task<List<MonitorRule>> GetActiveRulesAsync(CancellationToken ct)
    {
        var rules = new List<MonitorRule>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, company_code, rule_type, rule_name, params, notification_channels, notification_users
            FROM sales_monitor_rules
            WHERE is_active = true", conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new MonitorRule(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? new JsonObject() : JsonNode.Parse(reader.GetString(4)) as JsonObject ?? new JsonObject(),
                reader.IsDBNull(5) ? new JsonArray() : JsonNode.Parse(reader.GetString(5)) as JsonArray ?? new JsonArray(),
                reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)) as JsonArray
            ));
        }

        return rules;
    }

    #region 超期未纳品检测
    private async Task<List<AlertInfo>> CheckOverdueDeliveryAsync(MonitorRule rule, CancellationToken ct)
    {
        var alerts = new List<AlertInfo>();
        var thresholdDays = rule.Params["thresholdDays"]?.GetValue<int>() ?? 0;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT so.so_no, so.partner_code, 
                   COALESCE(bp.name, so.partner_code) as customer_name,
                   so.amount_total,
                   (so.payload->'header'->>'requestedDeliveryDate')::date as delivery_date,
                   CURRENT_DATE - (so.payload->'header'->>'requestedDeliveryDate')::date as overdue_days
            FROM sales_orders so
            LEFT JOIN businesspartners bp ON bp.company_code = so.company_code AND bp.partner_code = so.partner_code
            WHERE so.company_code = $1
              AND so.status = 'confirmed'
              AND (so.payload->'header'->>'requestedDeliveryDate')::date < CURRENT_DATE - $2
              AND NOT EXISTS (
                  SELECT 1 FROM delivery_notes dn 
                  WHERE dn.company_code = so.company_code AND dn.so_no = so.so_no 
                  AND dn.status IN ('shipped', 'delivered')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM sales_alerts sa 
                  WHERE sa.company_code = so.company_code AND sa.so_no = so.so_no 
                  AND sa.alert_type = 'overdue_delivery' AND sa.status = 'open'
              )
            LIMIT 50", conn);

        cmd.Parameters.AddWithValue(rule.CompanyCode);
        cmd.Parameters.AddWithValue(thresholdDays);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var soNo = reader.GetString(0);
            var customerCode = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var customerName = reader.IsDBNull(2) ? customerCode : reader.GetString(2);
            var amount = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
            var deliveryDate = reader.IsDBNull(4) ? DateOnly.FromDateTime(DateTime.Today) : reader.GetFieldValue<DateOnly>(4);
            var overdueDays = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);

            alerts.Add(new AlertInfo(
                "overdue_delivery",
                "high",
                $"納期超過: {soNo}",
                $"受注 {soNo} ({customerName}) の納期が {overdueDays} 日超過しています。希望納期: {deliveryDate:yyyy-MM-dd}",
                SoNo: soNo,
                CustomerCode: customerCode,
                CustomerName: customerName,
                Amount: amount,
                DueDate: deliveryDate.ToDateTime(TimeOnly.MinValue),
                OverdueDays: overdueDays
            ));
        }

        return alerts;
    }
    #endregion

    #region 应收账款超期检测
    private async Task<List<AlertInfo>> CheckOverduePaymentAsync(MonitorRule rule, CancellationToken ct)
    {
        var alerts = new List<AlertInfo>();
        var thresholdWorkDays = rule.Params["thresholdWorkDays"]?.GetValue<int>() ?? 1;
        var skipWeekends = rule.Params["skipWeekends"]?.GetValue<bool>() ?? true;
        var skipHolidays = rule.Params["skipHolidays"]?.GetValue<bool>() ?? true;

        // 计算阈值日期（支付期限 + N个工作日）
        var thresholdDate = CalculateWorkdayThresholdDate(DateTime.Today, thresholdWorkDays, skipWeekends, skipHolidays);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 检查超期的请求书（支付期限早于阈值日期）
        await using var cmd = new NpgsqlCommand(@"
            SELECT si.invoice_no, si.customer_code, si.customer_name,
                   si.amount_total, si.due_date,
                   CURRENT_DATE - si.due_date as overdue_days
            FROM sales_invoices si
            WHERE si.company_code = $1
              AND si.status = 'issued'
              AND si.due_date < $2::date
              AND NOT EXISTS (
                  SELECT 1 FROM sales_alerts sa 
                  WHERE sa.company_code = si.company_code AND sa.invoice_no = si.invoice_no 
                  AND sa.alert_type = 'overdue_payment' AND sa.status = 'open'
              )
            LIMIT 50", conn);

        cmd.Parameters.AddWithValue(rule.CompanyCode);
        cmd.Parameters.AddWithValue(thresholdDate);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var invoiceNo = reader.GetString(0);
            var customerCode = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var customerName = reader.IsDBNull(2) ? customerCode : reader.GetString(2);
            var amount = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
            var dueDate = reader.IsDBNull(4) ? DateTime.Today : reader.GetDateTime(4);
            var overdueDays = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            
            // 计算实际工作日超期天数
            var workdayOverdue = CountWorkdays(dueDate, DateTime.Today, skipWeekends, skipHolidays);

            alerts.Add(new AlertInfo(
                "overdue_payment",
                workdayOverdue > 20 ? "critical" : (workdayOverdue > 5 ? "high" : "medium"),
                $"入金超過: {invoiceNo}",
                $"請求書 {invoiceNo} ({customerName}) の入金が {workdayOverdue} 営業日超過しています。金額: ¥{amount:N0}、支払期限: {dueDate:yyyy-MM-dd}",
                InvoiceNo: invoiceNo,
                CustomerCode: customerCode,
                CustomerName: customerName,
                Amount: amount,
                DueDate: dueDate,
                OverdueDays: workdayOverdue
            ));
        }

        return alerts;
    }

    /// <summary>
    /// 计算从今天往前推N个工作日的阈值日期
    /// </summary>
    private DateTime CalculateWorkdayThresholdDate(DateTime fromDate, int workdays, bool skipWeekends, bool skipHolidays)
    {
        var date = fromDate;
        var count = 0;
        while (count < workdays)
        {
            date = date.AddDays(-1);
            if (IsWorkday(date, skipWeekends, skipHolidays))
            {
                count++;
            }
        }
        return date;
    }

    /// <summary>
    /// 计算两个日期之间的工作日数
    /// </summary>
    private int CountWorkdays(DateTime from, DateTime to, bool skipWeekends, bool skipHolidays)
    {
        if (from >= to) return 0;
        var count = 0;
        var date = from.AddDays(1);
        while (date <= to)
        {
            if (IsWorkday(date, skipWeekends, skipHolidays))
            {
                count++;
            }
            date = date.AddDays(1);
        }
        return count;
    }

    /// <summary>
    /// 判断是否为工作日
    /// </summary>
    private bool IsWorkday(DateTime date, bool skipWeekends, bool skipHolidays)
    {
        // 跳过周末
        if (skipWeekends && (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday))
        {
            return false;
        }

        // 跳过日本节假日（简化版，主要节假日）
        if (skipHolidays && IsJapaneseHoliday(date))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 判断是否为日本节假日（简化版）
    /// </summary>
    private static bool IsJapaneseHoliday(DateTime date)
    {
        var month = date.Month;
        var day = date.Day;

        // 固定节假日
        if (month == 1 && day == 1) return true;   // 元日
        if (month == 2 && day == 11) return true;  // 建国記念の日
        if (month == 2 && day == 23) return true;  // 天皇誕生日
        if (month == 4 && day == 29) return true;  // 昭和の日
        if (month == 5 && day == 3) return true;   // 憲法記念日
        if (month == 5 && day == 4) return true;   // みどりの日
        if (month == 5 && day == 5) return true;   // こどもの日
        if (month == 8 && day == 11) return true;  // 山の日
        if (month == 11 && day == 3) return true;  // 文化の日
        if (month == 11 && day == 23) return true; // 勤労感謝の日

        // 春分/秋分（大约日期，实际每年略有不同）
        if (month == 3 && (day == 20 || day == 21)) return true;  // 春分の日
        if (month == 9 && (day == 22 || day == 23)) return true;  // 秋分の日

        // 年末年始（12/29-1/3 通常休业）
        if (month == 12 && day >= 29) return true;
        if (month == 1 && day <= 3) return true;

        return false;
    }
    #endregion

    #region 客户流失检测
    private async Task<List<AlertInfo>> CheckCustomerChurnAsync(MonitorRule rule, CancellationToken ct)
    {
        var alerts = new List<AlertInfo>();
        var inactiveDays = rule.Params["inactiveDays"]?.GetValue<int>() ?? 30;
        var minOrders = rule.Params["minOrdersInPeriod"]?.GetValue<int>() ?? 3;
        var lookbackDays = rule.Params["lookbackDays"]?.GetValue<int>() ?? 180;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            WITH active_customers AS (
                -- 在过去lookbackDays内有足够订单的活跃客户
                SELECT partner_code, 
                       COUNT(*) as order_count,
                       MAX(order_date) as last_order_date,
                       SUM(amount_total) as total_amount
                FROM sales_orders
                WHERE company_code = $1
                  AND status != 'cancelled'
                  AND order_date >= CURRENT_DATE - $4 * INTERVAL '1 day'
                  AND order_date < CURRENT_DATE - $2 * INTERVAL '1 day'
                GROUP BY partner_code
                HAVING COUNT(*) >= $3
            )
            SELECT ac.partner_code, 
                   COALESCE(bp.name, ac.partner_code) as customer_name,
                   ac.order_count,
                   ac.last_order_date,
                   ac.total_amount,
                   CURRENT_DATE - ac.last_order_date::date as inactive_days
            FROM active_customers ac
            LEFT JOIN businesspartners bp ON bp.company_code = $1 AND bp.partner_code = ac.partner_code
            WHERE NOT EXISTS (
                SELECT 1 FROM sales_orders so 
                WHERE so.company_code = $1 AND so.partner_code = ac.partner_code 
                AND so.order_date >= CURRENT_DATE - $2 * INTERVAL '1 day'
            )
            AND NOT EXISTS (
                SELECT 1 FROM sales_alerts sa 
                WHERE sa.company_code = $1 AND sa.customer_code = ac.partner_code 
                AND sa.alert_type = 'customer_churn' AND sa.status = 'open'
            )
            ORDER BY ac.total_amount DESC
            LIMIT 20", conn);

        cmd.Parameters.AddWithValue(rule.CompanyCode);
        cmd.Parameters.AddWithValue(inactiveDays);
        cmd.Parameters.AddWithValue(minOrders);
        cmd.Parameters.AddWithValue(lookbackDays);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var customerCode = reader.GetString(0);
            var customerName = reader.IsDBNull(1) ? customerCode : reader.GetString(1);
            var orderCount = reader.GetInt64(2);
            var lastOrderDate = reader.GetDateTime(3);
            var totalAmount = reader.GetDecimal(4);
            var inactiveDaysActual = reader.GetInt32(5);

            alerts.Add(new AlertInfo(
                "customer_churn",
                "medium",
                $"顧客離脱警告: {customerName}",
                $"活発な顧客 {customerName} が {inactiveDaysActual} 日間注文がありません。過去 {lookbackDays} 日間の注文: {orderCount} 件、売上: ¥{totalAmount:N0}",
                CustomerCode: customerCode,
                CustomerName: customerName,
                Amount: totalAmount,
                OverdueDays: inactiveDaysActual
            ));
        }

        return alerts;
    }
    #endregion

    #region 库存不足检测
    private async Task<List<AlertInfo>> CheckInventoryShortageAsync(MonitorRule rule, CancellationToken ct)
    {
        var alerts = new List<AlertInfo>();
        var lookAheadDays = rule.Params["lookAheadDays"]?.GetValue<int>() ?? 14;
        var avgInboundDays = rule.Params["avgInboundDays"]?.GetValue<int>() ?? 30; // 默认30天

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // 复杂查询：考虑当前库存、待出库、预计入库
        await using var cmd = new NpgsqlCommand(@"
            WITH pending_delivery AS (
                -- 待出库数量（已确认未出库的纳品书）
                SELECT line->>'materialCode' as material_code,
                       SUM((line->>'deliveryQty')::numeric) as pending_qty
                FROM delivery_notes dn,
                     jsonb_array_elements(dn.payload->'lines') line
                WHERE dn.company_code = $1
                  AND dn.status = 'confirmed'
                  AND dn.delivery_date <= CURRENT_DATE + $2 * INTERVAL '1 day'
                GROUP BY line->>'materialCode'
            ),
            avg_inbound AS (
                -- 日均入库量（过去N天）
                SELECT material_code,
                       COALESCE(SUM(quantity) / NULLIF($3, 0), 0) as daily_avg
                FROM inventory_ledger
                WHERE company_code = $1
                  AND movement_type = 'IN'
                  AND movement_date >= CURRENT_DATE - $3 * INTERVAL '1 day'
                GROUP BY material_code
            ),
            current_stock AS (
                -- 当前库存
                SELECT material_code, SUM(quantity) as qty
                FROM inventory_balances
                WHERE company_code = $1
                GROUP BY material_code
            )
            SELECT pd.material_code,
                   COALESCE(m.name, pd.material_code) as material_name,
                   COALESCE(cs.qty, 0) as current_qty,
                   pd.pending_qty,
                   COALESCE(ai.daily_avg * $2, 0) as projected_inbound,
                   COALESCE(cs.qty, 0) + COALESCE(ai.daily_avg * $2, 0) - pd.pending_qty as projected_balance
            FROM pending_delivery pd
            LEFT JOIN current_stock cs ON cs.material_code = pd.material_code
            LEFT JOIN avg_inbound ai ON ai.material_code = pd.material_code
            LEFT JOIN materials m ON m.company_code = $1 AND m.material_code = pd.material_code
            WHERE COALESCE(cs.qty, 0) + COALESCE(ai.daily_avg * $2, 0) < pd.pending_qty
              AND NOT EXISTS (
                  SELECT 1 FROM sales_alerts sa 
                  WHERE sa.company_code = $1 AND sa.material_code = pd.material_code 
                  AND sa.alert_type = 'inventory_shortage' AND sa.status = 'open'
              )
            ORDER BY pd.pending_qty - COALESCE(cs.qty, 0) DESC
            LIMIT 20", conn);

        cmd.Parameters.AddWithValue(rule.CompanyCode);
        cmd.Parameters.AddWithValue(lookAheadDays);
        cmd.Parameters.AddWithValue(avgInboundDays);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var materialCode = reader.GetString(0);
            var materialName = reader.IsDBNull(1) ? materialCode : reader.GetString(1);
            var currentQty = reader.GetDecimal(2);
            var pendingQty = reader.GetDecimal(3);
            var projectedInbound = reader.GetDecimal(4);
            var projectedBalance = reader.GetDecimal(5);
            var shortage = pendingQty - currentQty - projectedInbound;

            alerts.Add(new AlertInfo(
                "inventory_shortage",
                shortage > pendingQty * 0.5m ? "high" : "medium",
                $"在庫不足警告: {materialName}",
                $"品目 {materialName} ({materialCode}) が {lookAheadDays} 日以内に不足する可能性があります。現在庫: {currentQty:N0}、出庫予定: {pendingQty:N0}、入庫見込: {projectedInbound:N0}、不足数: {shortage:N0}",
                MaterialCode: materialCode,
                MaterialName: materialName,
                Amount: shortage
            ));
        }

        return alerts;
    }
    #endregion

    private async Task CreateAlertAndNotifyAsync(MonitorRule rule, AlertInfo alert, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 创建告警记录
            var alertId = Guid.NewGuid();
            await using (var insertCmd = new NpgsqlCommand(@"
                INSERT INTO sales_alerts (id, company_code, alert_type, severity, status, 
                    so_no, delivery_no, invoice_no, customer_code, customer_name, 
                    material_code, material_name, title, description, amount, due_date, overdue_days)
                VALUES ($1, $2, $3, $4, 'open', $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)", conn, tx))
            {
                insertCmd.Parameters.AddWithValue(alertId);
                insertCmd.Parameters.AddWithValue(rule.CompanyCode);
                insertCmd.Parameters.AddWithValue(alert.AlertType);
                insertCmd.Parameters.AddWithValue(alert.Severity);
                insertCmd.Parameters.AddWithValue(alert.SoNo ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.DeliveryNo ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.InvoiceNo ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.CustomerCode ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.CustomerName ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.MaterialCode ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.MaterialName ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.Title);
                insertCmd.Parameters.AddWithValue(alert.Description ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.Amount ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.DueDate ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue(alert.OverdueDays ?? (object)DBNull.Value);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }

            // 创建任务
            var channels = rule.NotificationChannels;
            if (channels.Any(c => c?.ToString() == "task"))
            {
                var taskId = Guid.NewGuid();
                var taskType = alert.AlertType switch
                {
                    "overdue_delivery" => "follow_up",
                    "overdue_payment" => "collection",
                    "customer_churn" => "contact",
                    "inventory_shortage" => "restock",
                    _ => "follow_up"
                };

                await using var taskCmd = new NpgsqlCommand(@"
                    INSERT INTO alert_tasks (id, company_code, alert_id, task_type, title, description, priority, due_date)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", conn, tx);
                taskCmd.Parameters.AddWithValue(taskId);
                taskCmd.Parameters.AddWithValue(rule.CompanyCode);
                taskCmd.Parameters.AddWithValue(alertId);
                taskCmd.Parameters.AddWithValue(taskType);
                taskCmd.Parameters.AddWithValue(alert.Title);
                taskCmd.Parameters.AddWithValue(alert.Description ?? (object)DBNull.Value);
                taskCmd.Parameters.AddWithValue(alert.Severity == "critical" ? "urgent" : (alert.Severity == "high" ? "high" : "medium"));
                taskCmd.Parameters.AddWithValue(DateTime.Today.AddDays(3));
                await taskCmd.ExecuteNonQueryAsync(ct);

                // 更新告警的任务ID
                await using var updateCmd = new NpgsqlCommand(@"
                    UPDATE sales_alerts SET task_id = $1 WHERE id = $2", conn, tx);
                updateCmd.Parameters.AddWithValue(taskId);
                updateCmd.Parameters.AddWithValue(alertId);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }

            // 发送企业微信通知
            if (channels.Any(c => c?.ToString() == "wecom"))
            {
                try
                {
                    await _wecomService.SendAlertAsync(rule.CompanyCode, alert, ct);
                    
                    // 标记已通知
                    await using var notifyCmd = new NpgsqlCommand(@"
                        UPDATE sales_alerts SET notified_wecom = true, notified_at = now() WHERE id = $1", conn, tx);
                    notifyCmd.Parameters.AddWithValue(alertId);
                    await notifyCmd.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SalesMonitor] Failed to send WeCom notification for alert {AlertId}", alertId);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "[SalesMonitor] Failed to create alert for {Company}", rule.CompanyCode);
        }
    }

    private record MonitorRule(
        Guid Id,
        string CompanyCode,
        string RuleType,
        string RuleName,
        JsonObject Params,
        JsonArray NotificationChannels,
        JsonArray? NotificationUsers
    );

    public record AlertInfo(
        string AlertType,
        string Severity,
        string Title,
        string Description,
        string? SoNo = null,
        string? DeliveryNo = null,
        string? InvoiceNo = null,
        string? CustomerCode = null,
        string? CustomerName = null,
        string? MaterialCode = null,
        string? MaterialName = null,
        decimal? Amount = null,
        DateTime? DueDate = null,
        int? OverdueDays = null
    );
}


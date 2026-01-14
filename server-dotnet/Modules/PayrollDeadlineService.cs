using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// Service for monitoring payroll calculation deadlines and sending notifications
/// when deadlines are approaching or have passed.
/// </summary>
public sealed class PayrollDeadlineService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly EmailService _email;
    private readonly ApnsService? _apns;
    private readonly ILogger<PayrollDeadlineService>? _logger;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    public PayrollDeadlineService(
        NpgsqlDataSource ds,
        EmailService email,
        ApnsService? apns = null,
        ILogger<PayrollDeadlineService>? logger = null)
    {
        _ds = ds;
        _email = email;
        _apns = apns;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("[PayrollDeadline] Service starting, check interval: {Interval} minutes", CheckInterval.TotalMinutes);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckDeadlinesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PayrollDeadline] Error checking payroll deadlines");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        
        _logger?.LogInformation("[PayrollDeadline] Service stopped");
    }

    private async Task CheckDeadlinesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Load pending deadlines
        var deadlines = await LoadPendingDeadlinesAsync(ct);

        foreach (var deadline in deadlines)
        {
            // Check if deadline has passed
            if (deadline.DeadlineAt <= now)
            {
                await HandleMissedDeadlineAsync(deadline, ct);
            }
            // Check if warning should be sent
            else if (deadline.WarningAt.HasValue && deadline.WarningAt.Value <= now && !deadline.WarningSentAt.HasValue)
            {
                await HandleWarningDeadlineAsync(deadline, ct);
            }
        }

        // Also check for companies without deadlines for the current month
        await CheckMissingDeadlinesAsync(ct);
    }

    private async Task<List<DeadlineRecord>> LoadPendingDeadlinesAsync(CancellationToken ct)
    {
        var list = new List<DeadlineRecord>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, company_code, period_month, deadline_at, warning_at, status, notified_at, metadata
            FROM payroll_deadlines
            WHERE status IN ('pending', 'warning_sent')
            ORDER BY deadline_at;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DeadlineRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : JsonNode.Parse(reader.GetString(7))?.AsObject()));
        }

        return list;
    }

    private async Task HandleMissedDeadlineAsync(DeadlineRecord deadline, CancellationToken ct)
    {
        _logger?.LogWarning("Payroll deadline missed for {Company} month {Month}", deadline.CompanyCode, deadline.PeriodMonth);

        // Get admin contacts
        var contacts = await GetAdminContactsAsync(deadline.CompanyCode, ct);

        // Send notifications
        var variables = new Dictionary<string, string>
        {
            ["month"] = deadline.PeriodMonth,
            ["deadline"] = deadline.DeadlineAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ["overdue"] = FormatDuration(DateTimeOffset.UtcNow - deadline.DeadlineAt)
        };

        foreach (var contact in contacts)
        {
            if (!string.IsNullOrWhiteSpace(contact.Email))
            {
                await _email.SendPayrollNotificationAsync(contact.Email, "payroll_deadline_missed", variables, ct);
            }

            if (_apns is not null && !string.IsNullOrWhiteSpace(contact.PushToken))
            {
                await _apns.SendAsync(contact.BundleId, contact.PushToken,
                    "給与計算期限超過",
                    $"{deadline.PeriodMonth}分の給与計算期限を過ぎています。至急対応してください。",
                    contact.IsSandbox);
            }
        }

        // Update deadline status
        await UpdateDeadlineStatusAsync(deadline.Id, "overdue", ct);
    }

    private async Task HandleWarningDeadlineAsync(DeadlineRecord deadline, CancellationToken ct)
    {
        _logger?.LogInformation("Payroll deadline warning for {Company} month {Month}", deadline.CompanyCode, deadline.PeriodMonth);

        // Check if payroll is already completed for this month
        var isCompleted = await IsPayrollCompletedAsync(deadline.CompanyCode, deadline.PeriodMonth, ct);
        if (isCompleted)
        {
            await UpdateDeadlineStatusAsync(deadline.Id, "completed", ct);
            return;
        }

        // Get admin contacts
        var contacts = await GetAdminContactsAsync(deadline.CompanyCode, ct);

        var remaining = deadline.DeadlineAt - DateTimeOffset.UtcNow;
        var variables = new Dictionary<string, string>
        {
            ["month"] = deadline.PeriodMonth,
            ["deadline"] = deadline.DeadlineAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ["remaining"] = FormatDuration(remaining),
            ["status"] = "未完了"
        };

        foreach (var contact in contacts)
        {
            if (!string.IsNullOrWhiteSpace(contact.Email))
            {
                await _email.SendPayrollNotificationAsync(contact.Email, "payroll_deadline_warning", variables, ct);
            }

            if (_apns is not null && !string.IsNullOrWhiteSpace(contact.PushToken))
            {
                await _apns.SendAsync(contact.BundleId, contact.PushToken,
                    "給与計算期限のお知らせ",
                    $"{deadline.PeriodMonth}分の給与計算期限まで{FormatDuration(remaining)}です。",
                    contact.IsSandbox);
            }
        }

        // Mark warning as sent
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE payroll_deadlines SET status = 'warning_sent', notified_at = now(), updated_at = now() WHERE id = $1";
        cmd.Parameters.AddWithValue(deadline.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CheckMissingDeadlinesAsync(CancellationToken ct)
    {
        // Get current month
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");

        // Find companies that have employees but no deadline for current month
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT e.company_code
            FROM employees e
            WHERE COALESCE(e.payload->>'status', 'active') <> 'inactive'
              AND NOT EXISTS (
                  SELECT 1 FROM payroll_deadlines d
                  WHERE d.company_code = e.company_code AND d.period_month = $1
              )
              AND NOT EXISTS (
                  SELECT 1 FROM payroll_runs r
                  WHERE r.company_code = e.company_code AND r.period_month = $1 AND r.status = 'completed'
              );
            """;
        cmd.Parameters.AddWithValue(currentMonth);

        var companiesWithoutDeadlines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            companiesWithoutDeadlines.Add(reader.GetString(0));
        }

        // Create default deadlines for these companies (end of month, warn 3 days before)
        foreach (var companyCode in companiesWithoutDeadlines)
        {
            var deadline = GetEndOfMonth(DateTime.UtcNow);
            var warning = deadline.AddDays(-3);

            await CreateDeadlineAsync(companyCode, currentMonth, deadline, warning, ct);
            _logger?.LogInformation("Created default payroll deadline for {Company} month {Month}", companyCode, currentMonth);
        }
    }

    private async Task<bool> IsPayrollCompletedAsync(string companyCode, string month, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM payroll_runs WHERE company_code = $1 AND period_month = $2 AND status = 'completed' LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task UpdateDeadlineStatusAsync(Guid deadlineId, string status, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // Set completed_at when status changes to 'completed' or 'overdue'
        if (status is "completed" or "overdue")
        {
            cmd.CommandText = "UPDATE payroll_deadlines SET status = $2, completed_at = now(), updated_at = now() WHERE id = $1";
        }
        else
        {
            cmd.CommandText = "UPDATE payroll_deadlines SET status = $2, updated_at = now() WHERE id = $1";
        }
        
        cmd.Parameters.AddWithValue(deadlineId);
        cmd.Parameters.AddWithValue(status);
        await cmd.ExecuteNonQueryAsync(ct);
        
        _logger?.LogInformation("[PayrollDeadline] Updated deadline {DeadlineId} to status {Status}", deadlineId, status);
    }

    /// <summary>
    /// Creates or updates a deadline for a company's payroll month.
    /// </summary>
    public async Task CreateDeadlineAsync(
        string companyCode,
        string month,
        DateTimeOffset deadline,
        DateTimeOffset? warning,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        // NOTE:
        // 生产库可能因为历史重复数据导致 UNIQUE(company_code, period_month) 约束创建失败，
        // 从而触发 ON CONFLICT (...) 的 42P10 异常。这里改为“先 UPDATE 再 INSERT”的幂等写法，
        // 不依赖唯一约束即可工作（即使存在重复行，也会更新所有匹配行）。

        await using (var update = conn.CreateCommand())
        {
            update.CommandText = """
                UPDATE payroll_deadlines
                SET deadline_at = $3,
                    warning_at = $4,
                    updated_at = now()
                WHERE company_code = $1
                  AND period_month = $2
                  AND status IN ('pending', 'warning_sent');
                """;
            update.Parameters.AddWithValue(companyCode);
            update.Parameters.AddWithValue(month);
            update.Parameters.AddWithValue(deadline);
            update.Parameters.AddWithValue(warning.HasValue ? warning.Value : DBNull.Value);
            var updated = await update.ExecuteNonQueryAsync(ct);
            if (updated > 0) return;
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO payroll_deadlines (company_code, period_month, deadline_at, warning_at, status)
                VALUES ($1, $2, $3, $4, 'pending');
                """;
            insert.Parameters.AddWithValue(companyCode);
            insert.Parameters.AddWithValue(month);
            insert.Parameters.AddWithValue(deadline);
            insert.Parameters.AddWithValue(warning.HasValue ? warning.Value : DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Gets the current deadline status for a company's month.
    /// </summary>
    public async Task<DeadlineStatus?> GetDeadlineStatusAsync(string companyCode, string month, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT deadline_at, warning_at, status, notified_at, completed_at
            FROM payroll_deadlines
            WHERE company_code = $1 AND period_month = $2;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new DeadlineStatus(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
        }

        return null;
    }

    #region Helpers

    private async Task<List<AdminContact>> GetAdminContactsAsync(string companyCode, CancellationToken ct)
    {
        var list = new List<AdminContact>();

        // Get users with admin role
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.id, u.email, 
                   dt.bundle_id, dt.token, COALESCE(dt.environment, 'sandbox') as env
            FROM users u
            LEFT JOIN device_tokens dt ON dt.user_id = u.id AND dt.platform = 'ios'
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id
            WHERE u.company_code = $1
              AND (r.role_code = 'admin' OR r.role_code = 'payroll_admin' OR r.role_code = 'hr_admin')
            ORDER BY u.created_at;
            """;
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdminContact(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) || reader.GetString(4).Equals("sandbox", StringComparison.OrdinalIgnoreCase)));
        }

        return list;
    }

    private static DateTimeOffset GetEndOfMonth(DateTime date)
    {
        var lastDay = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month), 18, 0, 0, DateTimeKind.Utc);
        return new DateTimeOffset(lastDay, TimeSpan.Zero);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}日{duration.Hours}時間";
        }
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}時間{duration.Minutes}分";
        }
        return $"{(int)duration.TotalMinutes}分";
    }

    private sealed record DeadlineRecord(
        Guid Id,
        string CompanyCode,
        string PeriodMonth,
        DateTimeOffset DeadlineAt,
        DateTimeOffset? WarningAt,
        string Status,
        DateTimeOffset? WarningSentAt,
        JsonObject? Metadata);

    private sealed record AdminContact(
        Guid UserId,
        string? Email,
        string? BundleId,
        string? PushToken,
        bool IsSandbox);

    #endregion

    /// <summary>
    /// Public record for deadline status queries.
    /// </summary>
    public sealed record DeadlineStatus(
        DateTimeOffset DeadlineAt,
        DateTimeOffset? WarningAt,
        string Status,
        DateTimeOffset? NotifiedAt,
        DateTimeOffset? CompletedAt);
}


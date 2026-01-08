using System.Text.Json;
using System.Text.RegularExpressions;
using Cronos;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public static class NotificationsPoliciesModule
{
    public static void MapNotificationsPoliciesModule(this WebApplication app)
    {
        // Compile natural language into simple rule JSON (minimal rules engine).
        app.MapPost("/ai/notifications/compile", async (HttpRequest req) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var nl = doc.RootElement.GetProperty("nl").GetString() ?? string.Empty;

            // Minimal parsing: extract keywords/time phrases to build rules (can be replaced by LLM compilation).
            var rules = new List<object>();
            string? schedule = null; string? scheduleCron = null; string timezone = "Asia/Shanghai";
            var m = Regex.Match(nl, "(?<hh>\\d{1,2}):(?<mm>\\d{2})");
            if (m.Success)
            {
                var hh = int.Parse(m.Groups["hh"].Value).ToString("00");
                var mm = int.Parse(m.Groups["mm"].Value).ToString("00");
                if (nl.Contains("工作日")) schedule = $"workday {hh}:{mm} {timezone}";
                else if (nl.Contains("每天") || nl.Contains("每日")) schedule = $"daily {hh}:{mm} {timezone}";
            }
            var cronIdx = nl.IndexOf("cron:", StringComparison.OrdinalIgnoreCase);
            if (cronIdx >= 0)
            {
                var expr = nl.Substring(cronIdx + 5).Trim().Split(new[]{'\n','\r',';','。'},2,StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                if (!string.IsNullOrWhiteSpace(expr)) { scheduleCron = expr; }
            }

            if (nl.Contains("工资计算报错"))
            {
                rules.Add(new
                {
                    key = "payroll_error",
                    type = "payroll_error",
                    audience = new { userCode = "admin" },
                    channel = "push",
                    rateLimit = new { perUserPerDay = 5 },
                    message = new { title = "工资计算失败", body = "工资计算出现错误，请尽快处理" },
                    schedule,
                    scheduleCron,
                    timezone
                });
            }
            if (nl.Contains("待办里出现新的项目"))
            {
                rules.Add(new
                {
                    key = "approval_new",
                    type = "approval_new",
                    audience = new { role = "approver_of_task" },
                    channel = "push",
                    rateLimit = new { perUserPerDay = 10 },
                    message = new { title = "新的待办", body = "您有新的审批待办" },
                    schedule,
                    scheduleCron,
                    timezone
                });
            }
            if (nl.Contains("超过3个工作日"))
            {
                rules.Add(new
                {
                    key = "approval_overdue:3",
                    type = "approval_overdue",
                    thresholdWorkdays = 3,
                    audience = new { role = "approver_of_task" },
                    channel = "push",
                    rateLimit = new { perUserPerDay = 1 }, // Limit to one per user per day.
                    message = new { title = "待办超时提醒", body = "有待办超过3个工作日未处理" },
                    schedule,
                    scheduleCron,
                    timezone
                });
            }
            return Results.Ok(new { compiled = new { rules } });
        });

        // Save or update policies.
        app.MapPost("/admin/notifications/policies", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var name = doc.RootElement.GetProperty("name").GetString()!;
            var nl = doc.RootElement.TryGetProperty("nl", out var nle) ? nle.GetString() : null;
            var compiled = doc.RootElement.GetProperty("compiled").GetRawText();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO notification_policies(company_code,name,nl,compiled,is_active)
VALUES($1,$2,$3,$4::jsonb,TRUE)
ON CONFLICT (company_code,name) DO UPDATE SET nl=excluded.nl, compiled=excluded.compiled, is_active=TRUE, updated_at=now()
RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(name);
            cmd.Parameters.AddWithValue((object?)nl ?? DBNull.Value);
            cmd.Parameters.AddWithValue(compiled);
            var id = (Guid?)await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id });
        }).RequireAuthorization();

        // Execute policies (evaluate schedule/scheduleCron to determine triggers).
        app.MapPost("/maintenance/notifications/run", async (HttpRequest req, NpgsqlDataSource ds, ApnsService apns, Server.Infrastructure.EmailService emailService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            var force = req.Query.TryGetValue("force", out var fv) && string.Equals(fv.ToString(), "true", StringComparison.OrdinalIgnoreCase);

            var sent = await RunPoliciesAsync(ds, apns, cc!.ToString(), force, emailService);
            return Results.Ok(sent);
        }).RequireAuthorization();
    }

    // Reusable executor (within the same process): loads company policies and executes rules based on schedule.
    internal static async Task<List<object>> RunPoliciesAsync(NpgsqlDataSource ds, ApnsService apns, string companyCode, bool force,
        Server.Infrastructure.EmailService? emailService = null)
    {
        // Load active policies.
        var policies = new List<(Guid id, JsonElement compiled)>();
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, compiled FROM notification_policies WHERE company_code=$1 AND is_active=TRUE";
            cmd.Parameters.AddWithValue(companyCode);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                policies.Add((rd.GetGuid(0), JsonDocument.Parse(rd.GetFieldValue<string>(1)).RootElement.Clone()));
            }
        }

        var sent = new List<object>();

        foreach (var (policyId, compiled) in policies)
        {
            if (compiled.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.EnumerateArray())
                {
                    if (!force && !await ShouldRunNowAsync(ds, companyCode, policyId, rule)) continue;

                    // Record this trigger (minute-level debouncing).
                    await TouchLastRunAsync(ds, companyCode, policyId, rule);

                    var type = rule.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "payroll_error":
                            await ExecutePayrollErrorAsync(ds, apns, companyCode, policyId, rule, sent, emailService);
                            break;
                        case "approval_new":
                            await ExecuteApprovalNewAsync(ds, apns, companyCode, policyId, rule, sent);
                            break;
                        case "approval_overdue":
                            await ExecuteApprovalOverdueAsync(ds, apns, companyCode, policyId, rule, sent);
                            break;
                    }
                }
            }
        }

        return sent;
    }

    private static string GetRuleKey(JsonElement rule)
    {
        if (rule.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
            return k.GetString()!;
        var type = rule.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "unknown";
        // Append context to differentiate similar rules (threshold/channel etc.) for stability.
        var suffix = rule.TryGetProperty("thresholdWorkdays", out var th) && th.ValueKind == JsonValueKind.Number ? $":{th.GetInt32()}" : string.Empty;
        return type + suffix;
    }

    private static TimeZoneInfo ResolveTz(string? tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tz!); } catch { }
        // Fallback for common aliases.
        if (tz!.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        return TimeZoneInfo.Utc;
    }

    private static async Task<bool> ShouldRunNowAsync(NpgsqlDataSource ds, string companyCode, Guid policyId, JsonElement rule)
    {
        // Rules without schedule: only run when force=true or invoked manually.
        var hasCron = rule.TryGetProperty("scheduleCron", out var cronProp) && cronProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cronProp.GetString());
        var hasSpec = rule.TryGetProperty("schedule", out var specProp) && specProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(specProp.GetString());
        if (!hasCron && !hasSpec) return false;

        var key = GetRuleKey(rule);

        // Load last execution time (minute precision).
        DateTimeOffset? lastRun = null;
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT last_run_at FROM notification_rule_runs WHERE company_code=$1 AND policy_id=$2 AND rule_key=$3";
            cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(policyId); cmd.Parameters.AddWithValue(key);
            var obj = await cmd.ExecuteScalarAsync();
            if (obj is DateTime dt) lastRun = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            else if (obj is DateTimeOffset dto) lastRun = dto;
        }

        var nowUtc = DateTimeOffset.UtcNow;

        if (hasCron)
        {
            var cron = cronProp!.GetString()!;
            var tz = rule.TryGetProperty("timezone", out var tzProp) && tzProp.ValueKind == JsonValueKind.String ? tzProp.GetString() : "UTC";
            CronExpression expr;
            try { expr = CronExpression.Parse(cron, CronFormat.Standard); } catch { return false; }
            var zone = ResolveTz(tz);
            var windowStart = (lastRun ?? nowUtc.AddMinutes(-1)).UtcDateTime;
            var next = expr.GetNextOccurrence(windowStart, zone);
            if (next.HasValue && next.Value <= nowUtc.ToOffset(TimeSpan.Zero).UtcDateTime)
                return true;
            return false;
        }

        // Interpret textual schedules (minimal support: workday HH:mm TZ / daily HH:mm TZ).
        var spec = specProp!.GetString()!;
        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var kind = parts[0].ToLowerInvariant();
            var time = parts[1];
            var tz = parts.Length >= 3 ? parts[2] : "UTC";
            if (TimeSpan.TryParse(time, out var hhmm))
            {
                var zone = ResolveTz(tz);
                var nowZ = TimeZoneInfo.ConvertTime(nowUtc, zone);
                var shouldToday = kind switch
                {
                    "workday" => nowZ.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
                    "daily" => true,
                    _ => false
                };
                if (!shouldToday) return false;
                var targetZ = new DateTimeOffset(nowZ.Year, nowZ.Month, nowZ.Day, hhmm.Hours, hhmm.Minutes, 0, nowZ.Offset);
                // Trigger if within [target, target+60s) and not executed yet.
                if (nowZ >= targetZ && nowZ < targetZ.AddMinutes(1))
                {
                    if (lastRun.HasValue && lastRun.Value >= targetZ.ToUniversalTime()) return false;
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task TouchLastRunAsync(NpgsqlDataSource ds, string companyCode, Guid policyId, JsonElement rule)
    {
        var key = GetRuleKey(rule);
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO notification_rule_runs(company_code,policy_id,rule_key,last_run_at)
VALUES($1,$2,$3,now())
ON CONFLICT (company_code,policy_id,rule_key) DO UPDATE SET last_run_at=excluded.last_run_at";
        cmd.Parameters.AddWithValue(companyCode); cmd.Parameters.AddWithValue(policyId); cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync();
    }

    // Payroll error notification: supports both push and email channels.
    private static async Task ExecutePayrollErrorAsync(NpgsqlDataSource ds, ApnsService apns, string companyCode, Guid policyId, JsonElement rule, List<object> sent,
        Server.Infrastructure.EmailService? emailService = null)
    {
        var userCode = rule.GetProperty("audience").GetProperty("userCode").GetString();
        var channel = rule.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String ? chEl.GetString() : "push";
        var (userId, bundleId, token, sandbox) = await ResolveOneUserDeviceAsync(ds, companyCode, userCode!);
        
        var ruleKey = GetRuleKey(rule);
        if (userId.HasValue && await SentTodayForUserAsync(ds, companyCode, userId.Value, policyId, ruleKey)) return;

        var pushSent = false;
        var emailSent = false;
        string? pushId = null, pushErr = null, emailId = null, emailErr = null;

        // Send push notification
        if ((channel == "push" || channel == "both") && userId.HasValue && bundleId != null && token != null)
        {
            var (ok, id, err) = await apns.SendAsync(bundleId, token, "工资计算失败", "工资计算出现错误，请尽快处理", sandbox);
            pushSent = ok;
            pushId = id;
            pushErr = err;
        }

        // Send email notification
        if ((channel == "email" || channel == "both") && emailService != null)
        {
            var email = await ResolveUserEmailAsync(ds, companyCode, userCode!);
            if (!string.IsNullOrWhiteSpace(email))
            {
                var variables = new Dictionary<string, string>
                {
                    ["month"] = DateTime.Now.ToString("yyyy-MM"),
                    ["error"] = "工资计算过程中发生错误",
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                var result = await emailService.SendPayrollNotificationAsync(email, "payroll_calculation_failed", variables);
                emailSent = result.Success;
                emailId = result.MessageId;
                emailErr = result.Error;
            }
        }

        if (userId.HasValue)
        {
            await LogAsync(ds, companyCode, policyId, ruleKey, userId.Value, null, null);
        }
        sent.Add(new { userId, pushSent, pushId, pushErr, emailSent, emailId, emailErr, channel });
    }

    // Helper to resolve user email
    private static async Task<string?> ResolveUserEmailAsync(NpgsqlDataSource ds, string companyCode, string userCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload->>'email' FROM users WHERE company_code=$1 AND employee_code=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userCode);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    // New approval tasks: notify approvers for today's pending items (one notification per task per day).
    private static async Task ExecuteApprovalNewAsync(NpgsqlDataSource ds, ApnsService apns, string companyCode, Guid policyId, JsonElement rule, List<object> sent)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, approver_user_id FROM approval_tasks
WHERE company_code=$1 AND status='pending' AND created_at >= date_trunc('day', now())";
        cmd.Parameters.AddWithValue(companyCode);
        await using var rd = await cmd.ExecuteReaderAsync();
        var ruleKey = GetRuleKey(rule);
        while (await rd.ReadAsync())
        {
            var taskId = rd.GetGuid(0);
            var userId = Guid.Parse(rd.GetString(1));
            if (await SentTodayForTaskAsync(ds, companyCode, taskId, userId)) continue; // One notification per task per day.

            var (bundleId, token, sandbox) = await ResolveDeviceByUserIdAsync(ds, companyCode, userId);
            if (bundleId == null || token == null) continue;
            var (ok, id, err) = await apns.SendAsync(bundleId, token, "新的待办", "您有新的审批待办", sandbox);
            await LogAsync(ds, companyCode, policyId, ruleKey, userId, "approval_task", taskId);
            sent.Add(new { taskId, userId, ok, id, err });
        }
    }

    // Overdue (3 business days): compute duration excluding weekends.
    private static async Task ExecuteApprovalOverdueAsync(NpgsqlDataSource ds, ApnsService apns, string companyCode, Guid policyId, JsonElement rule, List<object> sent)
    {
        var threshold = rule.TryGetProperty("thresholdWorkdays", out var th) ? th.GetInt32() : 3;
        var createdBefore = DateTime.UtcNow.AddDays(-WorkdaysToDays(threshold));

        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, approver_user_id FROM approval_tasks
WHERE company_code=$1 AND status='pending' AND created_at < $2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(createdBefore);
        await using var rd = await cmd.ExecuteReaderAsync();
        var ruleKey = GetRuleKey(rule);
        while (await rd.ReadAsync())
        {
            var taskId = rd.GetGuid(0);
            var userId = Guid.Parse(rd.GetString(1));
            if (await SentTodayForTaskAsync(ds, companyCode, taskId, userId)) continue; // One notification per task per day.

            var (bundleId, token, sandbox) = await ResolveDeviceByUserIdAsync(ds, companyCode, userId);
            if (bundleId == null || token == null) continue;
            var (ok, id, err) = await apns.SendAsync(bundleId, token, "待办超时提醒", "有待办超过3个工作日未处理", sandbox);
            await LogAsync(ds, companyCode, policyId, ruleKey, userId, "approval_task", taskId);
            sent.Add(new { taskId, userId, ok, id, err });
        }
    }

    private static int WorkdaysToDays(int workdays)
    {
        // Simplified business-day approximation using 5/7 ratio.
        var weeks = workdays / 5;
        var remainder = workdays % 5;
        return weeks * 7 + remainder; // Approximation.
    }

    private static async Task<bool> SentTodayForUserAsync(NpgsqlDataSource ds, string companyCode, Guid userId, Guid policyId, string? ruleKey)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM notification_logs
WHERE company_code=$1 AND user_id=$2 AND sent_day = current_date
  AND policy_id=$3 AND (rule_key=$4 OR (rule_key IS NULL AND $4 IS NULL))
LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(policyId);
        cmd.Parameters.AddWithValue((object?)ruleKey ?? DBNull.Value);
        return (await cmd.ExecuteScalarAsync()) != null;
    }

    private static async Task<bool> SentTodayForTaskAsync(NpgsqlDataSource ds, string companyCode, Guid taskId, Guid userId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM notification_logs WHERE company_code=$1 AND related_entity='approval_task' AND related_id=$2 AND user_id=$3 AND sent_day=current_date LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(userId);
        return (await cmd.ExecuteScalarAsync()) != null;
    }

    private static async Task LogAsync(NpgsqlDataSource ds, string companyCode, Guid policyId, string ruleKey, Guid userId, string? entity, Guid? objId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO notification_logs(company_code,policy_id,rule_key,user_id,related_entity,related_id)
VALUES($1,$2,$3,$4,$5,$6)";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(policyId);
        cmd.Parameters.AddWithValue(ruleKey);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue((object?)entity ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)objId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(Guid? userId, string? bundleId, string? token, bool sandbox)> ResolveOneUserDeviceAsync(NpgsqlDataSource ds, string companyCode, string userCode)
    {
        // Resolve userId via users.employee_code, then locate registered devices.
        Guid? uid = null; string? bundleId = null; string? token = null; string env = "sandbox";
        await using (var conn = await ds.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT id FROM users WHERE company_code=$1 AND employee_code=$2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(userCode);
            uid = (Guid?)await cmd.ExecuteScalarAsync();
        }
        if (uid == null) return (null, null, null, true);
        await using (var c2 = await ds.OpenConnectionAsync())
        await using (var q = c2.CreateCommand())
        {
            q.CommandText = @"SELECT bundle_id, token, COALESCE(environment,'sandbox') FROM device_tokens WHERE company_code=$1 AND user_id=$2 AND platform='ios' ORDER BY updated_at DESC LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(uid.Value);
            await using var rd = await q.ExecuteReaderAsync();
            if (await rd.ReadAsync()) { bundleId = rd.IsDBNull(0) ? null : rd.GetString(0); token = rd.GetString(1); env = rd.IsDBNull(2) ? "sandbox" : rd.GetString(2); }
        }
        return (uid, bundleId, token, string.Equals(env, "sandbox", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(string? bundleId, string? token, bool sandbox)> ResolveDeviceByUserIdAsync(NpgsqlDataSource ds, string companyCode, Guid userId)
    {
        string? bundleId = null; string? token = null; string env = "sandbox";
        await using var conn = await ds.OpenConnectionAsync();
        await using var q = conn.CreateCommand();
        q.CommandText = @"SELECT bundle_id, token, COALESCE(environment,'sandbox') FROM device_tokens WHERE company_code=$1 AND user_id=$2 AND platform='ios' ORDER BY updated_at DESC LIMIT 1";
        q.Parameters.AddWithValue(companyCode);
        q.Parameters.AddWithValue(userId);
        await using var rd = await q.ExecuteReaderAsync();
        if (await rd.ReadAsync()) { bundleId = rd.IsDBNull(0) ? null : rd.GetString(0); token = rd.GetString(1); env = rd.IsDBNull(2) ? "sandbox" : rd.GetString(2); }
        return (bundleId, token, string.Equals(env, "sandbox", StringComparison.OrdinalIgnoreCase));
    }
}



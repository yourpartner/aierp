using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules;

namespace Server.Infrastructure;

/// <summary>
/// Background worker that interprets natural-language task specs and executes scheduled jobs
/// (payroll batch, timesheet compliance, moneytree sync). Tasks are stored in <c>scheduler_tasks</c>.
/// </summary>
public sealed class TaskSchedulerService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly LawDatasetService _law;
    private readonly EmailService? _email;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TaskSchedulerService>? _logger;
    private readonly string _workerId;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ShortDelay = TimeSpan.FromSeconds(8);

    public TaskSchedulerService(NpgsqlDataSource ds, LawDatasetService law, IServiceScopeFactory scopeFactory, EmailService? email = null, ILogger<TaskSchedulerService>? logger = null)
    {
        _ds = ds;
        _law = law;
        _scopeFactory = scopeFactory;
        _email = email;
        _logger = logger;
        _workerId = $"tasksched-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Entry point for the background worker: continuously fetches due tasks and processes them.
    /// </summary>
    private static readonly TimeSpan StaleLockTimeout = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 回收超过 10 分钟的过期锁（服务重启后任务卡在 running 状态）
                await RecoverStaleLocks(stoppingToken);

                var tasks = await FetchDueTasksAsync(stoppingToken);
                if (tasks.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                foreach (var task in tasks)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessTaskAsync(task, stoppingToken);
                }

                await Task.Delay(ShortDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { _logger?.LogError(ex, "任务调度执行失败"); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { }
            }
        }
    }

    /// <summary>
    /// Recovers tasks that are stuck in 'running' state due to service restarts.
    /// If a task has been locked for more than StaleLockTimeout, reset it to 'pending'.
    /// </summary>
    private async Task RecoverStaleLocks(CancellationToken ct)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE scheduler_tasks
SET payload = jsonb_set(COALESCE(payload, '{}'::jsonb), '{status}', '""pending""'),
    locked_by = NULL,
    locked_at = NULL,
    next_run_at = now(),
    updated_at = now()
WHERE locked_by IS NOT NULL
  AND locked_at < now() - $1::interval
RETURNING id, company_code, payload->'plan'->>'action' AS action";
            cmd.Parameters.AddWithValue(StaleLockTimeout);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var company = reader.IsDBNull(1) ? "?" : reader.GetString(1);
                var action = reader.IsDBNull(2) ? "?" : reader.GetString(2);
                _logger?.LogWarning("[TaskScheduler] Recovered stale lock: task={TaskId}, company={Company}, action={Action}", id, company, action);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TaskScheduler] Failed to recover stale locks");
        }
    }

    /// <summary>
    /// Picks due scheduler tasks by pessimistically locking rows and marking them as running.
    /// </summary>
    private async Task<List<SchedulerTaskRecord>> FetchDueTasksAsync(CancellationToken ct)
    {
        var list = new List<SchedulerTaskRecord>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH pick AS (
    SELECT id FROM scheduler_tasks
    WHERE status = 'pending' AND (next_run_at IS NULL OR next_run_at <= now())
    ORDER BY next_run_at NULLS FIRST, created_at
    LIMIT 5
    FOR UPDATE SKIP LOCKED
)
UPDATE scheduler_tasks t
SET payload = jsonb_set(COALESCE(t.payload, '{}'::jsonb), '{status}', to_jsonb('running'::text), true),
    locked_by = $1,
    locked_at = now(),
    updated_at = now()
FROM pick
WHERE t.id = pick.id
RETURNING t.id, t.company_code, t.payload, t.next_run_at, t.last_run_at;";
        cmd.Parameters.AddWithValue(_workerId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetGuid(0);
            var company = rd.GetString(1);
            var payloadJson = rd.GetString(2);
            var payload = JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject();
            DateTime? nextRunRaw = rd.IsDBNull(3) ? (DateTime?)null : rd.GetFieldValue<DateTime>(3);
            DateTime? lastRunRaw = rd.IsDBNull(4) ? (DateTime?)null : rd.GetFieldValue<DateTime>(4);
            DateTimeOffset? nextRun = nextRunRaw.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(nextRunRaw.Value, DateTimeKind.Utc)) : null;
            DateTimeOffset? lastRun = lastRunRaw.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(lastRunRaw.Value, DateTimeKind.Utc)) : null;
            list.Add(new SchedulerTaskRecord(id, company, payload, nextRun, lastRun));
        }
        return list;
    }

    /// <summary>
    /// Executes a scheduler task: interprets plan/schedule from natural language and invokes the
    /// corresponding action (currently payroll batch execution).
    /// </summary>
    private async Task ProcessTaskAsync(SchedulerTaskRecord task, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var payload = task.Payload;
            var nlSpec = ReadString(payload, "nlSpec");
            if (string.IsNullOrWhiteSpace(nlSpec))
            {
                payload["status"] = "failed";
                payload["result"] = new JsonObject { ["error"] = "缺少自然语言描述" };
                await FailTaskAsync(task.Id, payload, ct);
                return;
            }

            var notes = ReadNotes(payload);
            var plan = payload.TryGetPropertyValue("plan", out var planNode) ? planNode as JsonObject : null;
            var schedule = payload.TryGetPropertyValue("schedule", out var scheduleNode) ? scheduleNode as JsonObject : null;

            if (plan is null)
            {
                var interpreted = SchedulerPlanHelper.Interpret(task.CompanyCode, nlSpec);
                plan = interpreted.Plan;
                if (interpreted.Notes.Length > 0) notes.AddRange(interpreted.Notes);
                if (plan is null)
                {
                    var failResult = new JsonObject
                    {
                        ["nlSpec"] = nlSpec,
                        ["notes"] = interpreted.Notes.Length > 0 ? new JsonArray(interpreted.Notes.Select(note => JsonValue.Create(note)).ToArray()) : null
                    };
                    payload["status"] = "failed";
                    payload["result"] = failResult;
                    await FailTaskAsync(task.Id, payload, ct);
                    return;
                }
                payload["plan"] = plan;
                if (interpreted.Schedule is not null)
                {
                    schedule = interpreted.Schedule;
                    payload["schedule"] = schedule;
                }
            }

            if (schedule is null && payload.TryGetPropertyValue("schedule", out var schedNode) && schedNode is JsonObject schedObj)
            {
                schedule = schedObj;
            }

            payload["status"] = "running";

            var action = plan.TryGetPropertyValue("action", out var actNode) && actNode is JsonValue actValue && actValue.TryGetValue<string>(out var actStr)
                ? actStr
                : null;
            if (string.IsNullOrWhiteSpace(action))
            {
                payload["status"] = "failed";
                payload["result"] = new JsonObject { ["error"] = "计划缺少 action 字段", ["plan"] = plan };
                await FailTaskAsync(task.Id, payload, ct);
                return;
            }

            TaskExecutionOutcome outcome =
                string.Equals(action, "payroll.batch_execute", StringComparison.OrdinalIgnoreCase)
                    ? await ExecutePayrollBatchAsync(task, plan, schedule, startedAt, ct)
                    : string.Equals(action, "timesheet.compliance_check", StringComparison.OrdinalIgnoreCase)
                        ? await ExecuteTimesheetComplianceAsync(task, plan, schedule, startedAt, ct)
                        : string.Equals(action, "moneytree.sync", StringComparison.OrdinalIgnoreCase)
                            ? await ExecuteMoneytreeSyncAsync(task, plan, schedule, startedAt, ct)
                        : TaskExecutionOutcome.Failure("未支持的任务类型", plan);

            if (schedule is not null)
            {
                payload["schedule"] = schedule;
            }
            payload["plan"] = plan;
            if (notes.Count > 0)
            {
                payload["notes"] = new JsonArray(notes.Select(note => JsonValue.Create(note)).ToArray());
                outcome.Result["notes"] = payload["notes"]!.DeepClone();
            }
            outcome.Result["plan"] = plan.DeepClone();
            outcome.Result["nlSpec"] = nlSpec;
            outcome.Result["companyCode"] = task.CompanyCode;
            outcome.Result["completedAt"] = DateTimeOffset.UtcNow.ToString("O");

            payload["result"] = outcome.Result;
            payload["status"] = outcome.FinalStatus;
            if (outcome.HasFailures)
            {
                payload["hasFailures"] = true;
            }
            else
            {
                payload.Remove("hasFailures");
            }

            var nextRun = outcome.NextRunAt ?? SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
            await CompleteTaskAsync(task.Id, payload, nextRun, DateTimeOffset.UtcNow, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try { _logger?.LogError(ex, "任务 {TaskId} 执行异常", task.Id); } catch { }
            var payload = task.Payload;
            payload["status"] = "failed";
            payload["result"] = new JsonObject
            {
                ["error"] = ex.Message,
                ["nlSpec"] = ReadString(payload, "nlSpec")
            };
            await FailTaskAsync(task.Id, payload, ct);
        }
    }

    /// <summary>
    /// Handles the payroll.batch_execute action by calling into <see cref="PayrollService"/> and
    /// aggregating success/failure information for the scheduler payload.
    /// </summary>
    private async Task<TaskExecutionOutcome> ExecutePayrollBatchAsync(SchedulerTaskRecord task, JsonObject plan, JsonObject? schedule, DateTimeOffset startedAt, CancellationToken ct)
    {
        var month = plan.TryGetPropertyValue("month", out var monthNode) && monthNode is JsonValue monthVal && monthVal.TryGetValue<string>(out var monthStr) && !string.IsNullOrWhiteSpace(monthStr)
            ? monthStr
            : DateTime.UtcNow.ToString("yyyy-MM");

        Guid? policyId = null;
        if (plan.TryGetPropertyValue("policyId", out var policyNode) && policyNode is JsonValue policyVal && policyVal.TryGetValue<string>(out var policyText) && Guid.TryParse(policyText, out var parsedPolicy))
        {
            policyId = parsedPolicy;
        }

        var employeeIds = new List<Guid>();
        if (plan.TryGetPropertyValue("employeeIds", out var idsNode) && idsNode is JsonArray idArray)
        {
            foreach (var node in idArray)
            {
                if (node is JsonValue val && val.TryGetValue<string>(out var idText) && Guid.TryParse(idText, out var gid))
                {
                    employeeIds.Add(gid);
                }
            }
        }

        if (employeeIds.Count == 0)
        {
            var filterKind = "all_active";
            if (plan.TryGetPropertyValue("employeeFilter", out var filterNode) && filterNode is JsonObject filterObj && filterObj.TryGetPropertyValue("kind", out var kindNode) && kindNode is JsonValue kindVal && kindVal.TryGetValue<string>(out var kindStr) && !string.IsNullOrWhiteSpace(kindStr))
            {
                filterKind = kindStr;
            }

            var sql = "SELECT id FROM employees WHERE company_code=$1";
            if (string.Equals(filterKind, "all_active", StringComparison.OrdinalIgnoreCase))
            {
                sql += " AND COALESCE(payload->>'status','active') <> 'inactive'";
            }

            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue(task.CompanyCode);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                employeeIds.Add(rd.GetGuid(0));
            }
        }

        if (employeeIds.Count == 0)
        {
            var payload = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["month"] = month,
                    ["totalEmployees"] = 0,
                    ["error"] = "没有找到待处理的员工"
                }
            };
            return TaskExecutionOutcome.Failure("没有找到符合条件的员工", payload);
        }

        var employeeResults = new JsonArray();
        int success = 0;
        int failed = 0;

        foreach (var empId in employeeIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var exec = await HrPayrollModule.ExecutePayrollInternal(_ds, _law, task.CompanyCode, empId, month, policyId, false, null, ct);
                success++;
                var total = SumAmounts(exec.PayrollSheet);
                var empObj = new JsonObject
                {
                    ["employeeId"] = empId.ToString(),
                    ["status"] = "success",
                    ["totalAmount"] = total,
                    ["itemCount"] = exec.PayrollSheet.Count
                };
                empObj["payrollSheet"] = JsonSerializer.SerializeToNode(exec.PayrollSheet);
                empObj["accountingDraft"] = JsonSerializer.SerializeToNode(exec.AccountingDraft);
                employeeResults.Add(empObj);
            }
            catch (PayrollExecutionException ex)
            {
                failed++;
                var empObj = new JsonObject
                {
                    ["employeeId"] = empId.ToString(),
                    ["status"] = "error",
                    ["error"] = JsonSerializer.SerializeToNode(ex.Payload)
                };
                employeeResults.Add(empObj);
            }
            catch (Exception ex)
            {
                failed++;
                var empObj = new JsonObject
                {
                    ["employeeId"] = empId.ToString(),
                    ["status"] = "error",
                    ["error"] = ex.Message
                };
                employeeResults.Add(empObj);
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var summary = new JsonObject
        {
            ["month"] = month,
            ["totalEmployees"] = employeeIds.Count,
            ["successCount"] = success,
            ["failedCount"] = failed,
            ["hasFailures"] = failed > 0,
            ["policyId"] = policyId?.ToString(),
            ["startedAt"] = startedAt.ToString("O"),
            ["finishedAt"] = finishedAt.ToString("O"),
            ["durationMs"] = (finishedAt - startedAt).TotalMilliseconds
        };

        var result = new JsonObject
        {
            ["employees"] = employeeResults,
            ["summary"] = summary
        };

        var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
        if (nextRun.HasValue)
        {
            result["nextRunPreview"] = nextRun.Value.ToString("O");
        }

        if (success == 0 && failed > 0)
        {
            return new TaskExecutionOutcome("failed", result, nextRun, true);
        }

        return new TaskExecutionOutcome("waiting_review", result, nextRun, failed > 0);
    }

    /// <summary>
    /// Japan timesheet compliance check: evaluates overtime against common management thresholds (e.g. 45/80/100h per month),
    /// and notifies admins. Deterministic rules only (no AI judgement).
    /// </summary>
    private async Task<TaskExecutionOutcome> ExecuteTimesheetComplianceAsync(SchedulerTaskRecord task, JsonObject plan, JsonObject? schedule, DateTimeOffset startedAt, CancellationToken ct)
    {
        var month =
            plan.TryGetPropertyValue("month", out var monthNode) && monthNode is JsonValue mv && mv.TryGetValue<string>(out var monthStr) && !string.IsNullOrWhiteSpace(monthStr)
                ? monthStr.Trim()
                : DateTime.UtcNow.ToString("yyyy-MM");

        int warn = 45, high = 80, critical = 100;
        if (plan.TryGetPropertyValue("thresholds", out var thNode) && thNode is JsonObject th)
        {
            warn = ReadInt(th, "warnOvertimeHours", warn);
            high = ReadInt(th, "highOvertimeHours", high);
            critical = ReadInt(th, "criticalOvertimeHours", critical);
        }
        if (high < warn) high = warn;
        if (critical < high) critical = high;

        var alerts = new JsonArray();
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT t.created_by,
       COALESCE(SUM(COALESCE((t.payload->>'overtime')::numeric,0)),0) AS total_overtime,
       COALESCE(u.employee_code,'') AS employee_code,
       COALESCE(u.name,'') AS user_name
FROM timesheets t
LEFT JOIN users u ON u.company_code=t.company_code AND u.id::text=t.created_by
WHERE t.company_code=$1 AND t.month=$2
GROUP BY t.created_by, u.employee_code, u.name
ORDER BY total_overtime DESC";
            cmd.Parameters.AddWithValue(task.CompanyCode);
            cmd.Parameters.AddWithValue(month);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var uid = rd.IsDBNull(0) ? "" : rd.GetString(0);
                var ot = rd.IsDBNull(1) ? 0m : rd.GetDecimal(1);
                var code = rd.IsDBNull(2) ? "" : rd.GetString(2);
                var name = rd.IsDBNull(3) ? "" : rd.GetString(3);
                if (string.IsNullOrWhiteSpace(uid)) continue;

                var sev =
                    ot >= critical ? "critical"
                    : ot >= high ? "high"
                    : ot >= warn ? "warning"
                    : "none";
                if (sev == "none") continue;

                alerts.Add(new JsonObject
                {
                    ["type"] = "jp_overtime_monthly",
                    ["severity"] = sev,
                    ["month"] = month,
                    ["userId"] = uid,
                    ["employeeCode"] = string.IsNullOrWhiteSpace(code) ? null : code,
                    ["userName"] = string.IsNullOrWhiteSpace(name) ? null : name,
                    ["totalOvertimeHours"] = Math.Round(ot, 2),
                    ["thresholdWarn"] = warn,
                    ["thresholdHigh"] = high,
                    ["thresholdCritical"] = critical,
                    ["message"] = $"月残業が基準を超過しました: {Math.Round(ot, 2)}h (warn={warn}, high={high}, critical={critical})"
                });
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var summary = new JsonObject
        {
            ["month"] = month,
            ["thresholds"] = new JsonObject { ["warn"] = warn, ["high"] = high, ["critical"] = critical },
            ["alertCount"] = alerts.Count,
            ["startedAt"] = startedAt.ToString("O"),
            ["finishedAt"] = finishedAt.ToString("O"),
            ["durationMs"] = (finishedAt - startedAt).TotalMilliseconds
        };

        if (alerts.Count > 0)
        {
            try
            {
                var emails = await GetAdminEmailsAsync(task.CompanyCode, ct);
                if (_email is not null && emails.Count > 0)
                {
                    var subject = $"[勤怠警告] {task.CompanyCode} {month} 残業超過 {alerts.Count}件";
                    var body = BuildEmailBody(task.CompanyCode, month, warn, high, critical, alerts);
                    foreach (var to in emails)
                    {
                        try { await _email.SendAsync(to, subject, body, false, ct); } catch { }
                    }
                }
            }
            catch { }
        }

        var result = new JsonObject
        {
            ["summary"] = summary,
            ["alerts"] = alerts
        };

        var finalStatus = alerts.Count > 0 ? "waiting_review" : "pending";
        return new TaskExecutionOutcome(finalStatus, result, null, false);

        static int ReadInt(JsonObject obj, string key, int fallback)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) return fallback;
            if (node is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var j)) return j;
                if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            }
            return fallback;
        }

        async Task<List<string>> GetAdminEmailsAsync(string companyCode, CancellationToken token)
        {
            var list = new List<string>();
            await using var conn = await _ds.OpenConnectionAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT COALESCE(e.payload->>'contact.email','') AS email
FROM users u
JOIN user_roles ur ON ur.user_id=u.id
JOIN roles r ON r.id=ur.role_id AND LOWER(r.role_code)=LOWER('ADMIN')
LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
WHERE u.company_code=$1";
            cmd.Parameters.AddWithValue(companyCode);
            await using var rd = await cmd.ExecuteReaderAsync(token);
            while (await rd.ReadAsync(token))
            {
                var email = rd.IsDBNull(0) ? "" : rd.GetString(0);
                if (!string.IsNullOrWhiteSpace(email)) list.Add(email);
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string BuildEmailBody(string companyCode, string monthText, int warnTh, int highTh, int critTh, JsonArray alertsArr)
        {
            var lines = new List<string>
            {
                $"会社: {companyCode}",
                $"対象月: {monthText}",
                $"基準: warn={warnTh}h / high={highTh}h / critical={critTh}h",
                $"件数: {alertsArr.Count}",
                "",
                "上位警告:"
            };
            int take = 20;
            foreach (var node in alertsArr.Take(take))
            {
                if (node is not JsonObject a) continue;
                var code = a.TryGetPropertyValue("employeeCode", out var c) ? c?.ToString() : "";
                var name = a.TryGetPropertyValue("userName", out var n) ? n?.ToString() : "";
                var ot = a.TryGetPropertyValue("totalOvertimeHours", out var o) ? o?.ToString() : "";
                var sev = a.TryGetPropertyValue("severity", out var s) ? s?.ToString() : "";
                lines.Add($"- [{sev}] {code} {name} overtime={ot}h");
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Handles the moneytree.sync action: downloads bank transactions from Moneytree and imports them.
    /// </summary>
    private async Task<TaskExecutionOutcome> ExecuteMoneytreeSyncAsync(SchedulerTaskRecord task, JsonObject plan, JsonObject? schedule, DateTimeOffset startedAt, CancellationToken ct)
    {
        // 获取同步天数配置，默认同步最近7天
        var daysBack = 7;
        if (plan.TryGetPropertyValue("daysBack", out var daysNode) && daysNode is JsonValue daysVal)
        {
            if (daysVal.TryGetValue<int>(out var d)) daysBack = d;
            else if (daysVal.TryGetValue<string>(out var ds) && int.TryParse(ds, out var dp)) daysBack = dp;
        }

        var tz = ResolveJstTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var endLocalDate = nowLocal.Date;
        var lastSuccessEnd = ReadLastSuccessEnd(task.Payload);
        var startLocalDate = lastSuccessEnd ?? endLocalDate.AddDays(-daysBack);
        if (startLocalDate > endLocalDate) startLocalDate = endLocalDate;

        var startDate = BuildLocalDateTimeOffset(startLocalDate, tz, isEndOfDay: false);
        var endDate = BuildLocalDateTimeOffset(endLocalDate, tz, isEndOfDay: true);

        _logger.LogInformation("[TaskScheduler] Moneytree sync date range: lastSuccessEnd={LastSuccessEnd}, start={Start}, end={End}",
            lastSuccessEnd?.ToString("yyyy-MM-dd") ?? "(null)", startLocalDate.ToString("yyyy-MM-dd"), endLocalDate.ToString("yyyy-MM-dd"));

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<MoneytreeImportService>();

            var importResult = await importService.ImportAsync(
                task.CompanyCode,
                new MoneytreeImportService.MoneytreeImportRequest(null, startDate, endDate),
                "scheduler",
                ct);

            var finishedAt = DateTimeOffset.UtcNow;
            
            // 检查是否为空结果（指定日期范围内没有新数据）
            var isEmpty = importResult.BatchId == Guid.Empty && importResult.TotalRows == 0;
            
            var summary = new JsonObject
            {
                ["startDate"] = startDate.ToString("yyyy-MM-dd"),
                ["endDate"] = endDate.ToString("yyyy-MM-dd"),
                ["batchId"] = importResult.BatchId.ToString(),
                ["totalRows"] = importResult.TotalRows,
                ["insertedRows"] = importResult.InsertedRows,
                ["skippedRows"] = importResult.SkippedRows,
                ["linkedRows"] = importResult.LinkedRows,
                ["startedAt"] = startedAt.ToString("O"),
                ["finishedAt"] = finishedAt.ToString("O"),
                ["durationMs"] = (finishedAt - startedAt).TotalMilliseconds,
                ["isEmpty"] = isEmpty
            };
            
            if (isEmpty)
            {
                summary["message"] = "指定日期範囲内に新しい銀行明細がありません（0件）";
            }

            var result = new JsonObject
            {
                ["summary"] = summary,
                ["lastSuccessEnd"] = isEmpty
                    ? (lastSuccessEnd?.ToString("yyyy-MM-dd") ?? endLocalDate.AddDays(-daysBack).ToString("yyyy-MM-dd"))
                    : endLocalDate.ToString("yyyy-MM-dd"),
                ["retry"] = new JsonObject
                {
                    ["attempts"] = 0,
                    ["maxAttempts"] = ReadRetryMaxAttempts(schedule),
                    ["intervalMinutes"] = ReadRetryIntervalMinutes(schedule)
                }
            };

            var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
            if (nextRun.HasValue)
            {
                result["nextRunPreview"] = nextRun.Value.ToString("O");
            }

            if (isEmpty)
            {
                _logger?.LogInformation("[TaskScheduler] Moneytree sync completed for {Company}: no new data in date range {Start} ~ {End}",
                    task.CompanyCode, startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));
            }
            else
            {
                _logger?.LogInformation("[TaskScheduler] Moneytree sync completed for {Company}: inserted={Inserted}, skipped={Skipped}",
                    task.CompanyCode, importResult.InsertedRows, importResult.SkippedRows);
            }

            return new TaskExecutionOutcome("pending", result, nextRun, false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TaskScheduler] Moneytree sync failed for {Company}", task.CompanyCode);

            var finishedAt = DateTimeOffset.UtcNow;
            var maxAttempts = ReadRetryMaxAttempts(schedule);
            var intervalMinutes = ReadRetryIntervalMinutes(schedule);
            var attempts = ReadRetryAttempts(task.Payload) + 1;

            DateTimeOffset? retryAt = null;
            DateTimeOffset? nextRun = null;
            var finalStatus = "failed";
            var hasSchedule = schedule != null && schedule.TryGetPropertyValue("kind", out var kindNode) 
                && kindNode is JsonValue kv && kv.TryGetValue<string>(out var kind) 
                && !string.Equals(kind, "once", StringComparison.OrdinalIgnoreCase);

            if (attempts < maxAttempts)
            {
                // 重试未耗尽：设置重试时间
                retryAt = DateTimeOffset.UtcNow.AddMinutes(intervalMinutes);
                nextRun = retryAt;
                finalStatus = "pending";
            }
            else if (hasSchedule)
            {
                // 重试已耗尽，但有定期计划：重置状态为 pending，等待下次定期执行
                // 计算下次定期执行时间
                nextRun = SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
                finalStatus = "pending"; // 关键修改：保持 pending 状态，让调度器能继续拾取
                _logger?.LogInformation("[TaskScheduler] Moneytree sync retries exhausted for {Company}, scheduling next regular run at {NextRun}", 
                    task.CompanyCode, nextRun?.ToString("O") ?? "null");
            }
            else
            {
                // 重试已耗尽且无定期计划：标记为失败
                nextRun = null;
            }

            // 失败时保留上一次成功的 lastSuccessEnd，避免重试时日期范围回退
            var previousLastSuccessEnd = ReadLastSuccessEnd(task.Payload);

            var result = new JsonObject
            {
                ["error"] = ex.Message,
                ["retry"] = new JsonObject
                {
                    // 如果是定期任务且重试耗尽，重置 attempts 为 0，以便下次执行时能重新重试
                    ["attempts"] = (attempts >= maxAttempts && hasSchedule) ? 0 : attempts,
                    ["maxAttempts"] = maxAttempts,
                    ["intervalMinutes"] = intervalMinutes,
                    ["nextRunAt"] = retryAt?.ToString("O")
                },
                ["summary"] = new JsonObject
                {
                    ["startDate"] = startDate.ToString("yyyy-MM-dd"),
                    ["endDate"] = endDate.ToString("yyyy-MM-dd"),
                    ["startedAt"] = startedAt.ToString("O"),
                    ["finishedAt"] = finishedAt.ToString("O"),
                    ["durationMs"] = (finishedAt - startedAt).TotalMilliseconds,
                    ["retriesExhausted"] = attempts >= maxAttempts,
                    ["willRetryOnSchedule"] = attempts >= maxAttempts && hasSchedule
                }
            };

            // 保留上一次成功的 lastSuccessEnd
            if (previousLastSuccessEnd.HasValue)
            {
                result["lastSuccessEnd"] = previousLastSuccessEnd.Value.ToString("yyyy-MM-dd");
            }

            // 只有当重试耗尽且无定期计划时才发送失败邮件
            if (attempts >= maxAttempts && !hasSchedule)
            {
                await SendMoneytreeFailureEmailAsync(task.CompanyCode, ex.Message, startDate, endDate, attempts, maxAttempts, ct);
            }
            else if (attempts >= maxAttempts && hasSchedule)
            {
                // 有定期计划时，发送一封通知邮件（非严重错误）
                _logger?.LogWarning("[TaskScheduler] Moneytree sync failed for {Company} after {Attempts} retries, will retry on next schedule", 
                    task.CompanyCode, attempts);
            }

            return new TaskExecutionOutcome(finalStatus, result, nextRun, finalStatus == "failed");
        }

        static TimeZoneInfo ResolveJstTimeZone()
        {
            foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { }
            }
            return TimeZoneInfo.Utc;
        }

        static DateTimeOffset BuildLocalDateTimeOffset(DateTime localDate, TimeZoneInfo tz, bool isEndOfDay)
        {
            var time = isEndOfDay ? new TimeSpan(23, 59, 59) : TimeSpan.Zero;
            var local = new DateTime(localDate.Year, localDate.Month, localDate.Day, time.Hours, time.Minutes, time.Seconds, DateTimeKind.Unspecified);
            var offset = tz.GetUtcOffset(local);
            return new DateTimeOffset(local, offset);
        }

        static DateTime? ReadLastSuccessEnd(JsonObject payload)
        {
            if (payload.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject resultObj)
            {
                if (resultObj.TryGetPropertyValue("lastSuccessEnd", out var node) && node is JsonValue val && val.TryGetValue<string>(out var text))
                {
                    if (DateTime.TryParse(text, out var dt)) return dt.Date;
                }
            }
            return null;
        }

        static int ReadRetryAttempts(JsonObject payload)
        {
            if (payload.TryGetPropertyValue("result", out var resultNode) && resultNode is JsonObject resultObj)
            {
                if (resultObj.TryGetPropertyValue("retry", out var retryNode) && retryNode is JsonObject retryObj)
                {
                    if (retryObj.TryGetPropertyValue("attempts", out var node) && node is JsonValue val && val.TryGetValue<int>(out var i)) return i;
                }
            }
            return 0;
        }

        static int ReadRetryMaxAttempts(JsonObject? schedule)
        {
            if (schedule != null && schedule.TryGetPropertyValue("retry", out var retryNode) && retryNode is JsonObject retryObj)
            {
                if (retryObj.TryGetPropertyValue("maxAttempts", out var node) && node is JsonValue val && val.TryGetValue<int>(out var i)) return i;
            }
            return 3;
        }

        static int ReadRetryIntervalMinutes(JsonObject? schedule)
        {
            if (schedule != null && schedule.TryGetPropertyValue("retry", out var retryNode) && retryNode is JsonObject retryObj)
            {
                if (retryObj.TryGetPropertyValue("intervalMinutes", out var node) && node is JsonValue val && val.TryGetValue<int>(out var i)) return i;
            }
            return 10;
        }
    }

    private async Task SendMoneytreeFailureEmailAsync(
        string companyCode,
        string errorMessage,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        int attempts,
        int maxAttempts,
        CancellationToken ct)
    {
        if (_email is null) return;
        try
        {
            var subject = $"[Moneytree Sync Failed] {companyCode} retries exhausted";
            var body = string.Join("\n", new[]
            {
                $"Company: {companyCode}",
                $"DateRange: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd}",
                $"Attempts: {attempts}/{maxAttempts}",
                $"Error: {errorMessage}"
            });
            await _email.SendAsync("ryuhou@your-partner.co.jp", subject, body, false, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TaskScheduler] Failed to send Moneytree failure email for {Company}", companyCode);
        }
    }

    private static decimal SumAmounts(IReadOnlyList<object> sheet)
    {
        decimal total = 0m;
        foreach (var item in sheet)
        {
            var prop = item.GetType().GetProperty("amount");
            if (prop?.GetValue(item) is decimal val)
                total += val;
        }
        return total;
    }

    private static string ReadString(JsonObject payload, string key)
    {
        if (payload.TryGetPropertyValue(key, out var node) && node is JsonValue val && val.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str))
        {
            return str;
        }
        return string.Empty;
    }

    private static List<string> ReadNotes(JsonObject payload)
    {
        var list = new List<string>();
        if (payload.TryGetPropertyValue("notes", out var node) && node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Marks the task as completed (or waiting for review) and schedules the next run if applicable.
    /// </summary>
    private async Task CompleteTaskAsync(Guid taskId, JsonObject payload, DateTimeOffset? nextRunAt, DateTimeOffset finishedAt, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE scheduler_tasks
SET payload=$2::jsonb,
    next_run_at=$3,
    last_run_at=$4,
    locked_by=NULL,
    locked_at=NULL,
    updated_at=now()
WHERE id=$1";
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        if (nextRunAt.HasValue) cmd.Parameters.AddWithValue(nextRunAt.Value.UtcDateTime);
        else cmd.Parameters.AddWithValue(DBNull.Value);
        cmd.Parameters.AddWithValue(finishedAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Marks the task as failed and releases the lock.
    /// </summary>
    private async Task FailTaskAsync(Guid taskId, JsonObject payload, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE scheduler_tasks
SET payload=$2::jsonb,
    next_run_at=NULL,
    last_run_at=now(),
    locked_by=NULL,
    locked_at=NULL,
    updated_at=now()
WHERE id=$1";
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(payload.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Represents a locked scheduler task being processed by this worker instance.
    /// </summary>
    private sealed record SchedulerTaskRecord(Guid Id, string CompanyCode, JsonObject Payload, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt);

    /// <summary>
    /// Represents the scheduler's view of a task execution result, including next-run info.
    /// </summary>
    private sealed record TaskExecutionOutcome(string FinalStatus, JsonObject Result, DateTimeOffset? NextRunAt, bool HasFailures)
    {
        /// <summary>
        /// Helper for creating a failure outcome with a simple error payload.
        /// </summary>
        public static TaskExecutionOutcome Failure(string message, JsonObject plan)
        {
            var obj = new JsonObject
            {
                ["error"] = message,
                ["plan"] = plan
            };
            return new TaskExecutionOutcome("failed", obj, null, true);
        }
    }
}


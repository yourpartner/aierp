using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Modules;
using static Server.Modules.MoneytreeImportService;

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
    private readonly MoneytreeImportService? _moneytreeImport;
    private readonly ILogger<TaskSchedulerService>? _logger;
    private readonly string _workerId;

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ShortDelay = TimeSpan.FromSeconds(8);

    public TaskSchedulerService(
        NpgsqlDataSource ds,
        LawDatasetService law,
        EmailService? email = null,
        MoneytreeImportService? moneytreeImport = null,
        ILogger<TaskSchedulerService>? logger = null)
    {
        _ds = ds;
        _law = law;
        _email = email;
        _moneytreeImport = moneytreeImport;
        _logger = logger;
        _workerId = $"tasksched-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Entry point for the background worker: continuously fetches due tasks and processes them.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
                var exec = await HrPayrollModule.ExecutePayrollInternal(_ds, _law, task.CompanyCode, empId, month, policyId, false, ct);
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
    /// Executes Moneytree bank sync: downloads CSV from Moneytree and imports to database.
    /// Supports dynamic date range and retry logic.
    /// </summary>
    private async Task<TaskExecutionOutcome> ExecuteMoneytreeSyncAsync(SchedulerTaskRecord task, JsonObject plan, JsonObject? schedule, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (_moneytreeImport is null)
        {
            return TaskExecutionOutcome.Failure("Moneytree 导入服务未配置", plan);
        }

        // Read retry state from payload
        var payload = task.Payload;
        int currentAttempt = 1;
        if (payload.TryGetPropertyValue("_retryState", out var retryNode) && retryNode is JsonObject retryState)
        {
            if (retryState.TryGetPropertyValue("attempt", out var attemptNode) && attemptNode is JsonValue av && av.TryGetValue<int>(out var a))
                currentAttempt = a;
        }

        var (maxAttempts, retryInterval) = SchedulerPlanHelper.GetRetrySettings(schedule);
        _logger?.LogInformation("[TaskScheduler] Moneytree sync starting: company={Company}, attempt={Attempt}/{Max}",
            task.CompanyCode, currentAttempt, maxAttempts);

        // Calculate date range
        DateTimeOffset startDate;
        DateTimeOffset endDate = DateTimeOffset.UtcNow.Date;
        
        var dateRangeKind = "dynamic";
        if (plan.TryGetPropertyValue("dateRange", out var drNode) && drNode is JsonObject dateRange)
        {
            if (dateRange.TryGetPropertyValue("kind", out var kindNode) && kindNode is JsonValue kv && kv.TryGetValue<string>(out var kind))
                dateRangeKind = kind;
        }

        if (dateRangeKind == "dynamic")
        {
            // Get last successful sync date from database
            var lastSuccessDate = await GetLastSuccessfulSyncDateAsync(task.CompanyCode, ct);
            startDate = lastSuccessDate ?? endDate.AddMonths(-3); // Default 3 months if no previous sync
            _logger?.LogInformation("[TaskScheduler] Moneytree sync date range: {Start} to {End} (last success: {Last})",
                startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"), lastSuccessDate?.ToString("yyyy-MM-dd") ?? "none");
        }
        else
        {
            // Fixed date range (fallback)
            startDate = endDate.AddMonths(-1);
        }

        try
        {
            var importResult = await _moneytreeImport.ImportAsync(
                task.CompanyCode,
                new MoneytreeImportService.MoneytreeImportRequest(
                    null, // OTP secret from config
                    startDate,
                    endDate,
                    MoneytreeImportService.ImportMode.Normal),
                "scheduler",
                ct);

            var finishedAt = DateTimeOffset.UtcNow;
            var summary = new JsonObject
            {
                ["dateRange"] = new JsonObject
                {
                    ["start"] = startDate.ToString("yyyy-MM-dd"),
                    ["end"] = endDate.ToString("yyyy-MM-dd")
                },
                ["batchId"] = importResult.BatchId.ToString(),
                ["totalRows"] = importResult.TotalRows,
                ["insertedRows"] = importResult.InsertedRows,
                ["skippedRows"] = importResult.SkippedRows,
                ["linkedRows"] = importResult.LinkedRows,
                ["attempt"] = currentAttempt,
                ["startedAt"] = startedAt.ToString("O"),
                ["finishedAt"] = finishedAt.ToString("O"),
                ["durationMs"] = (finishedAt - startedAt).TotalMilliseconds
            };

            var result = new JsonObject { ["summary"] = summary };
            
            // Record success date for next dynamic range calculation
            await RecordSyncSuccessAsync(task.CompanyCode, endDate, ct);
            
            // Clear retry state on success
            payload.Remove("_retryState");

            var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
            if (nextRun.HasValue)
            {
                result["nextRunPreview"] = nextRun.Value.ToString("O");
            }

            _logger?.LogInformation("[TaskScheduler] Moneytree sync success: company={Company}, inserted={Inserted}, skipped={Skipped}",
                task.CompanyCode, importResult.InsertedRows, importResult.SkippedRows);

            return new TaskExecutionOutcome("pending", result, nextRun, false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TaskScheduler] Moneytree sync failed: company={Company}, attempt={Attempt}/{Max}",
                task.CompanyCode, currentAttempt, maxAttempts);

            var errorResult = new JsonObject
            {
                ["error"] = ex.Message,
                ["attempt"] = currentAttempt,
                ["maxAttempts"] = maxAttempts,
                ["dateRange"] = new JsonObject
                {
                    ["start"] = startDate.ToString("yyyy-MM-dd"),
                    ["end"] = endDate.ToString("yyyy-MM-dd")
                }
            };

            if (currentAttempt < maxAttempts)
            {
                // Schedule retry
                var retryTime = DateTimeOffset.UtcNow.AddMinutes(retryInterval);
                errorResult["nextRetryAt"] = retryTime.ToString("O");
                errorResult["status"] = "retrying";

                // Save retry state
                payload["_retryState"] = new JsonObject
                {
                    ["attempt"] = currentAttempt + 1,
                    ["lastError"] = ex.Message,
                    ["lastAttemptAt"] = DateTimeOffset.UtcNow.ToString("O")
                };

                _logger?.LogInformation("[TaskScheduler] Moneytree sync will retry in {Minutes} minutes (attempt {Next}/{Max})",
                    retryInterval, currentAttempt + 1, maxAttempts);

                return new TaskExecutionOutcome("pending", errorResult, retryTime, true);
            }
            else
            {
                // Max retries reached - send alert
                errorResult["status"] = "max_retries_reached";
                await SendSyncFailureAlertAsync(task.CompanyCode, currentAttempt, ex.Message, ct);
                
                // Clear retry state and schedule next regular run
                payload.Remove("_retryState");
                var nextRun = SchedulerPlanHelper.ComputeNextOccurrence(schedule, DateTimeOffset.UtcNow);
                
                _logger?.LogError("[TaskScheduler] Moneytree sync failed after {Max} attempts, alert sent", maxAttempts);

                return new TaskExecutionOutcome("failed", errorResult, nextRun, true);
            }
        }
    }

    /// <summary>
    /// Get the last successful sync date for a company.
    /// </summary>
    private async Task<DateTimeOffset?> GetLastSuccessfulSyncDateAsync(string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MAX(transaction_date)
            FROM moneytree_transactions
            WHERE company_code = $1 AND transaction_date IS NOT NULL";
        cmd.Parameters.AddWithValue(companyCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is DateTime dt)
        {
            return new DateTimeOffset(dt, TimeSpan.Zero);
        }
        return null;
    }

    /// <summary>
    /// Record successful sync for future date range calculation.
    /// </summary>
    private async Task RecordSyncSuccessAsync(string companyCode, DateTimeOffset syncDate, CancellationToken ct)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO system_settings (company_code, key, value, updated_at)
                VALUES ($1, 'moneytree.last_sync_date', $2, now())
                ON CONFLICT (company_code, key) DO UPDATE SET value = $2, updated_at = now()";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(syncDate.ToString("yyyy-MM-dd"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TaskScheduler] Failed to record sync success date");
        }
    }

    /// <summary>
    /// Send alert when sync fails after max retries.
    /// </summary>
    private async Task SendSyncFailureAlertAsync(string companyCode, int attempts, string errorMessage, CancellationToken ct)
    {
        try
        {
            // 1. Record alert in database
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO system_alerts (company_code, alert_type, severity, title, message, metadata, created_at)
                VALUES ($1, 'moneytree_sync_failure', 'critical', $2, $3, $4::jsonb, now())";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue($"银行明细同步失败 ({companyCode})");
            cmd.Parameters.AddWithValue($"Moneytree 银行明细自动同步在重试 {attempts} 次后仍然失败。\n\n错误信息: {errorMessage}");
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(new { companyCode, attempts, error = errorMessage }));
            await cmd.ExecuteNonQueryAsync(ct);

            // 2. Send email to admins
            if (_email is not null)
            {
                var adminEmails = await GetAdminEmailsAsync(companyCode, ct);
                if (adminEmails.Count > 0)
                {
                    var subject = $"[警报] {companyCode} 银行明细同步失败";
                    var body = $"""
                        会社コード: {companyCode}
                        警報タイプ: 銀行明細同期失敗
                        重要度: 重大
                        
                        Moneytree銀行明細の自動同期が{attempts}回のリトライ後も失敗しました。
                        
                        エラー内容:
                        {errorMessage}
                        
                        対応が必要な場合は、システム管理者にお問い合わせください。
                        
                        ---
                        このメールは自動送信されています。
                        """;
                    foreach (var email in adminEmails)
                    {
                        try { await _email.SendAsync(email, subject, body, false, ct); }
                        catch (Exception emailEx)
                        {
                            _logger?.LogWarning(emailEx, "[TaskScheduler] Failed to send alert email to {Email}", email);
                        }
                    }
                }
            }

            _logger?.LogInformation("[TaskScheduler] Sync failure alert recorded and notified for {Company}", companyCode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[TaskScheduler] Failed to send sync failure alert for {Company}", companyCode);
        }
    }

    /// <summary>
    /// Get admin emails for a company.
    /// </summary>
    private async Task<List<string>> GetAdminEmailsAsync(string companyCode, CancellationToken ct)
    {
        var list = new List<string>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT COALESCE(e.payload->>'contact.email','') AS email
            FROM users u
            JOIN user_roles ur ON ur.user_id=u.id
            JOIN roles r ON r.id=ur.role_id AND LOWER(r.role_code)=LOWER('ADMIN')
            LEFT JOIN employees e ON e.company_code=u.company_code AND e.payload->>'code'=u.employee_code
            WHERE u.company_code=$1";
        cmd.Parameters.AddWithValue(companyCode);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var email = rd.IsDBNull(0) ? "" : rd.GetString(0);
            if (!string.IsNullOrWhiteSpace(email)) list.Add(email);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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


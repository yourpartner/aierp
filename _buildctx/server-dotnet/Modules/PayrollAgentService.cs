using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// AI Agent service that autonomously decides when to trigger payroll calculations.
/// The agent monitors data readiness, business rules, and deadlines to make intelligent
/// decisions about payroll timing.
/// 
/// This is a BackgroundService (Singleton) that uses IServiceScopeFactory to create
/// scoped services for each evaluation cycle.
/// </summary>
public sealed class PayrollAgentService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PayrollAgentService>? _logger;

    // Check every 30 minutes during business hours
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    public PayrollAgentService(
        NpgsqlDataSource ds,
        IServiceScopeFactory scopeFactory,
        ILogger<PayrollAgentService>? logger = null)
    {
        _ds = ds;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("[PayrollAgent] Service starting, waiting for initialization...");
        
        // Wait a bit for other services to start
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        
        _logger?.LogInformation("[PayrollAgent] Service started, beginning evaluation cycles every {Interval} minutes", 
            CheckInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAndDecideAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PayrollAgent] Error during evaluation cycle");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
        
        _logger?.LogInformation("[PayrollAgent] Service stopped");
    }

    /// <summary>
    /// Main decision loop: evaluate conditions and decide whether to trigger payroll.
    /// </summary>
    private async Task EvaluateAndDecideAsync(CancellationToken ct)
    {
        _logger?.LogDebug("[PayrollAgent] Starting evaluation cycle at {Time}", DateTimeOffset.UtcNow);
        
        var companies = await GetActiveCompaniesAsync(ct);
        _logger?.LogDebug("[PayrollAgent] Found {Count} active companies to evaluate", companies.Count);

        foreach (var companyCode in companies)
        {
            try
            {
                // Create a new scope for each company to get fresh scoped services
                await using var scope = _scopeFactory.CreateAsyncScope();
                var preflight = scope.ServiceProvider.GetRequiredService<PayrollPreflightService>();
                
                var decision = await EvaluateCompanyAsync(companyCode, preflight, ct);
                
                if (decision.ShouldCalculate)
                {
                    _logger?.LogInformation(
                        "[PayrollAgent] Decided to trigger payroll for {Company} month {Month}. Reason: {Reason}",
                        companyCode, decision.Month, decision.Reason);

                    await TriggerPayrollCalculationAsync(companyCode, decision, ct);
                }
                else if (!string.IsNullOrEmpty(decision.Reason))
                {
                    _logger?.LogDebug(
                        "[PayrollAgent] Not triggering payroll for {Company}. Reason: {Reason}",
                        companyCode, decision.Reason);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PayrollAgent] Error evaluating company {Company}", companyCode);
            }
        }
        
        _logger?.LogDebug("[PayrollAgent] Completed evaluation cycle");
    }

    /// <summary>
    /// Evaluates whether a company's payroll should be calculated now.
    /// This is the core "AI decision" logic.
    /// </summary>
    private async Task<PayrollDecision> EvaluateCompanyAsync(
        string companyCode, 
        PayrollPreflightService preflight,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var currentMonth = now.ToString("yyyy-MM");

        // 1. Check if payroll already completed this month
        if (await IsPayrollCompletedAsync(companyCode, currentMonth, ct))
        {
            return PayrollDecision.NoAction("当月工资已计算完成");
        }

        // 2. Check company's payroll settings
        var settings = await GetPayrollSettingsAsync(companyCode, ct);

        // 3. Check if we're in the calculation window
        var (inWindow, windowReason) = IsInCalculationWindow(now, settings);
        if (!inWindow)
        {
            return PayrollDecision.NoAction(windowReason);
        }

        // 4. Check data readiness using preflight service
        var preflightResult = await preflight.RunPreflightChecksAsync(companyCode, null, currentMonth, null, ct);
        
        _logger?.LogDebug("[PayrollAgent] Preflight result for {Company}: CanProceed={CanProceed}, Passed={Passed}, Failed={Failed}",
            companyCode, preflightResult.CanProceed, preflightResult.PassedEmployees, preflightResult.FailedEmployees);
        
        if (!preflightResult.CanProceed)
        {
            // Check if we're past the deadline - force calculation anyway with warnings
            if (IsPastDeadline(now, settings))
            {
                _logger?.LogWarning("[PayrollAgent] Past deadline for {Company}, forcing calculation with {IssueCount} issues",
                    companyCode, preflightResult.GlobalWarnings.Count);
                    
                return new PayrollDecision(
                    true,
                    currentMonth,
                    $"已过截止日期，强制计算（存在问题：{preflightResult.GlobalWarnings.Count}项）",
                    preflightResult.GlobalWarnings.ToList(),
                    true);
            }

            var issues = string.Join("; ", preflightResult.GlobalWarnings.Take(3));
            return PayrollDecision.NoAction($"数据未就绪: {issues}");
        }

        // 5. All conditions met - decide based on timing strategy
        var reason = DetermineCalculationReason(now, settings);
        
        return new PayrollDecision(true, currentMonth, reason, new List<string>(), false);
    }

    /// <summary>
    /// Determines if we're in an appropriate time window for payroll calculation.
    /// 
    /// Timeline (working backwards from pay day):
    /// - Pay Day: e.g., 25th
    /// - FB submission to bank: at least 3 business days before pay day → ~20th
    /// - Manager approval deadline: at least 3 business days before FB → ~15th  
    /// - Calculation deadline: before manager approval → ~14th or earlier
    /// </summary>
    private static (bool InWindow, string Reason) IsInCalculationWindow(DateTimeOffset now, PayrollSettings settings)
    {
        var localTime = now.ToOffset(TimeSpan.FromHours(9)); // JST
        var dayOfMonth = localTime.Day;
        var hour = localTime.Hour;

        // Can start calculation after attendance data is typically available (after 1st)
        if (dayOfMonth < settings.EarliestCalculationDay)
        {
            return (false, $"等待考勤数据确定（{dayOfMonth}日），{settings.EarliestCalculationDay}日后开始检查");
        }

        // Prefer business hours (9-20)
        if (hour < 9 || hour > 20)
        {
            return (false, $"非工作时间（{hour}时），等待工作时间");
        }

        // Prefer weekdays, but calculate on weekends if urgent
        if (localTime.DayOfWeek == DayOfWeek.Saturday || localTime.DayOfWeek == DayOfWeek.Sunday)
        {
            // Calculate anyway if approaching calculation deadline (need buffer for manager approval + FB)
            if (dayOfMonth >= settings.CalculationDeadlineDay)
            {
                return (true, "周末但已接近计算截止日，需尽快完成");
            }
            return (false, "周末，等待工作日");
        }

        return (true, "在计算窗口内");
    }

    /// <summary>
    /// Checks if we're past the payroll calculation deadline.
    /// This is NOT the pay day, but the deadline for completing calculation
    /// to allow time for manager approval and FB submission.
    /// </summary>
    private static bool IsPastDeadline(DateTimeOffset now, PayrollSettings settings)
    {
        var localTime = now.ToOffset(TimeSpan.FromHours(9)); // JST
        return localTime.Day > settings.CalculationDeadlineDay;
    }

    /// <summary>
    /// Determines the reason for triggering calculation based on timing.
    /// </summary>
    private static string DetermineCalculationReason(DateTimeOffset now, PayrollSettings settings)
    {
        var localTime = now.ToOffset(TimeSpan.FromHours(9));
        var dayOfMonth = localTime.Day;

        // Calculate days remaining until calculation deadline
        var daysUntilDeadline = settings.CalculationDeadlineDay - dayOfMonth;

        if (dayOfMonth >= settings.CalculationDeadlineDay)
        {
            return $"已到达计算截止日（{settings.CalculationDeadlineDay}日），需立即计算以确保管理者有足够时间确认";
        }
        
        if (dayOfMonth >= settings.PreferredCalculationDay)
        {
            return $"已到达建议计算日（{settings.PreferredCalculationDay}日），数据已就绪，" +
                   $"距离截止还有{daysUntilDeadline}天";
        }

        return $"所有前置条件已满足，提前完成计算。距离截止还有{daysUntilDeadline}天，" +
               "为管理者确认和FB提交预留充足时间";
    }

    /// <summary>
    /// Triggers the actual payroll calculation and creates review tasks.
    /// Uses fault-tolerant approach: skips employees with errors and continues with others.
    /// </summary>
    private async Task TriggerPayrollCalculationAsync(string companyCode, PayrollDecision decision, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid();
        
        _logger?.LogInformation(
            "[PayrollAgent] Starting payroll calculation for {Company} month {Month}. SessionId: {SessionId}",
            companyCode, decision.Month, sessionId);

        try
        {
            // Create a scope to get scoped services
            await using var scope = _scopeFactory.CreateAsyncScope();
            var payrollService = scope.ServiceProvider.GetRequiredService<PayrollService>();
            var payrollTaskService = scope.ServiceProvider.GetRequiredService<PayrollTaskService>();

            // 1. Get all employees that need payroll calculation
            var allEmployeeIds = await payrollService.ResolveEmployeeIdsAsync(companyCode, null, ct);
            if (allEmployeeIds.Count == 0)
            {
                _logger?.LogWarning("[PayrollAgent] No employees found for {Company}, skipping", companyCode);
                return;
            }

            _logger?.LogInformation("[PayrollAgent] Found {Count} employees for {Company}", allEmployeeIds.Count, companyCode);

            // 2. Execute payroll calculation per employee (fault-tolerant)
            var successfulEntries = new List<PayrollService.PayrollPreviewEntry>();
            var failedEmployees = new List<(Guid EmployeeId, string? EmployeeCode, string Reason)>();
            Guid? policyId = null;

            foreach (var employeeId in allEmployeeIds)
            {
                try
                {
                    var preview = await payrollService.ManualRunAsync(
                        companyCode,
                        new List<Guid> { employeeId },
                        decision.Month,
                        policyId: policyId,
                        debug: false,
                        ct);

                    policyId ??= preview.PolicyId;

                    if (preview.Entries.Count > 0)
                    {
                        successfulEntries.AddRange(preview.Entries);
                    }
                }
                catch (PayrollExecutionException pex)
                {
                    // Record specific payroll errors (e.g., missing timesheet for hourly workers)
                    string? empCode = null;
                    string reason = pex.Message;
                    
                    if (pex.Payload is JsonDocument jdoc)
                    {
                        if (jdoc.RootElement.TryGetProperty("employeeCode", out var ec))
                            empCode = ec.GetString();
                        if (jdoc.RootElement.TryGetProperty("error", out var err))
                            reason = err.GetString() ?? pex.Message;
                    }
                    else if (pex.Payload is { } payload)
                    {
                        // Try to extract from anonymous object
                        var payloadJson = JsonSerializer.Serialize(payload);
                        using var doc = JsonDocument.Parse(payloadJson);
                        if (doc.RootElement.TryGetProperty("employeeCode", out var ec))
                            empCode = ec.GetString();
                        if (doc.RootElement.TryGetProperty("error", out var err))
                            reason = err.GetString() ?? pex.Message;
                    }
                    
                    failedEmployees.Add((employeeId, empCode, reason));
                    _logger?.LogWarning("[PayrollAgent] Skipping employee {EmployeeId} ({Code}): {Reason}",
                        employeeId, empCode ?? "unknown", reason);
                }
                catch (Exception ex)
                {
                    failedEmployees.Add((employeeId, null, ex.Message));
                    _logger?.LogWarning(ex, "[PayrollAgent] Skipping employee {EmployeeId} due to error", employeeId);
                }
            }

            if (successfulEntries.Count == 0)
            {
                _logger?.LogWarning("[PayrollAgent] No payroll entries generated for {Company}. Failed: {FailedCount}",
                    companyCode, failedEmployees.Count);
                
                // Create alert tasks for failed employees if any
                if (failedEmployees.Count > 0)
                {
                    await CreateFailureAlertTasksAsync(companyCode, decision.Month, failedEmployees, payrollTaskService, sessionId, ct);
                }
                return;
            }

            _logger?.LogInformation("[PayrollAgent] Calculated {SuccessCount} entries for {Company} (skipped {FailedCount})",
                successfulEntries.Count, companyCode, failedEmployees.Count);

            // 3. Convert preview entries to save entries
            var saveEntries = successfulEntries.Select(e => new PayrollService.PayrollManualSaveEntry
            {
                EmployeeId = e.EmployeeId,
                EmployeeCode = e.EmployeeCode,
                EmployeeName = e.EmployeeName,
                DepartmentCode = e.DepartmentCode,
                TotalAmount = e.TotalAmount,
                PayrollSheet = JsonDocument.Parse(e.PayrollSheet.ToJsonString()).RootElement,
                AccountingDraft = JsonDocument.Parse(e.AccountingDraft.ToJsonString()).RootElement,
                DiffSummary = e.DiffSummary is null ? null : JsonDocument.Parse(e.DiffSummary.ToJsonString()).RootElement,
                Trace = e.Trace is null ? null : JsonDocument.Parse(e.Trace.ToJsonString()).RootElement,
                Metadata = JsonDocument.Parse(new JsonObject
                {
                    ["triggeredBy"] = "PayrollAgent",
                    ["reason"] = decision.Reason,
                    ["forcedDueToDeadline"] = decision.ForcedDueToDeadline,
                    ["skippedEmployees"] = failedEmployees.Count
                }.ToJsonString()).RootElement
            }).ToList();

            // 4. Create system user context for saving
            var systemUser = new Auth.UserCtx(
                UserId: "system-payroll-agent",
                Roles: new[] { "admin" },
                Caps: new[] { "payroll:write" },
                DeptId: null,
                EmployeeCode: null,
                UserName: "Payroll Agent",
                CompanyCode: companyCode);

            // 5. Save payroll results
            var saveRequest = new PayrollService.PayrollManualSaveRequest
            {
                Month = decision.Month,
                PolicyId = policyId,
                Overwrite = true,
                RunType = "agent",
                Entries = saveEntries
            };

            var saveResult = await payrollService.SaveManualAsync(companyCode, systemUser, saveRequest, ct);

            _logger?.LogInformation("[PayrollAgent] Saved payroll run {RunId} with {Count} entries for {Company}",
                saveResult.RunId, saveResult.EntryCount, companyCode);

            // 6. Get default reviewer for this company
            var reviewerId = await GetPayrollReviewerAsync(companyCode, ct);

            // 7. Create review tasks for successful entries
            var taskCandidates = successfulEntries.Select(e =>
            {
                saveResult.EntryIdsByEmployeeId.TryGetValue(e.EmployeeId, out var entryId);
                return new PayrollTaskService.PayrollTaskCandidate(
                    EntryId: entryId,
                    EmployeeId: e.EmployeeId,
                    EmployeeCode: e.EmployeeCode,
                    EmployeeName: e.EmployeeName,
                    PeriodMonth: decision.Month,
                    TotalAmount: e.TotalAmount,
                    DiffSummary: e.DiffSummary,
                    Summary: $"[Agent] {decision.Reason}",
                    NeedsConfirmation: true,
                    IsAnomaly: false,
                    IsCritical: false,
                    AnomalyReason: null);
            }).ToList();

            var tasksCreated = await payrollTaskService.CreateTasksAsync(
                sessionId,
                companyCode,
                saveResult.RunId,
                targetUserId: reviewerId,
                taskCandidates,
                ct);

            // 8. Create alert tasks for failed employees
            if (failedEmployees.Count > 0)
            {
                await CreateFailureAlertTasksAsync(companyCode, decision.Month, failedEmployees, payrollTaskService, sessionId, ct);
            }

            _logger?.LogInformation(
                "[PayrollAgent] Completed payroll for {Company} month {Month}. RunId: {RunId}, Tasks: {TaskCount}, Skipped: {SkippedCount}",
                companyCode, decision.Month, saveResult.RunId, tasksCreated, failedEmployees.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PayrollAgent] Failed to execute payroll for {Company} month {Month}",
                companyCode, decision.Month);
            throw;
        }
    }

    /// <summary>
    /// Creates alert tasks for employees whose payroll calculation failed.
    /// </summary>
    private async Task CreateFailureAlertTasksAsync(
        string companyCode,
        string month,
        List<(Guid EmployeeId, string? EmployeeCode, string Reason)> failedEmployees,
        PayrollTaskService payrollTaskService,
        Guid sessionId,
        CancellationToken ct)
    {
        var reviewerId = await GetPayrollReviewerAsync(companyCode, ct);
        
        var alertCandidates = failedEmployees.Select(f => new PayrollTaskService.PayrollTaskCandidate(
            EntryId: Guid.Empty, // No entry for failed employees
            EmployeeId: f.EmployeeId,
            EmployeeCode: f.EmployeeCode,
            EmployeeName: f.EmployeeCode, // May not have name
            PeriodMonth: month,
            TotalAmount: 0,
            DiffSummary: null,
            Summary: $"[要対応] 給与計算失敗: {f.Reason}",
            NeedsConfirmation: true,
            IsAnomaly: true,
            IsCritical: true,
            AnomalyReason: f.Reason)).ToList();

        if (alertCandidates.Any())
        {
            await payrollTaskService.CreateTasksAsync(
                sessionId,
                companyCode,
                runId: Guid.Empty, // No run associated with failed employees
                targetUserId: reviewerId,
                alertCandidates,
                ct);
            
            _logger?.LogInformation("[PayrollAgent] Created {AlertCount} alert tasks for failed employees in {Company}",
                alertCandidates.Count, companyCode);
        }
    }

    /// <summary>
    /// Gets the default payroll reviewer for a company.
    /// Auto-discovers users with appropriate roles/permissions.
    /// Priority: hr_admin > payroll:approve > admin
    /// </summary>
    private async Task<string?> GetPayrollReviewerAsync(string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = """
            SELECT user_id FROM users 
            WHERE company_code = $1 
              AND (
                payload->'caps' @> '["payroll:approve"]'::jsonb
                OR payload->'roles' @> '["admin"]'::jsonb
                OR payload->'roles' @> '["hr_admin"]'::jsonb
              )
            ORDER BY 
              CASE WHEN payload->'roles' @> '["hr_admin"]'::jsonb THEN 1
                   WHEN payload->'caps' @> '["payroll:approve"]'::jsonb THEN 2
                   ELSE 3 END
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        var result = await cmd.ExecuteScalarAsync(ct);
        
        if (result is string userId && !string.IsNullOrWhiteSpace(userId))
        {
            _logger?.LogDebug("[PayrollAgent] Auto-discovered reviewer {UserId} for {Company}", userId, companyCode);
            return userId;
        }

        // No authorized reviewer found - log error
        _logger?.LogError("[PayrollAgent] No authorized reviewer (hr_admin/payroll:approve/admin) found for company {Company}. Payroll tasks will be orphaned!", companyCode);
        return null;
    }

    /// <summary>
    /// Gets all active companies that might need payroll calculation.
    /// </summary>
    private async Task<List<string>> GetActiveCompaniesAsync(CancellationToken ct)
    {
        var companies = new List<string>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT company_code 
            FROM employees 
            WHERE COALESCE(payload->>'status', 'active') <> 'inactive'
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            companies.Add(reader.GetString(0));
        }
        return companies;
    }

    /// <summary>
    /// Checks if payroll has already been completed for the given month.
    /// Returns true only if ALL active employees have payroll entries for the month.
    /// </summary>
    private async Task<bool> IsPayrollCompletedAsync(string companyCode, string month, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // Get count of active employees
        int activeEmployeeCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM employees 
                WHERE company_code = $1 
                  AND COALESCE(payload->>'status', 'active') <> 'inactive'
                  AND COALESCE(payload->>'employment_type', '') NOT IN ('contractor', '個人事業主')
                  AND (payload->>'hire_date' IS NULL OR (payload->>'hire_date')::date <= ($2 || '-01')::date + interval '1 month' - interval '1 day')
                  AND (payload->>'leave_date' IS NULL OR (payload->>'leave_date')::date >= ($2 || '-01')::date)
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(month);
            activeEmployeeCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }

        if (activeEmployeeCount == 0)
        {
            return true; // No employees to calculate
        }

        // Get count of employees with payroll entries for this month
        int calculatedCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(DISTINCT pe.employee_id) 
                FROM payroll_entries pe
                JOIN payroll_runs pr ON pe.run_id = pr.id
                WHERE pr.company_code = $1 
                  AND pr.period_month = $2
                  AND pr.run_type IN ('manual', 'agent', 'auto')
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(month);
            calculatedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }

        var isComplete = calculatedCount >= activeEmployeeCount;
        
        if (!isComplete)
        {
            _logger?.LogDebug("[PayrollAgent] Payroll incomplete for {Company} {Month}: {Calculated}/{Total} employees",
                companyCode, month, calculatedCount, activeEmployeeCount);
        }

        return isComplete;
    }

    /// <summary>
    /// Gets company-specific payroll settings.
    /// </summary>
    private async Task<PayrollSettings> GetPayrollSettingsAsync(string companyCode, CancellationToken ct)
    {
        // Try to load from company settings, fall back to defaults
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT payload FROM companies WHERE company_code = $1 LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string json)
        {
            try
            {
                var payload = JsonNode.Parse(json)?.AsObject();
                var payrollSettings = payload?["payrollSettings"]?.AsObject();
                if (payrollSettings is not null)
                {
                    var payDay = payrollSettings["payDay"]?.GetValue<int>() ?? 25;
                    return new PayrollSettings(
                        EarliestCalculationDay: payrollSettings["earliestDay"]?.GetValue<int>() ?? 5,
                        PreferredCalculationDay: payrollSettings["preferredDay"]?.GetValue<int>() ?? 10,
                        CalculationDeadlineDay: payrollSettings["calculationDeadlineDay"]?.GetValue<int>() ?? CalculateDeadline(payDay, 9),
                        ManagerApprovalDeadlineDay: payrollSettings["managerApprovalDeadlineDay"]?.GetValue<int>() ?? CalculateDeadline(payDay, 5),
                        FBSubmissionDeadlineDay: payrollSettings["fbSubmissionDeadlineDay"]?.GetValue<int>() ?? CalculateDeadline(payDay, 3),
                        PayDay: payDay
                    );
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[PayrollAgent] Failed to parse payroll settings for {Company}, using defaults", companyCode);
            }
        }

        // Default settings for Japanese companies (pay day = 25th)
        // Timeline: Calculate by 14th → Manager approve by 17th → FB submit by 20th → Pay on 25th
        return new PayrollSettings(
            EarliestCalculationDay: 5,        // Can start after 5th (attendance data available)
            PreferredCalculationDay: 10,      // Prefer to start around 10th
            CalculationDeadlineDay: 14,       // Must complete calculation by 14th
            ManagerApprovalDeadlineDay: 17,   // Manager must approve by 17th
            FBSubmissionDeadlineDay: 20,      // FB must be submitted by 20th (3 business days before pay)
            PayDay: 25                        // Salary payment date
        );
    }

    /// <summary>
    /// Calculates a deadline date by subtracting approximate business days from pay day.
    /// This is a simple approximation; in production, use a proper business day calendar.
    /// </summary>
    private static int CalculateDeadline(int payDay, int businessDaysBefore)
    {
        // Rough approximation: 1 business day ≈ 1.4 calendar days
        var calendarDays = (int)Math.Ceiling(businessDaysBefore * 1.4);
        var deadline = payDay - calendarDays;
        return Math.Max(1, deadline);
    }

    /// <summary>
    /// Company payroll timing settings.
    /// 
    /// Timeline example for pay day = 25th:
    /// - EarliestCalculationDay: 5th (after attendance data available)
    /// - PreferredCalculationDay: 10th (ideal start date)
    /// - CalculationDeadlineDay: 14th (must complete by this date)
    /// - ManagerApprovalDeadlineDay: 17th (manager must approve by this date)
    /// - FBSubmissionDeadlineDay: 20th (FB file must be submitted to bank)
    /// - PayDay: 25th (salary payment date)
    /// </summary>
    private sealed record PayrollSettings(
        int EarliestCalculationDay,
        int PreferredCalculationDay,
        int CalculationDeadlineDay,
        int ManagerApprovalDeadlineDay,
        int FBSubmissionDeadlineDay,
        int PayDay);

    /// <summary>
    /// Represents the agent's decision about whether to calculate payroll.
    /// </summary>
    private sealed record PayrollDecision(
        bool ShouldCalculate,
        string Month,
        string Reason,
        List<string> Warnings,
        bool ForcedDueToDeadline)
    {
        public static PayrollDecision NoAction(string reason) =>
            new(false, "", reason, new List<string>(), false);
    }
}

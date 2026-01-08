using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
    /// Triggers the actual payroll calculation by creating an AI task.
    /// </summary>
    private async Task TriggerPayrollCalculationAsync(string companyCode, PayrollDecision decision, CancellationToken ct)
    {
        // Create an AI task for review
        var taskId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // Record the agent's decision
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_payroll_tasks (
                id, company_code, session_id, task_type, status, 
                period_month, summary, metadata, created_at, updated_at
            ) VALUES (
                $1, $2, $3, 'agent_triggered', 'pending',
                $4, $5, $6::jsonb, now(), now()
            );
            """;
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(decision.Month);
        cmd.Parameters.AddWithValue($"[Agent] {decision.Reason}");
        
        var metadata = new JsonObject
        {
            ["triggeredBy"] = "PayrollAgent",
            ["reason"] = decision.Reason,
            ["warnings"] = decision.Warnings.Count > 0 
                ? new JsonArray(decision.Warnings.Select(w => JsonValue.Create(w)).ToArray())
                : null,
            ["forcedDueToDeadline"] = decision.ForcedDueToDeadline,
            ["triggeredAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        cmd.Parameters.AddWithValue(metadata.ToJsonString());

        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogInformation(
            "[PayrollAgent] Created payroll task {TaskId} for {Company} month {Month}. SessionId: {SessionId}",
            taskId, companyCode, decision.Month, sessionId);

        // Optionally: trigger immediate calculation here if needed
        // For now, the task will be picked up by the task processor or manual trigger
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
    /// </summary>
    private async Task<bool> IsPayrollCompletedAsync(string companyCode, string month, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM payroll_runs 
            WHERE company_code = $1 
              AND period_month = $2 
              AND status = 'completed'
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        return await cmd.ExecuteScalarAsync(ct) is not null;
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

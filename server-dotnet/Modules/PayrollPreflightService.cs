using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// Service responsible for checking preconditions before payroll calculation.
/// Validates attendance data, employee salary configurations, and other prerequisites.
/// </summary>
public sealed class PayrollPreflightService
{
    private readonly NpgsqlDataSource _ds;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PayrollPreflightService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    /// <summary>
    /// Result of a single preflight check item.
    /// </summary>
    public sealed record PreflightCheckItem(
        string CheckId,
        string CheckName,
        bool Passed,
        string? Message,
        JsonObject? Details);

    /// <summary>
    /// Aggregated result of all preflight checks for an employee.
    /// </summary>
    public sealed record EmployeePreflightResult(
        Guid EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        bool AllPassed,
        IReadOnlyList<PreflightCheckItem> Checks);

    /// <summary>
    /// Overall preflight result for a payroll run.
    /// </summary>
    public sealed record PreflightResult(
        string Month,
        bool CanProceed,
        int TotalEmployees,
        int PassedEmployees,
        int FailedEmployees,
        IReadOnlyList<EmployeePreflightResult> EmployeeResults,
        IReadOnlyList<string> GlobalWarnings);

    /// <summary>
    /// Threshold configuration for preflight checks.
    /// </summary>
    public sealed record PreflightConfig(
        int MinTimesheetDays = 15,
        bool RequireBaseSalary = true,
        bool RequireSocialInsuranceBase = false,
        bool RequireConfirmedTimesheet = false);

    /// <summary>
    /// Runs all preflight checks for the specified employees and month.
    /// </summary>
    /// <param name="companyCode">Company identifier.</param>
    /// <param name="employeeIds">List of employee IDs to check. If empty, checks all active employees.</param>
    /// <param name="month">Target month in yyyy-MM format.</param>
    /// <param name="config">Optional configuration for check thresholds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PreflightResult> RunPreflightChecksAsync(
        string companyCode,
        IReadOnlyCollection<Guid>? employeeIds,
        string month,
        PreflightConfig? config,
        CancellationToken ct)
    {
        config ??= new PreflightConfig();
        var globalWarnings = new List<string>();

        // Resolve employee list
        var employees = await LoadEmployeesAsync(companyCode, employeeIds, ct);
        if (employees.Count == 0)
        {
            globalWarnings.Add("対象従業員が見つかりません。");
            return new PreflightResult(month, false, 0, 0, 0, Array.Empty<EmployeePreflightResult>(), globalWarnings);
        }

        // Parse month range
        if (!TryParseMonth(month, out var monthStart, out var monthEnd))
        {
            globalWarnings.Add($"無効な月形式: {month}");
            return new PreflightResult(month, false, employees.Count, 0, employees.Count,
                Array.Empty<EmployeePreflightResult>(), globalWarnings);
        }

        // Load timesheet data for the month
        var timesheetData = await LoadTimesheetDataAsync(companyCode, employees.Select(e => e.Id).ToList(), monthStart, monthEnd, ct);

        // Run checks for each employee
        var employeeResults = new List<EmployeePreflightResult>();
        int passed = 0, failed = 0;

        foreach (var emp in employees)
        {
            var checks = new List<PreflightCheckItem>();

            // Check 1: Base salary configuration
            var baseSalaryCheck = CheckBaseSalary(emp, config);
            checks.Add(baseSalaryCheck);

            // Check 2: Social insurance base (if required)
            if (config.RequireSocialInsuranceBase)
            {
                var socialInsCheck = CheckSocialInsuranceBase(emp);
                checks.Add(socialInsCheck);
            }

            // Check 3: Timesheet data availability
            timesheetData.TryGetValue(emp.Id, out var empTimesheets);
            var timesheetCheck = CheckTimesheetData(emp, empTimesheets, monthStart, monthEnd, config);
            checks.Add(timesheetCheck);

            // Check 4: Timesheet confirmation status (if required)
            if (config.RequireConfirmedTimesheet)
            {
                var confirmCheck = CheckTimesheetConfirmation(emp, empTimesheets);
                checks.Add(confirmCheck);
            }

            // Check 5: Department assignment
            var deptCheck = CheckDepartmentAssignment(emp);
            checks.Add(deptCheck);

            var allPassed = checks.All(c => c.Passed);
            employeeResults.Add(new EmployeePreflightResult(
                emp.Id,
                emp.Code,
                emp.Name,
                allPassed,
                checks));

            if (allPassed) passed++;
            else failed++;
        }

        // Add global warnings based on overall status
        if (failed > 0 && passed == 0)
        {
            globalWarnings.Add("すべての従業員で前提条件チェックに失敗しました。給与計算を実行できません。");
        }
        else if (failed > 0)
        {
            globalWarnings.Add($"{failed}名の従業員で前提条件に問題があります。個別対応が必要です。");
        }

        var canProceed = passed > 0;
        return new PreflightResult(month, canProceed, employees.Count, passed, failed, employeeResults, globalWarnings);
    }

    /// <summary>
    /// Quick check to determine if payroll can be calculated for the given month.
    /// Returns a summary without detailed per-employee results.
    /// </summary>
    public async Task<(bool CanProceed, int ReadyCount, int NotReadyCount, List<string> Issues)> QuickCheckAsync(
        string companyCode,
        string month,
        CancellationToken ct)
    {
        var issues = new List<string>();
        var config = new PreflightConfig();

        // Get active employee count
        var employees = await LoadEmployeesAsync(companyCode, null, ct);
        if (employees.Count == 0)
        {
            issues.Add("対象従業員がいません");
            return (false, 0, 0, issues);
        }

        if (!TryParseMonth(month, out var monthStart, out var monthEnd))
        {
            issues.Add($"無効な月形式: {month}");
            return (false, 0, employees.Count, issues);
        }

        // Check timesheet coverage
        var timesheetCoverage = await GetTimesheetCoverageAsync(companyCode, employees.Select(e => e.Id).ToList(), monthStart, monthEnd, ct);

        int readyCount = 0, notReadyCount = 0;

        foreach (var emp in employees)
        {
            var hasBaseSalary = emp.BaseSalaryMonth > 0;
            timesheetCoverage.TryGetValue(emp.Id, out var tsDays);
            var hasTimesheets = tsDays >= config.MinTimesheetDays;

            if (hasBaseSalary && hasTimesheets)
            {
                readyCount++;
            }
            else
            {
                notReadyCount++;
                if (!hasBaseSalary)
                {
                    issues.Add($"{emp.Name ?? emp.Code ?? emp.Id.ToString()}: 基本給未設定");
                }
                if (!hasTimesheets)
                {
                    issues.Add($"{emp.Name ?? emp.Code ?? emp.Id.ToString()}: 勤怠データ不足 ({tsDays}日/{config.MinTimesheetDays}日)");
                }
            }
        }

        // Limit issues for summary
        if (issues.Count > 10)
        {
            var remaining = issues.Count - 10;
            issues = issues.Take(10).ToList();
            issues.Add($"...他 {remaining} 件の問題");
        }

        return (readyCount > 0, readyCount, notReadyCount, issues);
    }

    #region Check Methods

    private static PreflightCheckItem CheckBaseSalary(EmployeeInfo emp, PreflightConfig config)
    {
        var hasBaseSalary = emp.BaseSalaryMonth > 0;
        var passed = !config.RequireBaseSalary || hasBaseSalary;

        var details = new JsonObject
        {
            ["baseSalaryMonth"] = emp.BaseSalaryMonth,
            ["configured"] = hasBaseSalary
        };

        return new PreflightCheckItem(
            "base_salary",
            "基本給設定",
            passed,
            passed ? null : "基本給が設定されていません。従業員マスタで設定してください。",
            details);
    }

    private static PreflightCheckItem CheckSocialInsuranceBase(EmployeeInfo emp)
    {
        var hasHealthBase = emp.HealthBase > 0;
        var hasPensionBase = emp.PensionBase > 0;
        var passed = hasHealthBase && hasPensionBase;

        var details = new JsonObject
        {
            ["healthBase"] = emp.HealthBase,
            ["pensionBase"] = emp.PensionBase
        };

        string? message = null;
        if (!passed)
        {
            var missing = new List<string>();
            if (!hasHealthBase) missing.Add("健康保険標準報酬月額");
            if (!hasPensionBase) missing.Add("厚生年金標準報酬月額");
            message = $"{string.Join("、", missing)}が未設定です。";
        }

        return new PreflightCheckItem(
            "social_insurance_base",
            "社会保険基数",
            passed,
            message,
            details);
    }

    private static PreflightCheckItem CheckTimesheetData(
        EmployeeInfo emp,
        List<TimesheetEntry>? timesheets,
        DateOnly monthStart,
        DateOnly monthEnd,
        PreflightConfig config)
    {
        var dayCount = timesheets?.Select(t => t.Date).Distinct().Count() ?? 0;
        var totalDaysInMonth = monthEnd.DayNumber - monthStart.DayNumber + 1;
        var passed = dayCount >= config.MinTimesheetDays;

        var details = new JsonObject
        {
            ["recordedDays"] = dayCount,
            ["requiredDays"] = config.MinTimesheetDays,
            ["totalDaysInMonth"] = totalDaysInMonth,
            ["coverage"] = totalDaysInMonth > 0 ? Math.Round((decimal)dayCount / totalDaysInMonth * 100, 1) : 0
        };

        return new PreflightCheckItem(
            "timesheet_data",
            "勤怠データ",
            passed,
            passed ? null : $"勤怠データが不足しています。{dayCount}日/{config.MinTimesheetDays}日以上必要です。",
            details);
    }

    private static PreflightCheckItem CheckTimesheetConfirmation(EmployeeInfo emp, List<TimesheetEntry>? timesheets)
    {
        var totalCount = timesheets?.Count ?? 0;
        var confirmedCount = timesheets?.Count(t => t.IsConfirmed) ?? 0;
        var passed = totalCount > 0 && confirmedCount == totalCount;

        var details = new JsonObject
        {
            ["totalEntries"] = totalCount,
            ["confirmedEntries"] = confirmedCount
        };

        string? message = null;
        if (!passed)
        {
            if (totalCount == 0)
            {
                message = "勤怠データがありません。";
            }
            else
            {
                var unconfirmed = totalCount - confirmedCount;
                message = $"{unconfirmed}件の勤怠データが未確認です。";
            }
        }

        return new PreflightCheckItem(
            "timesheet_confirmation",
            "勤怠確認状況",
            passed,
            message,
            details);
    }

    private static PreflightCheckItem CheckDepartmentAssignment(EmployeeInfo emp)
    {
        var hasDept = !string.IsNullOrWhiteSpace(emp.DepartmentCode);

        var details = new JsonObject
        {
            ["departmentCode"] = emp.DepartmentCode
        };

        // Department is optional but recommended
        return new PreflightCheckItem(
            "department",
            "部門設定",
            true, // Always pass, but provide warning in message
            hasDept ? null : "部門が未設定です（任意）。仕訳の部門配賦が行われません。",
            details);
    }

    #endregion

    #region Data Loading

    private sealed record EmployeeInfo(
        Guid Id,
        string? Code,
        string? Name,
        string? DepartmentCode,
        decimal BaseSalaryMonth,
        decimal HealthBase,
        decimal PensionBase,
        string? Status);

    private sealed record TimesheetEntry(DateOnly Date, decimal Hours, string? Status, bool IsConfirmed);

    private async Task<List<EmployeeInfo>> LoadEmployeesAsync(string companyCode, IReadOnlyCollection<Guid>? employeeIds, CancellationToken ct)
    {
        var list = new List<EmployeeInfo>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sql = "SELECT id, employee_code, payload FROM employees WHERE company_code = $1";
        if (employeeIds is { Count: > 0 })
        {
            sql += " AND id = ANY($2)";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(employeeIds.ToArray());
        }
        else
        {
            sql += " AND COALESCE(payload->>'status', 'active') <> 'inactive'";
            cmd.Parameters.AddWithValue(companyCode);
        }
        sql += " ORDER BY employee_code";

        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var code = reader.IsDBNull(1) ? null : reader.GetString(1);
            var payloadJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2);

            using var doc = JsonDocument.Parse(payloadJson);
            var payload = doc.RootElement;

            var name = GetJsonString(payload, "nameKanji") ?? GetJsonString(payload, "name");
            var deptCode = GetJsonString(payload, "departmentCode");
            var baseSalary = GetJsonDecimal(payload, "baseSalaryMonth");
            var healthBase = GetJsonDecimal(payload, "healthBase");
            var pensionBase = GetJsonDecimal(payload, "pensionBase");
            var status = GetJsonString(payload, "status") ?? "active";

            list.Add(new EmployeeInfo(id, code, name, deptCode, baseSalary, healthBase, pensionBase, status));
        }

        return list;
    }

    private async Task<Dictionary<Guid, List<TimesheetEntry>>> LoadTimesheetDataAsync(
        string companyCode,
        List<Guid> employeeIds,
        DateOnly monthStart,
        DateOnly monthEnd,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<TimesheetEntry>>();
        if (employeeIds.Count == 0) return result;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT payload->>'employeeId' as emp_id, timesheet_date, payload->>'hours' as hours, status
            FROM timesheets
            WHERE company_code = $1
              AND timesheet_date >= $2
              AND timesheet_date <= $3";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(monthStart);
        cmd.Parameters.AddWithValue(monthEnd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var empIdStr = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(empIdStr) || !Guid.TryParse(empIdStr, out var empId))
                continue;

            if (!employeeIds.Contains(empId))
                continue;

            var date = reader.IsDBNull(1) ? DateOnly.MinValue : DateOnly.FromDateTime(reader.GetDateTime(1));
            var hoursStr = reader.IsDBNull(2) ? "0" : reader.GetString(2);
            decimal.TryParse(hoursStr, out var hours);
            var status = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isConfirmed = status is "confirmed" or "approved" or "locked" or "submitted";

            if (!result.ContainsKey(empId))
                result[empId] = new List<TimesheetEntry>();

            result[empId].Add(new TimesheetEntry(date, hours, status, isConfirmed));
        }

        return result;
    }

    private async Task<Dictionary<Guid, int>> GetTimesheetCoverageAsync(
        string companyCode,
        List<Guid> employeeIds,
        DateOnly monthStart,
        DateOnly monthEnd,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var id in employeeIds)
            result[id] = 0;

        if (employeeIds.Count == 0) return result;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT payload->>'employeeId' as emp_id, COUNT(DISTINCT timesheet_date) as days
            FROM timesheets
            WHERE company_code = $1
              AND timesheet_date >= $2
              AND timesheet_date <= $3
            GROUP BY payload->>'employeeId'";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(monthStart);
        cmd.Parameters.AddWithValue(monthEnd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var empIdStr = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(empIdStr) || !Guid.TryParse(empIdStr, out var empId))
                continue;

            var days = reader.GetInt32(1);
            result[empId] = days;
        }

        return result;
    }

    #endregion

    #region Helpers

    private static bool TryParseMonth(string month, out DateOnly start, out DateOnly end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(month)) return false;

        // Support yyyy-MM and yyyy/MM formats
        var normalized = month.Replace("/", "-");
        if (normalized.Length == 7 && DateTime.TryParseExact(normalized + "-01", "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
        {
            start = DateOnly.FromDateTime(dt);
            end = start.AddMonths(1).AddDays(-1);
            return true;
        }

        return false;
    }

    private static string? GetJsonString(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var node)) return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }

    private static decimal GetJsonDecimal(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0m;
        if (!el.TryGetProperty(prop, out var node)) return 0m;
        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetDecimal(out var d)) return d;
            if (node.TryGetDouble(out var dbl)) return Convert.ToDecimal(dbl);
        }
        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString();
            if (!string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, out var parsed))
                return parsed;
        }
        return 0m;
    }

    #endregion
}


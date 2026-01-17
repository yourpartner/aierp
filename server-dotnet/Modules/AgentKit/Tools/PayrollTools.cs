using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;
using Server.Modules;

namespace Server.Modules.AgentKit.Tools;

/// <summary>
/// 工资计算前置检查
/// </summary>
public sealed class PreflightCheckTool : AgentToolBase
{
    private readonly PayrollPreflightService _preflight;

    public PreflightCheckTool(PayrollPreflightService preflight, ILogger<PreflightCheckTool> logger) : base(logger)
    {
        _preflight = preflight;
    }

    public override string Name => "preflight_check";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "month") ?? GetString(args, "period_month");
        if (string.IsNullOrWhiteSpace(month))
        {
            return ErrorResult(Localize(context.Language, "month が必要です", "month 必填"));
        }

        List<Guid>? employeeIds = null;
        if (args.TryGetProperty("employee_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            employeeIds = new List<Guid>();
            foreach (var id in idsEl.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g))
                {
                    employeeIds.Add(g);
                }
            }
        }

        PayrollPreflightService.PreflightConfig? config = null;
        if (args.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object)
        {
            var minDays = cfgEl.TryGetProperty("minTimesheetDays", out var md) && md.TryGetInt32(out var v) ? v : 15;
            var requireBaseSalary = cfgEl.TryGetProperty("requireBaseSalary", out var rb) && rb.ValueKind == JsonValueKind.True;
            var requireSocial = cfgEl.TryGetProperty("requireSocialInsuranceBase", out var rs) && rs.ValueKind == JsonValueKind.True;
            var requireConfirmed = cfgEl.TryGetProperty("requireConfirmedTimesheet", out var rc) && rc.ValueKind == JsonValueKind.True;
            config = new PayrollPreflightService.PreflightConfig(minDays, requireBaseSalary, requireSocial, requireConfirmed);
        }

        var result = await _preflight.RunPreflightChecksAsync(context.CompanyCode, employeeIds, month.Trim(), config, ct);
        return SuccessResult(result);
    }
}

/// <summary>
/// 工资计算预览
/// </summary>
public sealed class CalculatePayrollTool : AgentToolBase
{
    private readonly PayrollService _payroll;

    public CalculatePayrollTool(PayrollService payroll, ILogger<CalculatePayrollTool> logger) : base(logger)
    {
        _payroll = payroll;
    }

    public override string Name => "calculate_payroll";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "month") ?? GetString(args, "period_month");
        if (string.IsNullOrWhiteSpace(month))
        {
            return ErrorResult(Localize(context.Language, "month が必要です", "month 必填"));
        }

        Guid? policyId = null;
        if (args.TryGetProperty("policy_id", out var pidEl) && pidEl.ValueKind == JsonValueKind.String && Guid.TryParse(pidEl.GetString(), out var pid))
        {
            policyId = pid;
        }

        var debug = args.TryGetProperty("debug", out var dbgEl) && dbgEl.ValueKind == JsonValueKind.True;

        IReadOnlyCollection<Guid>? employeeIds = null;
        if (args.TryGetProperty("employee_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Guid>();
            foreach (var id in idsEl.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g))
                {
                    list.Add(g);
                }
            }
            employeeIds = list;
        }

        var resolvedIds = await _payroll.ResolveEmployeeIdsAsync(context.CompanyCode, employeeIds, ct);
        if (resolvedIds.Count == 0)
        {
            return ErrorResult(Localize(context.Language, "対象従業員がいません", "未找到员工"));
        }

        var result = await _payroll.ManualRunAsync(context.CompanyCode, resolvedIds, month.Trim(), policyId, debug, ct);
        return SuccessResult(result);
    }
}

/// <summary>
/// 工资结果保存
/// </summary>
public sealed class SavePayrollTool : AgentToolBase
{
    private readonly PayrollService _payroll;

    public SavePayrollTool(PayrollService payroll, ILogger<SavePayrollTool> logger) : base(logger)
    {
        _payroll = payroll;
    }

    public override string Name => "save_payroll";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "month") ?? GetString(args, "period_month");
        if (string.IsNullOrWhiteSpace(month))
        {
            return ErrorResult(Localize(context.Language, "month が必要です", "month 必填"));
        }

        var overwrite = args.TryGetProperty("overwrite", out var ow) && ow.ValueKind == JsonValueKind.True;

        var entries = new List<PayrollService.PayrollManualSaveEntry>();
        if (args.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entriesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var entry = new PayrollService.PayrollManualSaveEntry
                {
                    EmployeeId = item.TryGetProperty("employeeId", out var idEl) && Guid.TryParse(idEl.GetString(), out var id) ? id : Guid.Empty,
                    EmployeeCode = item.TryGetProperty("employeeCode", out var codeEl) ? codeEl.GetString() : null,
                    EmployeeName = item.TryGetProperty("employeeName", out var nameEl) ? nameEl.GetString() : null,
                    DepartmentCode = item.TryGetProperty("departmentCode", out var deptEl) ? deptEl.GetString() : null,
                    TotalAmount = item.TryGetProperty("totalAmount", out var taEl) && taEl.TryGetDecimal(out var ta) ? ta : 0m,
                    PayrollSheet = item.TryGetProperty("payrollSheet", out var psEl) ? psEl : default,
                    AccountingDraft = item.TryGetProperty("accountingDraft", out var adEl) ? adEl : default,
                    DiffSummary = item.TryGetProperty("diffSummary", out var dsEl) ? dsEl : null,
                    Trace = item.TryGetProperty("trace", out var trEl) ? trEl : null,
                    Metadata = item.TryGetProperty("metadata", out var mdEl) ? mdEl : null
                };
                if (entry.EmployeeId != Guid.Empty)
                {
                    entries.Add(entry);
                }
            }
        }

        if (entries.Count == 0)
        {
            return ErrorResult(Localize(context.Language, "entries が必要です", "entries 必填"));
        }

        var request = new PayrollService.PayrollManualSaveRequest
        {
            Month = month.Trim(),
            Overwrite = overwrite,
            RunType = "manual",
            Entries = entries
        };

        var result = await _payroll.SaveManualAsync(context.CompanyCode, context.UserCtx, request, ct);
        return SuccessResult(result);
    }
}

/// <summary>
/// 工资历史查询（按月）
/// </summary>
public sealed class GetPayrollHistoryTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public GetPayrollHistoryTool(NpgsqlDataSource ds, ILogger<GetPayrollHistoryTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "get_payroll_history";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "month");
        var limit = GetInt(args, "limit") ?? 10;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, period_month, run_type, status, total_amount, created_at
FROM payroll_runs
WHERE company_code = $1
  AND ($2::text IS NULL OR period_month = $2)
ORDER BY period_month DESC, created_at DESC
LIMIT $3";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(month) ? (object)DBNull.Value : month!.Trim());
        cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 50));

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                runId = reader.GetGuid(0),
                periodMonth = reader.GetString(1),
                runType = reader.GetString(2),
                status = reader.GetString(3),
                totalAmount = reader.GetDecimal(4),
                createdAt = reader.GetFieldValue<DateTimeOffset>(5)
            });
        }

        return SuccessResult(new { count = items.Count, items });
    }
}

/// <summary>
/// 员工自助工资查询
/// </summary>
public sealed class GetMyPayrollTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public GetMyPayrollTool(NpgsqlDataSource ds, ILogger<GetMyPayrollTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "get_my_payroll";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "year_month") ?? GetString(args, "month");

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var empCmd = conn.CreateCommand();
        empCmd.CommandText = "SELECT id, employee_code, payload FROM employees WHERE company_code=$1 AND payload->>'userId' = $2 LIMIT 1";
        empCmd.Parameters.AddWithValue(context.CompanyCode);
        empCmd.Parameters.AddWithValue(context.UserCtx.UserId ?? string.Empty);
        Guid? employeeId = null;
        string? employeeCode = null;
        string? employeeName = null;
        await using (var reader = await empCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                employeeId = reader.GetGuid(0);
                employeeCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                var payload = reader.IsDBNull(2) ? "{}" : reader.GetString(2);
                using var doc = JsonDocument.Parse(payload);
                employeeName = doc.RootElement.TryGetProperty("nameKanji", out var n) ? n.GetString() : null;
            }
        }

        if (!employeeId.HasValue)
        {
            return ErrorResult(Localize(context.Language, "従業員が見つかりません", "未找到员工"));
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.id, r.period_month, e.total_amount, e.payroll_sheet, e.accounting_draft
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
WHERE r.company_code = $1
  AND e.employee_id = $2
  AND ($3::text IS NULL OR r.period_month = $3)
ORDER BY r.period_month DESC, e.created_at DESC
LIMIT 1";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(employeeId.Value);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(month) ? (object)DBNull.Value : month!.Trim());

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct))
        {
            return SuccessResult(new { found = false, employeeCode, employeeName });
        }

        var payrollSheet = rd.IsDBNull(3) ? null : JsonNode.Parse(rd.GetString(3));
        var accountingDraft = rd.IsDBNull(4) ? null : JsonNode.Parse(rd.GetString(4));

        return SuccessResult(new
        {
            found = true,
            employeeCode,
            employeeName,
            periodMonth = rd.GetString(1),
            totalAmount = rd.GetDecimal(2),
            payrollSheet,
            accountingDraft
        });
    }
}

/// <summary>
/// 工资对比（按月汇总）
/// </summary>
public sealed class GetPayrollComparisonTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public GetPayrollComparisonTool(NpgsqlDataSource ds, ILogger<GetPayrollComparisonTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "get_payroll_comparison";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var months = new List<string>();
        if (args.TryGetProperty("months", out var monthsEl) && monthsEl.ValueKind == JsonValueKind.Array)
        {
            months.AddRange(monthsEl.EnumerateArray().Select(m => m.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))!.Select(s => s!.Trim()));
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (months.Count >= 2)
        {
            cmd.CommandText = @"
SELECT period_month, SUM(total_amount) AS total
FROM payroll_runs
WHERE company_code = $1 AND period_month = ANY($2)
GROUP BY period_month";
            cmd.Parameters.AddWithValue(context.CompanyCode);
            cmd.Parameters.AddWithValue(months.ToArray());
        }
        else
        {
            cmd.CommandText = @"
SELECT period_month, SUM(total_amount) AS total
FROM payroll_runs
WHERE company_code = $1
GROUP BY period_month
ORDER BY period_month DESC
LIMIT 2";
            cmd.Parameters.AddWithValue(context.CompanyCode);
        }

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new { periodMonth = reader.GetString(0), totalAmount = reader.GetDecimal(1) });
        }

        return SuccessResult(new { items });
    }
}

/// <summary>
/// 部门汇总
/// </summary>
public sealed class GetDepartmentSummaryTool : AgentToolBase
{
    private readonly NpgsqlDataSource _ds;

    public GetDepartmentSummaryTool(NpgsqlDataSource ds, ILogger<GetDepartmentSummaryTool> logger) : base(logger)
    {
        _ds = ds;
    }

    public override string Name => "get_department_summary";

    public override async Task<AgentKitService.ToolExecutionResult> ExecuteAsync(JsonElement args, AgentKitService.AgentExecutionContext context, CancellationToken ct)
    {
        var month = GetString(args, "month");
        if (string.IsNullOrWhiteSpace(month))
        {
            return ErrorResult(Localize(context.Language, "month が必要です", "month 必填"));
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COALESCE(e.department_code, 'unknown') AS department_code,
       SUM(e.total_amount) AS total_amount,
       COUNT(*) AS entry_count
FROM payroll_run_entries e
JOIN payroll_runs r ON e.run_id = r.id
WHERE r.company_code = $1 AND r.period_month = $2
GROUP BY COALESCE(e.department_code, 'unknown')
ORDER BY total_amount DESC";
        cmd.Parameters.AddWithValue(context.CompanyCode);
        cmd.Parameters.AddWithValue(month.Trim());

        var items = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new
            {
                departmentCode = reader.GetString(0),
                totalAmount = reader.GetDecimal(1),
                entryCount = reader.GetInt32(2)
            });
        }

        return SuccessResult(new { items });
    }
}


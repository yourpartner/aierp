using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// Core service coordinating payroll preview, persistence, voucher linkage, and task creation.
/// </summary>
public sealed class PayrollService
{
    private readonly NpgsqlDataSource _ds;
    private readonly LawDatasetService _law;
    private readonly FinanceService _finance;
    private readonly ILogger<PayrollService>? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PayrollService(NpgsqlDataSource ds, LawDatasetService law, FinanceService finance, ILogger<PayrollService>? logger = null)
    {
        _ds = ds;
        _law = law;
        _finance = finance;
        _logger = logger;
    }

    /// <summary>
    /// Payload returned by the preview endpoint for each employee, exposing sheet/draft/warnings/trace.
    /// </summary>
    public sealed record PayrollPreviewEntry(
        Guid EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        string? DepartmentCode,
        string? DepartmentName,
        decimal TotalAmount,
        JsonArray PayrollSheet,
        JsonArray AccountingDraft,
        JsonObject? DiffSummary,
        JsonArray? Trace,
        JsonObject? WorkHours,
        JsonArray? Warnings);

    /// <summary>
    /// Envelope returned by the manual run endpoint summarizing month/policy/entries.
    /// </summary>
    public sealed record PayrollManualRunResult(
        string Month,
        Guid? PolicyId,
        bool HasExisting,
        IReadOnlyList<Guid> ExistingEmployeeIds,
        IReadOnlyList<PayrollPreviewEntry> Entries);

    /// <summary>
    /// Executes the payroll engine for each requested employee and produces a preview payload
    /// (payroll sheet, accounting draft, trace, diffs) without persisting anything.
    /// </summary>
    /// <param name="companyCode">Tenant identifier supplied via x-company-code.</param>
    /// <param name="employeeIds">Employees that should be calculated.</param>
    /// <param name="month">Target month in yyyy-MM format.</param>
    /// <param name="policyId">Optional payroll policy override.</param>
    /// <param name="debug">Whether to capture rule trace info.</param>
    /// <param name="manualWorkHours">Optional manual work hours for hourly-rate employees.</param>
    /// <param name="ct">Cancellation token propagated from HTTP pipeline.</param>
    public async Task<PayrollManualRunResult> ManualRunAsync(
        string companyCode,
        IReadOnlyCollection<Guid> employeeIds,
        string month,
        Guid? policyId,
        bool debug,
        Dictionary<string, ManualWorkHoursInput>? manualWorkHours,
        CancellationToken ct)
    {
        var entries = new List<PayrollPreviewEntry>();
        foreach (var employeeId in employeeIds.Distinct())
        {
            // 检查是否有该员工的手动工时输入
            ManualWorkHoursInput? manualHours = null;
            if (manualWorkHours != null)
            {
                manualWorkHours.TryGetValue(employeeId.ToString(), out manualHours);
            }
            
            var exec = await HrPayrollModule.ExecutePayrollInternal(_ds, _law, companyCode, employeeId, month, policyId, debug, manualHours, ct);
            var diff = await BuildDiffSummaryAsync(companyCode, employeeId, policyId, month, exec.NetAmount, ct);
            var traceNode = exec.Trace is null ? null : exec.Trace.DeepClone().AsArray();

            entries.Add(new PayrollPreviewEntry(
                employeeId,
                exec.EmployeeCode,
                exec.EmployeeName,
                exec.DepartmentCode,
                exec.DepartmentName,
                exec.NetAmount,
                CloneArray(exec.PayrollSheet),
                CloneArray(exec.AccountingDraft),
                diff,
                traceNode,
                exec.WorkHours is null ? null : exec.WorkHours.DeepClone().AsObject(),
                exec.Warnings is null ? null : exec.Warnings.DeepClone().AsArray()));
        }

        var existingEmployeeIds = await GetExistingEmployeeIdsAsync(companyCode, policyId, month, employeeIds, ct);

        return new PayrollManualRunResult(
            Month: month,
            PolicyId: policyId,
            HasExisting: existingEmployeeIds.Count > 0,
            ExistingEmployeeIds: existingEmployeeIds,
            Entries: entries);
    }
    
    /// <summary>
    /// Overload without manual work hours for backward compatibility.
    /// </summary>
    public Task<PayrollManualRunResult> ManualRunAsync(
        string companyCode,
        IReadOnlyCollection<Guid> employeeIds,
        string month,
        Guid? policyId,
        bool debug,
        CancellationToken ct)
        => ManualRunAsync(companyCode, employeeIds, month, policyId, debug, null, ct);

    /// <summary>
    /// Resolves the list of employees that should be processed for an automatic payroll run.
    /// When <paramref name="employeeIds"/> is null/empty the method enumerates everybody
    /// under the specified company code.
    /// </summary>
    /// <param name="companyCode">Tenant identifier.</param>
    /// <param name="employeeIds">Optional explicit list of employees to reuse.</param>
    /// <param name="ct">Cancellation token from caller.</param>
    public async Task<IReadOnlyList<Guid>> ResolveEmployeeIdsAsync(string companyCode, IReadOnlyCollection<Guid>? employeeIds, CancellationToken ct)
    {
        if (employeeIds is not null && employeeIds.Count > 0)
        {
            return employeeIds.Where(id => id != Guid.Empty).Distinct().ToList();
        }

        var list = new List<Guid>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM employees WHERE company_code=$1 ORDER BY created_at";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetGuid(0));
            }
        }
        return list;
    }

    /// <summary>
    /// Describes the persisted entry payload when saving a payroll run.
    /// Uses JsonDocument/JsonElement for flexible JSON handling.
    /// </summary>
    public sealed class PayrollManualSaveEntry
    {
        public Guid EmployeeId { get; set; }
        public string? EmployeeCode { get; set; }
        public string? EmployeeName { get; set; }
        public string? DepartmentCode { get; set; }
        public decimal TotalAmount { get; set; }
        public JsonElement PayrollSheet { get; set; }
        public JsonElement AccountingDraft { get; set; }
        public JsonElement? DiffSummary { get; set; }
        public JsonElement? Trace { get; set; }
        public JsonElement? Metadata { get; set; }

        /// <summary>
        /// Gets the raw JSON string for PayrollSheet, handling edge cases.
        /// </summary>
        public string GetPayrollSheetJson()
        {
            if (PayrollSheet.ValueKind == JsonValueKind.Undefined || PayrollSheet.ValueKind == JsonValueKind.Null)
                return "[]";
            return PayrollSheet.GetRawText();
        }

        /// <summary>
        /// Gets the raw JSON string for AccountingDraft, handling edge cases.
        /// </summary>
        public string GetAccountingDraftJson()
        {
            if (AccountingDraft.ValueKind == JsonValueKind.Undefined || AccountingDraft.ValueKind == JsonValueKind.Null)
                return "[]";
            return AccountingDraft.GetRawText();
        }

        /// <summary>
        /// Gets the raw JSON string for DiffSummary, or null if not present.
        /// </summary>
        public string? GetDiffSummaryJson()
        {
            if (!DiffSummary.HasValue || DiffSummary.Value.ValueKind == JsonValueKind.Undefined || DiffSummary.Value.ValueKind == JsonValueKind.Null)
                return null;
            return DiffSummary.Value.GetRawText();
        }

        /// <summary>
        /// Gets the raw JSON string for Trace, or null if not present.
        /// </summary>
        public string? GetTraceJson()
        {
            if (!Trace.HasValue || Trace.Value.ValueKind == JsonValueKind.Undefined || Trace.Value.ValueKind == JsonValueKind.Null)
                return null;
            return Trace.Value.GetRawText();
        }

        /// <summary>
        /// Gets the raw JSON string for Metadata, or null if not present.
        /// </summary>
        public string? GetMetadataJson()
        {
            if (!Metadata.HasValue || Metadata.Value.ValueKind == JsonValueKind.Undefined || Metadata.Value.ValueKind == JsonValueKind.Null)
                return null;
            return Metadata.Value.GetRawText();
        }
    }

    /// <summary>
    /// Request body for persisting payroll runs (manual or auto).
    /// </summary>
    public sealed class PayrollManualSaveRequest
    {
        public string Month { get; set; } = "";
        public Guid? PolicyId { get; set; }
        public bool Overwrite { get; set; }
        public string RunType { get; set; } = "manual";
        public List<PayrollManualSaveEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Response returned after saving payroll results, exposing run id, entry map, and voucher count.
    /// </summary>
    public sealed record PayrollManualSaveResult(Guid RunId, int EntryCount, int VoucherCount, IReadOnlyDictionary<Guid, Guid> EntryIdsByEmployeeId);

    /// <summary>
    /// Internal structure representing the voucher created for a payroll entry.
    /// </summary>
    private sealed record VoucherLink(Guid EmployeeId, Guid VoucherId, string VoucherNo);

    /// <summary>
    /// Persists a payroll run (manual or auto) including payroll sheets, accounting drafts,
    /// trace data, associated vouchers, and metadata. Handles overwrite semantics and wraps
    /// the entire operation inside a database transaction.
    /// </summary>
    /// <param name="companyCode">Tenant identifier.</param>
    /// <param name="userCtx">Authenticated user context used for auditing and voucher creation.</param>
    /// <param name="request">Save payload produced by the preview stage.</param>
    /// <param name="ct">Cancellation token propagated from HTTP layer.</param>
    /// <returns>Run id plus mapping of employeeId → entryId.</returns>
    public async Task<PayrollManualSaveResult> SaveManualAsync(
        string companyCode,
        Auth.UserCtx? userCtx,
        PayrollManualSaveRequest request,
        CancellationToken ct)
    {
        _logger?.LogInformation("[Payroll] SaveManualAsync started: Company={Company}, Month={Month}, RunType={RunType}, EntryCount={EntryCount}, Overwrite={Overwrite}",
            companyCode, request.Month, request.RunType, request.Entries?.Count ?? 0, request.Overwrite);
        
        // Validate request data
        if (request.Entries == null || request.Entries.Count == 0)
        {
            _logger?.LogWarning("[Payroll] Validation failed: entries is required and cannot be empty");
            throw new PayrollExecutionException(StatusCodes.Status400BadRequest, 
                new { error = "entries is required and cannot be empty" }, "validation_error");
        }

        foreach (var entry in request.Entries)
        {
            if (entry.EmployeeId == Guid.Empty)
            {
                _logger?.LogWarning("[Payroll] Validation failed: Invalid employeeId for entry {EmployeeCode}", entry.EmployeeCode);
                throw new PayrollExecutionException(StatusCodes.Status400BadRequest,
                    new { error = $"Invalid employeeId for entry: {entry.EmployeeCode ?? "unknown"}" }, "validation_error");
            }
        }

        var runType = string.IsNullOrWhiteSpace(request.RunType) ? "manual" : request.RunType;
        var hasExisting = await HasExistingRunAsync(companyCode, request.PolicyId, request.Month, runType, ct);
        if (hasExisting && !request.Overwrite)
        {
            _logger?.LogWarning("[Payroll] Conflict: payroll run already exists for {Company}/{Month}/{RunType}", companyCode, request.Month, runType);
            throw new PayrollExecutionException(StatusCodes.Status409Conflict, new { error = "既に同じ月の給与結果が存在します。上書きする場合は overwrite=true を指定してください。" }, "conflict");
        }

        Dictionary<Guid, VoucherLink> voucherLinks;
        try
        {
            var voucherSource = string.Equals(runType, "manual", StringComparison.OrdinalIgnoreCase)
                ? VoucherSource.Manual
                : VoucherSource.Auto;
            var targetUserId = voucherSource == VoucherSource.Auto ? userCtx?.UserId : null;
            _logger?.LogDebug("[Payroll] Creating voucher links for {Count} entries", request.Entries.Count);
            voucherLinks = await CreateVoucherLinksAsync(companyCode, request.Month, request.Entries, userCtx, voucherSource, targetUserId, ct);
            _logger?.LogDebug("[Payroll] Created {Count} voucher links", voucherLinks.Count);
        }
        catch (PayrollExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Payroll] Failed to create voucher links");
            throw new PayrollExecutionException(StatusCodes.Status500InternalServerError,
                new { error = "会計伝票の生成に失敗しました", detail = ex.Message }, "voucher_create_failed");
        }
        var createdVoucherIds = voucherLinks.Count > 0
            ? voucherLinks.Values.Select(v => v.VoucherId).ToArray()
            : Array.Empty<Guid>();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        // Use explicit isolation level for consistency
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        Guid runId = Guid.Empty;
        var entryMap = new Dictionary<Guid, Guid>();
        var entryEmployeeIds = request.Entries
            .Select(e => e.EmployeeId)
            .Distinct()
            .ToArray();

        try
        {
            List<Guid> previousVoucherIds = new();
            if (request.Overwrite && hasExisting)
            {
                runId = await LoadRunIdAsync(conn, tx, companyCode, request.PolicyId, request.Month, runType, ct);
                if (runId == Guid.Empty)
                {
                    throw new PayrollExecutionException(StatusCodes.Status500InternalServerError, new { error = "既存の給与結果が見つかりませんでした" }, "run_not_found");
                }

                previousVoucherIds = await LoadVoucherIdsForEmployeesAsync(conn, tx, companyCode, runId, entryEmployeeIds, ct);
                await using var delEntries = conn.CreateCommand();
                delEntries.Transaction = tx;
                delEntries.CommandText = """
                    DELETE FROM payroll_run_entries
                    WHERE run_id = $1
                      AND employee_id = ANY($2);
                    """;
                delEntries.Parameters.AddWithValue(runId);
                delEntries.Parameters.Add(new NpgsqlParameter
                {
                    Value = entryEmployeeIds,
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
                });
                await delEntries.ExecuteNonQueryAsync(ct);

                if (previousVoucherIds.Count > 0)
                {
                    await DeleteVouchersInternalAsync(conn, tx, companyCode, previousVoucherIds, ct);
                }
            }

            if (runId == Guid.Empty)
            {
                var totalAmount = request.Entries.Sum(e => e.TotalAmount);
                var metadata = new JsonObject
                {
                    ["runType"] = runType,
                    ["createdBy"] = userCtx?.UserId
                };

                await using (var insertRun = conn.CreateCommand())
                {
                    insertRun.Transaction = tx;
                    insertRun.CommandText = """
                        INSERT INTO payroll_runs(id, company_code, policy_id, period_month, run_type, status, total_amount, diff_summary, metadata, created_at, updated_at)
                        VALUES (gen_random_uuid(), $1, $2::uuid, $3, $4, 'completed', $5, NULL, $6::jsonb, now(), now())
                        RETURNING id;
                        """;
                    insertRun.Parameters.AddWithValue(companyCode);
                    insertRun.Parameters.AddWithValue(request.PolicyId.HasValue ? request.PolicyId.Value : (object)DBNull.Value);
                    insertRun.Parameters.AddWithValue(request.Month);
                    insertRun.Parameters.AddWithValue(runType);
                    insertRun.Parameters.AddWithValue(totalAmount);
                    insertRun.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));
                    var obj = await insertRun.ExecuteScalarAsync(ct);
                    runId = obj is Guid g ? g : Guid.Empty;
                }
            }

            if (runId == Guid.Empty)
            {
                throw new PayrollExecutionException(StatusCodes.Status500InternalServerError, new { error = "給与結果の保存に失敗しました" }, "save_failed");
            }

            var accountNameCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            async Task<string?> LookupAccountNameAsync(string code)
            {
                if (string.IsNullOrWhiteSpace(code)) return null;
                if (accountNameCache.TryGetValue(code, out var cached)) return cached;
                await using var nq = conn.CreateCommand();
                nq.Transaction = tx;
                nq.CommandText = "SELECT name FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
                nq.Parameters.AddWithValue(companyCode); nq.Parameters.AddWithValue(code);
                var n = (string?)await nq.ExecuteScalarAsync(ct);
                accountNameCache[code] = n;
                return n;
            }

            foreach (var entry in request.Entries)
            {
                var entryId = Guid.NewGuid();
                voucherLinks.TryGetValue(entry.EmployeeId, out var voucherLink);

                var accountingDraftJson = entry.GetAccountingDraftJson();
                if (!string.IsNullOrWhiteSpace(accountingDraftJson) && accountingDraftJson != "[]")
                {
                    var draftNode = JsonNode.Parse(accountingDraftJson);
                    if (draftNode is JsonArray draftArr)
                    {
                        bool modified = false;
                        foreach (var line in draftArr)
                        {
                            if (line is not JsonObject obj) continue;
                            var code = obj["accountCode"]?.GetValue<string>();
                            var name = obj["accountName"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                            {
                                var resolved = await LookupAccountNameAsync(code);
                                if (!string.IsNullOrWhiteSpace(resolved))
                                {
                                    obj["accountName"] = resolved;
                                    modified = true;
                                }
                            }
                        }
                        if (modified)
                            accountingDraftJson = draftNode.ToJsonString();
                    }
                }

                var payrollSheetJson = entry.GetPayrollSheetJson();
                var diffSummaryJson = entry.GetDiffSummaryJson();
                var metadataJson = entry.GetMetadataJson();
                var traceJson = entry.GetTraceJson();

                await using (var insertEntry = conn.CreateCommand())
                {
                    insertEntry.Transaction = tx;
                    insertEntry.CommandText = """
                        INSERT INTO payroll_run_entries(
                            id, run_id, company_code, employee_id, employee_code, employee_name, department_code,
                            total_amount, payroll_sheet, accounting_draft, diff_summary, metadata, voucher_id, voucher_no, created_at)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10::jsonb, $11::jsonb, $12::jsonb, $13, $14, now());
                        """;
                    insertEntry.Parameters.AddWithValue(entryId);
                    insertEntry.Parameters.AddWithValue(runId);
                    insertEntry.Parameters.AddWithValue(companyCode);
                    insertEntry.Parameters.AddWithValue(entry.EmployeeId);
                    insertEntry.Parameters.AddWithValue(string.IsNullOrWhiteSpace(entry.EmployeeCode) ? DBNull.Value : entry.EmployeeCode);
                    insertEntry.Parameters.AddWithValue(string.IsNullOrWhiteSpace(entry.EmployeeName) ? DBNull.Value : entry.EmployeeName);
                    insertEntry.Parameters.AddWithValue(string.IsNullOrWhiteSpace(entry.DepartmentCode) ? DBNull.Value : entry.DepartmentCode);
                    insertEntry.Parameters.AddWithValue(entry.TotalAmount);
                    insertEntry.Parameters.AddWithValue(payrollSheetJson);
                    insertEntry.Parameters.AddWithValue(accountingDraftJson);
                    insertEntry.Parameters.AddWithValue(diffSummaryJson is null ? (object)DBNull.Value : diffSummaryJson);
                    if (!string.IsNullOrWhiteSpace(metadataJson))
                    {
                        insertEntry.Parameters.AddWithValue(metadataJson);
                    }
                    else
                    {
                        var defaultMetadata = new JsonObject
                        {
                            ["source"] = runType
                        };
                        insertEntry.Parameters.AddWithValue(JsonSerializer.Serialize(defaultMetadata, JsonOptions));
                    }
                    insertEntry.Parameters.AddWithValue(voucherLink is null ? (object)DBNull.Value : voucherLink.VoucherId);
                    insertEntry.Parameters.AddWithValue(voucherLink is null || string.IsNullOrWhiteSpace(voucherLink.VoucherNo) ? (object)DBNull.Value : voucherLink.VoucherNo);
                    await insertEntry.ExecuteNonQueryAsync(ct);
                }

                if (!string.IsNullOrWhiteSpace(traceJson))
                {
                    await using var insertTrace = conn.CreateCommand();
                    insertTrace.Transaction = tx;
                    insertTrace.CommandText = """
                        INSERT INTO payroll_run_traces(id, run_id, entry_id, employee_id, trace, created_at)
                        VALUES (gen_random_uuid(), $1, $2, $3, $4::jsonb, now());
                        """;
                    insertTrace.Parameters.AddWithValue(runId);
                    insertTrace.Parameters.AddWithValue(entryId);
                    insertTrace.Parameters.AddWithValue(entry.EmployeeId);
                    insertTrace.Parameters.AddWithValue(traceJson);
                    await insertTrace.ExecuteNonQueryAsync(ct);
                }
                entryMap[entry.EmployeeId] = entryId;
            }

            await using (var updateRun = conn.CreateCommand())
            {
                updateRun.Transaction = tx;
                updateRun.CommandText = """
                    UPDATE payroll_runs
                    SET total_amount = (
                        SELECT COALESCE(SUM(total_amount), 0)
                        FROM payroll_run_entries
                        WHERE run_id = $1
                    ),
                    updated_at = now()
                    WHERE id = $1;
                    """;
                updateRun.Parameters.AddWithValue(runId);
                await updateRun.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger?.LogInformation("[Payroll] Successfully saved payroll run: RunId={RunId}, EntryCount={EntryCount}, VoucherCount={VoucherCount}",
                runId, request.Entries.Count, voucherLinks.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Payroll] Failed to save payroll run, rolling back transaction");
            await tx.RollbackAsync(ct);
            if (createdVoucherIds.Length > 0)
            {
                _logger?.LogDebug("[Payroll] Cleaning up {Count} created vouchers due to rollback", createdVoucherIds.Length);
                await DeleteVouchersAsync(companyCode, createdVoucherIds, ct);
            }
            throw;
        }

        return new PayrollManualSaveResult(runId, request.Entries.Count, voucherLinks.Count, entryMap);
    }

    /// <summary>
    /// Deep clones a list of JsonObject instances to prevent mutation when returning data to callers.
    /// </summary>
    private static JsonArray CloneArray(IEnumerable<JsonObject> source)
    {
        var arr = new JsonArray();
        foreach (var obj in source)
        {
            arr.Add(obj.DeepClone());
        }
        return arr;
    }

    /// <summary>
    /// Looks up which of the supplied employees already have payroll results for the month/policy.
    /// Used to warn the operator about potential overwrites.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> GetExistingEmployeeIdsAsync(string companyCode, Guid? policyId, string month, IEnumerable<Guid> employeeIds, CancellationToken ct)
    {
        var idList = employeeIds.Distinct().ToArray();
        if (idList.Length == 0) return Array.Empty<Guid>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT pre.employee_id
            FROM payroll_run_entries pre
            JOIN payroll_runs pr ON pre.run_id = pr.id
            WHERE pr.company_code = $1
              AND pr.period_month = $2
              AND pr.run_type = 'manual'
              AND (($3 IS NULL AND pr.policy_id IS NULL) OR pr.policy_id = $3)
              AND pre.employee_id = ANY($4);
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        cmd.Parameters.Add(new NpgsqlParameter { Value = policyId.HasValue ? policyId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(idList);
        var list = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(reader.GetGuid(0));
        }
        return list;
    }

    /// <summary>
    /// Configuration for accuracy evaluation thresholds.
    /// </summary>
    public sealed record AccuracyThresholds(
        decimal PercentThreshold = 0.15m,      // 15% deviation triggers warning
        decimal AmountThreshold = 30000m,       // ¥30,000 absolute deviation triggers warning
        decimal CriticalPercentThreshold = 0.30m, // 30% deviation is critical
        decimal CriticalAmountThreshold = 100000m); // ¥100,000 is critical

    /// <summary>
    /// Extended diff summary with anomaly detection flags.
    /// </summary>
    public sealed record DiffAnalysisResult(
        JsonObject? DiffSummary,
        bool IsAnomaly,
        bool IsCritical,
        string? AnomalyReason);

    /// <summary>
    /// Builds a comprehensive diff summary comparing the current payroll amount against historical data,
    /// including anomaly detection based on configurable thresholds.
    /// </summary>
    private async Task<DiffAnalysisResult> BuildDiffAnalysisAsync(
        string companyCode,
        Guid employeeId,
        Guid? policyId,
        string month,
        decimal currentAmount,
        AccuracyThresholds? thresholds,
        CancellationToken ct)
    {
        thresholds ??= new AccuracyThresholds();

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pr.period_month, pre.total_amount, pre.payroll_sheet
            FROM payroll_run_entries pre
            JOIN payroll_runs pr ON pre.run_id = pr.id
            WHERE pr.company_code = $1
              AND pre.employee_id = $2
              AND (($3 IS NULL AND pr.policy_id IS NULL) OR pr.policy_id = $3)
              AND pr.period_month < $4
            ORDER BY pr.period_month DESC
            LIMIT 6;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = policyId.HasValue ? policyId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(month);

        var samples = new List<(string Month, decimal Amount, JsonDocument? Sheet)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var m = reader.GetString(0);
            var amount = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            JsonDocument? sheet = null;
            if (!reader.IsDBNull(2))
            {
                try { sheet = JsonDocument.Parse(reader.GetString(2)); } catch { }
            }
            samples.Add((m, amount, sheet));
        }

        if (samples.Count == 0)
        {
            // No historical data - first time calculation
            return new DiffAnalysisResult(null, false, false, null);
        }

        // Use last 3 months for average calculation
        var recentSamples = samples.Take(3).ToList();
        var average = recentSamples.Average(x => x.Amount);
        var diff = currentAmount - average;
        decimal? percent = average == 0m ? null : diff / average;

        // Calculate standard deviation for more accurate anomaly detection
        decimal stdDev = 0m;
        if (samples.Count >= 3)
        {
            var variance = samples.Take(3).Average(x => (x.Amount - average) * (x.Amount - average));
            stdDev = (decimal)Math.Sqrt((double)variance);
        }

        // Build samples array
        var arr = new JsonArray();
        foreach (var item in samples)
        {
            var sampleObj = new JsonObject
            {
                ["month"] = item.Month,
                ["amount"] = item.Amount
            };
            item.Sheet?.Dispose();
            arr.Add(sampleObj);
        }

        // Determine anomaly status
        bool isAnomaly = false;
        bool isCritical = false;
        string? anomalyReason = null;

        var absDiff = Math.Abs(diff);
        var absPercent = percent.HasValue ? Math.Abs(percent.Value) : 0m;

        // Check for critical deviation
        if (absPercent >= thresholds.CriticalPercentThreshold || absDiff >= thresholds.CriticalAmountThreshold)
        {
            isAnomaly = true;
            isCritical = true;
            anomalyReason = absPercent >= thresholds.CriticalPercentThreshold
                ? $"重大偏差: {absPercent * 100:F1}% (閾値: {thresholds.CriticalPercentThreshold * 100:F0}%)"
                : $"重大偏差: ¥{absDiff:N0} (閾値: ¥{thresholds.CriticalAmountThreshold:N0})";
        }
        // Check for warning-level deviation
        else if (absPercent >= thresholds.PercentThreshold || absDiff >= thresholds.AmountThreshold)
        {
            isAnomaly = true;
            anomalyReason = absPercent >= thresholds.PercentThreshold
                ? $"偏差注意: {absPercent * 100:F1}% (閾値: {thresholds.PercentThreshold * 100:F0}%)"
                : $"偏差注意: ¥{absDiff:N0} (閾値: ¥{thresholds.AmountThreshold:N0})";
        }
        // Check for statistical outlier (>2 standard deviations)
        else if (stdDev > 0m && absDiff > stdDev * 2)
        {
            isAnomaly = true;
            anomalyReason = $"統計的外れ値: 標準偏差の{absDiff / stdDev:F1}倍";
        }

        var summary = new JsonObject
        {
            ["previousAverage"] = Math.Round(average, 2),
            ["difference"] = Math.Round(diff, 2),
            ["standardDeviation"] = Math.Round(stdDev, 2),
            ["isAnomaly"] = isAnomaly,
            ["isCritical"] = isCritical,
            ["thresholds"] = new JsonObject
            {
                ["percentWarning"] = thresholds.PercentThreshold,
                ["percentCritical"] = thresholds.CriticalPercentThreshold,
                ["amountWarning"] = thresholds.AmountThreshold,
                ["amountCritical"] = thresholds.CriticalAmountThreshold
            }
        };
        if (percent.HasValue) summary["differencePercent"] = Math.Round(percent.Value, 4);
        if (!string.IsNullOrWhiteSpace(anomalyReason)) summary["anomalyReason"] = anomalyReason;
        summary["samples"] = arr;
        summary["sampleCount"] = samples.Count;

        return new DiffAnalysisResult(summary, isAnomaly, isCritical, anomalyReason);
    }

    /// <summary>
    /// Builds a small diff summary comparing the current payroll amount against the previous three months.
    /// </summary>
    private async Task<JsonObject?> BuildDiffSummaryAsync(string companyCode, Guid employeeId, Guid? policyId, string month, decimal currentAmount, CancellationToken ct)
    {
        var analysis = await BuildDiffAnalysisAsync(companyCode, employeeId, policyId, month, currentAmount, null, ct);
        return analysis.DiffSummary;
    }

    /// <summary>
    /// Generates an accuracy report for a batch of payroll calculations.
    /// </summary>
    public async Task<AccuracyReport> GenerateAccuracyReportAsync(
        string companyCode,
        string month,
        IReadOnlyList<PayrollPreviewEntry> entries,
        AccuracyThresholds? thresholds,
        CancellationToken ct)
    {
        thresholds ??= new AccuracyThresholds();
        var employeeAnalyses = new List<EmployeeAccuracyResult>();
        int anomalyCount = 0;
        int criticalCount = 0;
        int normalCount = 0;
        int newEmployeeCount = 0;

        foreach (var entry in entries)
        {
            var analysis = await BuildDiffAnalysisAsync(
                companyCode,
                entry.EmployeeId,
                null, // Use default policy
                month,
                entry.TotalAmount,
                thresholds,
                ct);

            if (analysis.DiffSummary is null)
            {
                newEmployeeCount++;
                employeeAnalyses.Add(new EmployeeAccuracyResult(
                    entry.EmployeeId,
                    entry.EmployeeCode,
                    entry.EmployeeName,
                    entry.TotalAmount,
                    null,
                    false,
                    false,
                    "新規従業員（履歴データなし）"));
            }
            else
            {
                if (analysis.IsCritical) criticalCount++;
                else if (analysis.IsAnomaly) anomalyCount++;
                else normalCount++;

                employeeAnalyses.Add(new EmployeeAccuracyResult(
                    entry.EmployeeId,
                    entry.EmployeeCode,
                    entry.EmployeeName,
                    entry.TotalAmount,
                    analysis.DiffSummary,
                    analysis.IsAnomaly,
                    analysis.IsCritical,
                    analysis.AnomalyReason));
            }
        }

        // Calculate overall accuracy score (percentage of non-anomalous results)
        var totalWithHistory = entries.Count - newEmployeeCount;
        var accuracyScore = totalWithHistory > 0
            ? (decimal)normalCount / totalWithHistory * 100
            : 100m;

        return new AccuracyReport(
            month,
            entries.Count,
            normalCount,
            anomalyCount,
            criticalCount,
            newEmployeeCount,
            Math.Round(accuracyScore, 1),
            employeeAnalyses,
            thresholds);
    }

    /// <summary>
    /// Result of accuracy analysis for a single employee.
    /// </summary>
    public sealed record EmployeeAccuracyResult(
        Guid EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        decimal TotalAmount,
        JsonObject? DiffSummary,
        bool IsAnomaly,
        bool IsCritical,
        string? Notes);

    /// <summary>
    /// Overall accuracy report for a payroll batch.
    /// </summary>
    public sealed record AccuracyReport(
        string Month,
        int TotalEmployees,
        int NormalCount,
        int AnomalyCount,
        int CriticalCount,
        int NewEmployeeCount,
        decimal AccuracyScore,
        IReadOnlyList<EmployeeAccuracyResult> EmployeeResults,
        AccuracyThresholds Thresholds);

    /// <summary>
    /// Checks whether a payroll run already exists for the given month/policy/runType combination.
    /// </summary>
    private async Task<bool> HasExistingRunAsync(string companyCode, Guid? policyId, string month, string runType, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM payroll_runs
            WHERE company_code = $1
              AND period_month = $2
              AND run_type = $3
              AND (($4 IS NULL AND policy_id IS NULL) OR policy_id = $4)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        cmd.Parameters.AddWithValue(runType);
        cmd.Parameters.Add(new NpgsqlParameter { Value = policyId.HasValue ? policyId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var exists = await cmd.ExecuteScalarAsync(ct);
        return exists is not null;
    }

    /// <summary>
    /// Pre-creates vouchers for each payroll entry (if accounting drafts exist) so that when the
    /// payroll run is persisted the vouchers can be referenced immediately. Any failure causes the
    /// created vouchers to be deleted.
    /// </summary>
    private async Task<Dictionary<Guid, VoucherLink>> CreateVoucherLinksAsync(
        string companyCode,
        string month,
        IReadOnlyList<PayrollManualSaveEntry> entries,
        Auth.UserCtx? userCtx,
        VoucherSource source,
        string? targetUserId,
        CancellationToken ct)
    {
        var links = new Dictionary<Guid, VoucherLink>();
        if (entries.Count == 0) return links;

        var postingDate = ResolvePostingDate(month);
        var actor = userCtx ?? new Auth.UserCtx("system", Array.Empty<string>(), Array.Empty<string>(), null);
        var createdVoucherIds = new List<Guid>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        var paymentTerms = await LoadPayrollPaymentTermsAsync(conn, companyCode, ct);
        var paymentDateStr = CalculatePaymentDate(postingDate, paymentTerms);
        var fieldRulesCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        async Task<Dictionary<string, string>> GetFieldRulesAsync(string accountCode)
        {
            if (string.IsNullOrWhiteSpace(accountCode)) return new();
            if (fieldRulesCache.TryGetValue(accountCode, out var cached)) return cached;
            var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var q = conn.CreateCommand();
            q.CommandText = "SELECT payload FROM accounts WHERE company_code=$1 AND account_code=$2 LIMIT 1";
            q.Parameters.AddWithValue(companyCode);
            q.Parameters.AddWithValue(accountCode);
            var payloadText = (string?)await q.ExecuteScalarAsync(ct);
            if (!string.IsNullOrWhiteSpace(payloadText))
            {
                using var doc = JsonDocument.Parse(payloadText);
                if (doc.RootElement.TryGetProperty("fieldRules", out var fr) && fr.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in fr.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
                            rules[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
            fieldRulesCache[accountCode] = rules;
            return rules;
        }

        try
        {
            foreach (var entry in entries)
            {
                if (entry.AccountingDraft.ValueKind != JsonValueKind.Array) continue;
                var accountFieldRules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in entry.AccountingDraft.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.Object) continue;
                    var accountCode = ReadJsonString(line, "accountCode");
                    if (string.IsNullOrWhiteSpace(accountCode) || accountFieldRules.ContainsKey(accountCode)) continue;
                    accountFieldRules[accountCode] = await GetFieldRulesAsync(accountCode);
                }
                var payload = BuildVoucherPayload(entry, postingDate, month, accountFieldRules, paymentDateStr);
                if (payload is null)
                {
                    // Accounting draft exists but no valid lines -> fail fast
                    if (entry.AccountingDraft.GetArrayLength() > 0)
                    {
                        throw new PayrollExecutionException(StatusCodes.Status400BadRequest,
                            new { error = "会計仕訳に有効な行がありません。科目コード/金額を確認してください。" }, "invalid_accounting_draft");
                    }
                    continue;
                }

                var payloadJson = payload.ToJsonString();
                using var payloadDoc = JsonDocument.Parse(payloadJson);
                var (insertedJson, _) = await _finance.CreateVoucher(
                    companyCode, "vouchers", payloadDoc.RootElement, actor,
                    source, targetUserId: targetUserId);
                using var insertedDoc = JsonDocument.Parse(insertedJson);
                var root = insertedDoc.RootElement;

                var voucherId = Guid.Empty;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    Guid.TryParse(idEl.GetString(), out voucherId);
                }
                if (voucherId == Guid.Empty)
                    throw new PayrollExecutionException(StatusCodes.Status500InternalServerError, new { error = "会計伝票の生成に失敗しました" }, "voucher_create_failed");

                var voucherNo = ReadJsonString(root, "voucher_no");
                if (string.IsNullOrWhiteSpace(voucherNo) &&
                    root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind == JsonValueKind.Object &&
                    payloadEl.TryGetProperty("header", out var headerEl) && headerEl.ValueKind == JsonValueKind.Object)
                {
                    voucherNo = ReadJsonString(headerEl, "voucherNo");
                }

                createdVoucherIds.Add(voucherId);
                links[entry.EmployeeId] = new VoucherLink(entry.EmployeeId, voucherId, voucherNo ?? string.Empty);
            }
        }
        catch
        {
            if (createdVoucherIds.Count > 0)
            {
                await DeleteVouchersAsync(companyCode, createdVoucherIds, ct);
            }
            throw;
        }

        return links;
    }

    /// <summary>
    /// Builds the voucher payload from a payroll entry's accounting draft.
    /// </summary>
    private static JsonObject? BuildVoucherPayload(
        PayrollManualSaveEntry entry,
        DateOnly postingDate,
        string month,
        IDictionary<string, Dictionary<string, string>> accountFieldRules,
        string? paymentDateStr)
    {
        if (entry.AccountingDraft.ValueKind != JsonValueKind.Array) return null;
        var lines = new JsonArray();
        foreach (var line in entry.AccountingDraft.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object) continue;
            var accountCode = ReadJsonString(line, "accountCode");
            if (string.IsNullOrWhiteSpace(accountCode)) continue;
            var amount = ReadJsonDecimal(line, "amount");
            if (amount == 0m) continue;
            var drcr = ReadJsonString(line, "drcr");
            drcr = string.Equals(drcr, "CR", StringComparison.OrdinalIgnoreCase) ? "CR" : "DR";

            var lineObj = new JsonObject
            {
                ["accountCode"] = accountCode!,
                ["drcr"] = drcr,
                ["amount"] = Math.Abs(amount)
            };

            accountFieldRules.TryGetValue(accountCode!, out var rules);
            bool IsAllowed(string field) =>
                rules is null || !rules.TryGetValue(field, out var state)
                || !string.Equals(state, "hidden", StringComparison.OrdinalIgnoreCase);

            if (IsAllowed("paymentDate") && !string.IsNullOrWhiteSpace(paymentDateStr))
            {
                var hasPaymentDateRule = rules is not null && rules.TryGetValue("paymentDate", out var pdState)
                    && string.Equals(pdState, "required", StringComparison.OrdinalIgnoreCase);
                if (hasPaymentDateRule)
                    lineObj["paymentDate"] = paymentDateStr;
            }

            if (IsAllowed("employeeId") && entry.EmployeeId != Guid.Empty)
            {
                lineObj["employeeId"] = entry.EmployeeId.ToString();
            }

            if (IsAllowed("employeeId"))
            {
                var employeeCode = ReadJsonString(line, "employeeCode");
                if (string.IsNullOrWhiteSpace(employeeCode)) employeeCode = entry.EmployeeCode;
                if (!string.IsNullOrWhiteSpace(employeeCode))
                    lineObj["employeeCode"] = employeeCode;
            }

            if (IsAllowed("departmentId"))
            {
                var deptCode = ReadJsonString(line, "departmentCode");
                if (string.IsNullOrWhiteSpace(deptCode)) deptCode = entry.DepartmentCode;
                if (!string.IsNullOrWhiteSpace(deptCode))
                    lineObj["departmentCode"] = deptCode;
            }

            var description = ReadJsonString(line, "description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                lineObj["description"] = description;
            }

            lines.Add(lineObj);
        }

        if (lines.Count == 0) return null;

        var summaryLabel = entry.EmployeeName ?? entry.EmployeeCode ?? entry.EmployeeId.ToString();
        var summary = $"{month} 給与 {summaryLabel}";
        if (summary.Length > 120) summary = summary[..120];

        var header = new JsonObject
        {
            ["postingDate"] = postingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["voucherType"] = "SA",
            ["currency"] = "JPY",
            ["summary"] = summary,
            ["source"] = "payroll"
        };

        return new JsonObject
        {
            ["header"] = header,
            ["lines"] = lines
        };
    }

    private sealed record PaymentTerms(int CutOffDay, int PaymentMonth, int PaymentDay);

    private async Task<PaymentTerms> LoadPayrollPaymentTermsAsync(NpgsqlConnection conn, string companyCode, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT payload->'paymentTerms' FROM company_settings WHERE company_code=$1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            var obj = await cmd.ExecuteScalarAsync(ct);
            if (obj is string json && !string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var cutOffDay = root.TryGetProperty("cutOffDay", out var co) && co.TryGetInt32(out var cod) ? cod : 31;
                var paymentMonth = root.TryGetProperty("paymentMonth", out var pm) && pm.TryGetInt32(out var pmd) ? pmd : 1;
                var paymentDay = root.TryGetProperty("paymentDay", out var pd) && pd.TryGetInt32(out var pdd) ? pdd : 31;
                return new PaymentTerms(cutOffDay, paymentMonth, paymentDay);
            }
        }
        catch
        {
            // ignore; fallback to defaults
        }
        return new PaymentTerms(31, 1, 31);
    }

    private static string? CalculatePaymentDate(DateOnly postingDate, PaymentTerms terms)
    {
        var cutOffDay = terms.CutOffDay <= 0 ? 31 : terms.CutOffDay;
        var paymentMonth = terms.PaymentMonth;
        var paymentDay = terms.PaymentDay <= 0 ? 31 : terms.PaymentDay;

        var invoiceDate = new DateTime(postingDate.Year, postingDate.Month, postingDate.Day);
        var day = invoiceDate.Day;

        var baseMonth = invoiceDate.Month - 1;
        var baseYear = invoiceDate.Year;

        if (day > cutOffDay)
        {
            baseMonth += 1;
            if (baseMonth > 11)
            {
                baseMonth = 0;
                baseYear += 1;
            }
        }

        var dueMonth = baseMonth + paymentMonth;
        var dueYear = baseYear;
        while (dueMonth > 11)
        {
            dueMonth -= 12;
            dueYear += 1;
        }

        var lastDayOfMonth = DateTime.DaysInMonth(dueYear, dueMonth + 1);
        var dueDay = paymentDay > lastDayOfMonth ? lastDayOfMonth : paymentDay;
        var dueDate = new DateTime(dueYear, dueMonth + 1, dueDay);
        return dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Loads voucher ids linked to payroll entries for the specified month/runType so they can be deleted when overwriting.
    /// </summary>
    private static async Task<List<Guid>> LoadVoucherIdsForPeriodAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        string month,
        string runType,
        Guid? policyId,
        CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.voucher_id
            FROM payroll_run_entries e
            JOIN payroll_runs r ON e.run_id = r.id
            WHERE r.company_code = $1
              AND r.period_month = $2
              AND r.run_type = $3
              AND (($4 IS NULL AND r.policy_id IS NULL) OR r.policy_id = $4)
              AND e.voucher_id IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        cmd.Parameters.AddWithValue(runType);
        cmd.Parameters.Add(new NpgsqlParameter { Value = policyId.HasValue ? policyId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetGuid(0));
            }
        }
        return ids;
    }

    private static async Task<Guid> LoadRunIdAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        Guid? policyId,
        string month,
        string runType,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id
            FROM payroll_runs
            WHERE company_code = $1
              AND period_month = $2
              AND run_type = $3
              AND (($4 IS NULL AND policy_id IS NULL) OR policy_id = $4)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(month);
        cmd.Parameters.AddWithValue(runType);
        cmd.Parameters.Add(new NpgsqlParameter { Value = policyId.HasValue ? policyId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is Guid g ? g : Guid.Empty;
    }

    private static async Task<List<Guid>> LoadVoucherIdsForEmployeesAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string companyCode,
        Guid runId,
        IReadOnlyList<Guid> employeeIds,
        CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (employeeIds.Count == 0) return ids;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT voucher_id
            FROM payroll_run_entries
            WHERE company_code = $1
              AND run_id = $2
              AND employee_id = ANY($3)
              AND voucher_id IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = employeeIds.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetGuid(0));
            }
        }
        return ids;
    }

    /// <summary>
    /// Resolves the voucher posting date for payroll runs (last day of the target month if parsable).
    /// </summary>
    private static DateOnly ResolvePostingDate(string month)
    {
        if (TryParsePayrollMonth(month, out var parsed))
        {
            return parsed.AddMonths(1).AddDays(-1);
        }
        return DateOnly.FromDateTime(DateTime.Today);
    }

    /// <summary>
    /// Parses several yyyy-MM formats into a <see cref="DateOnly"/> representing the first day of the month.
    /// </summary>
    private static bool TryParsePayrollMonth(string text, out DateOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        static bool TryParse(string format, string value, out DateOnly date)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                date = DateOnly.FromDateTime(new DateTime(dt.Year, dt.Month, 1));
                return true;
            }
            date = default;
            return false;
        }

        if (TryParse("yyyy-MM", text, out result) ||
            TryParse("yyyy/MM", text, out result) ||
            TryParse("yyyyMM", text, out result))
        {
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            result = DateOnly.FromDateTime(new DateTime(dt.Year, dt.Month, 1));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes the provided vouchers using a new connection scope (used when recovery is needed).
    /// </summary>
    private async Task DeleteVouchersAsync(string companyCode, IReadOnlyCollection<Guid> voucherIds, CancellationToken ct)
    {
        if (voucherIds.Count == 0) return;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await DeleteVouchersInternalAsync(conn, null, companyCode, voucherIds, ct);
        
        // 刷新总账物化视图
        await FinanceService.RefreshGlViewAsync(conn);
    }

    /// <summary>
    /// Deletes the provided vouchers using the supplied connection/transaction (used inside existing tx).
    /// Also deletes related open_items to maintain data consistency.
    /// </summary>
    private static async Task DeleteVouchersInternalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string companyCode,
        IReadOnlyCollection<Guid> voucherIds,
        CancellationToken ct)
    {
        if (voucherIds.Count == 0) return;
        
        // Delete related open_items first
        await using var cmdOi = conn.CreateCommand();
        cmdOi.Transaction = tx;
        cmdOi.CommandText = "DELETE FROM open_items WHERE company_code=$1 AND voucher_id = ANY($2)";
        cmdOi.Parameters.AddWithValue(companyCode);
        cmdOi.Parameters.AddWithValue(voucherIds.ToArray());
        await cmdOi.ExecuteNonQueryAsync(ct);
        
        // Delete vouchers
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM vouchers WHERE company_code=$1 AND id = ANY($2)";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(voucherIds.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Helper for safely reading string properties from JsonElement objects.
    /// </summary>
    private static string? ReadJsonString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var node)) return null;
        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// Helper for safely reading decimal properties from JsonElement objects (string/number supported).
    /// </summary>
    private static decimal ReadJsonDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0m;
        if (!element.TryGetProperty(property, out var node)) return 0m;
        if (node.ValueKind == JsonValueKind.Number)
        {
            if (node.TryGetDecimal(out var dec)) return dec;
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
}


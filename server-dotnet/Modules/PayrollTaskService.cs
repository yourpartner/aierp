using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// Orchestrates the storage and retrieval of AI-review tasks that are created
/// after payroll runs. Tasks are stored in <c>ai_payroll_tasks</c> and later
/// consumed by the AI session UI so reviewers can approve or reject results.
/// </summary>
public sealed class PayrollTaskService
{
    private readonly NpgsqlDataSource _ds;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PayrollTaskService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    /// <summary>
    /// Task types for payroll review workflow.
    /// </summary>
    public static class TaskTypes
    {
        /// <summary>Normal confirmation task for standard payroll results.</summary>
        public const string Confirmation = "confirmation";
        /// <summary>Anomaly handling task for results with significant deviations.</summary>
        public const string AnomalyHandling = "anomaly_handling";
        /// <summary>Critical anomaly requiring immediate attention.</summary>
        public const string CriticalAnomaly = "critical_anomaly";
    }

    /// <summary>
    /// Task status values.
    /// </summary>
    public static class TaskStatuses
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string NeedsRevision = "needs_revision";
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// Snapshot of a persisted payroll task row returned to callers.
    /// </summary>
    public sealed record PayrollTask(
        Guid Id,
        Guid SessionId,
        string CompanyCode,
        Guid RunId,
        Guid EntryId,
        Guid EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        string PeriodMonth,
        string Status,
        string TaskType,
        string? Summary,
        JsonObject? Metadata,
        JsonObject? DiffSummary,
        string? TargetUserId,
        string? AssignedUserId,
        string? CompletedByUserId,
        string? Comments,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CompletedAt);

    /// <summary>
    /// Lightweight container produced during auto/manual runs that describes
    /// a payroll entry which should be turned into a review task.
    /// </summary>
    public sealed record PayrollTaskCandidate(
        Guid EntryId,
        Guid EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        string PeriodMonth,
        decimal TotalAmount,
        JsonObject? DiffSummary,
        string? Summary,
        bool NeedsConfirmation,
        bool IsAnomaly = false,
        bool IsCritical = false,
        string? AnomalyReason = null);

    /// <summary>
    /// Result of updating a task's status.
    /// </summary>
    public sealed record TaskUpdateResult(bool Success, string? Error, PayrollTask? Task);

    /// <summary>
    /// Summary statistics for payroll tasks.
    /// </summary>
    public sealed record TaskSummary(
        int TotalCount,
        int PendingCount,
        int InProgressCount,
        int ApprovedCount,
        int RejectedCount,
        int AnomalyCount,
        int CriticalCount);

    /// <summary>
    /// Creates payroll tasks for the supplied candidates inside the current AI session.
    /// Each candidate becomes a row in <c>ai_payroll_tasks</c> with status = pending.
    /// Task type is determined based on anomaly flags.
    /// </summary>
    public async Task<int> CreateTasksAsync(
        Guid sessionId,
        string companyCode,
        Guid runId,
        string? targetUserId,
        IEnumerable<PayrollTaskCandidate> candidates,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        int created = 0;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var candidate in candidates)
        {
            // Determine task type based on anomaly status
            var taskType = candidate.IsCritical ? TaskTypes.CriticalAnomaly
                : candidate.IsAnomaly ? TaskTypes.AnomalyHandling
                : TaskTypes.Confirmation;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ai_payroll_tasks(
                    id, session_id, company_code, run_id, entry_id, employee_id, employee_code, employee_name,
                    period_month, status, task_type, summary, metadata, diff_summary, target_user_id, created_at, updated_at)
                VALUES (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, $8, 'pending', $9, $10, $11::jsonb, $12::jsonb, $13, $14, $14)
                ON CONFLICT (company_code, run_id, entry_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(runId);
            cmd.Parameters.AddWithValue(candidate.EntryId);
            cmd.Parameters.AddWithValue(candidate.EmployeeId);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(candidate.EmployeeCode) ? DBNull.Value : candidate.EmployeeCode);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(candidate.EmployeeName) ? DBNull.Value : candidate.EmployeeName);
            cmd.Parameters.AddWithValue(candidate.PeriodMonth);
            cmd.Parameters.AddWithValue(taskType);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(candidate.Summary) ? DBNull.Value : candidate.Summary);

            var metadata = new JsonObject
            {
                ["totalAmount"] = candidate.TotalAmount,
                ["needsConfirmation"] = candidate.NeedsConfirmation,
                ["isAnomaly"] = candidate.IsAnomaly,
                ["isCritical"] = candidate.IsCritical
            };
            if (!string.IsNullOrWhiteSpace(candidate.AnomalyReason))
            {
                metadata["anomalyReason"] = candidate.AnomalyReason;
            }
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));
            cmd.Parameters.AddWithValue(candidate.DiffSummary is null ? (object)DBNull.Value : JsonSerializer.Serialize(candidate.DiffSummary, JsonOptions));
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(targetUserId) ? DBNull.Value : targetUserId);
            cmd.Parameters.AddWithValue(now);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected > 0) created++;
        }

        await tx.CommitAsync(ct);
        return created;
    }

    /// <summary>
    /// Updates the status of a task (approve, reject, etc.).
    /// </summary>
    public async Task<TaskUpdateResult> UpdateTaskStatusAsync(
        Guid taskId,
        string companyCode,
        string newStatus,
        string? userId,
        string? comments,
        CancellationToken ct)
    {
        var validStatuses = new[] { TaskStatuses.Pending, TaskStatuses.InProgress, TaskStatuses.Approved, 
            TaskStatuses.Rejected, TaskStatuses.NeedsRevision, TaskStatuses.Cancelled };
        if (!validStatuses.Contains(newStatus))
        {
            return new TaskUpdateResult(false, $"Invalid status: {newStatus}", null);
        }

        var now = DateTimeOffset.UtcNow;
        var isCompleting = newStatus is TaskStatuses.Approved or TaskStatuses.Rejected or TaskStatuses.Cancelled;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (isCompleting)
        {
            cmd.CommandText = """
                UPDATE ai_payroll_tasks
                SET status = $2, 
                    completed_by_user_id = $3, 
                    comments = COALESCE($4, comments),
                    completed_at = $5,
                    updated_at = $5
                WHERE id = $1 AND company_code = $6
                RETURNING *;
                """;
            cmd.Parameters.AddWithValue(taskId);
            cmd.Parameters.AddWithValue(newStatus);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(userId) ? DBNull.Value : userId);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(comments) ? DBNull.Value : comments);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(companyCode);
        }
        else
        {
            cmd.CommandText = """
                UPDATE ai_payroll_tasks
                SET status = $2,
                    assigned_user_id = COALESCE($3, assigned_user_id),
                    comments = COALESCE($4, comments),
                    updated_at = $5
                WHERE id = $1 AND company_code = $6
                RETURNING *;
                """;
            cmd.Parameters.AddWithValue(taskId);
            cmd.Parameters.AddWithValue(newStatus);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(userId) ? DBNull.Value : userId);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(comments) ? DBNull.Value : comments);
            cmd.Parameters.AddWithValue(now);
            cmd.Parameters.AddWithValue(companyCode);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            return new TaskUpdateResult(true, null, task);
        }

        return new TaskUpdateResult(false, "Task not found", null);
    }

    /// <summary>
    /// Assigns a task to a specific user.
    /// </summary>
    public async Task<TaskUpdateResult> AssignTaskAsync(
        Guid taskId,
        string companyCode,
        string assigneeUserId,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ai_payroll_tasks
            SET assigned_user_id = $2, status = 'in_progress', updated_at = now()
            WHERE id = $1 AND company_code = $3
            RETURNING *;
            """;
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(assigneeUserId);
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var task = ReadTask(reader);
            return new TaskUpdateResult(true, null, task);
        }

        return new TaskUpdateResult(false, "Task not found", null);
    }

    /// <summary>
    /// Lists payroll review tasks for a given session.
    /// </summary>
    public async Task<IReadOnlyList<PayrollTask>> ListBySessionAsync(Guid sessionId, CancellationToken ct)
    {
        var list = new List<PayrollTask>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildSelectQuery("session_id = $1", "created_at");
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadTask(reader));
        }
        return list;
    }

    /// <summary>
    /// Lists payroll tasks for a company with optional filters.
    /// </summary>
    public async Task<IReadOnlyList<PayrollTask>> ListByCompanyAsync(
        string companyCode,
        string? month,
        string? status,
        string? taskType,
        string? targetUserId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var list = new List<PayrollTask>();
        var conditions = new List<string> { "company_code = $1" };
        var paramIndex = 2;
        var parameters = new List<object> { companyCode };

        if (!string.IsNullOrWhiteSpace(month))
        {
            conditions.Add($"period_month = ${paramIndex++}");
            parameters.Add(month);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add($"status = ${paramIndex++}");
            parameters.Add(status);
        }
        if (!string.IsNullOrWhiteSpace(taskType))
        {
            conditions.Add($"task_type = ${paramIndex++}");
            parameters.Add(taskType);
        }
        if (!string.IsNullOrWhiteSpace(targetUserId))
        {
            conditions.Add($"(target_user_id = ${paramIndex} OR assigned_user_id = ${paramIndex++})");
            parameters.Add(targetUserId);
        }

        var offset = (page - 1) * pageSize;
        var sql = BuildSelectQuery(string.Join(" AND ", conditions), "created_at DESC") + $" LIMIT {pageSize} OFFSET {offset}";

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var param in parameters)
        {
            cmd.Parameters.AddWithValue(param);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadTask(reader));
        }
        return list;
    }

    /// <summary>
    /// Lists tasks assigned to or targeted at a specific user.
    /// </summary>
    public async Task<IReadOnlyList<PayrollTask>> ListByUserAsync(
        string companyCode,
        string userId,
        bool pendingOnly,
        CancellationToken ct)
    {
        var list = new List<PayrollTask>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var statusFilter = pendingOnly ? " AND status IN ('pending', 'in_progress')" : "";
        cmd.CommandText = BuildSelectQuery(
            $"company_code = $1 AND (target_user_id = $2 OR assigned_user_id = $2){statusFilter}",
            "CASE WHEN status = 'pending' THEN 0 WHEN status = 'in_progress' THEN 1 ELSE 2 END, created_at DESC");
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadTask(reader));
        }
        return list;
    }

    /// <summary>
    /// Gets a single task by ID.
    /// </summary>
    public async Task<PayrollTask?> GetByIdAsync(Guid taskId, string companyCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildSelectQuery("id = $1 AND company_code = $2", "");
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(companyCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadTask(reader);
        }
        return null;
    }

    /// <summary>
    /// Gets summary statistics for tasks.
    /// </summary>
    public async Task<TaskSummary> GetSummaryAsync(string companyCode, string? month, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var monthFilter = string.IsNullOrWhiteSpace(month) ? "" : " AND period_month = $2";
        cmd.CommandText = $"""
            SELECT 
                COUNT(*) as total,
                COUNT(*) FILTER (WHERE status = 'pending') as pending,
                COUNT(*) FILTER (WHERE status = 'in_progress') as in_progress,
                COUNT(*) FILTER (WHERE status = 'approved') as approved,
                COUNT(*) FILTER (WHERE status = 'rejected') as rejected,
                COUNT(*) FILTER (WHERE task_type = 'anomaly_handling') as anomaly,
                COUNT(*) FILTER (WHERE task_type = 'critical_anomaly') as critical
            FROM ai_payroll_tasks
            WHERE company_code = $1{monthFilter};
            """;
        cmd.Parameters.AddWithValue(companyCode);
        if (!string.IsNullOrWhiteSpace(month))
            cmd.Parameters.AddWithValue(month);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new TaskSummary(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6));
        }

        return new TaskSummary(0, 0, 0, 0, 0, 0, 0);
    }

    #region Helpers

    private static string BuildSelectQuery(string whereClause, string orderBy)
    {
        var orderClause = string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}";
        return $"""
            SELECT id, session_id, company_code, run_id, entry_id, employee_id, employee_code, employee_name,
                   period_month, status, COALESCE(task_type, 'confirmation') as task_type, summary, metadata, diff_summary, 
                   target_user_id, assigned_user_id, completed_by_user_id, comments, created_at, updated_at, completed_at
            FROM ai_payroll_tasks
            WHERE {whereClause}{orderClause}
            """;
    }

    private static PayrollTask ReadTask(NpgsqlDataReader reader)
    {
        var metadata = reader.IsDBNull(12) ? null : JsonNode.Parse(reader.GetString(12))?.AsObject();
        var diff = reader.IsDBNull(13) ? null : JsonNode.Parse(reader.GetString(13))?.AsObject();
        return new PayrollTask(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            metadata,
            diff,
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.GetFieldValue<DateTimeOffset>(18),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.IsDBNull(20) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(20));
    }

    #endregion

    // Legacy method for backward compatibility
    public async Task<IReadOnlyList<PayrollTask>> ListAsync(Guid sessionId, CancellationToken ct)
        => await ListBySessionAsync(sessionId, ct);
}


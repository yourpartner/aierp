using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// Unified service for managing all types of AI tasks (invoice, sales_order, payroll, alert, etc.)
/// Replaces individual task services with a single, consistent API.
/// </summary>
public sealed class UnifiedTaskService
{
    private readonly NpgsqlDataSource _ds;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public UnifiedTaskService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    #region Task Types

    public static class TaskTypes
    {
        public const string Invoice = "invoice";
        public const string SalesOrder = "sales_order";
        public const string Payroll = "payroll";
        public const string Alert = "alert";
        public const string InvoiceValidationWarning = "invoice_validation_warning";
        // User-facing master-data creation tasks (customer/vendor/account/employee etc.)
        public const string MasterData = "master_data";
    }

    public static class TaskStatus
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
    }

    #endregion

    #region Data Types

    public sealed record UnifiedTask(
        Guid Id,
        Guid? SessionId,
        string CompanyCode,
        string TaskType,
        string Status,
        string? Title,
        string? Summary,
        string? UserId,
        string? TargetUserId,
        string? AssignedUserId,
        JsonObject Payload,
        JsonObject Metadata,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CompletedAt,
        string? CompletedBy);

    public sealed record CreateTaskRequest
    {
        public Guid? SessionId { get; init; }
        public required string CompanyCode { get; init; }
        public required string TaskType { get; init; }
        public string Status { get; init; } = TaskStatus.Pending;
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? UserId { get; init; }
        public string? TargetUserId { get; init; }
        public string? AssignedUserId { get; init; }
        public JsonObject? Payload { get; init; }
        public JsonObject? Metadata { get; init; }
    }

    #endregion

    #region CRUD Operations

    /// <summary>
    /// Creates a new task with the specified parameters.
    /// </summary>
    public async Task<UnifiedTask> CreateAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = """
            INSERT INTO ai_tasks 
            (id, session_id, company_code, task_type, status, title, summary, 
             user_id, target_user_id, assigned_user_id, payload, metadata, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb, $12::jsonb, now(), now())
            RETURNING id, session_id, company_code, task_type, status, title, summary,
                      user_id, target_user_id, assigned_user_id, payload, metadata,
                      created_at, updated_at, completed_at, completed_by;
            """;
        
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.SessionId.HasValue ? request.SessionId.Value : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(request.CompanyCode);
        cmd.Parameters.AddWithValue(request.TaskType);
        cmd.Parameters.AddWithValue(request.Status);
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(request.Title) ? DBNull.Value : request.Title, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(request.Summary) ? DBNull.Value : request.Summary, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(request.UserId) ? DBNull.Value : request.UserId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(request.TargetUserId) ? DBNull.Value : request.TargetUserId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(request.AssignedUserId) ? DBNull.Value : request.AssignedUserId, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(request.Payload is null ? "{}" : JsonSerializer.Serialize(request.Payload, JsonOptions));
        cmd.Parameters.AddWithValue(request.Metadata is null ? "{}" : JsonSerializer.Serialize(request.Metadata, JsonOptions));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new Exception("Failed to create task");
        }
        return ReadTask(reader);
    }

    /// <summary>
    /// Gets a task by ID, optionally filtered by company code for security.
    /// </summary>
    public async Task<UnifiedTask?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, task_type, status, title, summary,
                   user_id, target_user_id, assigned_user_id, payload, metadata,
                   created_at, updated_at, completed_at, completed_by
            FROM ai_tasks
            WHERE id = $1;
            """;
        cmd.Parameters.AddWithValue(id);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadTask(reader);
    }

    /// <summary>
    /// Gets a task by ID with company code filtering for multi-tenant security.
    /// </summary>
    public async Task<UnifiedTask?> GetAsync(Guid id, string companyCode, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, task_type, status, title, summary,
                   user_id, target_user_id, assigned_user_id, payload, metadata,
                   created_at, updated_at, completed_at, completed_by
            FROM ai_tasks
            WHERE id = $1 AND company_code = $2;
            """;
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(companyCode);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadTask(reader);
    }

    /// <summary>
    /// Lists tasks for a session, optionally filtered by task type.
    /// </summary>
    public async Task<IReadOnlyList<UnifiedTask>> ListBySessionAsync(
        Guid sessionId, 
        string? taskType = null,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        if (string.IsNullOrWhiteSpace(taskType))
        {
            cmd.CommandText = """
                SELECT id, session_id, company_code, task_type, status, title, summary,
                       user_id, target_user_id, assigned_user_id, payload, metadata,
                       created_at, updated_at, completed_at, completed_by
                FROM ai_tasks
                WHERE session_id = $1
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue(sessionId);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, session_id, company_code, task_type, status, title, summary,
                       user_id, target_user_id, assigned_user_id, payload, metadata,
                       created_at, updated_at, completed_at, completed_by
                FROM ai_tasks
                WHERE session_id = $1 AND task_type = $2
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(taskType);
        }

        var result = new List<UnifiedTask>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadTask(reader));
        }
        return result;
    }

    /// <summary>
    /// Lists tasks for a company and target user (for notifications).
    /// </summary>
    public async Task<IReadOnlyList<UnifiedTask>> ListByTargetUserAsync(
        string companyCode,
        string targetUserId,
        string? status = null,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        if (string.IsNullOrWhiteSpace(status))
        {
            cmd.CommandText = """
                SELECT id, session_id, company_code, task_type, status, title, summary,
                       user_id, target_user_id, assigned_user_id, payload, metadata,
                       created_at, updated_at, completed_at, completed_by
                FROM ai_tasks
                WHERE company_code = $1 AND target_user_id = $2
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(targetUserId);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, session_id, company_code, task_type, status, title, summary,
                       user_id, target_user_id, assigned_user_id, payload, metadata,
                       created_at, updated_at, completed_at, completed_by
                FROM ai_tasks
                WHERE company_code = $1 AND target_user_id = $2 AND status = $3
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(targetUserId);
            cmd.Parameters.AddWithValue(status);
        }

        var result = new List<UnifiedTask>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadTask(reader));
        }
        return result;
    }

    /// <summary>
    /// Updates the status of a task.
    /// </summary>
    public async Task<bool> UpdateStatusAsync(
        Guid id, 
        string status, 
        JsonObject? additionalMetadata = null,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        if (additionalMetadata is not null)
        {
            cmd.CommandText = """
                UPDATE ai_tasks 
                SET status = $2, 
                    metadata = metadata || $3::jsonb,
                    updated_at = now(),
                    completed_at = CASE WHEN $2 = 'completed' THEN now() ELSE completed_at END
                WHERE id = $1;
                """;
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(additionalMetadata, JsonOptions));
        }
        else
        {
            cmd.CommandText = """
                UPDATE ai_tasks 
                SET status = $2, 
                    updated_at = now(),
                    completed_at = CASE WHEN $2 = 'completed' THEN now() ELSE completed_at END
                WHERE id = $1;
                """;
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(status);
        }
        
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    /// <summary>
    /// Completes a task with optional completion details.
    /// </summary>
    public async Task<bool> CompleteAsync(
        Guid id,
        string? completedBy = null,
        string? completionNote = null,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        var metadata = new JsonObject();
        if (!string.IsNullOrWhiteSpace(completionNote))
        {
            metadata["completionNote"] = completionNote;
        }
        
        cmd.CommandText = """
            UPDATE ai_tasks 
            SET status = 'completed',
                completed_at = now(),
                completed_by = $2,
                metadata = metadata || $3::jsonb,
                updated_at = now()
            WHERE id = $1;
            """;
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(completedBy) ? DBNull.Value : completedBy);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));
        
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    /// <summary>
    /// Cancels a task.
    /// </summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        cmd.CommandText = """
            UPDATE ai_tasks 
            SET status = 'cancelled', updated_at = now()
            WHERE id = $1 AND status IN ('pending', 'in_progress');
            """;
        cmd.Parameters.AddWithValue(id);
        
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    /// <summary>
    /// Deletes a task and its associated messages.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        
        try
        {
            // Delete associated messages
            await using (var msgCmd = conn.CreateCommand())
            {
                msgCmd.Transaction = tx;
                msgCmd.CommandText = "DELETE FROM ai_messages WHERE task_id = $1";
                msgCmd.Parameters.AddWithValue(id);
                await msgCmd.ExecuteNonQueryAsync(ct);
            }
            
            await using (var archiveCmd = conn.CreateCommand())
            {
                archiveCmd.Transaction = tx;
                archiveCmd.CommandText = "DELETE FROM ai_messages_archive WHERE task_id = $1";
                archiveCmd.Parameters.AddWithValue(id);
                await archiveCmd.ExecuteNonQueryAsync(ct);
            }
            
            // Delete task
            await using (var taskCmd = conn.CreateCommand())
            {
                taskCmd.Transaction = tx;
                taskCmd.CommandText = "DELETE FROM ai_tasks WHERE id = $1";
                taskCmd.Parameters.AddWithValue(id);
                var affected = await taskCmd.ExecuteNonQueryAsync(ct);
                
                if (affected == 0)
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }
            }
            
            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    #endregion

    #region Convenience Methods for Specific Task Types

    /// <summary>
    /// Creates an invoice task.
    /// </summary>
    public async Task<UnifiedTask> CreateInvoiceTaskAsync(
        Guid sessionId,
        string companyCode,
        string fileId,
        string documentSessionId,
        string fileName,
        string? contentType,
        long fileSize,
        string? blobName,
        string? documentLabel,
        string? storedPath,
        string? userId,
        JsonObject? analysis,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["fileId"] = fileId,
            ["documentSessionId"] = documentSessionId,
            ["fileName"] = fileName,
            ["contentType"] = contentType,
            ["fileSize"] = fileSize,
            ["blobName"] = blobName,
            ["documentLabel"] = documentLabel,
            ["storedPath"] = storedPath
        };
        
        if (analysis is not null)
        {
            payload["analysis"] = analysis.DeepClone();
        }

        return await CreateAsync(new CreateTaskRequest
        {
            SessionId = sessionId,
            CompanyCode = companyCode,
            TaskType = TaskTypes.Invoice,
            Title = fileName,
            Summary = BuildInvoiceSummary(analysis),
            UserId = userId,
            Payload = payload
        }, ct);
    }

    /// <summary>
    /// Creates an invoice validation warning task (for auto-created vouchers with invalid invoice registration).
    /// </summary>
    public async Task<UnifiedTask> CreateInvoiceValidationWarningAsync(
        string companyCode,
        string? sessionId,
        string? targetUserId,
        string registrationNo,
        string message,
        string? voucherNo,
        Guid? voucherId,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["registrationNo"] = registrationNo,
            ["message"] = message
        };
        
        if (!string.IsNullOrWhiteSpace(voucherNo))
        {
            payload["voucherNo"] = voucherNo;
        }
        if (voucherId.HasValue)
        {
            payload["voucherId"] = voucherId.Value.ToString();
        }

        return await CreateAsync(new CreateTaskRequest
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : Guid.TryParse(sessionId, out var sid) ? sid : null,
            CompanyCode = companyCode,
            TaskType = TaskTypes.InvoiceValidationWarning,
            Title = $"インボイス登録番号の検証警告: {registrationNo}",
            Summary = message,
            TargetUserId = targetUserId,
            Payload = payload
        }, ct);
    }

    /// <summary>
    /// Creates a sales order task.
    /// </summary>
    public async Task<UnifiedTask> CreateSalesOrderTaskAsync(
        Guid sessionId,
        string companyCode,
        string? userId,
        string? summary,
        JsonObject? payload,
        CancellationToken ct = default)
    {
        return await CreateAsync(new CreateTaskRequest
        {
            SessionId = sessionId,
            CompanyCode = companyCode,
            TaskType = TaskTypes.SalesOrder,
            Title = "Sales Order Task",
            Summary = summary,
            UserId = userId,
            Payload = payload
        }, ct);
    }

    /// <summary>
    /// Creates a payroll task.
    /// </summary>
    public async Task<UnifiedTask> CreatePayrollTaskAsync(
        Guid sessionId,
        string companyCode,
        Guid? runId,
        Guid? entryId,
        string? employeeCode,
        string? employeeName,
        string? periodMonth,
        string? taskType,
        string? summary,
        string? targetUserId,
        JsonObject? diffSummary,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["runId"] = runId?.ToString(),
            ["entryId"] = entryId?.ToString(),
            ["employeeCode"] = employeeCode,
            ["employeeName"] = employeeName,
            ["periodMonth"] = periodMonth,
            ["taskType"] = taskType
        };
        
        if (diffSummary is not null)
        {
            payload["diffSummary"] = diffSummary.DeepClone();
        }

        return await CreateAsync(new CreateTaskRequest
        {
            SessionId = sessionId,
            CompanyCode = companyCode,
            TaskType = TaskTypes.Payroll,
            Title = employeeName ?? "Payroll Task",
            Summary = summary,
            TargetUserId = targetUserId,
            Payload = payload
        }, ct);
    }

    /// <summary>
    /// Creates an alert task.
    /// </summary>
    public async Task<UnifiedTask> CreateAlertTaskAsync(
        string companyCode,
        string taskType,
        string title,
        string? description,
        string? priority,
        string? targetUserId,
        DateTime? dueDate,
        Guid? alertId,
        CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["alertTaskType"] = taskType,
            ["priority"] = priority ?? "medium"
        };
        
        if (dueDate.HasValue)
        {
            payload["dueDate"] = dueDate.Value.ToString("yyyy-MM-dd");
        }
        if (alertId.HasValue)
        {
            payload["alertId"] = alertId.Value.ToString();
        }

        return await CreateAsync(new CreateTaskRequest
        {
            CompanyCode = companyCode,
            TaskType = TaskTypes.Alert,
            Title = title,
            Summary = description,
            TargetUserId = targetUserId,
            Payload = payload
        }, ct);
    }

    #endregion

    #region Helper Methods

    private static UnifiedTask ReadTask(NpgsqlDataReader reader)
    {
        return new UnifiedTask(
            Id: reader.GetGuid(0),
            SessionId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
            CompanyCode: reader.GetString(2),
            TaskType: reader.GetString(3),
            Status: reader.GetString(4),
            Title: reader.IsDBNull(5) ? null : reader.GetString(5),
            Summary: reader.IsDBNull(6) ? null : reader.GetString(6),
            UserId: reader.IsDBNull(7) ? null : reader.GetString(7),
            TargetUserId: reader.IsDBNull(8) ? null : reader.GetString(8),
            AssignedUserId: reader.IsDBNull(9) ? null : reader.GetString(9),
            Payload: ParseJsonObject(reader.IsDBNull(10) ? null : reader.GetString(10)),
            Metadata: ParseJsonObject(reader.IsDBNull(11) ? null : reader.GetString(11)),
            CreatedAt: reader.GetDateTime(12),
            UpdatedAt: reader.GetDateTime(13),
            CompletedAt: reader.IsDBNull(14) ? null : reader.GetDateTime(14),
            CompletedBy: reader.IsDBNull(15) ? null : reader.GetString(15)
        );
    }

    private static JsonObject ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string? BuildInvoiceSummary(JsonObject? analysis)
    {
        if (analysis is null) return null;
        
        var parts = new List<string>();
        
        if (analysis.TryGetPropertyValue("vendorName", out var vendorNode) && 
            vendorNode is JsonValue vendorVal && 
            vendorVal.TryGetValue<string>(out var vendorName) && 
            !string.IsNullOrWhiteSpace(vendorName))
        {
            parts.Add(vendorName);
        }
        
        if (analysis.TryGetPropertyValue("totalAmount", out var amountNode))
        {
            if (amountNode is JsonValue amountVal)
            {
                if (amountVal.TryGetValue<decimal>(out var amount))
                {
                    parts.Add($"¥{amount:N0}");
                }
                else if (amountVal.TryGetValue<string>(out var amountStr))
                {
                    parts.Add(amountStr);
                }
            }
        }
        
        return parts.Count > 0 ? string.Join(" / ", parts) : null;
    }

    #endregion
}


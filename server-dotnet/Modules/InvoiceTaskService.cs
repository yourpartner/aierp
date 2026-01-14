using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// Stores and manages AI invoice processing tasks, including creation, cancellation, and status updates.
/// </summary>
public sealed class InvoiceTaskService
{
    private readonly NpgsqlDataSource _ds;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public InvoiceTaskService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public sealed record InvoiceTaskCancelResult(string? BlobName, string? StoredPath);

    /// <summary>
    /// Cancels an invoice task if it is still pending and belongs to the caller's session/company.
    /// Deletes associated AI messages and returns blob info so the caller can clean up storage.
    /// </summary>
    public async Task<InvoiceTaskCancelResult?> CancelAsync(
        Guid taskId,
        Guid sessionId,
        string companyCode,
        string? userId,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT 1
            FROM ai_tasks
            WHERE id = $1
              AND session_id = $2
              AND company_code = $3
              AND task_type = 'invoice'
              AND (status IS NULL OR lower(status) <> 'completed')
              AND ($4 IS NULL OR user_id IS NULL OR user_id = $4)
            FOR UPDATE;
            """;
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(userId) ? DBNull.Value : userId);

        var exists = await cmd.ExecuteScalarAsync(ct);
        if (exists is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await using (var deleteCurrent = conn.CreateCommand())
        {
            deleteCurrent.Transaction = tx;
            deleteCurrent.CommandText = "DELETE FROM ai_messages WHERE task_id = $1";
            deleteCurrent.Parameters.AddWithValue(taskId);
            await deleteCurrent.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteArchive = conn.CreateCommand())
        {
            deleteArchive.Transaction = tx;
            deleteArchive.CommandText = "DELETE FROM ai_messages_archive WHERE task_id = $1";
            deleteArchive.Parameters.AddWithValue(taskId);
            await deleteArchive.ExecuteNonQueryAsync(ct);
        }

        string? blobName = null;
        string? storedPath = null;

        await using (var deleteTask = conn.CreateCommand())
        {
            deleteTask.Transaction = tx;
            deleteTask.CommandText = """
                DELETE FROM ai_tasks
                WHERE id = $1
                  AND session_id = $2
                  AND company_code = $3
                  AND task_type = 'invoice'
                  AND (status IS NULL OR lower(status) <> 'completed')
                  AND ($4 IS NULL OR user_id IS NULL OR user_id = $4)
                RETURNING payload->>'blobName', metadata->>'storedPath';
                """;
            deleteTask.Parameters.AddWithValue(taskId);
            deleteTask.Parameters.AddWithValue(sessionId);
            deleteTask.Parameters.AddWithValue(companyCode);
            deleteTask.Parameters.AddWithValue(string.IsNullOrWhiteSpace(userId) ? DBNull.Value : userId);

            await using var reader = await deleteTask.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            if (!reader.IsDBNull(0))
            {
                blobName = reader.GetString(0);
            }

            if (!reader.IsDBNull(1))
            {
                storedPath = reader.GetString(1);
            }
        }

        await tx.CommitAsync(ct);
        return new InvoiceTaskCancelResult(blobName, storedPath);
    }

    public sealed record InvoiceTask(
        Guid Id,
        Guid? SessionId,
        string CompanyCode,
        string FileId,
        string DocumentSessionId,
        string FileName,
        string? ContentType,
        long Size,
        string? BlobName,
        string? DocumentLabel,
        string? StoredPath,
        string? UserId,
        string Status,
        string? Summary,
        JsonObject? Analysis,
        JsonObject Metadata,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// Creates a new invoice task row for the uploaded document, cloning analysis metadata if provided.
    /// </summary>
    internal async Task<InvoiceTask> CreateAsync(
        Guid sessionId,
        string companyCode,
        string fileId,
        UploadedFileRecord record,
        string documentSessionId,
        JsonObject? analysis,
        string? documentLabel,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var metadata = new JsonObject();
        if (!string.IsNullOrWhiteSpace(record.StoredPath))
        {
            metadata["storedPath"] = record.StoredPath;
        }
        metadata["uploadedAt"] = record.CreatedAt.ToString("O");
        var analysisClone = analysis?.DeepClone().AsObject();
        var summary = BuildSummary(analysisClone);

        var payload = new JsonObject
        {
            ["fileId"] = fileId,
            ["documentSessionId"] = documentSessionId,
            ["fileName"] = record.FileName ?? "uploaded",
            ["contentType"] = string.IsNullOrWhiteSpace(record.ContentType) ? "application/octet-stream" : record.ContentType,
            ["fileSize"] = record.Size,
            ["blobName"] = record.BlobName,
            ["documentLabel"] = documentLabel,
            ["analysis"] = analysisClone
        };

        cmd.CommandText = """
            INSERT INTO ai_tasks
            (id, session_id, company_code, task_type, status, title, summary, user_id, payload, metadata, created_at, updated_at)
            VALUES ($1, $2, $3, 'invoice', 'pending', $4, $5, $6, $7::jsonb, $8::jsonb, now(), now())
            RETURNING id, session_id, company_code, 
                      payload->>'fileId', payload->>'documentSessionId', title, 
                      payload->>'contentType', (payload->>'fileSize')::bigint, payload->>'blobName', 
                      payload->>'documentLabel', metadata->>'storedPath', user_id, status, summary, 
                      payload->'analysis', metadata, created_at, updated_at;
            """;
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(record.FileName ?? "uploaded");
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(summary) ? DBNull.Value : summary);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(record.UserId) ? DBNull.Value : record.UserId);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(payload, JsonOptions));
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new Exception("创建发票任务失败");
        }
        return ReadTask(reader);
    }

    /// <summary>
    /// Lists invoice tasks inside a session (ordered by creation time).
    /// </summary>
    public async Task<IReadOnlyList<InvoiceTask>> ListAsync(Guid sessionId, CancellationToken ct)
    {
        var list = new List<InvoiceTask>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, 
                   payload->>'fileId', payload->>'documentSessionId', title, 
                   payload->>'contentType', (payload->>'fileSize')::bigint, payload->>'blobName', 
                   payload->>'documentLabel', metadata->>'storedPath', user_id, status, summary, 
                   payload->'analysis', metadata, created_at, updated_at
            FROM ai_tasks
            WHERE session_id = $1 AND task_type = 'invoice'
            ORDER BY created_at;
            """;
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadTask(reader));
        }
        return list;
    }

    /// <summary>
    /// Retrieves a specific invoice task by id (returns null when absent).
    /// </summary>
    public async Task<InvoiceTask?> GetAsync(Guid taskId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, 
                   payload->>'fileId', payload->>'documentSessionId', title, 
                   payload->>'contentType', (payload->>'fileSize')::bigint, payload->>'blobName', 
                   payload->>'documentLabel', metadata->>'storedPath', user_id, status, summary, 
                   payload->'analysis', metadata, created_at, updated_at
            FROM ai_tasks
            WHERE id = $1 AND task_type = 'invoice';
            """;
        cmd.Parameters.AddWithValue(taskId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return ReadTask(reader);
    }

    /// <summary>
    /// Updates the task status and optionally merges metadata if new values are provided.
    /// </summary>
    public async Task UpdateStatusAsync(Guid taskId, string status, JsonObject? metadata, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (metadata is null)
        {
            cmd.CommandText = """
                UPDATE ai_tasks
                SET status = $2, updated_at = now()
                WHERE id = $1 AND task_type = 'invoice';
                """;
            cmd.Parameters.AddWithValue(taskId);
            cmd.Parameters.AddWithValue(status);
        }
        else
        {
            cmd.CommandText = """
                UPDATE ai_tasks
                SET status = $2,
                    metadata = metadata || $3::jsonb,
                    updated_at = now()
                WHERE id = $1 AND task_type = 'invoice';
                """;
            cmd.Parameters.AddWithValue(taskId);
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Materializes an <see cref="InvoiceTask"/> from the current data reader row.
    /// </summary>
    private static InvoiceTask ReadTask(NpgsqlDataReader reader)
    {
        var analysisObj = reader.IsDBNull(14)
            ? null
            : JsonNode.Parse(reader.GetString(14))?.AsObject();
        var metadataObj = reader.IsDBNull(15)
            ? new JsonObject()
            : (JsonNode.Parse(reader.GetString(15))?.AsObject() ?? new JsonObject());
        return new InvoiceTask(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? 0L : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            analysisObj,
            metadataObj,
            reader.GetFieldValue<DateTimeOffset>(16),
            reader.GetFieldValue<DateTimeOffset>(17));
    }

    /// <summary>
    /// Builds a short human-readable summary based on the analysis JSON (partner/date/amount).
    /// </summary>
    private static string? BuildSummary(JsonObject? analysis)
    {
        if (analysis is null) return null;
        var parts = new List<string>();
        if (TryGetJsonString(analysis, "partnerName", out var partner) && !string.IsNullOrWhiteSpace(partner))
        {
            parts.Add($"供应方：{partner}");
        }
        if (TryGetJsonString(analysis, "issueDate", out var issueDate) && !string.IsNullOrWhiteSpace(issueDate))
        {
            parts.Add($"日期：{issueDate}");
        }
        var amount = TryGetJsonDecimal(analysis, "totalAmount");
        if (amount.HasValue && amount.Value > 0)
        {
            parts.Add($"含税金额：{amount.Value:0.##}");
        }
        return parts.Count == 0 ? null : string.Join("，", parts);
    }

    /// <summary>
    /// Helper for safely reading a string field from a JsonObject.
    /// </summary>
    private static bool TryGetJsonString(JsonObject obj, string property, out string? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue jsonValue) return false;
        if (!jsonValue.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str)) return false;
        value = str.Trim();
        return true;
    }

    /// <summary>
    /// Helper for safely reading a decimal field from a JsonObject.
    /// </summary>
    private static decimal? TryGetJsonDecimal(JsonObject obj, string property)
    {
        if (!obj.TryGetPropertyValue(property, out var node) || node is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<decimal>(out var dec)) return dec;
        if (jsonValue.TryGetValue<double>(out var dbl)) return Convert.ToDecimal(dbl);
        if (jsonValue.TryGetValue<string>(out var str) && decimal.TryParse(str, out var parsed)) return parsed;
        return null;
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Server.Modules;

public sealed class SalesOrderTaskService
{
    private readonly NpgsqlDataSource _ds;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SalesOrderTaskService(NpgsqlDataSource ds)
    {
        _ds = ds;
    }

    public sealed record SalesOrderTask(
        Guid Id,
        Guid SessionId,
        string CompanyCode,
        string? UserId,
        string Status,
        string? Summary,
        JsonObject Payload,
        JsonObject Metadata,
        Guid? SalesOrderId,
        string? SalesOrderNo,
        string? CustomerCode,
        string? CustomerName,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CompletedAt);

    public async Task<SalesOrderTask> CreateAsync(
        Guid sessionId,
        string companyCode,
        string? userId,
        string status,
        string? summary,
        JsonObject? payload,
        JsonObject? metadata,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_sales_order_tasks
            (id, session_id, company_code, user_id, status, summary, payload, metadata, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb, now(), now())
            RETURNING id, session_id, company_code, user_id, status, summary, payload, metadata,
                      sales_order_id, sales_order_no, customer_code, customer_name,
                      created_at, updated_at, completed_at;
            """;
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(userId) ? (object)DBNull.Value : userId);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(status) ? "pending" : status);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(summary) ? (object)DBNull.Value : summary);
        cmd.Parameters.AddWithValue(payload is null ? (object)DBNull.Value : JsonSerializer.Serialize(payload, JsonOptions));
        cmd.Parameters.AddWithValue(metadata is null ? (object)DBNull.Value : JsonSerializer.Serialize(metadata, JsonOptions));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new Exception("创建受注任务失败");
        }
        return ReadTask(reader);
    }

    public async Task<SalesOrderTask?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, user_id, status, summary, payload, metadata,
                   sales_order_id, sales_order_no, customer_code, customer_name,
                   created_at, updated_at, completed_at
            FROM ai_sales_order_tasks
            WHERE id = $1;
            """;
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return ReadTask(reader);
    }

    public async Task<IReadOnlyList<SalesOrderTask>> ListAsync(Guid sessionId, CancellationToken ct)
    {
        var list = new List<SalesOrderTask>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, company_code, user_id, status, summary, payload, metadata,
                   sales_order_id, sales_order_no, customer_code, customer_name,
                   created_at, updated_at, completed_at
            FROM ai_sales_order_tasks
            WHERE session_id = $1
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

    public async Task UpdateStatusAsync(Guid id, string status, JsonObject? metadata, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (metadata is null)
        {
            cmd.CommandText = """
                UPDATE ai_sales_order_tasks
                SET status = $2,
                    updated_at = now()
                WHERE id = $1;
                """;
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(status);
        }
        else
        {
            cmd.CommandText = """
                UPDATE ai_sales_order_tasks
                SET status = $2,
                    metadata = COALESCE(metadata, '{}'::jsonb) || $3::jsonb,
                    updated_at = now()
                WHERE id = $1;
                """;
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(JsonSerializer.Serialize(metadata, JsonOptions));
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(
        Guid id,
        string status,
        JsonObject? payload,
        JsonObject? metadata,
        Guid? salesOrderId,
        string? salesOrderNo,
        string? customerCode,
        string? customerName,
        string? summary,
        bool markCompleted,
        CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ai_sales_order_tasks
            SET status = $2,
                payload = COALESCE($3::jsonb, payload),
                metadata = CASE
                    WHEN $4::jsonb IS NULL THEN metadata
                    ELSE COALESCE(metadata, '{}'::jsonb) || $4::jsonb
                END,
                sales_order_id = $5,
                sales_order_no = $6,
                customer_code = $7,
                customer_name = $8,
                summary = COALESCE($9, summary),
                updated_at = now(),
                completed_at = CASE WHEN $10 THEN now() ELSE completed_at END
            WHERE id = $1;
            """;
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(payload is null ? (object)DBNull.Value : JsonSerializer.Serialize(payload, JsonOptions));
        cmd.Parameters.AddWithValue(metadata is null ? (object)DBNull.Value : JsonSerializer.Serialize(metadata, JsonOptions));
        cmd.Parameters.AddWithValue(salesOrderId.HasValue ? (object)salesOrderId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(salesOrderNo) ? (object)DBNull.Value : salesOrderNo);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(customerCode) ? (object)DBNull.Value : customerCode);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(customerName) ? (object)DBNull.Value : customerName);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(summary) ? (object)DBNull.Value : summary);
        cmd.Parameters.AddWithValue(markCompleted);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SalesOrderTask ReadTask(NpgsqlDataReader reader)
    {
        var payload = reader.IsDBNull(6)
            ? new JsonObject()
            : (JsonNode.Parse(reader.GetString(6))?.AsObject() ?? new JsonObject());
        var metadata = reader.IsDBNull(7)
            ? new JsonObject()
            : (JsonNode.Parse(reader.GetString(7))?.AsObject() ?? new JsonObject());

        return new SalesOrderTask(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            payload,
            metadata,
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetFieldValue<DateTimeOffset>(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));
    }
}


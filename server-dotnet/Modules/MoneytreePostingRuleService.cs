using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class MoneytreePostingRuleService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<MoneytreePostingRuleService> _logger;

    public MoneytreePostingRuleService(NpgsqlDataSource ds, ILogger<MoneytreePostingRuleService> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MoneytreePostingRule>> ListAsync(string companyCode, bool includeInactive, CancellationToken ct = default)
    {
        var results = new List<MoneytreePostingRule>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, company_code, title, description, priority, matcher, action, is_active, created_by, updated_by, created_at, updated_at
FROM moneytree_posting_rules
WHERE company_code = $1 AND ($2 OR is_active = true)
ORDER BY priority ASC, updated_at DESC";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(includeInactive);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<MoneytreePostingRule?> GetAsync(string companyCode, Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, company_code, title, description, priority, matcher, action, is_active, created_by, updated_by, created_at, updated_at
FROM moneytree_posting_rules
WHERE company_code = $1 AND id = $2
LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return Map(reader);
        }
        return null;
    }

    public async Task<MoneytreePostingRule> CreateAsync(string companyCode, MoneytreePostingRuleUpsert payload, Auth.UserCtx user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.Title))
            throw new ArgumentException("title is required", nameof(payload));
        var now = DateTimeOffset.UtcNow;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO moneytree_posting_rules (company_code, title, description, priority, matcher, action, is_active, created_by, updated_by, created_at, updated_at)
VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $8, $9, $9)
RETURNING id, company_code, title, description, priority, matcher, action, is_active, created_by, updated_by, created_at, updated_at";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.Title);
        cmd.Parameters.AddWithValue((object?)payload.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Priority ?? 100);
        cmd.Parameters.AddWithValue(JsonSerialize(payload.Matcher));
        cmd.Parameters.AddWithValue(JsonSerialize(payload.Action));
        cmd.Parameters.AddWithValue(payload.IsActive ?? true);
        cmd.Parameters.AddWithValue((object?)user.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(now);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return Map(reader);
        }

        throw new InvalidOperationException("failed to insert moneytree posting rule");
    }

    public async Task<MoneytreePostingRule?> UpdateAsync(string companyCode, Guid id, MoneytreePostingRuleUpsert payload, Auth.UserCtx user, CancellationToken ct = default)
    {
        var existing = await GetAsync(companyCode, id, ct);
        if (existing is null) return null;
        var updatedAt = DateTimeOffset.UtcNow;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE moneytree_posting_rules
SET title = COALESCE($3, title),
    description = COALESCE($4, description),
    priority = COALESCE($5, priority),
    matcher = COALESCE($6::jsonb, matcher),
    action = COALESCE($7::jsonb, action),
    is_active = COALESCE($8, is_active),
    updated_by = $2,
    updated_at = $9
WHERE company_code = $1 AND id = $10
RETURNING id, company_code, title, description, priority, matcher, action, is_active, created_by, updated_by, created_at, updated_at";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue((object?)user.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)payload.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)payload.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Priority.HasValue ? payload.Priority.Value : existing.Priority);
        cmd.Parameters.AddWithValue(payload.Matcher is not null ? JsonSerialize(payload.Matcher) : DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Action is not null ? JsonSerialize(payload.Action) : DBNull.Value);
        cmd.Parameters.AddWithValue(payload.IsActive.HasValue ? payload.IsActive.Value : existing.IsActive);
        cmd.Parameters.AddWithValue(updatedAt);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return Map(reader);
        }

        return null;
    }

    private MoneytreePostingRule Map(NpgsqlDataReader reader)
    {
        var matcherJson = reader.GetFieldValue<string>(5);
        var actionJson = reader.GetFieldValue<string>(6);
        return new MoneytreePostingRule(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            JsonNode.Parse(matcherJson) ?? new JsonObject(),
            JsonNode.Parse(actionJson) ?? new JsonObject(),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<DateTimeOffset>(11));
    }

    private static string JsonSerialize(JsonNode? node)
    {
        if (node is null) return "{}";
        return node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public sealed record MoneytreePostingRule(
        Guid Id,
        string CompanyCode,
        string Title,
        string? Description,
        int Priority,
        JsonNode Matcher,
        JsonNode Action,
        bool IsActive,
        string? CreatedBy,
        string? UpdatedBy,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record MoneytreePostingRuleUpsert(
        string? Title,
        string? Description,
        int? Priority,
        JsonNode? Matcher,
        JsonNode? Action,
        bool? IsActive);
}


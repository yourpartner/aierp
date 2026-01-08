using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Server.Modules;

/// <summary>
/// Represents a stored workflow rule definition that can be executed by the automation engine.
/// </summary>
public sealed record WorkflowRule(Guid Id, string CompanyCode, string RuleKey, string Title, string Description, string Instructions, IReadOnlyList<WorkflowRuleAction> Actions, int Priority, DateTimeOffset UpdatedAt, bool IsActive);

/// <summary>
/// Represents a single action block inside a workflow rule (e.g., voucher.autoCreate).
/// </summary>
public sealed record WorkflowRuleAction(string Type, JsonObject Params);

/// <summary>
/// Draft payload posted by the UI when saving rule edits.
/// </summary>
public sealed record WorkflowRuleDraft(string RuleKey, string Title, string Description, string Instructions, JsonArray Actions, int Priority, bool IsActive);

/// <summary>
/// CRUD-style service that stores workflow rules in <c>ai_workflow_rules</c> and exposes helpers for automation services.
/// </summary>
public sealed class WorkflowRulesService
{
    private readonly NpgsqlDataSource _dataSource;

    public WorkflowRulesService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Lists all workflow rules for a company ordered by activity/priority.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowRule>> ListAsync(string companyCode, CancellationToken ct)
    {
        var list = new List<WorkflowRule>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, company_code, rule_key, title, description, instructions, actions, priority, updated_at, is_active FROM ai_workflow_rules WHERE company_code=$1 ORDER BY is_active DESC, priority, updated_at DESC";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadRule(reader));
        }
        return list;
    }

    /// <summary>
    /// Returns only active rules sorted by priority (used by runtime automation).
    /// </summary>
    public async Task<IReadOnlyList<WorkflowRule>> ListActiveAsync(string companyCode, CancellationToken ct)
    {
        var list = new List<WorkflowRule>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, company_code, rule_key, title, description, instructions, actions, priority, updated_at, is_active FROM ai_workflow_rules WHERE company_code=$1 AND is_active=TRUE ORDER BY priority, updated_at DESC";
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadRule(reader));
        }
        return list;
    }

    /// <summary>
    /// Fetches a rule by key (null if not found).
    /// </summary>
    public async Task<WorkflowRule?> GetAsync(string companyCode, string ruleKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, company_code, rule_key, title, description, instructions, actions, priority, updated_at, is_active FROM ai_workflow_rules WHERE company_code=$1 AND rule_key=$2 LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(ruleKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRule(reader);
        }
        return null;
    }

    /// <summary>
    /// Inserts or updates a workflow rule definition using an UPSERT statement.
    /// </summary>
    public async Task<WorkflowRule> UpsertAsync(string companyCode, string ruleKey, string title, string description, string instructions, JsonNode? actionsNode, int priority, bool isActive, CancellationToken ct)
    {
        var actionsJson = actionsNode is null ? "[]" : actionsNode.ToJsonString();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ai_workflow_rules (company_code, rule_key, title, description, instructions, actions, priority, is_active)
VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7,$8)
ON CONFLICT (company_code, rule_key) DO UPDATE SET
  title = EXCLUDED.title,
  description = EXCLUDED.description,
  instructions = EXCLUDED.instructions,
  actions = EXCLUDED.actions,
  priority = EXCLUDED.priority,
  is_active = EXCLUDED.is_active,
  updated_at = now()
RETURNING id, company_code, rule_key, title, description, instructions, actions, priority, updated_at, is_active;";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(ruleKey);
        cmd.Parameters.AddWithValue(title ?? string.Empty);
        cmd.Parameters.AddWithValue(description ?? string.Empty);
        cmd.Parameters.AddWithValue(instructions ?? string.Empty);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, actionsJson);
        cmd.Parameters.AddWithValue(priority);
        cmd.Parameters.AddWithValue(isActive);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadRule(reader);
        }
        throw new Exception("保存规则失败");
    }

    /// <summary>
    /// Soft-deletes a rule by marking it inactive (keeps history for audit).
    /// </summary>
    public async Task DeleteAsync(string companyCode, string ruleKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ai_workflow_rules SET is_active = FALSE, updated_at = now() WHERE company_code=$1 AND rule_key=$2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(ruleKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Generates a slug-style rule key from the provided title.
    /// </summary>
    public static string SuggestRuleKey(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return $"rule-{Guid.NewGuid():N}";
        var slug = title.Trim().ToLowerInvariant();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, "[^a-z0-9]+", ".");
        slug = slug.Trim('.');
        if (string.IsNullOrWhiteSpace(slug)) slug = $"rule-{Guid.NewGuid():N}";
        return slug;
    }

    /// <summary>
    /// Materializes a <see cref="WorkflowRule"/> from the given data reader row.
    /// </summary>
    private static WorkflowRule ReadRule(NpgsqlDataReader reader)
    {
        var id = reader.GetGuid(0);
        var company = reader.GetString(1);
        var ruleKey = reader.GetString(2);
        var title = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var description = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var instructions = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        var actions = new List<WorkflowRuleAction>();
        if (!reader.IsDBNull(6))
        {
            try
            {
                var json = reader.GetString(6);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                        var paramsNode = item.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object
                            ? JsonNode.Parse(p.GetRawText())?.AsObject()
                            : new JsonObject();
                        if (paramsNode is null) paramsNode = new JsonObject();
                        actions.Add(new WorkflowRuleAction(type, paramsNode));
                    }
                }
            }
            catch
            {
                // ignore malformed actions
            }
        }
        var priority = reader.IsDBNull(7) ? 100 : reader.GetInt32(7);
        var updatedAt = reader.IsDBNull(8) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(8);
        var isActive = !reader.IsDBNull(9) && reader.GetBoolean(9);
        return new WorkflowRule(id, company, ruleKey, title, description, instructions, actions.AsReadOnly(), priority, updatedAt, isActive);
    }
}


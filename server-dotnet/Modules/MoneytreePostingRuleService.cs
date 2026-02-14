using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 银行记账规则服务 - 现在从 agent_skill_rules 表读取（bank_auto_booking skill）。
/// 保持与旧接口兼容，MoneytreePostingService 无需修改调用代码。
/// </summary>
public sealed class MoneytreePostingRuleService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<MoneytreePostingRuleService> _logger;
    private const string BankSkillKey = "bank_auto_booking";

    public MoneytreePostingRuleService(NpgsqlDataSource ds, ILogger<MoneytreePostingRuleService> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    /// <summary>
    /// 从 agent_skill_rules 读取银行记账规则（替代旧的 moneytree_posting_rules）。
    /// </summary>
    public async Task<IReadOnlyList<MoneytreePostingRule>> ListAsync(string companyCode, bool includeInactive, CancellationToken ct = default)
    {
        var results = new List<MoneytreePostingRule>();
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT r.id, r.name, r.conditions, r.actions, r.priority, r.is_active, r.created_at, r.updated_at
FROM agent_skill_rules r
INNER JOIN agent_skills s ON s.id = r.skill_id
WHERE s.skill_key = $1 AND ($2 OR r.is_active = true)
ORDER BY r.priority ASC, r.updated_at DESC";
        cmd.Parameters.AddWithValue(BankSkillKey);
        cmd.Parameters.AddWithValue(includeInactive);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapFromSkillRule(reader, companyCode));
        }

        _logger.LogDebug("[MoneytreePostingRuleService] Loaded {Count} rules from agent_skill_rules for {SkillKey}", results.Count, BankSkillKey);
        return results;
    }

    /// <summary>
    /// 按 ID 获取规则。
    /// </summary>
    public async Task<MoneytreePostingRule?> GetAsync(string companyCode, Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT r.id, r.name, r.conditions, r.actions, r.priority, r.is_active, r.created_at, r.updated_at
FROM agent_skill_rules r
INNER JOIN agent_skills s ON s.id = r.skill_id
WHERE s.skill_key = $1 AND r.id = $2
LIMIT 1";
        cmd.Parameters.AddWithValue(BankSkillKey);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFromSkillRule(reader, companyCode);
        }
        return null;
    }

    /// <summary>
    /// 创建规则（写入 agent_skill_rules）。
    /// </summary>
    public async Task<MoneytreePostingRule> CreateAsync(string companyCode, MoneytreePostingRuleUpsert payload, Auth.UserCtx user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.Title))
            throw new ArgumentException("title is required", nameof(payload));

        var skillId = await GetBankSkillIdAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var ruleKey = GenerateRuleKey(payload.Title);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO agent_skill_rules (skill_id, rule_key, name, conditions, actions, priority, is_active, created_at, updated_at)
VALUES ($1, $2, $3, $4::jsonb, $5::jsonb, $6, $7, $8, $8)
RETURNING id, name, conditions, actions, priority, is_active, created_at, updated_at";
        cmd.Parameters.AddWithValue(skillId);
        cmd.Parameters.AddWithValue(ruleKey);
        cmd.Parameters.AddWithValue(payload.Title);
        cmd.Parameters.AddWithValue(JsonSerialize(payload.Matcher));
        cmd.Parameters.AddWithValue(JsonSerialize(payload.Action));
        cmd.Parameters.AddWithValue(payload.Priority ?? 100);
        cmd.Parameters.AddWithValue(payload.IsActive ?? true);
        cmd.Parameters.AddWithValue(now);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFromSkillRule(reader, companyCode);
        }

        throw new InvalidOperationException("failed to insert agent_skill_rule");
    }

    /// <summary>
    /// 更新规则（修改 agent_skill_rules）。
    /// </summary>
    public async Task<MoneytreePostingRule?> UpdateAsync(string companyCode, Guid id, MoneytreePostingRuleUpsert payload, Auth.UserCtx user, CancellationToken ct = default)
    {
        var existing = await GetAsync(companyCode, id, ct);
        if (existing is null) return null;

        var updatedAt = DateTimeOffset.UtcNow;
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE agent_skill_rules
SET name = COALESCE($2, name),
    conditions = COALESCE($3::jsonb, conditions),
    actions = COALESCE($4::jsonb, actions),
    priority = COALESCE($5, priority),
    is_active = COALESCE($6, is_active),
    updated_at = $7
WHERE id = $1
RETURNING id, name, conditions, actions, priority, is_active, created_at, updated_at";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue((object?)payload.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Matcher is not null ? JsonSerialize(payload.Matcher) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Action is not null ? JsonSerialize(payload.Action) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(payload.Priority.HasValue ? payload.Priority.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(payload.IsActive.HasValue ? payload.IsActive.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue(updatedAt);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFromSkillRule(reader, companyCode);
        }

        return null;
    }

    /// <summary>
    /// 删除（软删除：设为 inactive）。
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_skill_rules SET is_active = false, updated_at = now() WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // --- Mapping ---

    private MoneytreePostingRule MapFromSkillRule(NpgsqlDataReader reader, string companyCode)
    {
        // Columns: id, name, conditions, actions, priority, is_active, created_at, updated_at
        var conditionsJson = reader.GetFieldValue<string>(2);
        var actionsJson = reader.GetFieldValue<string>(3);
        // 从 actions 中提取 ruleDescription（迁移时写入的）
        string? description = null;
        try
        {
            var actionsObj = JsonNode.Parse(actionsJson) as JsonObject;
            if (actionsObj != null && actionsObj.TryGetPropertyValue("ruleDescription", out var descNode))
                description = descNode?.GetValue<string>();
        }
        catch { /* ignore */ }

        return new MoneytreePostingRule(
            reader.GetGuid(0),
            companyCode,
            reader.GetString(1), // name → Title
            description,
            reader.GetInt32(4),  // priority
            JsonNode.Parse(conditionsJson) ?? new JsonObject(), // conditions → Matcher
            JsonNode.Parse(actionsJson) ?? new JsonObject(),    // actions → Action
            reader.GetBoolean(5), // is_active
            null, // created_by not in agent_skill_rules
            null, // updated_by not in agent_skill_rules
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private async Task<Guid> GetBankSkillIdAsync(CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM agent_skills WHERE skill_key = $1 LIMIT 1";
        cmd.Parameters.AddWithValue(BankSkillKey);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : throw new InvalidOperationException($"Skill '{BankSkillKey}' not found");
    }

    private static string GenerateRuleKey(string title)
    {
        // 生成简洁的 rule_key
        var key = title.ToLowerInvariant()
            .Replace(" ", "_").Replace("　", "_")
            .Replace("-", "_").Replace("（", "").Replace("）", "")
            .Replace("(", "").Replace(")", "");
        if (key.Length > 50) key = key[..50];
        return "bank_" + key;
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

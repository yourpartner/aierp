using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Server.Modules;

/// <summary>
/// 统一 Agent Skills 管理服务
/// 每个 Skill = 一个完整的业务场景专家（Prompt + Rules + Examples + Tools + Config）
/// </summary>
public sealed class AgentSkillService
{
    private readonly NpgsqlDataSource _dataSource;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AgentSkillService(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // ====================== Data Models ======================

    public sealed record AgentSkillRecord(
        Guid Id,
        string? CompanyCode,
        string SkillKey,
        string Name,
        string? Description,
        string Category,
        string? Icon,
        JsonObject? Triggers,
        string? SystemPrompt,
        string? ExtractionPrompt,
        string? FollowupPrompt,
        string[]? EnabledTools,
        JsonObject? ModelConfig,
        JsonObject? BehaviorConfig,
        int Priority,
        bool IsActive,
        int Version,
        Guid? ParentId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record SkillRuleRecord(
        Guid Id,
        Guid SkillId,
        string? RuleKey,
        string Name,
        JsonObject Conditions,
        JsonObject Actions,
        int Priority,
        bool IsActive,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public sealed record SkillExampleRecord(
        Guid Id,
        Guid SkillId,
        string? Name,
        string InputType,
        JsonObject InputData,
        JsonObject ExpectedOutput,
        bool IsActive,
        DateTimeOffset CreatedAt);

    public sealed record SkillTestRecord(
        Guid Id,
        Guid SkillId,
        string Name,
        JsonObject TestInput,
        JsonObject? Expected,
        JsonObject? LastResult,
        DateTimeOffset? LastRunAt,
        bool? Passed,
        DateTimeOffset CreatedAt);

    // ====================== Input DTOs ======================

    public sealed record SkillInput(
        Guid? Id,
        string SkillKey,
        string Name,
        string? Description,
        string? Category,
        string? Icon,
        JsonObject? Triggers,
        string? SystemPrompt,
        string? ExtractionPrompt,
        string? FollowupPrompt,
        string[]? EnabledTools,
        JsonObject? ModelConfig,
        JsonObject? BehaviorConfig,
        int? Priority,
        bool? IsActive);

    public sealed record RuleInput(
        Guid? Id,
        string? RuleKey,
        string Name,
        JsonObject Conditions,
        JsonObject Actions,
        int? Priority,
        bool? IsActive);

    public sealed record ExampleInput(
        Guid? Id,
        string? Name,
        string? InputType,
        JsonObject InputData,
        JsonObject ExpectedOutput,
        bool? IsActive);

    // ====================== Skills CRUD ======================

    /// <summary>获取指定公司可用的所有 Skills（全局 + 公司定制，公司覆盖全局同 key）</summary>
    public async Task<IReadOnlyList<AgentSkillRecord>> ListAsync(string companyCode, bool includeInactive, CancellationToken ct)
    {
        var all = new List<AgentSkillRecord>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM agent_skills
            WHERE (company_code IS NULL OR company_code = $1)
            ORDER BY priority, created_at";
        cmd.Parameters.AddWithValue(companyCode);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            all.Add(ReadSkill(r));

        // 公司定制覆盖全局同 key
        var grouped = all.GroupBy(s => s.SkillKey);
        var result = new List<AgentSkillRecord>();
        foreach (var group in grouped)
        {
            var companySpecific = group.FirstOrDefault(s => s.CompanyCode == companyCode);
            var global = group.FirstOrDefault(s => s.CompanyCode == null);
            var chosen = companySpecific ?? global;
            if (chosen != null && (includeInactive || chosen.IsActive))
                result.Add(chosen);
        }
        return result.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>获取单个 Skill（优先公司定制，回退全局）</summary>
    public async Task<AgentSkillRecord?> GetByKeyAsync(string companyCode, string skillKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // 先找公司定制
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM agent_skills WHERE company_code = $1 AND skill_key = $2";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(skillKey);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) return ReadSkill(r);
        }
        // 回退全局
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM agent_skills WHERE company_code IS NULL AND skill_key = $1";
            cmd.Parameters.AddWithValue(skillKey);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) return ReadSkill(r);
        }
        return null;
    }

    public async Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agent_skills WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSkill(r) : null;
    }

    public async Task<AgentSkillRecord> UpsertAsync(string? companyCode, SkillInput input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();

        var id = input.Id ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var triggersJson = input.Triggers != null ? JsonSerializer.Serialize(input.Triggers, JsonOpts) : "{}";
        var modelJson = input.ModelConfig != null ? JsonSerializer.Serialize(input.ModelConfig, JsonOpts) : "{}";
        var behaviorJson = input.BehaviorConfig != null ? JsonSerializer.Serialize(input.BehaviorConfig, JsonOpts) : "{}";

        cmd.CommandText = @"
            INSERT INTO agent_skills (id, company_code, skill_key, name, description, category, icon,
                triggers, system_prompt, extraction_prompt, followup_prompt,
                enabled_tools, model_config, behavior_config,
                priority, is_active, version, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10, $11, $12, $13::jsonb, $14::jsonb, $15, $16, 1, $17, $17)
            ON CONFLICT ON CONSTRAINT uq_agent_skills_company_key
            DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                category = EXCLUDED.category,
                icon = EXCLUDED.icon,
                triggers = EXCLUDED.triggers,
                system_prompt = EXCLUDED.system_prompt,
                extraction_prompt = EXCLUDED.extraction_prompt,
                followup_prompt = EXCLUDED.followup_prompt,
                enabled_tools = EXCLUDED.enabled_tools,
                model_config = EXCLUDED.model_config,
                behavior_config = EXCLUDED.behavior_config,
                priority = EXCLUDED.priority,
                is_active = EXCLUDED.is_active,
                version = agent_skills.version + 1,
                updated_at = EXCLUDED.updated_at
            RETURNING *";

        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(companyCode != null ? (object)companyCode : DBNull.Value);
        cmd.Parameters.AddWithValue(input.SkillKey);
        cmd.Parameters.AddWithValue(input.Name);
        cmd.Parameters.AddWithValue(input.Description != null ? (object)input.Description : DBNull.Value);
        cmd.Parameters.AddWithValue(input.Category ?? "general");
        cmd.Parameters.AddWithValue(input.Icon != null ? (object)input.Icon : DBNull.Value);
        cmd.Parameters.AddWithValue(triggersJson);
        cmd.Parameters.AddWithValue(input.SystemPrompt != null ? (object)input.SystemPrompt : DBNull.Value);
        cmd.Parameters.AddWithValue(input.ExtractionPrompt != null ? (object)input.ExtractionPrompt : DBNull.Value);
        cmd.Parameters.AddWithValue(input.FollowupPrompt != null ? (object)input.FollowupPrompt : DBNull.Value);
        cmd.Parameters.AddWithValue(input.EnabledTools ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue(modelJson);
        cmd.Parameters.AddWithValue(behaviorJson);
        cmd.Parameters.AddWithValue(input.Priority ?? 100);
        cmd.Parameters.AddWithValue(input.IsActive ?? true);
        cmd.Parameters.AddWithValue(now);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadSkill(r);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_skills WHERE id = $1";
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ====================== Rules CRUD ======================

    public async Task<IReadOnlyList<SkillRuleRecord>> ListRulesAsync(Guid skillId, CancellationToken ct)
    {
        var list = new List<SkillRuleRecord>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agent_skill_rules WHERE skill_id = $1 ORDER BY priority, created_at";
        cmd.Parameters.AddWithValue(skillId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadRule(r));
        return list;
    }

    public async Task<SkillRuleRecord> UpsertRuleAsync(Guid skillId, RuleInput input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var id = input.Id ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var condJson = JsonSerializer.Serialize(input.Conditions, JsonOpts);
        var actJson = JsonSerializer.Serialize(input.Actions, JsonOpts);

        cmd.CommandText = @"
            INSERT INTO agent_skill_rules (id, skill_id, rule_key, name, conditions, actions, priority, is_active, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8, $9, $9)
            ON CONFLICT (id) DO UPDATE SET
                rule_key = EXCLUDED.rule_key,
                name = EXCLUDED.name,
                conditions = EXCLUDED.conditions,
                actions = EXCLUDED.actions,
                priority = EXCLUDED.priority,
                is_active = EXCLUDED.is_active,
                updated_at = EXCLUDED.updated_at
            RETURNING *";

        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(skillId);
        cmd.Parameters.AddWithValue(input.RuleKey != null ? (object)input.RuleKey : DBNull.Value);
        cmd.Parameters.AddWithValue(input.Name);
        cmd.Parameters.AddWithValue(condJson);
        cmd.Parameters.AddWithValue(actJson);
        cmd.Parameters.AddWithValue(input.Priority ?? 100);
        cmd.Parameters.AddWithValue(input.IsActive ?? true);
        cmd.Parameters.AddWithValue(now);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadRule(r);
    }

    public async Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_skill_rules WHERE id = $1";
        cmd.Parameters.AddWithValue(ruleId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ====================== Examples CRUD ======================

    public async Task<IReadOnlyList<SkillExampleRecord>> ListExamplesAsync(Guid skillId, CancellationToken ct)
    {
        var list = new List<SkillExampleRecord>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agent_skill_examples WHERE skill_id = $1 ORDER BY created_at";
        cmd.Parameters.AddWithValue(skillId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(ReadExample(r));
        return list;
    }

    public async Task<SkillExampleRecord> UpsertExampleAsync(Guid skillId, ExampleInput input, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var id = input.Id ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        cmd.CommandText = @"
            INSERT INTO agent_skill_examples (id, skill_id, name, input_type, input_data, expected_output, is_active, created_at)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6::jsonb, $7, $8)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                input_type = EXCLUDED.input_type,
                input_data = EXCLUDED.input_data,
                expected_output = EXCLUDED.expected_output,
                is_active = EXCLUDED.is_active
            RETURNING *";

        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(skillId);
        cmd.Parameters.AddWithValue(input.Name != null ? (object)input.Name : DBNull.Value);
        cmd.Parameters.AddWithValue(input.InputType ?? "text");
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(input.InputData, JsonOpts));
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(input.ExpectedOutput, JsonOpts));
        cmd.Parameters.AddWithValue(input.IsActive ?? true);
        cmd.Parameters.AddWithValue(now);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return ReadExample(r);
    }

    public async Task<bool> DeleteExampleAsync(Guid exampleId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_skill_examples WHERE id = $1";
        cmd.Parameters.AddWithValue(exampleId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ====================== Effective Config (Merged) ======================

    /// <summary>
    /// 获取合并后的有效 Skill 配置：全局模板 + 公司覆盖字段合并
    /// 如果公司有定制版本则以定制为主，未定制的字段从全局模板继承
    /// </summary>
    public async Task<AgentSkillRecord?> GetEffectiveSkillAsync(string companyCode, string skillKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        AgentSkillRecord? global = null;
        AgentSkillRecord? company = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM agent_skills WHERE skill_key = $1 AND (company_code IS NULL OR company_code = $2) ORDER BY company_code NULLS FIRST";
            cmd.Parameters.AddWithValue(skillKey);
            cmd.Parameters.AddWithValue(companyCode);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var rec = ReadSkill(r);
                if (rec.CompanyCode == null) global = rec;
                else company = rec;
            }
        }

        if (company == null) return global;
        if (global == null) return company;

        // 合并：公司配置覆盖全局，空字段回退全局
        return company with
        {
            SystemPrompt = !string.IsNullOrWhiteSpace(company.SystemPrompt) ? company.SystemPrompt : global.SystemPrompt,
            ExtractionPrompt = !string.IsNullOrWhiteSpace(company.ExtractionPrompt) ? company.ExtractionPrompt : global.ExtractionPrompt,
            FollowupPrompt = !string.IsNullOrWhiteSpace(company.FollowupPrompt) ? company.FollowupPrompt : global.FollowupPrompt,
            EnabledTools = company.EnabledTools is { Length: > 0 } ? company.EnabledTools : global.EnabledTools,
            ModelConfig = (company.ModelConfig?.Count ?? 0) > 0 ? company.ModelConfig : global.ModelConfig,
            BehaviorConfig = (company.BehaviorConfig?.Count ?? 0) > 0 ? company.BehaviorConfig : global.BehaviorConfig,
            Triggers = (company.Triggers?.Count ?? 0) > 0 ? company.Triggers : global.Triggers,
            ParentId = global.Id
        };
    }

    /// <summary>获取 Skill 的合并规则列表（公司定制 + 全局规则）</summary>
    public async Task<IReadOnlyList<SkillRuleRecord>> GetEffectiveRulesAsync(string companyCode, string skillKey, CancellationToken ct)
    {
        var skill = await GetEffectiveSkillAsync(companyCode, skillKey, ct);
        if (skill == null) return Array.Empty<SkillRuleRecord>();

        var rules = new List<SkillRuleRecord>();
        // 如果有公司定制版本，先加载公司规则
        if (skill.CompanyCode != null)
        {
            rules.AddRange(await ListRulesAsync(skill.Id, ct));
        }
        // 如果有全局模板，加载全局规则（排除已被公司覆盖的 rule_key）
        if (skill.ParentId.HasValue)
        {
            var globalRules = await ListRulesAsync(skill.ParentId.Value, ct);
            var overriddenKeys = new HashSet<string>(rules.Where(r => r.RuleKey != null).Select(r => r.RuleKey!));
            rules.AddRange(globalRules.Where(r => r.RuleKey == null || !overriddenKeys.Contains(r.RuleKey)));
        }
        return rules.OrderBy(r => r.Priority).ToList();
    }

    // ====================== Reader Helpers ======================

    private static AgentSkillRecord ReadSkill(NpgsqlDataReader r)
    {
        var triggersStr = r.IsDBNull(r.GetOrdinal("triggers")) ? "{}" : r.GetString(r.GetOrdinal("triggers"));
        var modelStr = r.IsDBNull(r.GetOrdinal("model_config")) ? "{}" : r.GetString(r.GetOrdinal("model_config"));
        var behaviorStr = r.IsDBNull(r.GetOrdinal("behavior_config")) ? "{}" : r.GetString(r.GetOrdinal("behavior_config"));

        return new AgentSkillRecord(
            Id: r.GetGuid(r.GetOrdinal("id")),
            CompanyCode: r.IsDBNull(r.GetOrdinal("company_code")) ? null : r.GetString(r.GetOrdinal("company_code")),
            SkillKey: r.GetString(r.GetOrdinal("skill_key")),
            Name: r.GetString(r.GetOrdinal("name")),
            Description: r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
            Category: r.IsDBNull(r.GetOrdinal("category")) ? "general" : r.GetString(r.GetOrdinal("category")),
            Icon: r.IsDBNull(r.GetOrdinal("icon")) ? null : r.GetString(r.GetOrdinal("icon")),
            Triggers: JsonNode.Parse(triggersStr) as JsonObject,
            SystemPrompt: r.IsDBNull(r.GetOrdinal("system_prompt")) ? null : r.GetString(r.GetOrdinal("system_prompt")),
            ExtractionPrompt: r.IsDBNull(r.GetOrdinal("extraction_prompt")) ? null : r.GetString(r.GetOrdinal("extraction_prompt")),
            FollowupPrompt: r.IsDBNull(r.GetOrdinal("followup_prompt")) ? null : r.GetString(r.GetOrdinal("followup_prompt")),
            EnabledTools: r.IsDBNull(r.GetOrdinal("enabled_tools")) ? null : (string[])r.GetValue(r.GetOrdinal("enabled_tools")),
            ModelConfig: JsonNode.Parse(modelStr) as JsonObject,
            BehaviorConfig: JsonNode.Parse(behaviorStr) as JsonObject,
            Priority: r.GetInt32(r.GetOrdinal("priority")),
            IsActive: r.GetBoolean(r.GetOrdinal("is_active")),
            Version: r.GetInt32(r.GetOrdinal("version")),
            ParentId: r.IsDBNull(r.GetOrdinal("parent_id")) ? null : r.GetGuid(r.GetOrdinal("parent_id")),
            CreatedAt: r.GetDateTime(r.GetOrdinal("created_at")),
            UpdatedAt: r.GetDateTime(r.GetOrdinal("updated_at")));
    }

    private static SkillRuleRecord ReadRule(NpgsqlDataReader r)
    {
        return new SkillRuleRecord(
            Id: r.GetGuid(r.GetOrdinal("id")),
            SkillId: r.GetGuid(r.GetOrdinal("skill_id")),
            RuleKey: r.IsDBNull(r.GetOrdinal("rule_key")) ? null : r.GetString(r.GetOrdinal("rule_key")),
            Name: r.GetString(r.GetOrdinal("name")),
            Conditions: JsonNode.Parse(r.IsDBNull(r.GetOrdinal("conditions")) ? "{}" : r.GetString(r.GetOrdinal("conditions"))) as JsonObject ?? new JsonObject(),
            Actions: JsonNode.Parse(r.IsDBNull(r.GetOrdinal("actions")) ? "{}" : r.GetString(r.GetOrdinal("actions"))) as JsonObject ?? new JsonObject(),
            Priority: r.GetInt32(r.GetOrdinal("priority")),
            IsActive: r.GetBoolean(r.GetOrdinal("is_active")),
            CreatedAt: r.GetDateTime(r.GetOrdinal("created_at")),
            UpdatedAt: r.GetDateTime(r.GetOrdinal("updated_at")));
    }

    private static SkillExampleRecord ReadExample(NpgsqlDataReader r)
    {
        return new SkillExampleRecord(
            Id: r.GetGuid(r.GetOrdinal("id")),
            SkillId: r.GetGuid(r.GetOrdinal("skill_id")),
            Name: r.IsDBNull(r.GetOrdinal("name")) ? null : r.GetString(r.GetOrdinal("name")),
            InputType: r.IsDBNull(r.GetOrdinal("input_type")) ? "text" : r.GetString(r.GetOrdinal("input_type")),
            InputData: JsonNode.Parse(r.IsDBNull(r.GetOrdinal("input_data")) ? "{}" : r.GetString(r.GetOrdinal("input_data"))) as JsonObject ?? new JsonObject(),
            ExpectedOutput: JsonNode.Parse(r.IsDBNull(r.GetOrdinal("expected_output")) ? "{}" : r.GetString(r.GetOrdinal("expected_output"))) as JsonObject ?? new JsonObject(),
            IsActive: r.GetBoolean(r.GetOrdinal("is_active")),
            CreatedAt: r.GetDateTime(r.GetOrdinal("created_at")));
    }
}

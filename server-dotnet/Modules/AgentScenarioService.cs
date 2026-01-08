using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Server.Modules;

public sealed class AgentScenarioService
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AgentScenarioService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public Task<IReadOnlyList<AgentScenario>> ListAsync(string companyCode, CancellationToken ct)
        => ListAsync(companyCode, includeInactive: true, ct);

    public async Task<IReadOnlyList<AgentScenario>> ListAsync(string companyCode, bool includeInactive, CancellationToken ct)
    {
        var list = new List<AgentScenario>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (includeInactive)
        {
            cmd.CommandText = @"
                SELECT id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at
                FROM (
                    SELECT DISTINCT ON (scenario_key)
                        id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at
                    FROM agent_scenarios
                    WHERE company_code = $1 OR company_code IS NULL
                    ORDER BY scenario_key,
                             CASE WHEN company_code = $1 THEN 0 ELSE 1 END,
                             is_active DESC,
                             priority,
                             updated_at DESC
                ) ranked
                ORDER BY is_active DESC, priority, updated_at DESC";
        }
        else
        {
            cmd.CommandText = @"
                SELECT id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at
                FROM (
                    SELECT DISTINCT ON (scenario_key)
                        id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at
                    FROM agent_scenarios
                    WHERE (company_code = $1 OR company_code IS NULL) AND is_active = TRUE
                    ORDER BY scenario_key,
                             CASE WHEN company_code = $1 THEN 0 ELSE 1 END,
                             priority,
                             updated_at DESC
                ) ranked
                ORDER BY priority, updated_at DESC";
        }
        cmd.Parameters.AddWithValue(companyCode);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadScenario(reader));
        }
        return list;
    }

    public Task<IReadOnlyList<AgentScenario>> ListActiveAsync(string companyCode, CancellationToken ct)
        => ListAsync(companyCode, includeInactive: false, ct);

    public async Task<AgentScenario?> GetAsync(string companyCode, string scenarioKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at
                             FROM agent_scenarios
                             WHERE company_code=$1 AND scenario_key=$2
                             LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(scenarioKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadScenario(reader);
        }
        return null;
    }

    public async Task<AgentScenario> UpsertAsync(string companyCode, AgentScenarioInput input, CancellationToken ct)
    {
        JsonObject? metadata = null;
        if (!string.IsNullOrWhiteSpace(input.MetadataJson))
        {
            try
            {
                metadata = JsonNode.Parse(input.MetadataJson!) as JsonObject;
            }
            catch
            {
                metadata = null;
            }
        }

        JsonArray? toolHints = null;
        if (input.ToolHints is { Count: > 0 })
        {
            toolHints = new JsonArray();
            foreach (var hint in input.ToolHints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    toolHints.Add(hint.Trim());
                }
            }
        }

        JsonNode? context = null;
        if (!string.IsNullOrWhiteSpace(input.ContextJson))
        {
            try
            {
                context = JsonNode.Parse(input.ContextJson!);
            }
            catch
            {
                context = null;
            }
        }

        var payload = new AgentScenarioPayload(
            Id: null,
            input.ScenarioKey,
            input.Title,
            input.Description,
            input.Instructions,
            metadata,
            toolHints,
            Context: context,
            input.Priority,
            input.IsActive
        );
        return await UpsertAsync(companyCode, payload, ct);
    }

    public async Task<AgentScenario> UpsertAsync(string companyCode, AgentScenarioPayload payload, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO agent_scenarios (id, company_code, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at)
                              VALUES (COALESCE($1, gen_random_uuid()), $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb, $9::jsonb, $10, $11, now())
                             ON CONFLICT (company_code, scenario_key) DO UPDATE SET
                               title = EXCLUDED.title,
                               description = EXCLUDED.description,
                               instructions = EXCLUDED.instructions,
                                metadata = EXCLUDED.metadata,
                               tool_hints = EXCLUDED.tool_hints,
                                context = EXCLUDED.context,
                               priority = EXCLUDED.priority,
                               is_active = EXCLUDED.is_active,
                               updated_at = now()
                              RETURNING id, scenario_key, title, description, instructions, metadata, tool_hints, context, priority, is_active, updated_at";
        cmd.Parameters.AddWithValue(payload.Id ?? (object?)DBNull.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.ScenarioKey);
        cmd.Parameters.AddWithValue(payload.Title ?? payload.ScenarioKey);
        cmd.Parameters.AddWithValue(payload.Description ?? string.Empty);
        cmd.Parameters.AddWithValue(payload.Instructions ?? string.Empty);
        cmd.Parameters.AddWithValue(payload.Metadata is null ? (object)DBNull.Value : JsonSerializer.Serialize(payload.Metadata, JsonOptions));
        cmd.Parameters.AddWithValue(payload.ToolHints is null ? (object)DBNull.Value : JsonSerializer.Serialize(payload.ToolHints, JsonOptions));
        cmd.Parameters.AddWithValue(payload.Context is null ? (object)DBNull.Value : JsonSerializer.Serialize(payload.Context, JsonOptions));
        cmd.Parameters.AddWithValue(payload.Priority);
        cmd.Parameters.AddWithValue(payload.IsActive);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadScenario(reader);
        }
        throw new Exception("保存场景失败");
    }

    public async Task DeleteAsync(string companyCode, string scenarioKey, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_scenarios WHERE company_code=$1 AND scenario_key=$2";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(scenarioKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public AgentScenarioSelection MatchScenario(string? message, IReadOnlyList<AgentScenario> scenarios)
    {
        if (scenarios.Count == 0) return AgentScenarioSelection.Empty;
        var text = message?.ToLowerInvariant() ?? string.Empty;
        foreach (var scenario in scenarios)
        {
            if (!scenario.IsActive) continue;
            var matcher = scenario.Metadata? ["matcher"] as JsonObject;
            if (matcher is null)
            {
                return new AgentScenarioSelection(scenario, AgentScenarioMatchReason.NoMatcher);
            }
            if (MatcherMatches(matcher, text))
            {
                return new AgentScenarioSelection(scenario, AgentScenarioMatchReason.MatcherMatched);
            }
        }
        return AgentScenarioSelection.Empty;
    }

    private static bool MatcherMatches(JsonObject matcher, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (matcher.TryGetPropertyValue("messageContains", out var containsNode) && containsNode is JsonArray arr && arr.Count > 0)
        {
            var requiresAll = matcher.TryGetPropertyValue("matchAll", out var matchAllNode) && matchAllNode is JsonValue matchAllVal && matchAllVal.TryGetValue<bool>(out var b) && b;
            var matchedCount = 0;
            foreach (var item in arr)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var keyword) || string.IsNullOrWhiteSpace(keyword))
                    continue;
                var normalized = keyword.Trim().ToLowerInvariant();
                var contains = text.Contains(normalized);
                if (contains && !requiresAll) return true;
                if (!contains && requiresAll) return false;
                if (contains) matchedCount++;
            }
            if (requiresAll) return matchedCount == arr.Count;
        }
        if (matcher.TryGetPropertyValue("contains", out var containsValue) && containsValue is JsonValue simple && simple.TryGetValue<string>(out var simpleStr))
        {
            if (!string.IsNullOrWhiteSpace(simpleStr) && text.Contains(simpleStr.Trim().ToLowerInvariant()))
            {
                return true;
                    }
                }
        return false;
    }

    private static AgentScenario ReadScenario(NpgsqlDataReader reader)
    {
        var metadataRaw = reader.IsDBNull(5) ? null : reader.GetFieldValue<string>(5);
        var toolHintsRaw = reader.IsDBNull(6) ? null : reader.GetFieldValue<string>(6);
        var contextRaw = reader.IsDBNull(7) ? null : reader.GetFieldValue<string>(7);
        JsonArray? toolHints = null;
        if (!string.IsNullOrWhiteSpace(toolHintsRaw))
        {
        try
        {
                toolHints = JsonNode.Parse(toolHintsRaw) as JsonArray;
            }
            catch
            {
                toolHints = null;
            }
        }

        return new AgentScenario(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            string.IsNullOrWhiteSpace(metadataRaw) ? null : JsonNode.Parse(metadataRaw)?.AsObject(),
            toolHints,
            string.IsNullOrWhiteSpace(contextRaw) ? null : JsonNode.Parse(contextRaw),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            reader.GetDateTime(10));
    }

    public sealed record AgentScenario(Guid Id, string ScenarioKey, string Title, string? Description, string? Instructions, JsonObject? Metadata, JsonArray? ToolHints, JsonNode? Context, int Priority, bool IsActive, DateTime UpdatedAt);

    public sealed record AgentScenarioInput(
        string ScenarioKey,
        string Title,
        string? Description,
        string? Instructions,
        IReadOnlyList<string> ToolHints,
        string? MetadataJson,
        string? ContextJson,
        int Priority,
        bool IsActive
    );

    public sealed record AgentScenarioPayload(
        Guid? Id,
        string ScenarioKey,
        string? Title,
        string? Description,
        string? Instructions,
        JsonObject? Metadata,
        JsonArray? ToolHints,
        JsonNode? Context,
        int Priority,
        bool IsActive
    );

    public sealed record AgentScenarioSelection(AgentScenario? Scenario, AgentScenarioMatchReason Reason)
    {
        public static AgentScenarioSelection Empty => new(null, AgentScenarioMatchReason.None);
        public bool HasScenario => Scenario is not null;
    }

    public enum AgentScenarioMatchReason
    {
        None,
        NoMatcher,
        MatcherMatched
    }
}

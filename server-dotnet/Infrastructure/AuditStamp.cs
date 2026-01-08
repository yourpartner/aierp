using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using System.Threading.Tasks;

namespace Server.Infrastructure;

public static class AuditStamp
{
    public static void ApplyCreate(JsonObject target, Auth.UserCtx ctx)
    {
        if (target is null) return;
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var userToken = ResolveUserToken(ctx);
        SetIfMissing(target, "createdAt", nowIso);
        SetIfMissing(target, "createdBy", userToken);
        target["updatedAt"] = JsonValue.Create(nowIso);
        target["updatedBy"] = JsonValue.Create(userToken);
    }

    public static void ApplyUpdate(JsonObject target, Auth.UserCtx ctx)
    {
        if (target is null) return;
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var userToken = ResolveUserToken(ctx);
        SetIfMissing(target, "createdAt", nowIso);
        SetIfMissing(target, "createdBy", userToken);
        target["updatedAt"] = JsonValue.Create(nowIso);
        target["updatedBy"] = JsonValue.Create(userToken);
    }

    public static async Task EnrichAsync(NpgsqlDataSource ds, string companyCode, IEnumerable<JsonObject> records)
    {
        if (ds is null) return;
        var list = records?.Where(r => r is not null).ToList() ?? new List<JsonObject>();
        if (list.Count == 0) return;

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in list)
        {
            CollectTokens(record, tokens);
        }
        if (tokens.Count == 0) return;

        var profiles = await LoadProfilesAsync(ds, companyCode, tokens);
        if (profiles.Count == 0) return;

        foreach (var record in list)
        {
            ApplyProfiles(record, profiles);
        }
    }

    private static string ResolveUserToken(Auth.UserCtx ctx)
    {
        if (!string.IsNullOrWhiteSpace(ctx.UserId)) return ctx.UserId!;
        if (!string.IsNullOrWhiteSpace(ctx.EmployeeCode)) return ctx.EmployeeCode!;
        return string.Empty;
    }

    private static void SetIfMissing(JsonObject obj, string key, string value)
    {
        if (obj is null || string.IsNullOrWhiteSpace(value)) return;
        if (!obj.TryGetPropertyValue(key, out var existing) || existing is null)
        {
            obj[key] = JsonValue.Create(value);
            return;
        }
        if (existing is JsonValue jv)
        {
            if (!jv.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str))
            {
                obj[key] = JsonValue.Create(value);
            }
        }
    }

    private static void CollectTokens(JsonObject record, HashSet<string> tokens)
    {
        if (record is null) return;
        CollectLocal(record, tokens);
        if (record.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is JsonObject payload)
        {
            CollectLocal(payload, tokens);
            if (payload.TryGetPropertyValue("header", out var headerNode) && headerNode is JsonObject header)
            {
                CollectLocal(header, tokens);
            }
        }
    }

    private static void CollectLocal(JsonObject obj, HashSet<string> tokens)
    {
        foreach (var field in new[] { "createdBy", "updatedBy" })
        {
            if (obj.TryGetPropertyValue(field, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                tokens.Add(text);
            }
        }
    }

    private static async Task<Dictionary<string, AuditProfile>> LoadProfilesAsync(NpgsqlDataSource ds, string companyCode, HashSet<string> tokens)
    {
        var profiles = new Dictionary<string, AuditProfile>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0) return profiles;

        var guidTokens = tokens
            .Select(t => Guid.TryParse(t, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToArray();
        var codeTokens = tokens
            .Where(t => !Guid.TryParse(t, out _))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var conn = await ds.OpenConnectionAsync();

        if (guidTokens.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, employee_code, name FROM users WHERE company_code=@companyCode AND id = ANY(@ids)";
            cmd.Parameters.AddWithValue("companyCode", companyCode);
            var idsParam = cmd.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid);
            idsParam.Value = guidTokens;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var userId = reader.GetGuid(0).ToString();
                var employeeCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                var name = reader.IsDBNull(2) ? null : reader.GetString(2);
                AddProfile(profiles, userId, employeeCode, name);
            }
        }

        if (codeTokens.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, employee_code, name FROM users WHERE company_code=@companyCode AND employee_code = ANY(@codes)";
            cmd.Parameters.AddWithValue("companyCode", companyCode);
            var codesParam = cmd.Parameters.Add("codes", NpgsqlDbType.Array | NpgsqlDbType.Text);
            codesParam.Value = codeTokens;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var userId = reader.GetGuid(0).ToString();
                var employeeCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                var name = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!string.IsNullOrWhiteSpace(employeeCode))
                {
                    AddProfile(profiles, userId, employeeCode, name);
                }
            }
        }

        return profiles;
    }

    private static void AddProfile(Dictionary<string, AuditProfile> profiles, string userId, string? employeeCode, string? name)
    {
        var profile = new AuditProfile(userId, employeeCode, name);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            profiles[userId] = profile;
        }
        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            profiles[employeeCode!] = profile;
        }
    }

    private static void ApplyProfiles(JsonObject record, Dictionary<string, AuditProfile> profiles)
    {
        if (record is null) return;
        ApplyProfilesToObject(record, profiles);
        if (record.TryGetPropertyValue("payload", out var payloadNode) && payloadNode is JsonObject payload)
        {
            ApplyProfilesToObject(payload, profiles);
            if (payload.TryGetPropertyValue("header", out var headerNode) && headerNode is JsonObject header)
            {
                ApplyProfilesToObject(header, profiles);
            }
        }
    }

    private static void ApplyProfilesToObject(JsonObject target, Dictionary<string, AuditProfile> profiles)
    {
        foreach (var (field, prefix) in new[] { ("createdBy", "created"), ("updatedBy", "updated") })
        {
            if (!target.TryGetPropertyValue(field, out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var token) || string.IsNullOrWhiteSpace(token))
            {
                continue;
            }
            if (!profiles.TryGetValue(token, out var profile))
            {
                // Normalize token as Guid and try lookup again.
                if (Guid.TryParse(token, out var g) && profiles.TryGetValue(g.ToString(), out var profileByGuid))
                {
                    profile = profileByGuid;
                }
            }
            if (profile is null) continue;

            var employeeKey = prefix + "ByEmployee";
            var nameKey = prefix + "ByName";
            var displayKey = prefix + "ByDisplay";

            if (!string.IsNullOrWhiteSpace(profile.EmployeeCode))
            {
                target[employeeKey] = JsonValue.Create(profile.EmployeeCode);
            }
            else if (target.ContainsKey(employeeKey))
            {
                target[employeeKey] = JsonValue.Create(string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(profile.Name))
            {
                target[nameKey] = JsonValue.Create(profile.Name);
            }
            else if (target.ContainsKey(nameKey))
            {
                target[nameKey] = JsonValue.Create(string.Empty);
            }

            var display = profile.Display;
            if (!string.IsNullOrWhiteSpace(display))
            {
                target[displayKey] = JsonValue.Create(display);
            }
            else if (target.ContainsKey(displayKey))
            {
                target[displayKey] = JsonValue.Create(string.Empty);
            }
        }
    }

    private sealed record AuditProfile(string UserId, string? EmployeeCode, string? Name)
    {
        public string Display => string.IsNullOrWhiteSpace(EmployeeCode)
            ? (Name ?? string.Empty)
            : (string.IsNullOrWhiteSpace(Name) ? EmployeeCode! : $"{EmployeeCode} {Name}");
    }
}


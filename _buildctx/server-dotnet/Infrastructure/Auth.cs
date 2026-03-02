using System.Text.Json;
using System.Linq;
using System.Collections.Generic;

namespace Server.Infrastructure;

// Authorization helper:
// - Parse user context from headers or JWT claims.
// - Validate action permissions via schema.auth.actions (RBAC-style).
// - Build row-level filters via schema.auth.scopes (simplified ABAC).
public static class Auth
{
    public record UserCtx(string? UserId, string[] Roles, string[] Caps, string? DeptId, string? EmployeeCode = null, string? UserName = null, string? CompanyCode = null);

    public static UserCtx GetUserCtx(HttpRequest req)
    {
        // Compatibility: read from JWT claims; fallback to custom headers if JWT is absent.
        string? uid = req.HttpContext.User?.FindFirst("uid")?.Value
            ?? (req.Headers.TryGetValue("x-user-id", out var u) ? (string?)u.ToString() : null);
        string? dept = req.HttpContext.User?.FindFirst("deptId")?.Value
            ?? (req.Headers.TryGetValue("x-dept-id", out var d) ? (string?)d.ToString() : null);
        string? employeeCode = req.HttpContext.User?.FindFirst("employeeCode")?.Value
            ?? (req.Headers.TryGetValue("x-employee-code", out var e) ? (string?)e.ToString() : null);
        string? userName = req.HttpContext.User?.FindFirst("name")?.Value
            ?? (req.Headers.TryGetValue("x-user-name", out var n) ? (string?)n.ToString() : null);
        string? companyCode = req.HttpContext.User?.FindFirst("companyCode")?.Value
            ?? (req.Headers.TryGetValue("x-company-code", out var ccode) ? (string?)ccode.ToString() : null);
        string rolesStr = req.HttpContext.User?.FindFirst("roles")?.Value
            ?? (req.Headers.TryGetValue("x-roles", out var r) ? r.ToString() : string.Empty);
        var roles = rolesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string capsStr = req.HttpContext.User?.FindFirst("caps")?.Value
            ?? (req.Headers.TryGetValue("x-caps", out var c) ? c.ToString() : string.Empty);
        var caps = capsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new UserCtx(uid, roles, caps, dept, employeeCode, userName, companyCode);
    }

    public static bool IsActionAllowed(JsonDocument schemaDoc, string action, UserCtx ctx)
    {
        if (!schemaDoc.RootElement.TryGetProperty("auth", out var auth)) return true;
        if (!auth.TryGetProperty("actions", out var actions)) return true;
        if (!actions.TryGetProperty(action, out var allowArr) || allowArr.ValueKind != JsonValueKind.Array) return true;
        var entries = allowArr.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        if (entries.Length == 0) return true;
        var allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowedCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.StartsWith("cap:", StringComparison.OrdinalIgnoreCase)) allowedCaps.Add(e.Substring(4));
            else if (e.StartsWith("role:", StringComparison.OrdinalIgnoreCase)) allowedRoles.Add(e.Substring(5));
            else { allowedRoles.Add(e); allowedCaps.Add(e); } // Compatibility: entries without prefix match both role/cap.
        }
        if (allowedRoles.Count == 0 && allowedCaps.Count == 0) return true;
        if (ctx.Roles.Any(r => allowedRoles.Contains(r))) return true;
        if (ctx.Caps.Any(c => allowedCaps.Contains(c))) return true;
        return false;
    }

    // Returns SQL fragment, argument list, and next placeholder index for auth scopes.
    public static (string sql, List<object?> args, int nextIdx) BuildAuthScopes(JsonDocument schemaDoc, UserCtx ctx, int startIdx)
    {
        var parts = new List<string>();
        var args = new List<object?>();
        var argIdx = startIdx;
        if (!schemaDoc.RootElement.TryGetProperty("auth", out var auth)) return (string.Empty, args, argIdx);
        if (!auth.TryGetProperty("scopes", out var scopes)) return (string.Empty, args, argIdx);
        var rules = new List<JsonElement>();
        if (scopes.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.Array) rules.AddRange(def.EnumerateArray());
        if (scopes.TryGetProperty("byRole", out var byRole) && byRole.ValueKind == JsonValueKind.Object)
        {
            foreach (var r in ctx.Roles)
            {
                if (byRole.TryGetProperty(r, out var arr) && arr.ValueKind == JsonValueKind.Array) rules.AddRange(arr.EnumerateArray());
            }
        }
        foreach (var rule in rules)
        {
            string? field = rule.TryGetProperty("field", out var fld) && fld.ValueKind == JsonValueKind.String ? fld.GetString() : null;
            string? json = rule.TryGetProperty("json", out var js) && js.ValueKind == JsonValueKind.String ? js.GetString() : null;
            var op = rule.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : "eq";
            object? valueObj = null;
            if (rule.TryGetProperty("value", out var val)) valueObj = val.ValueKind switch
            {
                JsonValueKind.String => val.GetString(),
                JsonValueKind.Number => val.ToString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => val,
                _ => val.ToString()
            };
            if (string.Equals(op, "eq_user", StringComparison.OrdinalIgnoreCase))
            {
                var key = (valueObj as string)?.ToLowerInvariant();
                valueObj = key switch { "id" => ctx.UserId, "deptid" => ctx.DeptId, _ => null };
                op = "eq";
            }
            if (field is not null)
            {
                if (op == "eq") { parts.Add($"{field} = ${argIdx}"); args.Add(valueObj); argIdx++; }
                else if (op == "ne") { parts.Add($"{field} <> ${argIdx}"); args.Add(valueObj); argIdx++; }
                else if (op == "contains") { parts.Add($"{field} ILIKE ${argIdx}"); args.Add("%" + valueObj + "%"); argIdx++; }
                else if (op == "in" && valueObj is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
                {
                    var n = arr.GetArrayLength(); var ph = string.Join(",", Enumerable.Range(0, n).Select(i => "$" + (argIdx + i)));
                    parts.Add($"{field} IN ({ph})"); foreach (var it in arr.EnumerateArray()) args.Add(it.ToString()); argIdx += n;
                }
            }
            else if (json is not null)
            {
                if (op == "eq") { parts.Add($"payload #>> string_to_array(${argIdx}, '.') = ${argIdx + 1}"); args.Add(json.Replace("[]", string.Empty)); args.Add(valueObj?.ToString()); argIdx += 2; }
                else if (op == "ne") { parts.Add($"payload #>> string_to_array(${argIdx}, '.') <> ${argIdx + 1}"); args.Add(json.Replace("[]", string.Empty)); args.Add(valueObj?.ToString()); argIdx += 2; }
                else if (op == "contains") { parts.Add($"(payload #>> string_to_array(${argIdx}, '.')) ILIKE ${argIdx + 1}"); args.Add(json.Replace("[]", string.Empty)); args.Add("%" + valueObj + "%"); argIdx += 2; }
                else if (op == "in" && valueObj is JsonElement arr2 && arr2.ValueKind == JsonValueKind.Array)
                { var local = new List<string>(); foreach (var it in arr2.EnumerateArray()) { local.Add($"payload #>> string_to_array(${argIdx}, '.') = ${argIdx + 1}"); args.Add(json.Replace("[]", string.Empty)); args.Add(it.ToString()); argIdx += 2; } if (local.Count > 0) parts.Add("(" + string.Join(" OR ", local) + ")"); }
            }
        }
        var sql = parts.Count == 0 ? string.Empty : (" AND " + string.Join(" AND ", parts));
        return (sql, args, argIdx);
    }
}



using System.Text.Json;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public class HrCrudService
{
    private readonly NpgsqlDataSource _ds;
    public HrCrudService(NpgsqlDataSource ds) { _ds = ds; }

    public async Task<string?> CreateEmploymentType(string companyCode, string table, JsonElement payload)
    {
        // Directly persist row; validation is handled by schema in Program.cs.
        return await Crud.InsertRawJson(_ds, table, companyCode, payload.GetRawText());
    }

    public async Task<string> CreatePayrollPolicy(string companyCode, string table, JsonElement payload)
    {
        var versionProvided = payload.TryGetProperty("version", out var v) && v.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
        var codeProvided = payload.TryGetProperty("code", out var pc) && pc.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(pc.GetString());
        var now = DateTimeOffset.Now;
        var genVer = now.ToString("yyyyMMdd-HHmmss");
        var code = codeProvided ? pc.GetString()! : ("POL" + genVer);
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(company_code, payload) VALUES ($1, jsonb_set(jsonb_set($2::jsonb, '{{version}}', to_jsonb($3::text), true), '{{code}}', to_jsonb($4::text), true)) RETURNING to_jsonb({table})";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload.GetRawText());
        cmd.Parameters.AddWithValue(versionProvided ? v.GetString()! : genVer);
        cmd.Parameters.AddWithValue(code);
        var json = (string?)await cmd.ExecuteScalarAsync();
        if (json is null) throw new Exception("insert failed");
        return json;
    }
}



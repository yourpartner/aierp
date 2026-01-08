using Npgsql;
using System.Text.Json;

namespace Server.Domain;

// Domain service for reading/writing schema definitions (supports company and global fallback).
public static class SchemasService
{
    // Load the active schema (entire to_jsonb(schemas) row).
    // Prefer an exact company_code match; fallback to company_code IS NULL (global) if missing.
    public static async Task<JsonDocument?> GetActiveSchema(NpgsqlDataSource ds, string name, string? companyCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        // 1) Company-specific.
        if (!string.IsNullOrWhiteSpace(companyCode))
        {
            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM schemas WHERE name=$1 AND company_code=$2 AND is_active=TRUE ORDER BY version DESC LIMIT 1) t";
                cmd1.Parameters.AddWithValue(name);
                cmd1.Parameters.AddWithValue(companyCode!);
                await using var r1 = await cmd1.ExecuteReaderAsync();
                if (await r1.ReadAsync()) return JsonDocument.Parse(r1.GetFieldValue<string>(0));
            }
        }
        // 2) Global fallback (company_code IS NULL).
        await using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM schemas WHERE name=$1 AND company_code IS NULL AND is_active=TRUE ORDER BY version DESC LIMIT 1) t";
            cmd2.Parameters.AddWithValue(name);
            await using var r2 = await cmd2.ExecuteReaderAsync();
            if (await r2.ReadAsync()) return JsonDocument.Parse(r2.GetFieldValue<string>(0));
        }
        return null;
    }

    // Save and activate a new version (company-specific or global when companyCode is null).
    public static async Task<string?> SaveAndActivate(NpgsqlDataSource ds, string name, JsonElement root, string? companyCode)
    {
        string GetOrEmpty(string prop) => root.TryGetProperty(prop, out var v) ? v.GetRawText() : "null";

        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        int nextVer;
        await using (var cmdVer = conn.CreateCommand())
        {
            cmdVer.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM schemas WHERE name=$1 AND ((company_code IS NULL AND $2::text IS NULL) OR company_code=$2)";
            cmdVer.Parameters.AddWithValue(name);
            cmdVer.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
            nextVer = Convert.ToInt32(await cmdVer.ExecuteScalarAsync());
        }

        await using (var cmdDeact = conn.CreateCommand())
        {
            cmdDeact.CommandText = "UPDATE schemas SET is_active=FALSE WHERE name=$1 AND ((company_code IS NULL AND $2::text IS NULL) OR company_code=$2) AND is_active=TRUE";
            cmdDeact.Parameters.AddWithValue(name);
            cmdDeact.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
            await cmdDeact.ExecuteNonQueryAsync();
        }

        await using (var cmdIns = conn.CreateCommand())
        {
            cmdIns.CommandText = @"INSERT INTO schemas(company_code, name, version, is_active, schema, ui, query, core_fields, validators, numbering, ai_hints)
                                VALUES ($1,$2,$3,TRUE, $4::jsonb, $5::jsonb, $6::jsonb, $7::jsonb, $8::jsonb, $9::jsonb, $10::jsonb)
                                RETURNING to_jsonb(schemas)";
            cmdIns.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
            cmdIns.Parameters.AddWithValue(name);
            cmdIns.Parameters.AddWithValue(nextVer);
            cmdIns.Parameters.AddWithValue(GetOrEmpty("schema"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("ui"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("query"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("core_fields"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("validators"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("numbering"));
            cmdIns.Parameters.AddWithValue(GetOrEmpty("ai_hints"));
            var inserted = await cmdIns.ExecuteScalarAsync();
            if (inserted is not string json)
            {
                await tx.RollbackAsync();
                return null;
            }
            await tx.CommitAsync();
            return json;
        }
    }
}



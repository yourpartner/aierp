using Npgsql;
using System.Text.Json;

namespace Server.Domain;

// Domain service for reading/writing schema definitions (supports company and global fallback).
public static class SchemasService
{
    // Load the schema row as JSON.
    // Prefer an exact company_code match; fallback to company_code IS NULL (global) if missing.
    public static async Task<JsonDocument?> GetActiveSchema(NpgsqlDataSource ds, string name, string? companyCode)
    {
        await using var conn = await ds.OpenConnectionAsync();
        // 1) Company-specific.
        if (!string.IsNullOrWhiteSpace(companyCode))
        {
            await using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM schemas WHERE name=$1 AND company_code=$2 LIMIT 1) t";
                cmd1.Parameters.AddWithValue(name);
                cmd1.Parameters.AddWithValue(companyCode!);
                await using var r1 = await cmd1.ExecuteReaderAsync();
                if (await r1.ReadAsync()) return JsonDocument.Parse(r1.GetFieldValue<string>(0));
            }
        }
        // 2) Global fallback (company_code IS NULL).
        await using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "SELECT to_jsonb(t) FROM (SELECT * FROM schemas WHERE name=$1 AND company_code IS NULL LIMIT 1) t";
            cmd2.Parameters.AddWithValue(name);
            await using var r2 = await cmd2.ExecuteReaderAsync();
            if (await r2.ReadAsync()) return JsonDocument.Parse(r2.GetFieldValue<string>(0));
        }
        return null;
    }

    // Save schema (update if exists; otherwise insert).
    public static async Task<string?> SaveAndActivate(NpgsqlDataSource ds, string name, JsonElement root, string? companyCode)
    {
        string GetOrEmpty(string prop) => root.TryGetProperty(prop, out var v) ? v.GetRawText() : "null";

        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        void AddParams(NpgsqlCommand cmd)
        {
            cmd.Parameters.AddWithValue((object?)companyCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(name);
            cmd.Parameters.AddWithValue(GetOrEmpty("schema"));
            cmd.Parameters.AddWithValue(GetOrEmpty("ui"));
            cmd.Parameters.AddWithValue(GetOrEmpty("query"));
            cmd.Parameters.AddWithValue(GetOrEmpty("core_fields"));
            cmd.Parameters.AddWithValue(GetOrEmpty("validators"));
            cmd.Parameters.AddWithValue(GetOrEmpty("numbering"));
            cmd.Parameters.AddWithValue(GetOrEmpty("ai_hints"));
        }

        await using (var cmdUpdate = conn.CreateCommand())
        {
            cmdUpdate.CommandText = @"
                UPDATE schemas
                SET schema = $3::jsonb,
                    ui = $4::jsonb,
                    query = $5::jsonb,
                    core_fields = $6::jsonb,
                    validators = $7::jsonb,
                    numbering = $8::jsonb,
                    ai_hints = $9::jsonb,
                    updated_at = NOW()
                WHERE name = $2
                  AND ((company_code IS NULL AND $1::text IS NULL) OR company_code = $1)
                RETURNING to_jsonb(schemas)";
            AddParams(cmdUpdate);
            var updated = await cmdUpdate.ExecuteScalarAsync();
            if (updated is string updatedJson)
            {
                await tx.CommitAsync();
                return updatedJson;
            }
        }

        await using (var cmdInsert = conn.CreateCommand())
        {
            cmdInsert.CommandText = @"
                INSERT INTO schemas(company_code, name, schema, ui, query, core_fields, validators, numbering, ai_hints)
                VALUES ($1, $2, $3::jsonb, $4::jsonb, $5::jsonb, $6::jsonb, $7::jsonb, $8::jsonb, $9::jsonb)
                RETURNING to_jsonb(schemas)";
            AddParams(cmdInsert);
            var inserted = await cmdInsert.ExecuteScalarAsync();
            if (inserted is string insertedJson)
            {
                await tx.CommitAsync();
                return insertedJson;
            }
        }

        await tx.RollbackAsync();
        return null;
    }
}



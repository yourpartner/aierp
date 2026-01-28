using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

public static class LawAdminModule
{
    public static void MapLawAdminModule(this WebApplication app)
    {
        // Bulk seed: insert sample rates (company-specific or global).
        app.MapPost("/admin/law-rates/seed", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var company = root.TryGetProperty("companyCode", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() : null;
            var rows = root.TryGetProperty("rows", out var r) && r.ValueKind==JsonValueKind.Array ? r.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            if (rows.Length == 0) return Results.BadRequest(new { error = "rows required" });
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var row in rows)
            {
                var kind = row.GetProperty("kind").GetString()!;
                var key = row.TryGetProperty("key", out var k) && k.ValueKind==JsonValueKind.String ? k.GetString() : null;
                var minAmt = row.TryGetProperty("min", out var mi) && mi.ValueKind==JsonValueKind.Number ? (mi.TryGetDecimal(out var md)? md : Convert.ToDecimal(mi.GetDouble())) : (decimal?)null;
                var maxAmt = row.TryGetProperty("max", out var ma) && ma.ValueKind==JsonValueKind.Number ? (ma.TryGetDecimal(out var md2)? md2 : Convert.ToDecimal(ma.GetDouble())) : (decimal?)null;
                var rate = row.TryGetProperty("rate", out var rt) && rt.ValueKind==JsonValueKind.Number ? (rt.TryGetDecimal(out var rd)? rd : Convert.ToDecimal(rt.GetDouble())) : 0m;
                var from = DateTime.Parse(row.GetProperty("from").GetString()!);
                var to = row.TryGetProperty("to", out var te) && te.ValueKind==JsonValueKind.String ? DateTime.Parse(te.GetString()!) : (DateTime?)null;
                var version = row.TryGetProperty("version", out var ve) && ve.ValueKind==JsonValueKind.String ? ve.GetString() : null;
                var note = row.TryGetProperty("note", out var ne) && ne.ValueKind==JsonValueKind.String ? ne.GetString() : null;
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)";
                cmd.Parameters.AddWithValue((object?)company ?? DBNull.Value);
                cmd.Parameters.AddWithValue(kind);
                cmd.Parameters.AddWithValue((object?)key ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)minAmt ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)maxAmt ?? DBNull.Value);
                cmd.Parameters.AddWithValue(rate);
                cmd.Parameters.AddWithValue(from);
                cmd.Parameters.AddWithValue((object?)to ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)version ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return Results.Ok(new { inserted = rows.Length });
        }).RequireAuthorization();

        // Single upsert: insert/overwrite based on unique (company_code, kind, key, effective_from).
        app.MapPost("/admin/law-rates/upsert", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var company = root.TryGetProperty("companyCode", out var c) && c.ValueKind==JsonValueKind.String ? c.GetString() : null;
            var kind = root.GetProperty("kind").GetString()!;
            var key = root.TryGetProperty("key", out var k) && k.ValueKind==JsonValueKind.String ? k.GetString() : null;
            var minAmt = root.TryGetProperty("min", out var mi) && mi.ValueKind==JsonValueKind.Number ? (mi.TryGetDecimal(out var md)? md : Convert.ToDecimal(mi.GetDouble())) : (decimal?)null;
            var maxAmt = root.TryGetProperty("max", out var ma) && ma.ValueKind==JsonValueKind.Number ? (ma.TryGetDecimal(out var md2)? md2 : Convert.ToDecimal(ma.GetDouble())) : (decimal?)null;
            var rate = root.TryGetProperty("rate", out var rt) && rt.ValueKind==JsonValueKind.Number ? (rt.TryGetDecimal(out var rd)? rd : Convert.ToDecimal(rt.GetDouble())) : 0m;
            var from = DateTime.Parse(root.GetProperty("from").GetString()!);
            var to = root.TryGetProperty("to", out var te) && te.ValueKind==JsonValueKind.String ? DateTime.Parse(te.GetString()!) : (DateTime?)null;
            var version = root.TryGetProperty("version", out var ve) && ve.ValueKind==JsonValueKind.String ? ve.GetString() : null;
            var note = root.TryGetProperty("note", out var ne) && ne.ValueKind==JsonValueKind.String ? ne.GetString() : null;
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                                ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue((object?)company ?? DBNull.Value);
            cmd.Parameters.AddWithValue(kind);
            cmd.Parameters.AddWithValue((object?)key ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)minAmt ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)maxAmt ?? DBNull.Value);
            cmd.Parameters.AddWithValue(rate);
            cmd.Parameters.AddWithValue(from);
            cmd.Parameters.AddWithValue((object?)to ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)version ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // Import preset: Kyoukai Kenpo Tokyo March 2025 (health + pension employee share).
        app.MapPost("/admin/law-rates/seed/kyoukai-kenpo/tokyo/2025", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            // Health insurance (Tokyo general, non-care) total 9.91% → employee share 4.955%.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                    VALUES (NULL, 'health', $1, NULL, NULL, $2, $3, NULL, $4, $5)";
                cmd.Parameters.AddWithValue("東京都");
                cmd.Parameters.AddWithValue(0.04955m);
                cmd.Parameters.AddWithValue(new DateTime(2025, 3, 1));
                cmd.Parameters.AddWithValue("JP-TOKYO-KK-2025-03");
                cmd.Parameters.AddWithValue("Kyoukai Kenpo Tokyo employee share 9.91%/2");
                await cmd.ExecuteNonQueryAsync();
            }
            // Kosei Nenkin total 18.3% → employee share 9.15%.
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                     VALUES (NULL, 'pension', NULL, NULL, NULL, $1, $2, NULL, $3, $4)";
                cmd2.Parameters.AddWithValue(0.0915m);
                cmd2.Parameters.AddWithValue(new DateTime(2025, 3, 1));
                cmd2.Parameters.AddWithValue("JP-PENSION-2025-03");
                cmd2.Parameters.AddWithValue("Kosei Nenkin employee share 18.3%/2");
                await cmd2.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 2 });
        }).RequireAuthorization();

        // Care insurance (kaigo hoken) for Category 2 insured (40-64 years old).
        // Rate is stored separately from health insurance for precise calculation.
        app.MapPost("/admin/law-rates/seed/kyoukai-kenpo/tokyo/2025/care", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = conn.CreateCommand())
            {
                // Care insurance (kaigo hoken) employee share.
                cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                    VALUES (NULL, 'care', $1, NULL, NULL, $2, $3, NULL, $4, $5)";
                cmd.Parameters.AddWithValue("東京都:care");
                cmd.Parameters.AddWithValue(0.0080m);
                cmd.Parameters.AddWithValue(new DateTime(2025, 3, 1));
                cmd.Parameters.AddWithValue("JP-CARE-2025-03");
                cmd.Parameters.AddWithValue("Kaigo Hoken (Care Insurance) employee share - applies to age 40-64");
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 1 });
        }).RequireAuthorization();

        // Employment insurance FY2025 employee share (general/construction/agriculture, Apr-Mar period).
        app.MapPost("/admin/law-rates/seed/employment/2025", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            async Task Insert(string key, decimal rate)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                    VALUES (NULL, 'employment', $1, NULL, NULL, $2, $3, $4, $5, $6)";
                cmd.Parameters.AddWithValue(key);
                cmd.Parameters.AddWithValue(rate);
                cmd.Parameters.AddWithValue(new DateTime(2025, 4, 1));
                cmd.Parameters.AddWithValue(new DateTime(2026, 3, 31));
                cmd.Parameters.AddWithValue("JP-EI-2025-04");
                cmd.Parameters.AddWithValue("MHLW FY2025 employment insurance rate (employee share)");
                await cmd.ExecuteNonQueryAsync();
            }
            await Insert("一般", 0.0055m);
            await Insert("建設", 0.0065m);
            await Insert("農林", 0.0065m);
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 3 });
        }).RequireAuthorization();

        // 源泉徴収税額表は SQL で直接ロードする（コードで解析しない）
    }
}



using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

public static class LawAdminModule
{
    public static void MapLawAdminModule(this WebApplication app)
    {
        // 批量种子：插入示例费率（公司级或全局）
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

        // 单条 upsert：根据 (company_code, kind, key, from) 唯一性做插入/覆盖
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

        // 一键导入：协会けんぽ 東京都 令和7年3月分（4月納付分） 健保 + 厚年（従業員負担）
        app.MapPost("/admin/law-rates/seed/kyoukai-kenpo/tokyo/2025", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            // 健康保険（東京・一般・介護非該当）全率9.91% → 折半 4.955%
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
            // 厚生年金 全率18.3% → 折半 9.15%
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

        // 追加：协会けんぽ 東京都 令和7年3月分（4月納付分） 介護保険 第2号該当（従業員負担）
        app.MapPost("/admin/law-rates/seed/kyoukai-kenpo/tokyo/2025/care2", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = conn.CreateCommand())
            {
                // 健康保険（東京・介護該当）全率11.50% → 折半 5.75%
                cmd.CommandText = @"INSERT INTO law_rates(company_code, kind, key, min_amount, max_amount, rate, effective_from, effective_to, version, note)
                                    VALUES (NULL, 'health', $1, NULL, NULL, $2, $3, NULL, $4, $5)";
                cmd.Parameters.AddWithValue("東京都:care2");
                cmd.Parameters.AddWithValue(0.0575m);
                cmd.Parameters.AddWithValue(new DateTime(2025, 3, 1));
                cmd.Parameters.AddWithValue("JP-TOKYO-KK-CARE2-2025-03");
                cmd.Parameters.AddWithValue("Kyoukai Kenpo Tokyo care-eligible employee share 11.50%/2");
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 1 });
        }).RequireAuthorization();

        // 雇用保険 令和7(2025)年度（労働者負担） 一般/建設/農林（4/1〜翌3/31）
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
                cmd.Parameters.AddWithValue("MHLW 令和7年度 雇用保険料率 労働者負担");
                await cmd.ExecuteNonQueryAsync();
            }
            await Insert("一般", 0.0055m);
            await Insert("建設", 0.0065m);
            await Insert("農林", 0.0065m);
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 3 });
        }).RequireAuthorization();

        // 导入：源泉所得税（令和7年分 電算機特例 月額表 甲欄 別表）区间速算
        app.MapPost("/admin/withholding/seed/monthly-ko-2025", async (NpgsqlDataSource ds) =>
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            async Task Ins(decimal min, decimal? max, decimal rate, decimal deduction, string ver, string note)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO withholding_rates(company_code, category, min_amount, max_amount, rate, deduction, effective_from, effective_to, version, note)
                                    VALUES (NULL, 'monthly_ko', $1, $2, $3, $4, $5, NULL, $6, $7)";
                cmd.Parameters.AddWithValue(min);
                if (max.HasValue) cmd.Parameters.AddWithValue(max.Value); else cmd.Parameters.AddWithValue(DBNull.Value);
                cmd.Parameters.AddWithValue(rate);
                cmd.Parameters.AddWithValue(deduction);
                cmd.Parameters.AddWithValue(new DateTime(2025,4,1));
                cmd.Parameters.AddWithValue(ver);
                cmd.Parameters.AddWithValue(note);
                await cmd.ExecuteNonQueryAsync();
            }
            // 別表第四（税率×復興特別）: 5.105%, 10.210%(-8296), 20.420%(-36374), 23.483%(-54113), 33.693%(-130688), 40.840%(-237893), 45.945%(-408061)
            await Ins(0m,         162500m, 0.05105m,      0m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(162501m,    275000m, 0.10210m,   8296m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(275001m,    579166m, 0.20420m,  36374m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(579167m,    750000m, 0.23483m,  54113m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(750001m,   1500000m, 0.33693m, 130688m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(1500001m,  3333333m, 0.40840m, 237893m,       "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await Ins(3333334m,       null, 0.45945m, 408061m,      "JP-WHT-MONTHLY-KO-2025", "NTA 別表第四 令和7年分");
            await tx.CommitAsync();
            return Results.Ok(new { inserted = 7, version = "JP-WHT-MONTHLY-KO-2025" });
        }).RequireAuthorization();
    }
}



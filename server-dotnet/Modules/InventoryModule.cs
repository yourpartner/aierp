using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public static class InventoryModule
{
    public static void MapInventoryModule(this WebApplication app)
    {
        // 通用：创建/更新/删除/详情/列表 for materials/warehouses/bins/stock_statuses/batches/inventory_movements
        var tableToEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["materials"] = "material",
            ["warehouses"] = "warehouse",
            ["bins"] = "bin",
            ["stock_statuses"] = "stockstatus",
            ["batches"] = "batch"
        };
        foreach (var kv in tableToEntity)
        {
            var table = kv.Key;
            var entity = kv.Value;

            app.MapPost($"/inventory/{entity}", async (HttpRequest req, NpgsqlDataSource ds) =>
            {
                if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var json = doc.RootElement.GetRawText();
                var inserted = await Crud.InsertRawJson(ds, table, cc!, json);
                return inserted is null ? Results.Problem("insert failed") : Results.Text(inserted, "application/json");
            }).RequireAuthorization();

            app.MapPut($"/inventory/{entity}/{{id:guid}}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
            {
                if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
                using var doc = await JsonDocument.ParseAsync(req.Body);
                var json = doc.RootElement.GetRawText();
                var updated = await Crud.UpdateRawJson(ds, table, id, cc!, json);
                return updated is null ? Results.NotFound() : Results.Text(updated, "application/json");
            }).RequireAuthorization();

            app.MapDelete($"/inventory/{entity}/{{id:guid}}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
            {
                if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
                var n = await Crud.DeleteById(ds, table, id, cc!);
                return n > 0 ? Results.Ok(new { ok = true, deleted = n }) : Results.NotFound();
            }).RequireAuthorization();
        }

        // 列表/详情（简化版，按表）
        app.MapGet("/inventory/{table}", async (string table, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            string[] allowed = new[] { "materials", "warehouses", "bins", "stock_statuses", "batches", "inventory_movements", "inventory_ledger", "inventory_balances" };
            if (!allowed.Contains(table)) return Results.BadRequest(new { error = "invalid table" });
            var sql = $"SELECT to_jsonb(t) FROM (SELECT * FROM {table} WHERE company_code=$1 ORDER BY created_at DESC LIMIT 200) t";
            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { cc.ToString() });
            return Results.Text("[" + string.Join(',', rows) + "]", "application/json");
        }).RequireAuthorization();

        app.MapGet("/inventory/{table}/{id:guid}", async (string table, Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            string[] allowed = new[] { "materials", "warehouses", "bins", "stock_statuses", "batches", "inventory_movements" };
            if (!allowed.Contains(table)) return Results.BadRequest(new { error = "invalid table" });
            var json = await Crud.GetDetailJson(ds, table, id, cc!, string.Empty, new List<object?>());
            return json is null ? Results.NotFound() : Results.Text(json, "application/json");
        }).RequireAuthorization();

        // 入库/出库/转储：写 movement、展开台账、更新现存量
        app.MapPost("/inventory/movements", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var movementType = root.TryGetProperty("movementType", out var mt) && mt.ValueKind==JsonValueKind.String ? mt.GetString() : null;
            if (string.IsNullOrWhiteSpace(movementType)) return Results.BadRequest(new { error = "movementType required" });
            var movementDateStr = root.TryGetProperty("movementDate", out var md) && md.ValueKind==JsonValueKind.String ? md.GetString() : DateTime.UtcNow.ToString("yyyy-MM-dd");

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                // 1) 插入 movement
                Guid movementId;
                await using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = @"INSERT INTO inventory_movements(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
                    ins.Parameters.AddWithValue(cc.ToString());
                    ins.Parameters.AddWithValue(root.GetRawText());
                    var obj = await ins.ExecuteScalarAsync();
                    movementId = obj is Guid g ? g : Guid.Empty;
                    if (movementId == Guid.Empty) throw new Exception("insert movement failed");
                }

                // 2) 展开台账 lines
                var lines = root.TryGetProperty("lines", out var ls) && ls.ValueKind==JsonValueKind.Array ? ls.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                int idx = 1;
                string NormalizeWarehouse(string? wh) => string.IsNullOrWhiteSpace(wh) ? string.Empty : wh!;
                async Task<decimal> GetCurrentBalance(string material, string? wh, string? bin, string? status, string? batch)
                {
                    await using var balCmd = conn.CreateCommand();
                    balCmd.CommandText = @"SELECT COALESCE(quantity,0)
FROM inventory_balances
WHERE company_code=$1
  AND material_code=$2
  AND warehouse_code=$3
  AND ((bin_code IS NULL AND $4 IS NULL) OR bin_code = $4)
  AND ((status_code IS NULL AND $5 IS NULL) OR status_code = $5)
  AND ((batch_no IS NULL AND $6 IS NULL) OR batch_no = $6)
LIMIT 1";
                    balCmd.Parameters.AddWithValue(cc.ToString());
                    balCmd.Parameters.AddWithValue(material);
                    balCmd.Parameters.AddWithValue(NormalizeWarehouse(wh));
                    balCmd.Parameters.AddWithValue((object?)bin ?? DBNull.Value);
                    balCmd.Parameters.AddWithValue((object?)status ?? DBNull.Value);
                    balCmd.Parameters.AddWithValue((object?)batch ?? DBNull.Value);
                    var obj = await balCmd.ExecuteScalarAsync();
                    if (obj is null || obj is DBNull) return 0m;
                    if (obj is decimal d) return d;
                    if (obj is double dbl) return (decimal)dbl;
                    if (obj is float flt) return (decimal)flt;
                    if (obj is long lng) return lng;
                    return Convert.ToDecimal(obj);
                }

                foreach (var line in lines)
                {
                    var materialCode = line.GetProperty("materialCode").GetString() ?? string.Empty;
                    decimal qty = 0m; if (line.TryGetProperty("quantity", out var q) && q.ValueKind==JsonValueKind.Number) { if (!q.TryGetDecimal(out qty)) qty = (decimal)q.GetDouble(); }
                    var uom = line.TryGetProperty("uom", out var u) && u.ValueKind==JsonValueKind.String ? u.GetString() : null;
                    var batchNo = line.TryGetProperty("batchNo", out var b) && b.ValueKind==JsonValueKind.String ? b.GetString() : null;
                    var statusCode = line.TryGetProperty("statusCode", out var st) && st.ValueKind==JsonValueKind.String ? st.GetString() : null;
                    var fromWh = root.TryGetProperty("fromWarehouse", out var fw) && fw.ValueKind==JsonValueKind.String ? fw.GetString() : null;
                    var fromBin = root.TryGetProperty("fromBin", out var fbn) && fbn.ValueKind==JsonValueKind.String ? fbn.GetString() : null;
                    var toWh = root.TryGetProperty("toWarehouse", out var tw) && tw.ValueKind==JsonValueKind.String ? tw.GetString() : null;
                    var toBin = root.TryGetProperty("toBin", out var tbn) && tbn.ValueKind==JsonValueKind.String ? tbn.GetString() : null;

                    var absQty = Math.Abs(qty);
                    if (absQty <= 0m)
                    {
                        idx++;
                        continue;
                    }

                    if (string.Equals(movementType, "OUT", StringComparison.OrdinalIgnoreCase) || string.Equals(movementType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
                    {
                        var available = await GetCurrentBalance(materialCode, fromWh, fromBin, statusCode, batchNo);
                        if (available < absQty)
                        {
                            throw new InvalidOperationException($"库存不足：物料 {materialCode} 在仓库 {fromWh ?? "(空)"} / 仓位 {fromBin ?? "(空)"} 可用 {available}，请求 {absQty}");
                        }
                    }

                    // 方向：IN 正数累加到 to，OUT 负数从 from，TRANSFER 分解为 from(负) + to(正)
                    async Task insertLedgerRow(string type, decimal quantity, string? fwh, string? fbi, string? twh, string? tbi)
                    {
                        await using var il = conn.CreateCommand();
                        il.CommandText = @"INSERT INTO inventory_ledger(company_code, movement_id, line_no, movement_type, movement_date, material_code, quantity, uom, from_warehouse, from_bin, to_warehouse, to_bin, batch_no, status_code)
                                          VALUES ($1,$2,$3,$4,$5::date,$6,$7,$8,$9,$10,$11,$12,$13,$14)";
                        il.Parameters.AddWithValue(cc.ToString());
                        il.Parameters.AddWithValue(movementId);
                        il.Parameters.AddWithValue(idx);
                        il.Parameters.AddWithValue(type);
                        il.Parameters.AddWithValue(movementDateStr!);
                        il.Parameters.AddWithValue(materialCode);
                        il.Parameters.AddWithValue(quantity);
                        il.Parameters.AddWithValue((object?)uom ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)fwh ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)fbi ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)twh ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)tbi ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)batchNo ?? DBNull.Value);
                        il.Parameters.AddWithValue((object?)statusCode ?? DBNull.Value);
                        await il.ExecuteNonQueryAsync();
                    }

                    async Task upsertBalance(string material, string? wh, string? bin, string? status, string? batch, decimal delta)
                    {
                        await using var ub = conn.CreateCommand();
                        ub.CommandText = @"INSERT INTO inventory_balances(company_code, material_code, warehouse_code, bin_code, status_code, batch_no, quantity)
                                           VALUES ($1,$2,$3,$4,$5,$6,$7)
                                           ON CONFLICT (company_code, material_code, warehouse_code, bin_code, status_code, batch_no)
                                           DO UPDATE SET quantity = inventory_balances.quantity + EXCLUDED.quantity, updated_at = now()";
                        ub.Parameters.AddWithValue(cc.ToString());
                        ub.Parameters.AddWithValue(material);
                        ub.Parameters.AddWithValue(wh ?? string.Empty);
                        ub.Parameters.AddWithValue((object?)bin ?? DBNull.Value);
                        ub.Parameters.AddWithValue((object?)status ?? DBNull.Value);
                        ub.Parameters.AddWithValue((object?)batch ?? DBNull.Value);
                        ub.Parameters.AddWithValue(delta);
                        await ub.ExecuteNonQueryAsync();
                    }

                    if (string.Equals(movementType, "IN", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("IN", Math.Abs(qty), null, null, toWh, toBin);
                        await upsertBalance(materialCode, toWh, toBin, statusCode, batchNo, Math.Abs(qty));
                    }
                    else if (string.Equals(movementType, "OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("OUT", -absQty, fromWh, fromBin, null, null);
                        await upsertBalance(materialCode, fromWh, fromBin, statusCode, batchNo, -absQty);
                    }
                    else if (string.Equals(movementType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
                    {
                        await insertLedgerRow("OUT", -absQty, fromWh, fromBin, null, null);
                        await upsertBalance(materialCode, fromWh, fromBin, statusCode, batchNo, -absQty);
                        await insertLedgerRow("IN", absQty, null, null, toWh, toBin);
                        await upsertBalance(materialCode, toWh, toBin, statusCode, batchNo, absQty);
                    }

                    idx++;
                }

                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = movementId });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();

        // 查询现存量（支持按物料/仓库/仓位/状态/批次过滤）
        app.MapGet("/inventory/balances/search", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            var qp = req.Query;
            string? material = qp["materialCode"].FirstOrDefault();
            string? wh = qp["warehouseCode"].FirstOrDefault();
            string? bin = qp["binCode"].FirstOrDefault();
            string? status = qp["statusCode"].FirstOrDefault();
            string? batch = qp["batchNo"].FirstOrDefault();

            var where = new List<string>{ "company_code=$1" };
            var args = new List<object?>{ cc.ToString() };
            int idx = 2;
            void add(string field, string? val)
            {
                if (!string.IsNullOrWhiteSpace(val)) { where.Add($"{field} = ${idx}"); args.Add(val); idx++; }
            }
            add("material_code", material);
            add("warehouse_code", wh);
            add("bin_code", bin);
            add("status_code", status);
            add("batch_no", batch);
            var sql = $"SELECT to_jsonb(t) FROM (SELECT * FROM inventory_balances WHERE {string.Join(" AND ", where)} ORDER BY material_code, warehouse_code, bin_code LIMIT 500) t";
            var rows = await Crud.QueryJsonRows(ds, sql, args);
            return Results.Text("[" + string.Join(',', rows) + "]", "application/json");
        }).RequireAuthorization();
    }
}



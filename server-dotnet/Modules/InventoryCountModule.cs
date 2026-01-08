using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public static class InventoryCountModule
{
    public static void MapInventoryCountModule(this WebApplication app)
    {
        // 获取盘点单列表
        app.MapGet("/inventory/counts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var warehouseCode = req.Query["warehouseCode"].FirstOrDefault();

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT ic.*, 
                           w.name as warehouse_name,
                           (SELECT COUNT(*) FROM inventory_count_lines WHERE count_id = ic.id) as line_count,
                           (SELECT COUNT(*) FROM inventory_count_lines WHERE count_id = ic.id AND actual_qty IS NOT NULL) as counted_count,
                           (SELECT COUNT(*) FROM inventory_count_lines WHERE count_id = ic.id AND variance_qty <> 0) as variance_count
                    FROM inventory_counts ic
                    LEFT JOIN warehouses w ON w.company_code = ic.company_code AND w.warehouse_code = ic.warehouse_code
                    WHERE ic.company_code = $1";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND ic.status = ${paramIndex}";
                parameters.Add(status);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(warehouseCode))
            {
                sql += $" AND ic.warehouse_code = ${paramIndex}";
                parameters.Add(warehouseCode);
            }

            sql += " ORDER BY ic.count_date DESC, ic.created_at DESC LIMIT 200) t";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 获取盘点单详情
        app.MapGet("/inventory/counts/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT ic.*, 
                           w.name as warehouse_name,
                           (SELECT COUNT(*) FROM inventory_count_lines WHERE count_id = ic.id) as line_count,
                           (SELECT COUNT(*) FROM inventory_count_lines WHERE count_id = ic.id AND actual_qty IS NOT NULL) as counted_count
                    FROM inventory_counts ic
                    LEFT JOIN warehouses w ON w.company_code = ic.company_code AND w.warehouse_code = ic.warehouse_code
                    WHERE ic.id = $1 AND ic.company_code = $2
                ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { id, cc.ToString() });
            if (rows.Count == 0) return Results.NotFound();
            return Results.Text(rows[0], "application/json");
        }).RequireAuthorization();

        // 获取盘点单明细
        app.MapGet("/inventory/counts/{id:guid}/lines", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT icl.*,
                           m.name as material_name_lookup,
                           b.name as bin_name
                    FROM inventory_count_lines icl
                    LEFT JOIN materials m ON m.company_code = icl.company_code AND m.material_code = icl.material_code
                    LEFT JOIN bins b ON b.company_code = icl.company_code AND b.bin_code = icl.bin_code
                    WHERE icl.count_id = $1 AND icl.company_code = $2
                    ORDER BY icl.line_no
                ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { id, cc.ToString() });
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 创建盘点单
        app.MapPost("/inventory/counts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var warehouseCode = root.TryGetProperty("warehouseCode", out var wc) ? wc.GetString() : null;
            var countDate = root.TryGetProperty("countDate", out var cd) ? cd.GetString() : DateTime.Today.ToString("yyyy-MM-dd");
            var description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null;
            var binCode = root.TryGetProperty("binCode", out var bc) ? bc.GetString() : null;
            var materialCodes = root.TryGetProperty("materialCodes", out var mc) && mc.ValueKind == JsonValueKind.Array
                ? mc.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()
                : null;

            if (string.IsNullOrEmpty(warehouseCode))
                return Results.BadRequest(new { error = "warehouseCode is required" });

            // 获取当前用户
            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 生成盘点单号
                var countNo = await GenerateCountNoAsync(conn, cc.ToString()!, tx);

                // 创建盘点主表
                var countId = Guid.NewGuid();
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO inventory_counts (id, company_code, count_no, warehouse_code, count_date, description, status, created_by, payload)
                    VALUES ($1, $2, $3, $4, $5, $6, 'draft', $7, '{}')", conn, tx))
                {
                    cmd.Parameters.AddWithValue(countId);
                    cmd.Parameters.AddWithValue(cc.ToString()!);
                    cmd.Parameters.AddWithValue(countNo);
                    cmd.Parameters.AddWithValue(warehouseCode);
                    cmd.Parameters.AddWithValue(DateOnly.Parse(countDate!));
                    cmd.Parameters.AddWithValue(description ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(currentUser);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 获取系统库存并创建盘点明细
                var balanceSql = @"
                    SELECT ib.material_code, ib.bin_code, ib.batch_no, ib.quantity, ib.uom,
                           m.name as material_name
                    FROM inventory_balances ib
                    LEFT JOIN materials m ON m.company_code = ib.company_code AND m.material_code = ib.material_code
                    WHERE ib.company_code = $1 AND ib.warehouse_code = $2";

                var balanceParams = new List<object?> { cc.ToString(), warehouseCode };
                int paramIdx = 3;

                if (!string.IsNullOrEmpty(binCode))
                {
                    balanceSql += $" AND ib.bin_code = ${paramIdx}";
                    balanceParams.Add(binCode);
                    paramIdx++;
                }

                if (materialCodes != null && materialCodes.Count > 0)
                {
                    balanceSql += $" AND ib.material_code = ANY(${paramIdx})";
                    balanceParams.Add(materialCodes.ToArray());
                }

                balanceSql += " ORDER BY ib.material_code, ib.bin_code, ib.batch_no";

                var lineNo = 0;
                await using (var cmd = new NpgsqlCommand(balanceSql, conn, tx))
                {
                    for (int i = 0; i < balanceParams.Count; i++)
                        cmd.Parameters.AddWithValue(balanceParams[i]!);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    var insertLines = new List<(string materialCode, string? materialName, string? binCode, string? batchNo, decimal systemQty, string? uom)>();
                    
                    while (await reader.ReadAsync())
                    {
                        insertLines.Add((
                            reader.GetString(0),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.GetDecimal(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4)
                        ));
                    }
                    await reader.CloseAsync();

                    // 插入明细行
                    foreach (var line in insertLines)
                    {
                        lineNo++;
                        await using var insertCmd = new NpgsqlCommand(@"
                            INSERT INTO inventory_count_lines (id, company_code, count_id, line_no, material_code, material_name, bin_code, batch_no, system_qty, uom, status)
                            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'pending')", conn, tx);
                        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
                        insertCmd.Parameters.AddWithValue(cc.ToString()!);
                        insertCmd.Parameters.AddWithValue(countId);
                        insertCmd.Parameters.AddWithValue(lineNo);
                        insertCmd.Parameters.AddWithValue(line.materialCode);
                        insertCmd.Parameters.AddWithValue(line.materialName ?? (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue(line.binCode ?? (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue(line.batchNo ?? (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue(line.systemQty);
                        insertCmd.Parameters.AddWithValue(line.uom ?? (object)DBNull.Value);
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                }

                // 如果没有库存记录但指定了物料，也创建空行
                if (lineNo == 0 && materialCodes != null && materialCodes.Count > 0)
                {
                    foreach (var matCode in materialCodes)
                    {
                        lineNo++;
                        // 获取物料名称
                        string? matName = null;
                        await using (var matCmd = new NpgsqlCommand("SELECT name FROM materials WHERE company_code = $1 AND material_code = $2", conn, tx))
                        {
                            matCmd.Parameters.AddWithValue(cc.ToString()!);
                            matCmd.Parameters.AddWithValue(matCode!);
                            var result = await matCmd.ExecuteScalarAsync();
                            matName = result?.ToString();
                        }

                        await using var insertCmd = new NpgsqlCommand(@"
                            INSERT INTO inventory_count_lines (id, company_code, count_id, line_no, material_code, material_name, bin_code, system_qty, status)
                            VALUES ($1, $2, $3, $4, $5, $6, $7, 0, 'pending')", conn, tx);
                        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
                        insertCmd.Parameters.AddWithValue(cc.ToString()!);
                        insertCmd.Parameters.AddWithValue(countId);
                        insertCmd.Parameters.AddWithValue(lineNo);
                        insertCmd.Parameters.AddWithValue(matCode!);
                        insertCmd.Parameters.AddWithValue(matName ?? (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue(binCode ?? (object)DBNull.Value);
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                }

                await tx.CommitAsync();
                return Results.Ok(new { id = countId, countNo, lineCount = lineNo });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to create inventory count: {ex.Message}");
            }
        }).RequireAuthorization();

        // 更新盘点单状态
        app.MapPut("/inventory/counts/{id:guid}/status", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (string.IsNullOrEmpty(status) || !new[] { "draft", "in_progress", "completed", "cancelled" }.Contains(status))
                return Results.BadRequest(new { error = "Invalid status" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            // 检查盘点单是否存在
            await using (var checkCmd = new NpgsqlCommand("SELECT status FROM inventory_counts WHERE id = $1 AND company_code = $2", conn))
            {
                checkCmd.Parameters.AddWithValue(id);
                checkCmd.Parameters.AddWithValue(cc.ToString()!);
                var currentStatus = await checkCmd.ExecuteScalarAsync();
                if (currentStatus == null) return Results.NotFound();

                // 已过账的盘点单不能修改状态
                if (currentStatus.ToString() == "posted")
                    return Results.BadRequest(new { error = "Cannot change status of posted count" });
            }

            var sql = status switch
            {
                "completed" => "UPDATE inventory_counts SET status = $1, completed_at = now(), completed_by = $3, updated_at = now() WHERE id = $2",
                _ => "UPDATE inventory_counts SET status = $1, updated_at = now() WHERE id = $2"
            };

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(id);
            if (status == "completed") cmd.Parameters.AddWithValue(currentUser);
            await cmd.ExecuteNonQueryAsync();

            return Results.Ok(new { success = true, status });
        }).RequireAuthorization();

        // 更新盘点明细（录入实际数量）
        app.MapPut("/inventory/counts/{countId:guid}/lines/{lineId:guid}", async (Guid countId, Guid lineId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var actualQty = root.TryGetProperty("actualQty", out var aq) ? aq.GetDecimal() : (decimal?)null;
            var varianceReason = root.TryGetProperty("varianceReason", out var vr) ? vr.GetString() : null;

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            // 检查盘点单状态
            await using (var checkCmd = new NpgsqlCommand("SELECT status FROM inventory_counts WHERE id = $1 AND company_code = $2", conn))
            {
                checkCmd.Parameters.AddWithValue(countId);
                checkCmd.Parameters.AddWithValue(cc.ToString()!);
                var status = await checkCmd.ExecuteScalarAsync();
                if (status == null) return Results.NotFound();
                if (status.ToString() != "draft" && status.ToString() != "in_progress")
                    return Results.BadRequest(new { error = "Cannot update lines of completed/cancelled count" });
            }

            await using var cmd = new NpgsqlCommand(@"
                UPDATE inventory_count_lines 
                SET actual_qty = $1, 
                    variance_reason = $2, 
                    status = CASE WHEN $1 IS NOT NULL THEN 'counted' ELSE 'pending' END,
                    counted_at = CASE WHEN $1 IS NOT NULL THEN now() ELSE NULL END,
                    counted_by = CASE WHEN $1 IS NOT NULL THEN $5 ELSE NULL END,
                    updated_at = now()
                WHERE id = $3 AND count_id = $4", conn);

            cmd.Parameters.AddWithValue(actualQty.HasValue ? actualQty.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(varianceReason ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(lineId);
            cmd.Parameters.AddWithValue(countId);
            cmd.Parameters.AddWithValue(currentUser);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 批量更新盘点明细
        app.MapPut("/inventory/counts/{countId:guid}/lines", async (Guid countId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("lines", out var linesArr) || linesArr.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "lines array required" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 检查盘点单状态
                await using (var checkCmd = new NpgsqlCommand("SELECT status FROM inventory_counts WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue(countId);
                    checkCmd.Parameters.AddWithValue(cc.ToString()!);
                    var status = await checkCmd.ExecuteScalarAsync();
                    if (status == null) return Results.NotFound();
                    if (status.ToString() != "draft" && status.ToString() != "in_progress")
                        return Results.BadRequest(new { error = "Cannot update lines of completed/cancelled count" });
                }

                var updatedCount = 0;
                foreach (var line in linesArr.EnumerateArray())
                {
                    if (!line.TryGetProperty("id", out var lineIdProp)) continue;
                    var lineId = Guid.Parse(lineIdProp.GetString()!);
                    var actualQty = line.TryGetProperty("actualQty", out var aq) && aq.ValueKind == JsonValueKind.Number ? aq.GetDecimal() : (decimal?)null;
                    var varianceReason = line.TryGetProperty("varianceReason", out var vr) ? vr.GetString() : null;

                    await using var cmd = new NpgsqlCommand(@"
                        UPDATE inventory_count_lines 
                        SET actual_qty = $1, 
                            variance_reason = $2, 
                            status = CASE WHEN $1 IS NOT NULL THEN 'counted' ELSE 'pending' END,
                            counted_at = CASE WHEN $1 IS NOT NULL THEN now() ELSE NULL END,
                            counted_by = CASE WHEN $1 IS NOT NULL THEN $5 ELSE NULL END,
                            updated_at = now()
                        WHERE id = $3 AND count_id = $4", conn, tx);

                    cmd.Parameters.AddWithValue(actualQty.HasValue ? actualQty.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue(varianceReason ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(lineId);
                    cmd.Parameters.AddWithValue(countId);
                    cmd.Parameters.AddWithValue(currentUser);

                    updatedCount += await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { success = true, updatedCount });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to update lines: {ex.Message}");
            }
        }).RequireAuthorization();

        // 过账盘点差异（生成库存调整）
        app.MapPost("/inventory/counts/{id:guid}/post", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取盘点单信息
                string? warehouseCode = null;
                string? countNo = null;
                DateOnly countDate = DateOnly.FromDateTime(DateTime.Today);

                await using (var checkCmd = new NpgsqlCommand(@"
                    SELECT status, warehouse_code, count_no, count_date 
                    FROM inventory_counts 
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue(id);
                    checkCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await checkCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync()) return Results.NotFound();

                    var status = reader.GetString(0);
                    if (status == "posted")
                        return Results.BadRequest(new { error = "Count already posted" });
                    if (status != "completed")
                        return Results.BadRequest(new { error = "Count must be completed before posting" });

                    warehouseCode = reader.GetString(1);
                    countNo = reader.GetString(2);
                    countDate = reader.GetFieldValue<DateOnly>(3);
                }

                // 获取有差异的明细
                var varianceLines = new List<(Guid lineId, string materialCode, string? binCode, string? batchNo, decimal varianceQty, string? uom)>();
                await using (var linesCmd = new NpgsqlCommand(@"
                    SELECT id, material_code, bin_code, batch_no, variance_qty, uom
                    FROM inventory_count_lines
                    WHERE count_id = $1 AND company_code = $2 AND variance_qty <> 0 AND actual_qty IS NOT NULL", conn, tx))
                {
                    linesCmd.Parameters.AddWithValue(id);
                    linesCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await linesCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        varianceLines.Add((
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.GetDecimal(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5)
                        ));
                    }
                }

                // 对每个有差异的行，更新库存余额和生成库存台账
                foreach (var line in varianceLines)
                {
                    // 更新库存余额
                    await using (var updateBalCmd = new NpgsqlCommand(@"
                        INSERT INTO inventory_balances (id, company_code, warehouse_code, bin_code, material_code, batch_no, quantity, uom)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                        ON CONFLICT (company_code, warehouse_code, COALESCE(bin_code, ''), material_code, COALESCE(batch_no, ''))
                        DO UPDATE SET quantity = inventory_balances.quantity + $7, updated_at = now()", conn, tx))
                    {
                        updateBalCmd.Parameters.AddWithValue(Guid.NewGuid());
                        updateBalCmd.Parameters.AddWithValue(cc.ToString()!);
                        updateBalCmd.Parameters.AddWithValue(warehouseCode!);
                        updateBalCmd.Parameters.AddWithValue(line.binCode ?? (object)DBNull.Value);
                        updateBalCmd.Parameters.AddWithValue(line.materialCode);
                        updateBalCmd.Parameters.AddWithValue(line.batchNo ?? (object)DBNull.Value);
                        updateBalCmd.Parameters.AddWithValue(line.varianceQty);
                        updateBalCmd.Parameters.AddWithValue(line.uom ?? (object)DBNull.Value);
                        await updateBalCmd.ExecuteNonQueryAsync();
                    }

                    // 生成库存台账
                    var movementType = line.varianceQty > 0 ? "COUNT_GAIN" : "COUNT_LOSS";
                    await using (var ledgerCmd = new NpgsqlCommand(@"
                        INSERT INTO inventory_ledger (id, company_code, warehouse_code, bin_code, material_code, batch_no, movement_type, quantity, uom, reference_no, reference_type, movement_date, payload)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'INVENTORY_COUNT', $11, '{}')", conn, tx))
                    {
                        ledgerCmd.Parameters.AddWithValue(Guid.NewGuid());
                        ledgerCmd.Parameters.AddWithValue(cc.ToString()!);
                        ledgerCmd.Parameters.AddWithValue(warehouseCode!);
                        ledgerCmd.Parameters.AddWithValue(line.binCode ?? (object)DBNull.Value);
                        ledgerCmd.Parameters.AddWithValue(line.materialCode);
                        ledgerCmd.Parameters.AddWithValue(line.batchNo ?? (object)DBNull.Value);
                        ledgerCmd.Parameters.AddWithValue(movementType);
                        ledgerCmd.Parameters.AddWithValue(Math.Abs(line.varianceQty));
                        ledgerCmd.Parameters.AddWithValue(line.uom ?? (object)DBNull.Value);
                        ledgerCmd.Parameters.AddWithValue(countNo!);
                        ledgerCmd.Parameters.AddWithValue(countDate);
                        await ledgerCmd.ExecuteNonQueryAsync();
                    }
                }

                // 更新盘点单状态为已过账
                await using (var updateCmd = new NpgsqlCommand(@"
                    UPDATE inventory_counts 
                    SET status = 'posted', posted_at = now(), posted_by = $3, updated_at = now()
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue(id);
                    updateCmd.Parameters.AddWithValue(cc.ToString()!);
                    updateCmd.Parameters.AddWithValue(currentUser);
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { success = true, adjustedLines = varianceLines.Count });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to post inventory count: {ex.Message}");
            }
        }).RequireAuthorization();

        // 删除盘点单
        app.MapDelete("/inventory/counts/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 检查状态
            await using (var checkCmd = new NpgsqlCommand("SELECT status FROM inventory_counts WHERE id = $1 AND company_code = $2", conn))
            {
                checkCmd.Parameters.AddWithValue(id);
                checkCmd.Parameters.AddWithValue(cc.ToString()!);
                var status = await checkCmd.ExecuteScalarAsync();
                if (status == null) return Results.NotFound();
                if (status.ToString() == "posted")
                    return Results.BadRequest(new { error = "Cannot delete posted count" });
            }

            await using var cmd = new NpgsqlCommand("DELETE FROM inventory_counts WHERE id = $1 AND company_code = $2", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            await cmd.ExecuteNonQueryAsync();

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 盘点报表 - 获取盘盈盘亏清单
        app.MapGet("/inventory/counts/report/variance", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var fromDate = req.Query["fromDate"].FirstOrDefault();
            var toDate = req.Query["toDate"].FirstOrDefault();
            var warehouseCode = req.Query["warehouseCode"].FirstOrDefault();
            var countId = req.Query["countId"].FirstOrDefault();

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT 
                        ic.id as count_id,
                        ic.count_no,
                        ic.count_date,
                        ic.warehouse_code,
                        w.name as warehouse_name,
                        ic.status,
                        icl.id as line_id,
                        icl.line_no,
                        icl.material_code,
                        COALESCE(icl.material_name, m.name) as material_name,
                        icl.bin_code,
                        icl.batch_no,
                        icl.uom,
                        icl.system_qty,
                        icl.actual_qty,
                        icl.variance_qty,
                        icl.variance_reason,
                        CASE WHEN icl.variance_qty > 0 THEN 'gain' WHEN icl.variance_qty < 0 THEN 'loss' ELSE 'match' END as variance_type
                    FROM inventory_counts ic
                    JOIN inventory_count_lines icl ON icl.count_id = ic.id
                    LEFT JOIN warehouses w ON w.company_code = ic.company_code AND w.warehouse_code = ic.warehouse_code
                    LEFT JOIN materials m ON m.company_code = icl.company_code AND m.material_code = icl.material_code
                    WHERE ic.company_code = $1 
                      AND icl.actual_qty IS NOT NULL
                      AND icl.variance_qty <> 0";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(fromDate))
            {
                sql += $" AND ic.count_date >= ${paramIndex}";
                parameters.Add(DateOnly.Parse(fromDate));
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(toDate))
            {
                sql += $" AND ic.count_date <= ${paramIndex}";
                parameters.Add(DateOnly.Parse(toDate));
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(warehouseCode))
            {
                sql += $" AND ic.warehouse_code = ${paramIndex}";
                parameters.Add(warehouseCode);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(countId) && Guid.TryParse(countId, out var cid))
            {
                sql += $" AND ic.id = ${paramIndex}";
                parameters.Add(cid);
            }

            sql += " ORDER BY ic.count_date DESC, ic.count_no, icl.line_no) t";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());

            // 计算汇总
            decimal totalGain = 0, totalLoss = 0;
            int gainCount = 0, lossCount = 0;

            foreach (var row in rows)
            {
                using var rowDoc = JsonDocument.Parse(row);
                if (rowDoc.RootElement.TryGetProperty("variance_qty", out var vq))
                {
                    var variance = vq.GetDecimal();
                    if (variance > 0) { totalGain += variance; gainCount++; }
                    else if (variance < 0) { totalLoss += Math.Abs(variance); lossCount++; }
                }
            }

            return Results.Text(JsonSerializer.Serialize(new
            {
                data = JsonSerializer.Deserialize<JsonElement>($"[{string.Join(",", rows)}]"),
                summary = new { totalGain, totalLoss, gainCount, lossCount, totalCount = gainCount + lossCount }
            }), "application/json");
        }).RequireAuthorization();
    }

    private static async Task<string> GenerateCountNoAsync(NpgsqlConnection conn, string companyCode, NpgsqlTransaction tx)
    {
        var now = DateTime.Now;
        var year = now.Year;
        var month = now.Month;
        var prefix = "IC";

        // 获取或创建序列
        await using var upsertCmd = new NpgsqlCommand(@"
            INSERT INTO inventory_count_sequences (company_code, prefix, year, month, last_number)
            VALUES ($1, $2, $3, $4, 1)
            ON CONFLICT (company_code, prefix, year, month)
            DO UPDATE SET last_number = inventory_count_sequences.last_number + 1
            RETURNING last_number", conn, tx);

        upsertCmd.Parameters.AddWithValue(companyCode);
        upsertCmd.Parameters.AddWithValue(prefix);
        upsertCmd.Parameters.AddWithValue(year);
        upsertCmd.Parameters.AddWithValue(month);

        var lastNumber = (int)(await upsertCmd.ExecuteScalarAsync())!;
        return $"{prefix}{year:D4}{month:D2}{lastNumber:D4}";
    }
}


using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 纳品书模块 - 使用 schema/payload 方式存储数据
/// 数据结构：{ header: {...}, lines: [...] }
/// </summary>
public static class DeliveryNoteModule
{
    public static void MapDeliveryNoteModule(this WebApplication app, AccountSelectionService? accountService = null)
    {
        // 获取纳品书列表
        app.MapGet("/delivery-notes", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var customerCode = req.Query["customerCode"].FirstOrDefault();
            var fromDate = req.Query["fromDate"].FirstOrDefault();
            var toDate = req.Query["toDate"].FirstOrDefault();
            var excludeInvoiced = req.Query["excludeInvoiced"].FirstOrDefault();

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT dn.id, dn.delivery_no, dn.so_no as sales_order_no, 
                           dn.customer_code, dn.customer_name, dn.delivery_date, dn.status,
                           dn.payload,
                           w.name as warehouse_name,
                           jsonb_array_length(COALESCE(dn.payload->'lines', '[]')) as line_count
                    FROM delivery_notes dn
                    LEFT JOIN warehouses w ON w.company_code = dn.company_code 
                        AND w.warehouse_code = dn.payload->'header'->>'warehouseCode'
                    WHERE dn.company_code = $1";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND dn.status = ${paramIndex}";
                parameters.Add(status);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(customerCode))
            {
                sql += $" AND dn.customer_code = ${paramIndex}";
                parameters.Add(customerCode);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(fromDate) && DateOnly.TryParse(fromDate, out var fd))
            {
                sql += $" AND dn.delivery_date >= ${paramIndex}";
                parameters.Add(fd);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(toDate) && DateOnly.TryParse(toDate, out var td))
            {
                sql += $" AND dn.delivery_date <= ${paramIndex}";
                parameters.Add(td);
                paramIndex++;
            }

            // 排除已被请求书引用的纳品书（避免重复请求）
            if (excludeInvoiced == "true" || excludeInvoiced == "1")
            {
                sql += @" AND NOT EXISTS (
                    SELECT 1 FROM sales_invoices si 
                    WHERE si.company_code = dn.company_code 
                      AND si.status NOT IN ('cancelled')
                      AND si.payload->'deliveryNoteIds' ? dn.id::text
                )";
            }

            sql += " ORDER BY dn.delivery_date DESC, dn.created_at DESC LIMIT 200) t";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 获取纳品书详情
        app.MapGet("/delivery-notes/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT dn.id, dn.delivery_no, dn.so_no as sales_order_no, 
                           dn.customer_code, dn.customer_name, dn.delivery_date, dn.status,
                           dn.payload,
                           w.name as warehouse_name
                    FROM delivery_notes dn
                    LEFT JOIN warehouses w ON w.company_code = dn.company_code 
                        AND w.warehouse_code = dn.payload->'header'->>'warehouseCode'
                    WHERE dn.id = $1 AND dn.company_code = $2
                ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { id, cc.ToString() });
            if (rows.Count == 0) return Results.NotFound();
            return Results.Text(rows[0], "application/json");
        }).RequireAuthorization();

        // 从销售订单创建纳品书
        app.MapPost("/delivery-notes/from-sales-order/{salesOrderId:guid}", async (Guid salesOrderId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var warehouseCode = root.TryGetProperty("warehouseCode", out var wc) ? wc.GetString() : null;
            var deliveryDate = root.TryGetProperty("deliveryDate", out var dd) ? dd.GetString() : DateTime.Today.ToString("yyyy-MM-dd");

            if (string.IsNullOrEmpty(warehouseCode))
                return Results.BadRequest(new { error = "warehouseCode is required" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取销售订单信息
                string? soNo = null, customerCode = null, customerName = null;
                Guid? customerId = null;
                JsonElement soPayload = default;

                await using (var soCmd = new NpgsqlCommand(@"
                    SELECT so_no, payload FROM sales_orders 
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    soCmd.Parameters.AddWithValue(salesOrderId);
                    soCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await soCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                        return Results.NotFound(new { error = "Sales order not found" });

                    soNo = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var payloadStr = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                    using var payloadDoc = JsonDocument.Parse(payloadStr);
                    soPayload = payloadDoc.RootElement.Clone();
                    
                    // 兼容 partnerCode/customerCode 两种字段名
                    customerCode = soPayload.TryGetProperty("partnerCode", out var pc1) ? pc1.GetString() 
                                 : soPayload.TryGetProperty("customerCode", out var cc1) ? cc1.GetString() : null;
                    customerName = soPayload.TryGetProperty("partnerName", out var pn1) ? pn1.GetString() 
                                 : soPayload.TryGetProperty("customerName", out var cn1) ? cn1.GetString() : null;
                    if (soPayload.TryGetProperty("partnerId", out var pi1) && Guid.TryParse(pi1.GetString(), out var pid))
                        customerId = pid;
                    else if (soPayload.TryGetProperty("customerId", out var ci1) && Guid.TryParse(ci1.GetString(), out var cid))
                        customerId = cid;
                }
                
                // 获取配送先
                string? deliveryAddress = null;
                if (soPayload.TryGetProperty("deliveryAddress", out var da) && da.ValueKind == JsonValueKind.String)
                {
                    deliveryAddress = da.GetString();
                }

                // 生成纳品书编号
                var deliveryNo = await GenerateDeliveryNoAsync(conn, cc.ToString()!, tx);

                // 构建 payload
                var lines = new List<object>();
                int lineNo = 0;

                // 获取销售订单明细
                if (soPayload.TryGetProperty("lines", out var soLines) && soLines.ValueKind == JsonValueKind.Array)
                {
                    // 获取已纳品数量（使用更可靠的查询方式，避免事务中止）
                    var deliveredQtys = new Dictionary<string, decimal>();
                    
                    // 先检查是否存在相关纳品书
                    await using (var checkCmd = new NpgsqlCommand(@"
                        SELECT id, payload->'lines' as lines FROM delivery_notes 
                        WHERE company_code = $1 AND so_no = $2 AND status NOT IN ('cancelled')", conn, tx))
                    {
                        checkCmd.Parameters.AddWithValue(cc.ToString()!);
                        checkCmd.Parameters.AddWithValue(soNo ?? "");
                        await using var checkReader = await checkCmd.ExecuteReaderAsync();
                        while (await checkReader.ReadAsync())
                        {
                            if (checkReader.IsDBNull(1)) continue;
                            var linesJson = checkReader.GetString(1);
                            try
                            {
                                using var linesDoc = JsonDocument.Parse(linesJson);
                                if (linesDoc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var line in linesDoc.RootElement.EnumerateArray())
                                    {
                                        var lineId = line.TryGetProperty("salesOrderLineId", out var lid) ? lid.GetString() ?? "" : "";
                                        var qty = line.TryGetProperty("deliveryQty", out var dq) && dq.TryGetDecimal(out var dqv) ? dqv : 0m;
                                        if (!string.IsNullOrEmpty(lineId))
                                        {
                                            deliveredQtys[lineId] = deliveredQtys.GetValueOrDefault(lineId, 0m) + qty;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore parse errors */ }
                        }
                    }

                    foreach (var soLine in soLines.EnumerateArray())
                    {
                        var soLineId = soLine.TryGetProperty("lineId", out var lid) ? lid.GetString() : Guid.NewGuid().ToString();
                        var materialCode = soLine.TryGetProperty("materialCode", out var mc) ? mc.GetString() : "";
                        var materialName = soLine.TryGetProperty("materialName", out var mn) ? mn.GetString() : "";
                        var orderedQty = soLine.TryGetProperty("quantity", out var oq) && oq.TryGetDecimal(out var oqv) ? oqv : 0m;
                        var uom = soLine.TryGetProperty("uom", out var u) ? u.GetString() : "";
                        var unitPrice = soLine.TryGetProperty("unitPrice", out var up) && up.TryGetDecimal(out var upv) ? upv : 0m;

                        var prevDelivered = deliveredQtys.GetValueOrDefault(soLineId ?? "", 0m);
                        var remainingQty = orderedQty - prevDelivered;

                        if (remainingQty <= 0) continue; // 已全部纳品

                        lineNo++;
                        lines.Add(new
                        {
                            lineNo,
                            salesOrderLineId = soLineId,
                            materialCode,
                            materialName,
                            orderedQty,
                            previouslyDeliveredQty = prevDelivered,
                            deliveryQty = remainingQty, // 默认纳品剩余数量
                            uom,
                            unitPrice,
                            amount = remainingQty * unitPrice
                        });
                    }
                }

                if (lineNo == 0)
                    return Results.BadRequest(new { error = "No remaining items to deliver" });

                var header = new
                {
                    deliveryNo,
                    salesOrderId = salesOrderId.ToString(),
                    salesOrderNo = soNo,
                    customerId = customerId?.ToString(),
                    customerCode,
                    customerName,
                    deliveryDate,
                    warehouseCode,
                    deliveryAddress,  // 配送先（从受注书复制）
                    status = "confirmed",  // 纳品书作成后直接进入出荷待ち状态
                    createdBy = currentUser
                };

                var payload = new { header, lines };
                var payloadJson = JsonSerializer.Serialize(payload);

                // 插入数据库（只插入非生成列，其他列通过 payload 自动生成）
                var deliveryNoteId = Guid.NewGuid();
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO delivery_notes (id, company_code, payload, created_at, updated_at)
                    VALUES ($1, $2, $3::jsonb, now(), now())", conn, tx))
                {
                    cmd.Parameters.AddWithValue(deliveryNoteId);
                    cmd.Parameters.AddWithValue(cc.ToString()!);
                    cmd.Parameters.AddWithValue(payloadJson);
                    await cmd.ExecuteNonQueryAsync();
                }

                // 更新销售订单状态为"partial_shipped"（纳品书已发行，等待出库）
                await using (var updateSoCmd = new NpgsqlCommand(@"
                    UPDATE sales_orders 
                    SET payload = jsonb_set(payload, '{status}', $3::jsonb),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2
                      AND COALESCE(payload->>'status', 'new') IN ('new', 'confirmed', 'draft')", conn, tx))
                {
                    updateSoCmd.Parameters.AddWithValue(salesOrderId);
                    updateSoCmd.Parameters.AddWithValue(cc.ToString()!);
                    updateSoCmd.Parameters.AddWithValue("\"partial_shipped\"");
                    await updateSoCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { id = deliveryNoteId, deliveryNo, lineCount = lineNo });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Console.WriteLine($"[DeliveryNote] Error creating from sales order: {ex}");
                return Results.Problem($"Failed to create delivery note: {ex.Message}");
            }
        }).RequireAuthorization();

        // 创建纳品书（无关联订单）
        app.MapPost("/delivery-notes", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var warehouseCode = root.TryGetProperty("warehouseCode", out var wc) ? wc.GetString() : null;
            var deliveryDate = root.TryGetProperty("deliveryDate", out var dd) ? dd.GetString() : DateTime.Today.ToString("yyyy-MM-dd");
            var customerId = root.TryGetProperty("customerId", out var ci) ? ci.GetString() : null;
            var customerCode = root.TryGetProperty("customerCode", out var cco) ? cco.GetString() : null;
            var customerName = root.TryGetProperty("customerName", out var cn) ? cn.GetString() : null;

            if (string.IsNullOrEmpty(warehouseCode))
                return Results.BadRequest(new { error = "warehouseCode is required" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                var deliveryNo = await GenerateDeliveryNoAsync(conn, cc.ToString()!, tx);
                var deliveryNoteId = Guid.NewGuid();

                var lines = new List<object>();
                if (root.TryGetProperty("lines", out var linesArr) && linesArr.ValueKind == JsonValueKind.Array)
                {
                    int lineNo = 0;
                    foreach (var line in linesArr.EnumerateArray())
                    {
                        lineNo++;
                        lines.Add(new
                        {
                            lineNo,
                            materialCode = line.TryGetProperty("materialCode", out var mc) ? mc.GetString() : "",
                            materialName = line.TryGetProperty("materialName", out var mn) ? mn.GetString() : "",
                            deliveryQty = line.TryGetProperty("deliveryQty", out var dq) && dq.TryGetDecimal(out var dqv) ? dqv : 0m,
                            uom = line.TryGetProperty("uom", out var u) ? u.GetString() : "",
                            binCode = line.TryGetProperty("binCode", out var bc) ? bc.GetString() : "",
                            batchNo = line.TryGetProperty("batchNo", out var bn) ? bn.GetString() : ""
                        });
                    }
                }

                var header = new
                {
                    deliveryNo,
                    customerId,
                    customerCode,
                    customerName,
                    deliveryDate,
                    warehouseCode,
                    status = "draft",
                    createdBy = currentUser
                };

                var payload = new { header, lines };
                var payloadJson = JsonSerializer.Serialize(payload);

                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO delivery_notes (id, company_code, delivery_no, customer_code, customer_name, 
                        delivery_date, status, payload, created_at, updated_at)
                    VALUES ($1, $2, $3, $4, $5, $6, 'draft', $7::jsonb, now(), now())", conn, tx))
                {
                    cmd.Parameters.AddWithValue(deliveryNoteId);
                    cmd.Parameters.AddWithValue(cc.ToString()!);
                    cmd.Parameters.AddWithValue(deliveryNo);
                    cmd.Parameters.AddWithValue(customerCode ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(customerName ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue(DateOnly.Parse(deliveryDate!));
                    cmd.Parameters.AddWithValue(payloadJson);
                    await cmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { id = deliveryNoteId, deliveryNo });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to create delivery note: {ex.Message}");
            }
        }).RequireAuthorization();

        // 更新纳品书明细
        app.MapPut("/delivery-notes/{id:guid}/lines", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("lines", out var linesArr) || linesArr.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "lines array required" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取当前数据
            string? currentPayloadStr = null;
            string? currentStatus = null;
            await using (var getCmd = new NpgsqlCommand("SELECT payload, status FROM delivery_notes WHERE id = $1 AND company_code = $2", conn))
            {
                getCmd.Parameters.AddWithValue(id);
                getCmd.Parameters.AddWithValue(cc.ToString()!);
                await using var reader = await getCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return Results.NotFound();
                currentPayloadStr = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
                currentStatus = reader.IsDBNull(1) ? "draft" : reader.GetString(1);
            }

            if (currentStatus != "draft")
                return Results.BadRequest(new { error = "Can only update draft delivery notes" });

            using var currentDoc = JsonDocument.Parse(currentPayloadStr);
            var currentPayload = currentDoc.RootElement;

            // 更新明细
            var updatedLines = new List<JsonElement>();
            foreach (var newLine in linesArr.EnumerateArray())
            {
                updatedLines.Add(newLine.Clone());
            }

            // 构建新的 payload
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                
                // 复制 header
                if (currentPayload.TryGetProperty("header", out var header))
                {
                    writer.WritePropertyName("header");
                    header.WriteTo(writer);
                }
                
                // 写入更新后的 lines
                writer.WritePropertyName("lines");
                writer.WriteStartArray();
                foreach (var line in updatedLines)
                {
                    line.WriteTo(writer);
                }
                writer.WriteEndArray();
                
                writer.WriteEndObject();
            }

            var newPayloadJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

            await using var updateCmd = new NpgsqlCommand(@"
                UPDATE delivery_notes SET payload = $1::jsonb, updated_at = now()
                WHERE id = $2 AND company_code = $3", conn);
            updateCmd.Parameters.AddWithValue(newPayloadJson);
            updateCmd.Parameters.AddWithValue(id);
            updateCmd.Parameters.AddWithValue(cc.ToString()!);
            await updateCmd.ExecuteNonQueryAsync();

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 确认纳品书
        // 用途：将纳品书从"草稿"状态变更为"确认"状态，确认后才能进行出货操作
        app.MapPost("/delivery-notes/{id:guid}/confirm", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            // 更新状态和 payload 中的确认信息（合并所有 jsonb_set 操作）
            // 注意：status 是 generated column，只能更新 payload
            await using var cmd = new NpgsqlCommand(@"
                UPDATE delivery_notes 
                SET payload = jsonb_set(
                        jsonb_set(
                            jsonb_set(payload, '{header,status}', '""confirmed""'),
                            '{header,confirmedAt}', to_jsonb(now()::text)
                        ),
                        '{header,confirmedBy}', $3::jsonb
                    ),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'draft'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue($"\"{currentUser}\"");

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.BadRequest(new { error = "Cannot confirm - not in draft status" });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 出货（生成出库记录并扣减库存，同时生成应收账款凭证）
        app.MapPost("/delivery-notes/{id:guid}/ship", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            // 获取 AccountSelectionService（如果可用）
            var acctService = accountService ?? req.HttpContext.RequestServices.GetService<AccountSelectionService>();

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取纳品书信息
                string? warehouseCode = null, deliveryNo = null, payloadStr = null;
                string? customerCode = null, customerName = null, soNo = null;
                DateOnly deliveryDate = DateOnly.FromDateTime(DateTime.Today);

                await using (var checkCmd = new NpgsqlCommand(@"
                    SELECT status, payload->'header'->>'warehouseCode', delivery_no, delivery_date, payload,
                           customer_code, customer_name, so_no
                    FROM delivery_notes 
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue(id);
                    checkCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await checkCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync()) return Results.NotFound();

                    var status = reader.GetString(0);
                    if (status != "confirmed")
                        return Results.BadRequest(new { error = "Can only ship confirmed delivery notes" });

                    warehouseCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                    deliveryNo = reader.GetString(2);
                    deliveryDate = reader.GetFieldValue<DateOnly>(3);
                    payloadStr = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
                    customerCode = reader.IsDBNull(5) ? null : reader.GetString(5);
                    customerName = reader.IsDBNull(6) ? null : reader.GetString(6);
                    soNo = reader.IsDBNull(7) ? null : reader.GetString(7);
                }

                if (string.IsNullOrEmpty(warehouseCode))
                    return Results.BadRequest(new { error = "Warehouse not specified" });

                using var payloadDoc = JsonDocument.Parse(payloadStr);
                var payload = payloadDoc.RootElement;

                // 计算总金额和税额
                decimal totalAmount = 0m;
                decimal taxAmount = 0m;

                // 获取明细并更新库存
                if (payload.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        var materialCode = line.TryGetProperty("materialCode", out var mc) ? mc.GetString() : "";
                        var deliveryQty = line.TryGetProperty("deliveryQty", out var dq) && dq.TryGetDecimal(out var dqv) ? dqv : 0m;
                        var binCode = line.TryGetProperty("binCode", out var bc) ? bc.GetString() : null;
                        var batchNo = line.TryGetProperty("batchNo", out var bn) ? bn.GetString() : null;
                        var uom = line.TryGetProperty("uom", out var u) ? u.GetString() : null;
                        var unitPrice = line.TryGetProperty("unitPrice", out var up) && up.TryGetDecimal(out var upv) ? upv : 0m;
                        var taxRate = line.TryGetProperty("taxRate", out var tr) && tr.TryGetDecimal(out var trv) ? trv : 10m; // 默认10%

                        if (string.IsNullOrEmpty(materialCode) || deliveryQty <= 0) continue;

                        // 计算金额
                        var lineAmount = deliveryQty * unitPrice;
                        var lineTax = Math.Round(lineAmount * taxRate / 100m, 0); // 日元四舍五入到整数
                        totalAmount += lineAmount + lineTax;
                        taxAmount += lineTax;

                        // 减少库存余额
                        await using (var updateBalCmd = new NpgsqlCommand(@"
                            UPDATE inventory_balances 
                            SET quantity = quantity - $1, updated_at = now()
                            WHERE company_code = $2 AND warehouse_code = $3 
                              AND COALESCE(bin_code, '') = COALESCE($4, '')
                              AND material_code = $5 
                              AND COALESCE(batch_no, '') = COALESCE($6, '')", conn, tx))
                        {
                            updateBalCmd.Parameters.AddWithValue(deliveryQty);
                            updateBalCmd.Parameters.AddWithValue(cc.ToString()!);
                            updateBalCmd.Parameters.AddWithValue(warehouseCode);
                            updateBalCmd.Parameters.AddWithValue(binCode ?? (object)DBNull.Value);
                            updateBalCmd.Parameters.AddWithValue(materialCode);
                            updateBalCmd.Parameters.AddWithValue(batchNo ?? (object)DBNull.Value);
                            await updateBalCmd.ExecuteNonQueryAsync();
                        }

                        // 生成库存台账（出库：from_warehouse/from_bin 填写，to为空）
                        var movementId = Guid.NewGuid();
                        await using (var ledgerCmd = new NpgsqlCommand(@"
                            INSERT INTO inventory_ledger (id, company_code, movement_id, line_no, movement_type, movement_date, 
                                material_code, quantity, uom, from_warehouse, from_bin, to_warehouse, to_bin, batch_no, status_code)
                            VALUES ($1, $2, $3, 1, 'OUT', $4, $5, $6, $7, $8, $9, NULL, NULL, $10, 'DELIVERED')", conn, tx))
                        {
                            ledgerCmd.Parameters.AddWithValue(Guid.NewGuid());
                            ledgerCmd.Parameters.AddWithValue(cc.ToString()!);
                            ledgerCmd.Parameters.AddWithValue(movementId);
                            ledgerCmd.Parameters.AddWithValue(deliveryDate);
                            ledgerCmd.Parameters.AddWithValue(materialCode);
                            ledgerCmd.Parameters.AddWithValue(deliveryQty);
                            ledgerCmd.Parameters.AddWithValue(uom ?? (object)DBNull.Value);
                            ledgerCmd.Parameters.AddWithValue(warehouseCode);
                            ledgerCmd.Parameters.AddWithValue(binCode ?? (object)DBNull.Value);
                            ledgerCmd.Parameters.AddWithValue(batchNo ?? (object)DBNull.Value);
                            await ledgerCmd.ExecuteNonQueryAsync();
                        }
                    }
                }

                // 注意：会计凭证在请求书发行时生成，出库时不生成凭证

                // 更新纳品书状态
                var updatePayload = new JsonObject
                {
                    ["status"] = "shipped",
                    ["shippedAt"] = DateTime.UtcNow.ToString("o"),
                    ["shippedBy"] = currentUser,
                    ["totalAmount"] = totalAmount,
                    ["taxAmount"] = taxAmount
                };

                await using (var updateCmd = new NpgsqlCommand(@"
                    UPDATE delivery_notes 
                    SET payload = payload || jsonb_build_object('header', payload->'header' || $3::jsonb),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue(id);
                    updateCmd.Parameters.AddWithValue(cc.ToString()!);
                    updateCmd.Parameters.AddWithValue(updatePayload.ToJsonString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { 
                    success = true, 
                    totalAmount, 
                    taxAmount
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Console.WriteLine($"[DeliveryNote.Ship] Error: {ex}");
                return Results.Problem($"Failed to ship: {ex.Message}");
            }
        }).RequireAuthorization();

        // 确认送达
        app.MapPost("/delivery-notes/{id:guid}/deliver", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            await using var cmd = new NpgsqlCommand(@"
                UPDATE delivery_notes 
                SET status = 'delivered',
                    payload = jsonb_set(
                        jsonb_set(
                            jsonb_set(payload, '{header,status}', '""delivered""'),
                            '{header,deliveredAt}', to_jsonb(now()::text)
                        ),
                        '{header,deliveredBy}', $3::jsonb
                    ),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'shipped'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue($"\"{currentUser}\"");

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.BadRequest(new { error = "Cannot deliver - not in shipped status" });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 取消纳品书
        app.MapPost("/delivery-notes/{id:guid}/cancel", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            await using var cmd = new NpgsqlCommand(@"
                UPDATE delivery_notes 
                SET status = 'cancelled',
                    payload = jsonb_set(payload, '{header,status}', '""cancelled""'),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status IN ('draft', 'confirmed')", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.BadRequest(new { error = "Cannot cancel - already shipped or delivered" });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 删除纳品书
        app.MapDelete("/delivery-notes/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            await using var cmd = new NpgsqlCommand(@"
                DELETE FROM delivery_notes 
                WHERE id = $1 AND company_code = $2 AND status = 'draft'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.BadRequest(new { error = "Cannot delete - not in draft status" });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();
    }

    private static async Task<string> GenerateDeliveryNoAsync(NpgsqlConnection conn, string companyCode, NpgsqlTransaction tx)
    {
        var now = DateTime.Now;
        var stamp = now.ToString("yyyyMMddHHmmssfff");
        
        // 尝试使用序列表
        try
        {
            await using var upsertCmd = new NpgsqlCommand(@"
                INSERT INTO delivery_note_sequences (company_code, prefix, year, month, last_number)
                VALUES ($1, 'DN', $2, $3, 1)
                ON CONFLICT (company_code, prefix, year, month)
                DO UPDATE SET last_number = delivery_note_sequences.last_number + 1
                RETURNING last_number", conn, tx);

            upsertCmd.Parameters.AddWithValue(companyCode);
            upsertCmd.Parameters.AddWithValue(now.Year);
            upsertCmd.Parameters.AddWithValue(now.Month);

            var lastNumber = await upsertCmd.ExecuteScalarAsync();
            if (lastNumber != null)
                return $"DN{now.Year:D4}{now.Month:D2}{(int)lastNumber:D4}";
        }
        catch
        {
            // 序列表不存在，使用时间戳方式
        }

        return $"DN-{stamp}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }
}

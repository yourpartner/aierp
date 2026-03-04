using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 销售请求书模块 - 处理请求书的创建、发行、取消等操作
/// </summary>
public static class SalesInvoiceModule
{
    public static void MapSalesInvoiceModule(this WebApplication app, AccountSelectionService? accountService = null)
    {
        // 获取请求书列表
        app.MapGet("/sales-invoices", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var customerCode = req.Query["customerCode"].FirstOrDefault();
            var fromDate = req.Query["fromDate"].FirstOrDefault();
            var toDate = req.Query["toDate"].FirstOrDefault();

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT id, invoice_no, customer_code, customer_name, 
                           invoice_date, due_date, amount_total, tax_amount, status,
                           payload,
                           created_at, updated_at
                    FROM sales_invoices
                    WHERE company_code = $1";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND status = ${paramIndex}";
                parameters.Add(status);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(customerCode))
            {
                sql += $" AND customer_code = ${paramIndex}";
                parameters.Add(customerCode);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(fromDate) && DateOnly.TryParse(fromDate, out var fd))
            {
                sql += $" AND invoice_date >= ${paramIndex}";
                parameters.Add(fd);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(toDate) && DateOnly.TryParse(toDate, out var td))
            {
                sql += $" AND invoice_date <= ${paramIndex}";
                parameters.Add(td);
                paramIndex++;
            }

            sql += " ORDER BY invoice_date DESC, created_at DESC LIMIT 200) t";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 获取请求书详情
        app.MapGet("/sales-invoices/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT id, invoice_no, customer_code, customer_name, 
                           invoice_date, due_date, amount_total, tax_amount, status,
                           payload,
                           created_at, updated_at
                    FROM sales_invoices
                    WHERE id = $1 AND company_code = $2
                ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { id, cc.ToString() });
            if (rows.Count == 0) return Results.NotFound();
            return Results.Text(rows[0], "application/json");
        }).RequireAuthorization();

        // 从纳品书创建请求书
        app.MapPost("/sales-invoices/from-delivery-notes", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            // 获取要包含的纳品书ID列表
            if (!root.TryGetProperty("deliveryNoteIds", out var dnIdsEl) || dnIdsEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "deliveryNoteIds array required" });

            var deliveryNoteIds = new List<Guid>();
            foreach (var idEl in dnIdsEl.EnumerateArray())
            {
                if (idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var dnId))
                    deliveryNoteIds.Add(dnId);
            }

            if (deliveryNoteIds.Count == 0)
                return Results.BadRequest(new { error = "No valid delivery note IDs provided" });

            var invoiceDate = root.TryGetProperty("invoiceDate", out var invDateEl) 
                ? invDateEl.GetString() 
                : DateTime.Today.ToString("yyyy-MM-dd");
            
            var dueDateStr = root.TryGetProperty("dueDate", out var dueDateEl) ? dueDateEl.GetString() : null;
            var note = root.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 检查所有纳品书是否已出库
                string? customerCode = null, customerName = null;
                var lines = new List<object>();
                int lineNo = 0;
                decimal totalAmount = 0m;
                decimal taxAmount = 0m;

                foreach (var dnId in deliveryNoteIds)
                {
                    await using var checkCmd = new NpgsqlCommand(@"
                        SELECT status, customer_code, customer_name, delivery_no, payload
                        FROM delivery_notes 
                        WHERE id = $1 AND company_code = $2", conn, tx);
                    checkCmd.Parameters.AddWithValue(dnId);
                    checkCmd.Parameters.AddWithValue(cc.ToString()!);

                    await using var reader = await checkCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                        return Results.BadRequest(new { error = $"Delivery note {dnId} not found" });

                    var status = reader.GetString(0);
                    if (status != "shipped" && status != "delivered")
                        return Results.BadRequest(new { error = $"Delivery note {dnId} has not been shipped yet. Status: {status}" });

                    var dnCustomerCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var dnCustomerName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var deliveryNo = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var payloadStr = reader.IsDBNull(4) ? "{}" : reader.GetString(4);

                    // 验证客户一致性
                    if (customerCode == null)
                    {
                        customerCode = dnCustomerCode;
                        customerName = dnCustomerName;
                    }
                    else if (customerCode != dnCustomerCode)
                    {
                        return Results.BadRequest(new { error = "All delivery notes must be for the same customer" });
                    }

                    await reader.CloseAsync();

                    // 解析纳品书明细
                    using var payloadDoc = JsonDocument.Parse(payloadStr);
                    var payload = payloadDoc.RootElement;

                    if (payload.TryGetProperty("lines", out var dnLines) && dnLines.ValueKind == JsonValueKind.Array)
                    {
                        // 获取关联的销售订单号
                        var soNo = payload.TryGetProperty("header", out var header) 
                            && header.TryGetProperty("salesOrderNo", out var soNoEl) 
                            ? soNoEl.GetString() : null;

                        foreach (var line in dnLines.EnumerateArray())
                        {
                            lineNo++;
                            var materialCode = line.TryGetProperty("materialCode", out var mc) ? mc.GetString() : "";
                            var materialName = line.TryGetProperty("materialName", out var mn) ? mn.GetString() : "";
                            var qty = line.TryGetProperty("deliveryQty", out var dq) && dq.TryGetDecimal(out var dqv) ? dqv : 0m;
                            var uom = line.TryGetProperty("uom", out var u) ? u.GetString() : "";
                            var unitPrice = line.TryGetProperty("unitPrice", out var up) && up.TryGetDecimal(out var upv) ? upv : 0m;
                            var taxRate = line.TryGetProperty("taxRate", out var tr) && tr.TryGetDecimal(out var trv) ? trv : 10m;

                            var amount = qty * unitPrice;
                            var lineTax = Math.Round(amount * taxRate / 100m, 0);
                            var amountWithTax = amount + lineTax;

                            totalAmount += amountWithTax;
                            taxAmount += lineTax;

                            lines.Add(new
                            {
                                lineNo,
                                soNo,
                                deliveryNo,
                                materialCode,
                                materialName,
                                quantity = qty,
                                uom,
                                unitPrice,
                                amount,
                                taxRate,
                                taxAmount = lineTax,
                                amountWithTax
                            });
                        }
                    }
                }

                if (string.IsNullOrEmpty(customerCode))
                    return Results.BadRequest(new { error = "Customer code not found in delivery notes" });

                // 计算支付期限（如果未指定，从客户获取默认值）
                if (string.IsNullOrEmpty(dueDateStr))
                {
                    var paymentTermDays = 30; // 默认30天
                    await using var partnerCmd = new NpgsqlCommand(@"
                        SELECT COALESCE((payload->>'paymentTermDays')::int, 30)
                        FROM businesspartners
                        WHERE company_code = $1 AND (payload->>'code' = $2 OR partner_code = $2)
                        LIMIT 1", conn, tx);
                    partnerCmd.Parameters.AddWithValue(cc.ToString()!);
                    partnerCmd.Parameters.AddWithValue(customerCode);
                    var termResult = await partnerCmd.ExecuteScalarAsync();
                    if (termResult is int term) paymentTermDays = term;

                    var invDate = DateOnly.Parse(invoiceDate!);
                    dueDateStr = invDate.AddDays(paymentTermDays).ToString("yyyy-MM-dd");
                }

                // 生成请求书编号
                var invoiceNo = await GenerateInvoiceNoAsync(conn, cc.ToString()!, tx);

                // 获取 AccountSelectionService
                var acctService = accountService ?? req.HttpContext.RequestServices.GetService<AccountSelectionService>();

                // 生成会计凭证
                string? voucherNo = null;
                Guid? voucherId = null;
                string? voucherError = null;

                if (totalAmount > 0 && !string.IsNullOrEmpty(customerCode) && acctService != null)
                {
                    try
                    {
                        var invoiceDateOnly = DateOnly.Parse(invoiceDate!);
                        var result = await acctService.CreateSalesVoucherAsync(
                            conn, tx, cc.ToString()!,
                            customerCode,
                            customerName ?? customerCode,
                            invoiceNo,
                            invoiceDateOnly,
                            totalAmount,
                            taxAmount,
                            currentUser
                        );
                        if (result.HasValue)
                        {
                            voucherId = result.Value.VoucherId;
                            voucherNo = result.Value.VoucherNo;
                            Console.WriteLine($"[SalesInvoice.Create] Voucher created: {voucherNo}");
                        }
                    }
                    catch (Exception voucherEx)
                    {
                        voucherError = voucherEx.Message;
                        Console.WriteLine($"[Warning] Failed to create sales voucher for {invoiceNo}: {voucherEx.Message}");
                    }
                }

                // 构建请求书 payload - 创建即发行
                var invoicePayload = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["invoiceNo"] = invoiceNo,
                        ["customerCode"] = customerCode,
                        ["customerName"] = customerName ?? customerCode,
                        ["invoiceDate"] = invoiceDate,
                        ["dueDate"] = dueDateStr,
                        ["amountTotal"] = totalAmount,
                        ["taxAmount"] = taxAmount,
                        ["currency"] = "JPY",
                        ["status"] = "issued",  // 创建即发行
                        ["createdBy"] = currentUser,
                        ["createdAt"] = DateTime.UtcNow.ToString("o"),
                        ["issuedAt"] = DateTime.UtcNow.ToString("o"),
                        ["issuedBy"] = currentUser,
                        ["deliveryNoteIds"] = JsonNode.Parse(JsonSerializer.Serialize(deliveryNoteIds))
                    },
                    ["lines"] = JsonNode.Parse(JsonSerializer.Serialize(lines)) ?? new JsonArray()
                };

                // 添加凭证信息
                var headerObj = (JsonObject)invoicePayload["header"]!;
                if (!string.IsNullOrEmpty(voucherNo))
                {
                    headerObj["voucherNo"] = voucherNo;
                    headerObj["voucherId"] = voucherId?.ToString();
                }
                if (!string.IsNullOrEmpty(voucherError))
                {
                    headerObj["voucherError"] = voucherError;
                }

                if (!string.IsNullOrEmpty(note))
                {
                    headerObj["note"] = note;
                }

                // 插入请求书
                var invoiceId = Guid.NewGuid();
                await using var insertCmd = new NpgsqlCommand(@"
                    INSERT INTO sales_invoices (id, company_code, payload)
                    VALUES ($1, $2, $3::jsonb)", conn, tx);
                insertCmd.Parameters.AddWithValue(invoiceId);
                insertCmd.Parameters.AddWithValue(cc.ToString()!);
                insertCmd.Parameters.AddWithValue(invoicePayload.ToJsonString());
                await insertCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();
                
                // 如果创建了凭证，刷新总账物化视图
                if (!string.IsNullOrEmpty(voucherNo))
                {
                    await FinanceService.RefreshGlViewAsync(conn);
                }

                // 生成请求书 PDF 并上传到 Azure Storage
                string? pdfBlobName = null;
                string? pdfUrl = null;
                string? pdfError = null;
                try
                {
                    var pdfService = req.HttpContext.RequestServices.GetService<SalesPdfService>();
                    if (pdfService != null)
                    {
                        var companyInfo = await SalesPdfService.GetCompanyInfoAsync(ds, cc.ToString()!);
                        using var payloadDoc2 = JsonDocument.Parse(invoicePayload.ToJsonString());
                        var invoiceData = SalesPdfService.ExtractInvoiceData(payloadDoc2.RootElement, invoiceNo);
                        pdfBlobName = await pdfService.GenerateInvoicePdfAsync(cc.ToString()!, companyInfo, invoiceData, CancellationToken.None);
                        pdfUrl = pdfService.GetReadUri(pdfBlobName);
                        Console.WriteLine($"[SalesInvoice.Pdf] Generated: {pdfBlobName}");
                    }
                }
                catch (Exception pdfEx)
                {
                    pdfError = pdfEx.Message;
                    Console.WriteLine($"[Warning] Failed to generate invoice PDF for {invoiceNo}: {pdfEx.Message}");
                }
                
                return Results.Ok(new { 
                    id = invoiceId, 
                    invoiceNo, 
                    amountTotal = totalAmount, 
                    taxAmount,
                    lineCount = lineNo,
                    voucherNo,
                    voucherError,
                    pdfBlobName,
                    pdfUrl,
                    pdfError
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to create invoice: {ex.Message}");
            }
        }).RequireAuthorization();

        // 重试生成会计凭证（当创建时凭证生成失败时使用）
        app.MapPost("/sales-invoices/{id:guid}/retry-voucher", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";
            var acctService = accountService ?? req.HttpContext.RequestServices.GetService<AccountSelectionService>();

            if (acctService == null)
                return Results.Problem("Account service not available");

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取请求书信息
                string? invoiceNo = null, customerCode = null, customerName = null, existingVoucherNo = null;
                decimal amountTotal = 0m, taxAmount = 0m;
                DateOnly invoiceDate = DateOnly.FromDateTime(DateTime.Today);

                await using (var getCmd = new NpgsqlCommand(@"
                    SELECT invoice_no, customer_code, customer_name, invoice_date, amount_total, tax_amount, 
                           payload->'header'->>'voucherNo'
                    FROM sales_invoices 
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    getCmd.Parameters.AddWithValue(id);
                    getCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await getCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound();
                    }

                    invoiceNo = reader.IsDBNull(0) ? null : reader.GetString(0);
                    customerCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                    customerName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    invoiceDate = reader.IsDBNull(3) ? DateOnly.FromDateTime(DateTime.Today) : reader.GetFieldValue<DateOnly>(3);
                    amountTotal = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
                    taxAmount = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5);
                    existingVoucherNo = reader.IsDBNull(6) ? null : reader.GetString(6);
                }

                // 如果已有凭证，不重复创建
                if (!string.IsNullOrEmpty(existingVoucherNo))
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "Voucher already exists", voucherNo = existingVoucherNo });
                }

                // 生成应收账款会计凭证
                string? voucherNo = null;
                Guid? voucherId = null;
                string? voucherError = null;

                try
                {
                    var result = await acctService.CreateSalesVoucherAsync(
                        conn, tx, cc.ToString()!,
                        customerCode!,
                        customerName ?? customerCode!,
                        invoiceNo!,
                        invoiceDate,
                        amountTotal,
                        taxAmount,
                        currentUser
                    );
                    if (result.HasValue)
                    {
                        voucherId = result.Value.VoucherId;
                        voucherNo = result.Value.VoucherNo;
                        Console.WriteLine($"[SalesInvoice.RetryVoucher] Voucher created: {voucherNo}");
                    }
                }
                catch (Exception voucherEx)
                {
                    voucherError = voucherEx.Message;
                    Console.WriteLine($"[SalesInvoice.RetryVoucher] Failed: {voucherEx.Message}");
                    await tx.RollbackAsync();
                    return Results.Problem($"Failed to create voucher: {voucherEx.Message}");
                }

                // 更新请求书（添加凭证信息，清除错误）
                var updatePayloadJson = new JsonObject
                {
                    ["voucherNo"] = voucherNo,
                    ["voucherId"] = voucherId?.ToString(),
                    ["voucherError"] = (JsonNode?)null  // 清除错误
                };

                await using (var updateCmd = new NpgsqlCommand(@"
                    UPDATE sales_invoices 
                    SET payload = jsonb_set(
                        payload - 'header' || jsonb_build_object('header', 
                            (payload->'header') - 'voucherError' || $3::jsonb
                        ),
                        '{header}',
                        (payload->'header') - 'voucherError' || $3::jsonb
                    ),
                    updated_at = now()
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue(id);
                    updateCmd.Parameters.AddWithValue(cc.ToString()!);
                    updateCmd.Parameters.AddWithValue(updatePayloadJson.ToJsonString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                
                // 刷新总账物化视图
                if (voucherId.HasValue)
                {
                    await FinanceService.RefreshGlViewAsync(conn);
                }
                
                return Results.Ok(new { success = true, voucherNo, voucherId });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Console.WriteLine($"[SalesInvoice.RetryVoucher] Error: {ex}");
                return Results.Problem($"Failed to retry voucher: {ex.Message}");
            }
        }).RequireAuthorization();

        // 发行并记账（用于旧的 draft 状态请求书）
        app.MapPost("/sales-invoices/{id:guid}/issue-and-post", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";
            var acctService = accountService ?? req.HttpContext.RequestServices.GetService<AccountSelectionService>();

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 获取请求书信息
                string? invoiceNo = null, customerCode = null, customerName = null, status = null;
                decimal amountTotal = 0m, taxAmount = 0m;
                DateOnly invoiceDate = DateOnly.FromDateTime(DateTime.Today);

                await using (var getCmd = new NpgsqlCommand(@"
                    SELECT invoice_no, customer_code, customer_name, invoice_date, amount_total, tax_amount, status
                    FROM sales_invoices 
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    getCmd.Parameters.AddWithValue(id);
                    getCmd.Parameters.AddWithValue(cc.ToString()!);
                    await using var reader = await getCmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound();
                    }

                    invoiceNo = reader.IsDBNull(0) ? null : reader.GetString(0);
                    customerCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                    customerName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    invoiceDate = reader.IsDBNull(3) ? DateOnly.FromDateTime(DateTime.Today) : reader.GetFieldValue<DateOnly>(3);
                    amountTotal = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
                    taxAmount = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5);
                    status = reader.IsDBNull(6) ? null : reader.GetString(6);
                }

                if (status != "draft")
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "Only draft invoices can be issued" });
                }

                // 生成会计凭证
                string? voucherNo = null;
                Guid? voucherId = null;
                string? voucherError = null;

                if (amountTotal > 0 && !string.IsNullOrEmpty(customerCode) && acctService != null)
                {
                    try
                    {
                        var result = await acctService.CreateSalesVoucherAsync(
                            conn, tx, cc.ToString()!,
                            customerCode,
                            customerName ?? customerCode,
                            invoiceNo!,
                            invoiceDate,
                            amountTotal,
                            taxAmount,
                            currentUser
                        );
                        if (result.HasValue)
                        {
                            voucherId = result.Value.VoucherId;
                            voucherNo = result.Value.VoucherNo;
                            Console.WriteLine($"[SalesInvoice.IssueAndPost] Voucher created: {voucherNo}");
                        }
                    }
                    catch (Exception voucherEx)
                    {
                        voucherError = voucherEx.Message;
                        Console.WriteLine($"[SalesInvoice.IssueAndPost] Voucher failed: {voucherEx.Message}");
                    }
                }

                // 更新请求书状态
                var updatePayloadJson = new JsonObject
                {
                    ["status"] = "issued",
                    ["issuedAt"] = DateTime.UtcNow.ToString("o"),
                    ["issuedBy"] = currentUser
                };
                if (!string.IsNullOrEmpty(voucherNo))
                {
                    updatePayloadJson["voucherNo"] = voucherNo;
                    updatePayloadJson["voucherId"] = voucherId?.ToString();
                }
                if (!string.IsNullOrEmpty(voucherError))
                {
                    updatePayloadJson["voucherError"] = voucherError;
                }

                await using (var updateCmd = new NpgsqlCommand(@"
                    UPDATE sales_invoices 
                    SET payload = payload || jsonb_build_object('header', payload->'header' || $3::jsonb),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    updateCmd.Parameters.AddWithValue(id);
                    updateCmd.Parameters.AddWithValue(cc.ToString()!);
                    updateCmd.Parameters.AddWithValue(updatePayloadJson.ToJsonString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                
                // 如果创建了凭证，刷新总账物化视图
                if (voucherId.HasValue)
                {
                    await FinanceService.RefreshGlViewAsync(conn);
                }
                
                return Results.Ok(new { success = true, voucherNo, voucherId, voucherError });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Console.WriteLine($"[SalesInvoice.IssueAndPost] Error: {ex}");
                return Results.Problem($"Failed to issue invoice: {ex.Message}");
            }
        }).RequireAuthorization();

        // 取消请求书
        app.MapPost("/sales-invoices/{id:guid}/cancel", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var reason = root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null;

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 检查是否有关联的入金清账
                await using (var checkCmd = new NpgsqlCommand(@"
                    SELECT invoice_no FROM sales_invoices WHERE id = $1 AND company_code = $2", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue(id);
                    checkCmd.Parameters.AddWithValue(cc.ToString()!);
                    var invoiceNo = (string?)await checkCmd.ExecuteScalarAsync();
                    
                    if (invoiceNo == null)
                        return Results.NotFound(new { error = "Invoice not found" });

                    // 检查 open_items 中是否有该请求书的已清账记录
                    // 通过检查 refs 字段中是否包含该请求书号
                    await using var clearCheckCmd = new NpgsqlCommand(@"
                        SELECT COUNT(*) FROM open_items 
                        WHERE company_code = $1 
                          AND refs::text LIKE '%' || $2 || '%'
                          AND residual_amount < original_amount", conn, tx);
                    clearCheckCmd.Parameters.AddWithValue(cc.ToString()!);
                    clearCheckCmd.Parameters.AddWithValue(invoiceNo);
                    var clearedCount = (long)(await clearCheckCmd.ExecuteScalarAsync() ?? 0L);

                    if (clearedCount > 0)
                        return Results.BadRequest(new { error = "Cannot cancel - invoice has associated payment clearings" });
                }

                // 执行取消
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE sales_invoices 
                    SET payload = jsonb_set(
                        jsonb_set(
                            jsonb_set(
                                jsonb_set(payload, '{header,status}', '""cancelled""'),
                                '{header,cancelledAt}', to_jsonb(now()::text)
                            ),
                            '{header,cancelledBy}', to_jsonb($3::text)
                        ),
                        '{header,cancelReason}', to_jsonb($4::text)
                    ),
                    updated_at = now()
                    WHERE id = $1 AND company_code = $2 AND status IN ('draft', 'issued')", conn, tx);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(cc.ToString()!);
                cmd.Parameters.AddWithValue(currentUser);
                cmd.Parameters.AddWithValue(reason ?? "");

                var affected = await cmd.ExecuteNonQueryAsync();
                if (affected == 0)
                {
                    await tx.RollbackAsync();
                    return Results.BadRequest(new { error = "Cannot cancel - invoice already paid or cancelled" });
                }

                await tx.CommitAsync();
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem($"Failed to cancel invoice: {ex.Message}");
            }
        }).RequireAuthorization();

        // 标记请求书为已付款
        app.MapPost("/sales-invoices/{id:guid}/mark-paid", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_invoices 
                SET payload = jsonb_set(
                    jsonb_set(
                        jsonb_set(payload, '{header,status}', '""paid""'),
                        '{header,paidAt}', to_jsonb(now()::text)
                    ),
                    '{header,paidBy}', to_jsonb($3::text)
                ),
                updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'issued'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(currentUser);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0)
                return Results.BadRequest(new { error = "Cannot mark as paid - invoice not in issued status" });

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 获取客户的未清请求书（用于入金匹配）
        app.MapGet("/sales-invoices/outstanding/{customerCode}", async (string customerCode, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT to_jsonb(t) FROM (
                    SELECT id, invoice_no, customer_code, customer_name,
                           invoice_date, due_date, amount_total, tax_amount, status,
                           CASE WHEN due_date < CURRENT_DATE THEN true ELSE false END as overdue,
                           CURRENT_DATE - due_date as overdue_days
                    FROM sales_invoices
                    WHERE company_code = $1 
                      AND customer_code = $2
                      AND status = 'issued'
                    ORDER BY due_date ASC, invoice_date ASC
                ) t";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { cc.ToString(), customerCode });
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // GET /sales-invoices/batch-preview?year=2026&month=2
        // 月次一括請求書作成のプレビュー情報を返す
        app.MapGet("/sales-invoices/batch-preview", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!int.TryParse(req.Query["year"].FirstOrDefault(), out var year) ||
                !int.TryParse(req.Query["month"].FirstOrDefault(), out var month))
                return Results.BadRequest(new { error = "year and month are required" });

            await using var conn = await ds.OpenConnectionAsync();

            // 既存請求書件数（当月）
            await using var invCountCmd = conn.CreateCommand();
            invCountCmd.CommandText = @"
                SELECT COUNT(*) FROM sales_invoices
                WHERE company_code=$1
                  AND EXTRACT(YEAR FROM invoice_date)=$2
                  AND EXTRACT(MONTH FROM invoice_date)=$3
                  AND status != 'cancelled'";
            invCountCmd.Parameters.AddWithValue(cc.ToString()!);
            invCountCmd.Parameters.AddWithValue(year);
            invCountCmd.Parameters.AddWithValue(month);
            var existingCount = (long)(await invCountCmd.ExecuteScalarAsync() ?? 0L);

            // 顧客別の納品書グループ（出荷済み）
            await using var grpCmd = conn.CreateCommand();
            grpCmd.CommandText = @"
                SELECT
                    dn.customer_code,
                    MAX(dn.customer_name) AS customer_name,
                    COUNT(*) AS total_dns,
                    COUNT(*) FILTER (WHERE NOT EXISTS (
                        SELECT 1 FROM sales_invoices si
                        WHERE si.company_code = dn.company_code
                          AND si.status != 'cancelled'
                          AND si.payload->'deliveryNoteIds' ? dn.id::text
                    )) AS uninvoiced_dns
                FROM delivery_notes dn
                WHERE dn.company_code=$1
                  AND dn.status IN ('shipped','delivered')
                  AND EXTRACT(YEAR FROM dn.delivery_date)=$2
                  AND EXTRACT(MONTH FROM dn.delivery_date)=$3
                GROUP BY dn.customer_code
                ORDER BY dn.customer_code";
            grpCmd.Parameters.AddWithValue(cc.ToString()!);
            grpCmd.Parameters.AddWithValue(year);
            grpCmd.Parameters.AddWithValue(month);

            var groups = new List<object>();
            await using var rd = await grpCmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                groups.Add(new
                {
                    customerCode = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    customerName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    totalDns = rd.IsDBNull(2) ? 0 : (int)rd.GetInt64(2),
                    uninvoicedDns = rd.IsDBNull(3) ? 0 : (int)rd.GetInt64(3)
                });
            }

            return Results.Ok(new
            {
                year,
                month,
                existingInvoiceCount = (int)existingCount,
                customerGroups = groups
            });
        }).RequireAuthorization();

        // POST /sales-invoices/batch-create
        // 月次一括請求書作成
        // Body: { year, month, mode: "missing_only"|"all", invoiceDate? }
        app.MapPost("/sales-invoices/batch-create", async (HttpRequest req, NpgsqlDataSource ds, AccountSelectionService? acctService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var bodyDoc = await JsonDocument.ParseAsync(req.Body);
            var body = bodyDoc.RootElement;
            if (!body.TryGetProperty("year", out var yearEl) || !body.TryGetProperty("month", out var monthEl))
                return Results.BadRequest(new { error = "year and month are required" });

            var year = yearEl.GetInt32();
            var month = monthEl.GetInt32();
            var mode = body.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "missing_only" : "missing_only";
            var invoiceDate = body.TryGetProperty("invoiceDate", out var invDateEl) && invDateEl.ValueKind == JsonValueKind.String
                ? invDateEl.GetString() : DateTime.Today.ToString("yyyy-MM-dd");
            var currentUser = req.HttpContext.User?.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();

            // 顧客別 uninvoiced 納品書を取得
            await using var grpCmd = conn.CreateCommand();
            var whereInvoiced = mode == "all"
                ? ""
                : @" AND NOT EXISTS (
                        SELECT 1 FROM sales_invoices si
                        WHERE si.company_code = dn.company_code
                          AND si.status != 'cancelled'
                          AND si.payload->'deliveryNoteIds' ? dn.id::text
                    )";
            grpCmd.CommandText = $@"
                SELECT dn.customer_code, MAX(dn.customer_name), ARRAY_AGG(dn.id::text)
                FROM delivery_notes dn
                WHERE dn.company_code=$1
                  AND dn.status IN ('shipped','delivered')
                  AND EXTRACT(YEAR FROM dn.delivery_date)=$2
                  AND EXTRACT(MONTH FROM dn.delivery_date)=$3
                  {whereInvoiced}
                GROUP BY dn.customer_code
                ORDER BY dn.customer_code";
            grpCmd.Parameters.AddWithValue(cc.ToString()!);
            grpCmd.Parameters.AddWithValue(year);
            grpCmd.Parameters.AddWithValue(month);

            var customerGroups = new List<(string Code, string Name, List<Guid> DnIds)>();
            await using var grpRd = await grpCmd.ExecuteReaderAsync();
            while (await grpRd.ReadAsync())
            {
                var code = grpRd.IsDBNull(0) ? "" : grpRd.GetString(0);
                var name = grpRd.IsDBNull(1) ? code : grpRd.GetString(1);
                var idsArr = grpRd.IsDBNull(2) ? Array.Empty<string>() : (string[])grpRd.GetValue(2);
                var ids = idsArr.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
                customerGroups.Add((code, name, ids));
            }
            await grpRd.CloseAsync();

            if (customerGroups.Count == 0)
                return Results.Ok(new { results = Array.Empty<object>(), message = "対象の納品書がありません" });

            var results = new List<object>();
            var batchAcctService = acctService ?? req.HttpContext.RequestServices.GetService<AccountSelectionService>();

            foreach (var (customerCode, customerName, dnIds) in customerGroups)
            {
                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    decimal totalAmount = 0m, taxAmount = 0m;
                    var lines = new List<object>();
                    var lineNo = 0;
                    string? dueDateStr = null;

                    foreach (var dnId in dnIds)
                    {
                        await using var dnCmd = new NpgsqlCommand(@"
                            SELECT dn.delivery_no, dn.payload
                            FROM delivery_notes dn
                            WHERE dn.id=$1 AND dn.company_code=$2", conn, tx);
                        dnCmd.Parameters.AddWithValue(dnId);
                        dnCmd.Parameters.AddWithValue(cc.ToString()!);
                        await using var dnRd = await dnCmd.ExecuteReaderAsync();
                        if (!await dnRd.ReadAsync()) { await dnRd.CloseAsync(); continue; }

                        var deliveryNo = dnRd.IsDBNull(0) ? null : dnRd.GetString(0);
                        var payloadStr = dnRd.IsDBNull(1) ? "{}" : dnRd.GetString(1);
                        await dnRd.CloseAsync();

                        using var payDoc = JsonDocument.Parse(payloadStr);
                        var payload = payDoc.RootElement;
                        var soNo = payload.TryGetProperty("header", out var hdr) && hdr.TryGetProperty("salesOrderNo", out var snEl) ? snEl.GetString() : null;

                        if (payload.TryGetProperty("lines", out var dnLines) && dnLines.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in dnLines.EnumerateArray())
                            {
                                lineNo++;
                                var mc = line.TryGetProperty("materialCode", out var mcEl) ? mcEl.GetString() : "";
                                var mn = line.TryGetProperty("materialName", out var mnEl) ? mnEl.GetString() : "";
                                var qty = line.TryGetProperty("deliveryQty", out var qEl) && qEl.TryGetDecimal(out var qv) ? qv : 0m;
                                var uom = line.TryGetProperty("uom", out var uomEl) ? uomEl.GetString() : "";
                                var price = line.TryGetProperty("unitPrice", out var prEl) && prEl.TryGetDecimal(out var prv) ? prv : 0m;
                                var taxRate = line.TryGetProperty("taxRate", out var trEl) && trEl.TryGetDecimal(out var trv) ? trv : 10m;
                                var amt = qty * price;
                                var lineTax = Math.Round(amt * taxRate / 100m, 0);
                                totalAmount += amt + lineTax;
                                taxAmount += lineTax;
                                lines.Add(new { lineNo, soNo, deliveryNo, materialCode = mc, materialName = mn, quantity = qty, uom, unitPrice = price, amount = amt, taxRate, taxAmount = lineTax, amountWithTax = amt + lineTax });
                            }
                        }
                    }

                    // 支払期限
                    await using var ptCmd = new NpgsqlCommand(@"
                        SELECT COALESCE((payload->>'paymentTermDays')::int, 30)
                        FROM businesspartners WHERE company_code=$1 AND (payload->>'code'=$2 OR partner_code=$2) LIMIT 1", conn, tx);
                    ptCmd.Parameters.AddWithValue(cc.ToString()!);
                    ptCmd.Parameters.AddWithValue(customerCode);
                    var ptRes = await ptCmd.ExecuteScalarAsync();
                    var termDays = ptRes is int td ? td : 30;
                    var invDateOnly = DateOnly.Parse(invoiceDate!);
                    dueDateStr = invDateOnly.AddDays(termDays).ToString("yyyy-MM-dd");

                    var invoiceNo = await GenerateInvoiceNoAsync(conn, cc.ToString()!, tx);

                    string? voucherNo = null, voucherError = null;
                    Guid? voucherId = null;
                    if (totalAmount > 0 && batchAcctService != null)
                    {
                        try
                        {
                            var vr = await batchAcctService.CreateSalesVoucherAsync(conn, tx, cc.ToString()!, customerCode, customerName, invoiceNo, invDateOnly, totalAmount, taxAmount, currentUser);
                            if (vr.HasValue) { voucherId = vr.Value.VoucherId; voucherNo = vr.Value.VoucherNo; }
                        }
                        catch (Exception vex) { voucherError = vex.Message; }
                    }

                    var dnIdsJsonArr = new JsonArray();
                    foreach (var id in dnIds) dnIdsJsonArr.Add(id.ToString());

                    var invoicePayload = new JsonObject
                    {
                        ["header"] = new JsonObject
                        {
                            ["invoiceNo"] = invoiceNo,
                            ["customerCode"] = customerCode,
                            ["customerName"] = customerName,
                            ["invoiceDate"] = invoiceDate,
                            ["dueDate"] = dueDateStr,
                            ["amountTotal"] = totalAmount,
                            ["taxAmount"] = taxAmount,
                            ["voucherNo"] = voucherNo,
                            ["voucherId"] = voucherId?.ToString(),
                            ["voucherError"] = voucherError,
                            ["createdBy"] = currentUser
                        },
                        ["lines"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(lines)),
                        ["deliveryNoteIds"] = dnIdsJsonArr
                    };

                    var batchInvoiceId = Guid.NewGuid();
                    await using var insCmd = new NpgsqlCommand(@"
                        INSERT INTO sales_invoices (id, company_code, payload)
                        VALUES ($1, $2, $3::jsonb)", conn, tx);
                    insCmd.Parameters.AddWithValue(batchInvoiceId);
                    insCmd.Parameters.AddWithValue(cc.ToString()!);
                    insCmd.Parameters.AddWithValue(invoicePayload.ToJsonString());
                    await insCmd.ExecuteNonQueryAsync();
                    await tx.CommitAsync();

                    // PDF 生成
                    string? pdfBlobName = null, pdfUrl = null;
                    try
                    {
                        var pdfService = req.HttpContext.RequestServices.GetService<SalesPdfService>();
                        if (pdfService != null)
                        {
                            var companyInfo = await SalesPdfService.GetCompanyInfoAsync(ds, cc.ToString()!);
                            using var payloadDoc = JsonDocument.Parse(invoicePayload.ToJsonString());
                            var invData = SalesPdfService.ExtractInvoiceData(payloadDoc.RootElement, invoiceNo);
                            pdfBlobName = await pdfService.GenerateInvoicePdfAsync(cc.ToString()!, companyInfo, invData, CancellationToken.None);
                            pdfUrl = pdfService.GetReadUri(pdfBlobName);
                        }
                    }
                    catch (Exception pdfEx) { Console.WriteLine($"[batch-create] PDF error for {invoiceNo}: {pdfEx.Message}"); }

                    results.Add(new { customerCode, customerName, invoiceNo, success = true, dnCount = dnIds.Count, voucherNo, voucherError, pdfBlobName, pdfUrl });
                }
                catch (Exception ex)
                {
                    try { await tx.RollbackAsync(); } catch { }
                    results.Add(new { customerCode, customerName, invoiceNo = (string?)null, success = false, dnCount = dnIds.Count, error = ex.Message });
                }
            }

            return Results.Ok(new { results });
        }).RequireAuthorization();
    }

    private static async Task<string> GenerateInvoiceNoAsync(NpgsqlConnection conn, string companyCode, NpgsqlTransaction tx)
    {
        var now = DateTime.Now;
        
        await using var upsertCmd = new NpgsqlCommand(@"
            INSERT INTO sales_invoice_sequences (company_code, prefix, year, month, last_number)
            VALUES ($1, 'INV', $2, $3, 1)
            ON CONFLICT (company_code, prefix, year, month)
            DO UPDATE SET last_number = sales_invoice_sequences.last_number + 1
            RETURNING last_number", conn, tx);

        upsertCmd.Parameters.AddWithValue(companyCode);
        upsertCmd.Parameters.AddWithValue(now.Year);
        upsertCmd.Parameters.AddWithValue(now.Month);

        var lastNumber = await upsertCmd.ExecuteScalarAsync();
        if (lastNumber != null)
            return $"INV{now.Year:D4}{now.Month:D2}-{(int)lastNumber:D4}";

        // 回退方案
        var stamp = now.ToString("yyyyMMddHHmmssfff");
        return $"INV-{stamp}";
    }
}


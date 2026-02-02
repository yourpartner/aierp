using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Server.Domain;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Staffing;

/// <summary>
/// 請求管理モジュール - 派遣/業務委託の請求書生成・管理
/// </summary>
public class StaffingBillingModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_billing",
        Name = "請求管理",
        Description = "契約・勤怠に基づく請求書の自動生成・管理",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_contract", "staffing_timesheet", "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_staffing_invoices", Label = "menu.staffingInvoices", Icon = "Tickets", Path = "/staffing/invoices", ParentId = "menu_staffing", Order = 258 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        // 請求書一覧取得
        app.MapGet("/staffing/invoices", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_invoice", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var query = req.Query;
            var yearMonth = query["yearMonth"].FirstOrDefault();
            var status = query["status"].FirstOrDefault();
            var clientId = query["clientId"].FirstOrDefault();

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var sql = $@"
                SELECT i.id, i.invoice_no, i.client_partner_id,
                       i.billing_year_month,
                       fn_jsonb_date(i.payload,'billing_period_start') as billing_period_start,
                       fn_jsonb_date(i.payload,'billing_period_end') as billing_period_end,
                       fn_jsonb_numeric(i.payload,'subtotal') as subtotal,
                       COALESCE(fn_jsonb_numeric(i.payload,'tax_rate'), 0.10) as tax_rate,
                       COALESCE(fn_jsonb_numeric(i.payload,'tax_amount'), 0) as tax_amount,
                       COALESCE(fn_jsonb_numeric(i.payload,'total_amount'), 0) as total_amount,
                       fn_jsonb_date(i.payload,'invoice_date') as invoice_date,
                       fn_jsonb_date(i.payload,'due_date') as due_date,
                       i.status,
                       COALESCE(i.paid_amount, 0) as paid_amount,
                       fn_jsonb_date(i.payload,'last_payment_date') as last_payment_date,
                       bp.partner_code as client_code, bp.payload->>'name' as client_name,
                       i.created_at, i.updated_at
                FROM {invTable} i
                LEFT JOIN businesspartners bp ON i.client_partner_id = bp.id
                WHERE i.company_code = $1";

            cmd.Parameters.AddWithValue(cc.ToString());
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(yearMonth))
            {
                sql += $" AND i.billing_year_month = ${idx}";
                cmd.Parameters.AddWithValue(yearMonth);
                idx++;
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                sql += $" AND i.status = ${idx}";
                cmd.Parameters.AddWithValue(status);
                idx++;
            }

            if (!string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId, out var cid))
            {
                sql += $" AND i.client_partner_id = ${idx}";
                cmd.Parameters.AddWithValue(cid);
                idx++;
            }

            sql += " ORDER BY i.billing_year_month DESC, i.invoice_no";
            cmd.CommandText = sql;

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    invoiceNo = reader.GetString(1),
                    clientPartnerId = reader.GetGuid(2),
                    billingYearMonth = reader.GetString(3),
                    billingPeriodStart = reader.GetDateTime(4),
                    billingPeriodEnd = reader.GetDateTime(5),
                    subtotal = reader.GetDecimal(6),
                    taxRate = reader.GetDecimal(7),
                    taxAmount = reader.IsDBNull(8) ? 0 : reader.GetDecimal(8),
                    totalAmount = reader.GetDecimal(9),
                    invoiceDate = reader.GetDateTime(10),
                    dueDate = reader.GetDateTime(11),
                    status = reader.GetString(12),
                    paidAmount = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                    lastPaymentDate = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14),
                    clientCode = reader.IsDBNull(15) ? null : reader.GetString(15),
                    clientName = reader.IsDBNull(16) ? null : reader.GetString(16),
                    createdAt = reader.GetDateTime(17),
                    updatedAt = reader.GetDateTime(18)
                });
            }

            return Results.Ok(new { data = results, total = results.Count });
        }).RequireAuthorization();

        // 請求書詳細取得（明細含む）
        app.MapGet("/staffing/invoices/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_invoice", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var invTable = Crud.TableFor("staffing_invoice");
            var lineTable = Crud.TableFor("staffing_invoice_line");
            var cTable = Crud.TableFor("staffing_contract");
            var rTable = Crud.TableFor("resource");

            await using var conn = await ds.OpenConnectionAsync();

            // 請求書ヘッダ
            await using var headerCmd = conn.CreateCommand();
            headerCmd.CommandText = $@"
                SELECT to_jsonb(i) || jsonb_build_object(
                    'clientCode', bp.partner_code,
                    'clientName', bp.payload->>'name'
                )
                FROM {invTable} i
                LEFT JOIN businesspartners bp ON i.client_partner_id = bp.id
                WHERE i.id = $1 AND i.company_code = $2";
            headerCmd.Parameters.AddWithValue(id);
            headerCmd.Parameters.AddWithValue(cc.ToString());

            var headerJson = await headerCmd.ExecuteScalarAsync() as string;
            if (headerJson == null) return Results.NotFound();

            // 請求明細
            await using var linesCmd = conn.CreateCommand();
            linesCmd.CommandText = $@"
                SELECT l.id, l.line_no, l.contract_id, l.resource_id,
                       l.payload->>'description' as description,
                       fn_jsonb_numeric(l.payload,'quantity') as quantity,
                       l.payload->>'unit' as unit,
                       fn_jsonb_numeric(l.payload,'unit_price') as unit_price,
                       fn_jsonb_numeric(l.payload,'overtime_hours') as overtime_hours,
                       fn_jsonb_numeric(l.payload,'overtime_amount') as overtime_amount,
                       fn_jsonb_numeric(l.payload,'adjustment_amount') as adjustment_amount,
                       l.payload->>'adjustment_description' as adjustment_description,
                       fn_jsonb_numeric(l.payload,'line_amount') as line_amount,
                       c.contract_no, r.display_name as resource_name, r.resource_code
                FROM {lineTable} l
                LEFT JOIN {cTable} c ON l.contract_id = c.id
                LEFT JOIN {rTable} r ON l.resource_id = r.id
                WHERE l.invoice_id = $1 AND l.company_code = $2
                ORDER BY l.line_no";
            linesCmd.Parameters.AddWithValue(id);
            linesCmd.Parameters.AddWithValue(cc.ToString());

            var lines = new List<object>();
            await using var linesReader = await linesCmd.ExecuteReaderAsync();
            while (await linesReader.ReadAsync())
            {
                lines.Add(new
                {
                    id = linesReader.GetGuid(0),
                    lineNo = linesReader.GetInt32(1),
                    contractId = linesReader.IsDBNull(2) ? null : (Guid?)linesReader.GetGuid(2),
                    resourceId = linesReader.IsDBNull(3) ? null : (Guid?)linesReader.GetGuid(3),
                    description = linesReader.IsDBNull(4) ? null : linesReader.GetString(4),
                    quantity = linesReader.IsDBNull(5) ? null : (decimal?)linesReader.GetDecimal(5),
                    unit = linesReader.IsDBNull(6) ? null : linesReader.GetString(6),
                    unitPrice = linesReader.IsDBNull(7) ? null : (decimal?)linesReader.GetDecimal(7),
                    overtimeHours = linesReader.IsDBNull(8) ? 0 : linesReader.GetDecimal(8),
                    overtimeAmount = linesReader.IsDBNull(9) ? 0 : linesReader.GetDecimal(9),
                    adjustmentAmount = linesReader.IsDBNull(10) ? 0 : linesReader.GetDecimal(10),
                    adjustmentDescription = linesReader.IsDBNull(11) ? null : linesReader.GetString(11),
                    lineAmount = linesReader.IsDBNull(12) ? 0 : linesReader.GetDecimal(12),
                    contractNo = linesReader.IsDBNull(13) ? null : linesReader.GetString(13),
                    resourceName = linesReader.IsDBNull(14) ? null : linesReader.GetString(14),
                    resourceCode = linesReader.IsDBNull(15) ? null : linesReader.GetString(15)
                });
            }

            // JSONを結合して返す
            using var headerDoc = JsonDocument.Parse(headerJson);
            var result = new Dictionary<string, object>();
            foreach (var prop in headerDoc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }
            result["lines"] = lines;

            return Results.Ok(result);
        }).RequireAuthorization();

        // 請求書自動生成（確定済み勤怠から）
        app.MapPost("/staffing/invoices/generate", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_invoice", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "create", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var yearMonth = root.TryGetProperty("yearMonth", out var ym) ? ym.GetString() : null;
            if (string.IsNullOrWhiteSpace(yearMonth))
                return Results.BadRequest(new { error = "yearMonth is required (YYYY-MM format)" });

            var year = int.Parse(yearMonth.Substring(0, 4));
            var month = int.Parse(yearMonth.Substring(5, 2));
            var periodStart = new DateTime(year, month, 1);
            var periodEnd = periodStart.AddMonths(1).AddDays(-1);
            var invoiceDate = DateTime.Today;
            var dueDate = invoiceDate.AddMonths(1); // デフォルト：翌月末

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            var cTable = Crud.TableFor("staffing_contract");
            var invTable = Crud.TableFor("staffing_invoice");
            var lineTable = Crud.TableFor("staffing_invoice_line");
            var rTable = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();

            // 確定済み勤怠サマリーを顧客別に集計
            var summarySql = $@"
                SELECT c.client_partner_id, 
                       array_agg(ts.id) as summary_ids,
                       COALESCE(SUM(ts.total_billing_amount),0) as total_amount
                FROM {tsTable} ts
                JOIN {cTable} c ON ts.contract_id = c.id
                WHERE ts.company_code = $1 
                  AND ts.year_month = $2 
                  AND ts.status = 'confirmed'
                  AND NOT EXISTS (
                      SELECT 1 FROM {lineTable} il 
                      WHERE il.company_code = ts.company_code AND il.timesheet_summary_id = ts.id
                  )
                GROUP BY c.client_partner_id";

            await using var summaryCmd = conn.CreateCommand();
            summaryCmd.CommandText = summarySql;
            summaryCmd.Parameters.AddWithValue(cc.ToString());
            summaryCmd.Parameters.AddWithValue(yearMonth);

            var clientSummaries = new List<(Guid clientId, Guid[] summaryIds, decimal totalAmount)>();
            await using var summaryReader = await summaryCmd.ExecuteReaderAsync();
            while (await summaryReader.ReadAsync())
            {
                clientSummaries.Add((
                    summaryReader.GetGuid(0),
                    (Guid[])summaryReader.GetValue(1),
                    summaryReader.GetDecimal(2)
                ));
            }
            await summaryReader.CloseAsync();

            var generatedInvoices = new List<object>();

            foreach (var (clientId, summaryIds, totalAmount) in clientSummaries)
            {
                // 採番
                await using var seqCmd = conn.CreateCommand();
                seqCmd.CommandText = "SELECT nextval('seq_staffing_invoice')";
                var seq = await seqCmd.ExecuteScalarAsync();
                var invoiceNo = $"SI{yearMonth.Replace("-", "")}{seq:D4}";

                // 税計算
                var taxRate = 0.10m;
                var taxAmount = Math.Round(totalAmount * taxRate);
                var grandTotal = totalAmount + taxAmount;

                // 請求書ヘッダ作成
                var invPayload = new JsonObject
                {
                    ["invoice_no"] = invoiceNo,
                    ["client_partner_id"] = clientId.ToString(),
                    ["billing_year_month"] = yearMonth,
                    ["billing_period_start"] = periodStart.ToString("yyyy-MM-dd"),
                    ["billing_period_end"] = periodEnd.ToString("yyyy-MM-dd"),
                    ["subtotal"] = totalAmount,
                    ["tax_rate"] = taxRate,
                    ["tax_amount"] = taxAmount,
                    ["total_amount"] = grandTotal,
                    ["invoice_date"] = invoiceDate.ToString("yyyy-MM-dd"),
                    ["due_date"] = dueDate.ToString("yyyy-MM-dd"),
                    ["paid_amount"] = 0,
                    ["status"] = "draft"
                };

                Guid invoiceId;
                await using (var invoiceCmd = conn.CreateCommand())
                {
                    invoiceCmd.CommandText = $"INSERT INTO {invTable}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
                    invoiceCmd.Parameters.AddWithValue(cc.ToString());
                    invoiceCmd.Parameters.AddWithValue(invPayload.ToJsonString());
                    var idObj = await invoiceCmd.ExecuteScalarAsync();
                    if (idObj is not Guid gid) return Results.Problem("Failed to create invoice");
                    invoiceId = gid;
                }

                // 明細作成（勤怠サマリーから）
                var lineNo = 0;
                foreach (var summaryId in summaryIds)
                {
                    lineNo++;

                    // 勤怠サマリー詳細取得
                    await using var tsCmd = conn.CreateCommand();
                    tsCmd.CommandText = $@"
                        SELECT ts.contract_id, ts.resource_id,
                               COALESCE(fn_jsonb_numeric(ts.payload,'settlement_hours'), ts.actual_hours, 0) as settlement_hours,
                               COALESCE(ts.total_billing_amount, 0) as total_billing_amount,
                               COALESCE(ts.overtime_hours, 0) as overtime_hours,
                               COALESCE(fn_jsonb_numeric(ts.payload,'overtime_amount'), 0) as overtime_amount,
                               COALESCE(fn_jsonb_numeric(ts.payload,'adjustment_amount'), 0) as adjustment_amount,
                               c.billing_rate,
                               COALESCE(c.payload->>'billing_rate_type','monthly') as billing_rate_type,
                               r.display_name
                        FROM {tsTable} ts
                        JOIN {cTable} c ON ts.contract_id = c.id
                        LEFT JOIN {rTable} r ON ts.resource_id = r.id
                        WHERE ts.id = $1 AND ts.company_code = $2";
                    tsCmd.Parameters.AddWithValue(summaryId);
                    tsCmd.Parameters.AddWithValue(cc.ToString());

                    await using var tsReader = await tsCmd.ExecuteReaderAsync();
                    if (await tsReader.ReadAsync())
                    {
                        var contractId = tsReader.GetGuid(0);
                        var resourceId = tsReader.IsDBNull(1) ? (Guid?)null : tsReader.GetGuid(1);
                        var settlementHours = tsReader.IsDBNull(2) ? 0 : tsReader.GetDecimal(2);
                        var lineAmount = tsReader.IsDBNull(3) ? 0 : tsReader.GetDecimal(3);
                        var overtimeHours = tsReader.IsDBNull(4) ? 0 : tsReader.GetDecimal(4);
                        var overtimeAmount = tsReader.IsDBNull(5) ? 0 : tsReader.GetDecimal(5);
                        var adjustmentAmount = tsReader.IsDBNull(6) ? 0 : tsReader.GetDecimal(6);
                        var unitPrice = tsReader.IsDBNull(7) ? 0 : tsReader.GetDecimal(7);
                        var rateType = tsReader.IsDBNull(8) ? "monthly" : tsReader.GetString(8);
                        var resourceName = tsReader.IsDBNull(9) ? "" : tsReader.GetString(9);

                        var unit = rateType switch
                        {
                            "hourly" => "時間",
                            "daily" => "日",
                            _ => "人月"
                        };
                        var quantity = rateType == "monthly" ? 1 : settlementHours;
                        var description = $"{resourceName} {yearMonth}稼働分";

                        await tsReader.CloseAsync();

                        var linePayload = new JsonObject
                        {
                            ["invoice_id"] = invoiceId.ToString(),
                            ["line_no"] = lineNo,
                            ["contract_id"] = contractId.ToString(),
                            ["resource_id"] = resourceId?.ToString(),
                            ["timesheet_summary_id"] = summaryId.ToString(),
                            ["description"] = description,
                            ["quantity"] = quantity,
                            ["unit"] = unit,
                            ["unit_price"] = unitPrice,
                            ["overtime_hours"] = overtimeHours,
                            ["overtime_amount"] = overtimeAmount,
                            ["adjustment_amount"] = adjustmentAmount,
                            ["line_amount"] = lineAmount
                        };

                        await using var lineCmd = conn.CreateCommand();
                        lineCmd.CommandText = $"INSERT INTO {lineTable}(company_code, payload) VALUES ($1, $2::jsonb)";
                        lineCmd.Parameters.AddWithValue(cc.ToString());
                        lineCmd.Parameters.AddWithValue(linePayload.ToJsonString());
                        await lineCmd.ExecuteNonQueryAsync();

                        // 勤怠サマリーのステータスを更新（payload）
                        await using var updateTsCmd = conn.CreateCommand();
                        updateTsCmd.CommandText = $@"
                            UPDATE {tsTable}
                            SET payload = payload || jsonb_build_object('status','invoiced'),
                                updated_at = now()
                            WHERE id = $1 AND company_code = $2";
                        updateTsCmd.Parameters.AddWithValue(summaryId);
                        updateTsCmd.Parameters.AddWithValue(cc.ToString());
                        await updateTsCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        await tsReader.CloseAsync();
                    }
                }

                // PDF生成・Azure Storageへアップロード（必須）
                var blobService = app.Services.GetService<AzureBlobService>();
                if (blobService?.IsConfigured != true)
                {
                    return Results.Problem("Azure Storage is not configured. PDF generation requires Azure Storage.", statusCode: 503);
                }

                var cfg = app.Configuration;
                string? pdfBlobPath;
                try
                {
                    pdfBlobPath = await GenerateAndUploadInvoicePdfAsync(ds, cfg, blobService, cc.ToString(), invoiceId, conn);
                }
                catch (Exception pdfEx)
                {
                    // PDF生成失敗時は請求書作成も失敗させる
                    Console.Error.WriteLine($"[StaffingBilling] PDF generation failed for invoice {invoiceId}: {pdfEx.Message}");
                    return Results.Problem($"PDF generation failed: {pdfEx.Message}", statusCode: 500);
                }

                generatedInvoices.Add(new { id = invoiceId, invoiceNo, clientId, totalAmount = grandTotal, lineCount = lineNo, pdfBlobPath });
            }

            return Results.Ok(new { generated = generatedInvoices.Count, invoices = generatedInvoices, yearMonth });
        }).RequireAuthorization();

        // 請求書確定
        app.MapPost("/staffing/invoices/{id:guid}/confirm", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {invTable}
                SET payload = payload || jsonb_build_object('status','confirmed','confirmed_at',$3::text),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'draft'
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("O"));

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound(new { error = "Not found or not in draft status" });
            return Results.Ok(new { confirmed = true });
        }).RequireAuthorization();

        // 請求書発行
        app.MapPost("/staffing/invoices/{id:guid}/issue", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {invTable}
                SET payload = payload || jsonb_build_object('status','issued','issued_at',$3::text),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'confirmed'
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("O"));

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound(new { error = "Not found or not confirmed" });
            return Results.Ok(new { issued = true });
        }).RequireAuthorization();

        // 入金記録
        app.MapPost("/staffing/invoices/{id:guid}/payment", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var amount = root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : 0;
            var paymentDate = root.TryGetProperty("paymentDate", out var pd) && pd.ValueKind == JsonValueKind.String
                ? DateTime.TryParse(pd.GetString(), out var d) ? d : DateTime.Today : DateTime.Today;

            if (amount <= 0)
                return Results.BadRequest(new { error = "amount must be positive" });

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();

            // 現在の金額を取得
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = $"SELECT COALESCE(total_amount,0), COALESCE(paid_amount,0) FROM {invTable} WHERE id = $1 AND company_code = $2";
            getCmd.Parameters.AddWithValue(id);
            getCmd.Parameters.AddWithValue(cc.ToString());

            decimal totalAmount = 0, paidAmount = 0;
            await using var getReader = await getCmd.ExecuteReaderAsync();
            if (await getReader.ReadAsync())
            {
                totalAmount = getReader.GetDecimal(0);
                paidAmount = getReader.IsDBNull(1) ? 0 : getReader.GetDecimal(1);
            }
            else
            {
                return Results.NotFound();
            }
            await getReader.CloseAsync();

            var newPaidAmount = paidAmount + amount;
            var newStatus = newPaidAmount >= totalAmount ? "paid" : "partial_paid";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {invTable}
                SET payload = payload || jsonb_build_object(
                    'paid_amount',$3,
                    'last_payment_date',$4::text,
                    'status',$5::text
                ),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(newPaidAmount);
            cmd.Parameters.AddWithValue(paymentDate);
            cmd.Parameters.AddWithValue(newStatus);

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound();
            return Results.Ok(new { paid = true, paidAmount = newPaidAmount, status = newStatus });
        }).RequireAuthorization();

        // 請求書更新（ドラフト時のみ）- 更新後PDFも再生成
        app.MapPut("/staffing/invoices/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();

            // Load current payload
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = $"SELECT payload FROM {invTable} WHERE id=$1 AND company_code=$2 AND status='draft'";
            getCmd.Parameters.AddWithValue(id);
            getCmd.Parameters.AddWithValue(cc.ToString());
            var payloadJson = (string?)await getCmd.ExecuteScalarAsync();
            if (string.IsNullOrWhiteSpace(payloadJson)) return Results.NotFound(new { error = "Not found or not in draft status" });

            var obj = (JsonNode.Parse(payloadJson) as JsonObject) ?? new JsonObject();
            if (root.TryGetProperty("invoiceDate", out var invDate) && invDate.ValueKind == JsonValueKind.String)
                obj["invoice_date"] = invDate.GetString();
            if (root.TryGetProperty("dueDate", out var due) && due.ValueKind == JsonValueKind.String)
                obj["due_date"] = due.GetString();
            if (root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String)
                obj["notes"] = notes.GetString();

            await using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE {invTable} SET payload=$3::jsonb, updated_at=now() WHERE id=$1 AND company_code=$2 RETURNING id";
            upd.Parameters.AddWithValue(id);
            upd.Parameters.AddWithValue(cc.ToString());
            upd.Parameters.AddWithValue(obj.ToJsonString());
            var result = await upd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound();

            // PDF再生成（Azure Storage上のPDFも更新）
            var blobService = app.Services.GetService<AzureBlobService>();
            if (blobService?.IsConfigured == true)
            {
                try
                {
                    var cfg = app.Configuration;
                    await GenerateAndUploadInvoicePdfAsync(ds, cfg, blobService, cc.ToString(), id, conn);
                }
                catch (Exception pdfEx)
                {
                    Console.Error.WriteLine($"[StaffingBilling] PDF regeneration failed for invoice {id}: {pdfEx.Message}");
                    return Results.Problem($"Invoice updated but PDF regeneration failed: {pdfEx.Message}", statusCode: 500);
                }
            }

            return Results.Ok(new { id, updated = true });
        }).RequireAuthorization();

        // 請求書キャンセル
        app.MapPost("/staffing/invoices/{id:guid}/cancel", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var invTable = Crud.TableFor("staffing_invoice");
            var lineTable = Crud.TableFor("staffing_invoice_line");
            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            await using var conn = await ds.OpenConnectionAsync();

            // 関連する勤怠サマリーのステータスを戻す
            await using var tsCmd = conn.CreateCommand();
            tsCmd.CommandText = $@"
                UPDATE {tsTable} ts
                SET payload = payload || jsonb_build_object('status','confirmed'),
                    updated_at = now()
                FROM {lineTable} il
                WHERE il.timesheet_summary_id = ts.id
                  AND il.invoice_id = $1
                  AND ts.company_code = il.company_code";
            tsCmd.Parameters.AddWithValue(id);
            await tsCmd.ExecuteNonQueryAsync();

            // 取得PDF路径（用于删除）
            string? pdfBlobPath = null;
            await using (var pdfCmd = conn.CreateCommand())
            {
                pdfCmd.CommandText = $"SELECT payload->>'pdf_blob_path' FROM {invTable} WHERE id = $1 AND company_code = $2";
                pdfCmd.Parameters.AddWithValue(id);
                pdfCmd.Parameters.AddWithValue(cc.ToString());
                pdfBlobPath = (await pdfCmd.ExecuteScalarAsync()) as string;
            }

            // 請求書をキャンセル
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {invTable}
                SET payload = payload || jsonb_build_object('status','cancelled'),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status IN ('draft', 'confirmed')
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound(new { error = "Not found or cannot be cancelled" });

            // 删除Azure Storage上的PDF
            if (!string.IsNullOrWhiteSpace(pdfBlobPath))
            {
                try
                {
                    var blobService = app.Services.GetService<AzureBlobService>();
                    if (blobService?.IsConfigured == true)
                    {
                        await blobService.DeleteAsync(pdfBlobPath, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[StaffingBilling] Failed to delete PDF for cancelled invoice {id}: {ex.Message}");
                }
            }

            return Results.Ok(new { cancelled = true });
        }).RequireAuthorization();

        // PDF下载接口
        app.MapGet("/staffing/invoices/{id:guid}/pdf", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT payload->>'pdf_blob_path', invoice_no FROM {invTable} WHERE id = $1 AND company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return Results.NotFound(new { error = "Invoice not found" });

            var pdfBlobPath = reader.IsDBNull(0) ? null : reader.GetString(0);
            var invoiceNo = reader.IsDBNull(1) ? "invoice" : reader.GetString(1);

            if (string.IsNullOrWhiteSpace(pdfBlobPath))
                return Results.NotFound(new { error = "PDF not generated yet" });

            var blobService = app.Services.GetService<AzureBlobService>();
            if (blobService?.IsConfigured != true)
                return Results.Problem("Azure Storage is not configured", statusCode: 503);

            var sasUrl = blobService.GetReadUri(pdfBlobPath);
            return Results.Ok(new { url = sasUrl, filename = $"{invoiceNo}.pdf" });
        }).RequireAuthorization();

        // 手动重新生成PDF接口
        app.MapPost("/staffing/invoices/{id:guid}/regenerate-pdf", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var invTable = Crud.TableFor("staffing_invoice");
            await using var conn = await ds.OpenConnectionAsync();

            // 验证请求书存在
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT 1 FROM {invTable} WHERE id = $1 AND company_code = $2";
            checkCmd.Parameters.AddWithValue(id);
            checkCmd.Parameters.AddWithValue(cc.ToString());
            if (await checkCmd.ExecuteScalarAsync() == null)
                return Results.NotFound(new { error = "Invoice not found" });

            try
            {
                var blobService = app.Services.GetService<AzureBlobService>();
                var cfg = app.Configuration;
                var pdfResult = await GenerateAndUploadInvoicePdfAsync(ds, cfg, blobService, cc.ToString(), id, conn);
                return Results.Ok(new { success = true, pdfBlobPath = pdfResult });
            }
            catch (Exception ex)
            {
                return Results.Problem($"PDF generation failed: {ex.Message}", statusCode: 500);
            }
        }).RequireAuthorization();
    }

    /// <summary>
    /// 生成请求书PDF并上传到Azure Storage
    /// </summary>
    private static async Task<string?> GenerateAndUploadInvoicePdfAsync(
        NpgsqlDataSource ds,
        IConfiguration cfg,
        AzureBlobService? blobService,
        string companyCode,
        Guid invoiceId,
        NpgsqlConnection conn)
    {
        // 获取公司信息和印章
        var (companyName, companyAddress, companyRep, sealDataUrl, sealSize, sealOffsetX, sealOffsetY, sealOpacity, companyZip, companyTel, companyEmail, bankInfo) 
            = await LoadCompanyBasicsForInvoiceAsync(ds, companyCode);

        // 获取请求书数据
        var invTable = Crud.TableFor("staffing_invoice");
        var lineTable = Crud.TableFor("staffing_invoice_line");
        var rTable = Crud.TableFor("resource");

        string? invoiceNo = null;
        string? clientName = null;
        string? clientAddress = null;
        string? invoiceDate = null;
        string? dueDate = null;
        decimal subtotal = 0;
        decimal taxRate = 0.10m;
        decimal taxAmount = 0;
        decimal totalAmount = 0;
        string? remarks = null;

        // 获取请求书头信息
        await using (var invCmd = conn.CreateCommand())
        {
            invCmd.CommandText = $@"
                SELECT i.invoice_no, 
                       bp.payload->>'name' as client_name,
                       bp.payload->>'address' as client_address,
                       fn_jsonb_date(i.payload,'invoice_date') as invoice_date,
                       fn_jsonb_date(i.payload,'due_date') as due_date,
                       COALESCE(fn_jsonb_numeric(i.payload,'subtotal'), 0) as subtotal,
                       COALESCE(fn_jsonb_numeric(i.payload,'tax_rate'), 0.10) as tax_rate,
                       COALESCE(fn_jsonb_numeric(i.payload,'tax_amount'), 0) as tax_amount,
                       COALESCE(fn_jsonb_numeric(i.payload,'total_amount'), 0) as total_amount,
                       i.payload->>'remarks' as remarks
                FROM {invTable} i
                LEFT JOIN businesspartners bp ON i.client_partner_id = bp.id
                WHERE i.id = $1 AND i.company_code = $2";
            invCmd.Parameters.AddWithValue(invoiceId);
            invCmd.Parameters.AddWithValue(companyCode);

            await using var invReader = await invCmd.ExecuteReaderAsync();
            if (!await invReader.ReadAsync())
                throw new InvalidOperationException("Invoice not found");

            invoiceNo = invReader.IsDBNull(0) ? "" : invReader.GetString(0);
            clientName = invReader.IsDBNull(1) ? "" : invReader.GetString(1);
            clientAddress = invReader.IsDBNull(2) ? null : invReader.GetString(2);
            invoiceDate = invReader.IsDBNull(3) ? "" : invReader.GetDateTime(3).ToString("yyyy年MM月dd日");
            dueDate = invReader.IsDBNull(4) ? "" : invReader.GetDateTime(4).ToString("yyyy年MM月dd日");
            subtotal = invReader.IsDBNull(5) ? 0 : invReader.GetDecimal(5);
            taxRate = invReader.IsDBNull(6) ? 0.10m : invReader.GetDecimal(6);
            taxAmount = invReader.IsDBNull(7) ? 0 : invReader.GetDecimal(7);
            totalAmount = invReader.IsDBNull(8) ? 0 : invReader.GetDecimal(8);
            remarks = invReader.IsDBNull(9) ? null : invReader.GetString(9);
        }

        // 获取请求书明细
        var lines = new List<object>();
        await using (var lineCmd = conn.CreateCommand())
        {
            lineCmd.CommandText = $@"
                SELECT l.payload->>'description' as description,
                       fn_jsonb_numeric(l.payload,'quantity') as quantity,
                       l.payload->>'unit' as unit,
                       fn_jsonb_numeric(l.payload,'unit_price') as unit_price,
                       fn_jsonb_numeric(l.payload,'line_amount') as line_amount,
                       r.display_name as resource_name
                FROM {lineTable} l
                LEFT JOIN {rTable} r ON l.resource_id = r.id
                WHERE l.invoice_id = $1 AND l.company_code = $2
                ORDER BY l.line_no";
            lineCmd.Parameters.AddWithValue(invoiceId);
            lineCmd.Parameters.AddWithValue(companyCode);

            await using var lineReader = await lineCmd.ExecuteReaderAsync();
            while (await lineReader.ReadAsync())
            {
                lines.Add(new
                {
                    description = lineReader.IsDBNull(0) ? "" : lineReader.GetString(0),
                    quantity = lineReader.IsDBNull(1) ? (decimal?)null : lineReader.GetDecimal(1),
                    unit = lineReader.IsDBNull(2) ? "" : lineReader.GetString(2),
                    unitPrice = lineReader.IsDBNull(3) ? (decimal?)null : lineReader.GetDecimal(3),
                    amount = lineReader.IsDBNull(4) ? (decimal?)null : lineReader.GetDecimal(4)
                });
            }
        }

        // 构建PDF payload
        var pdfPayload = new
        {
            template = "staffing_invoice",
            invoiceNo,
            invoiceDate,
            dueDate,
            clientName,
            clientAddress,
            companyName,
            companyAddress,
            companyZip,
            companyTel,
            companyEmail,
            subtotal,
            taxRate,
            taxAmount,
            totalAmount,
            remarks,
            bankInfo,
            lines,
            seal = string.IsNullOrWhiteSpace(sealDataUrl) ? null : new
            {
                image = sealDataUrl,
                size = sealSize ?? 56.0,
                offsetX = sealOffsetX ?? 0.0,
                offsetY = sealOffsetY ?? 0.0,
                opacity = sealOpacity ?? 0.8
            },
            docId = invoiceId.ToString()
        };

        // 调用 Agent Service 生成 PDF
        var agentBase = (cfg["Agent:Base"] ?? Environment.GetEnvironmentVariable("AGENT_BASE") ?? "http://localhost:3030").TrimEnd('/');
        var handler = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        async Task<HttpResponseMessage?> TryRender(string baseUrl)
        {
            try
            {
                return await http.PostAsync(
                    baseUrl + "/pdf/render",
                    new StringContent(JsonSerializer.Serialize(new { pdf = pdfPayload }), Encoding.UTF8, "application/json"));
            }
            catch
            {
                return null;
            }
        }

        var resp = await TryRender(agentBase);
        if (resp == null || !resp.IsSuccessStatusCode)
        {
            // 尝试 localhost/127.0.0.1 切换
            var alt = agentBase.Contains("localhost")
                ? agentBase.Replace("localhost", "127.0.0.1")
                : agentBase.Replace("127.0.0.1", "localhost");
            if (alt != agentBase)
            {
                var resp2 = await TryRender(alt);
                if (resp2 != null) resp = resp2;
            }
        }

        if (resp == null || !resp.IsSuccessStatusCode)
        {
            var errText = resp != null ? await resp.Content.ReadAsStringAsync() : "connection failed";
            throw new InvalidOperationException($"PDF render failed: {errText}");
        }

        var respText = await resp.Content.ReadAsStringAsync();
        using var respDoc = JsonDocument.Parse(respText);
        var pdfBase64 = respDoc.RootElement.GetProperty("data").GetString();
        if (string.IsNullOrWhiteSpace(pdfBase64))
            throw new InvalidOperationException("PDF render returned empty data");

        var pdfBytes = Convert.FromBase64String(pdfBase64);

        // Azure Storage 必须配置
        if (blobService?.IsConfigured != true)
        {
            throw new InvalidOperationException("Azure Storage is not configured. PDF generation requires Azure Storage.");
        }

        // 上传到 Azure Storage（路径规则: {companyCode}/staffing/invoices/{yyyy/MM/dd}/{invoiceNo}_{invoiceId}.pdf）
        var blobPath = $"{companyCode}/staffing/invoices/{DateTime.UtcNow:yyyy/MM/dd}/{invoiceNo}_{invoiceId:N}.pdf";
        using var pdfStream = new MemoryStream(pdfBytes);
        await blobService.UploadAsync(pdfStream, blobPath, "application/pdf", CancellationToken.None);

        // 更新请求书记录
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = $@"
            UPDATE {invTable} 
            SET payload = payload || jsonb_build_object('pdf_blob_path', $3::text, 'pdf_generated_at', $4::text),
                updated_at = now()
            WHERE id = $1 AND company_code = $2";
        updateCmd.Parameters.AddWithValue(invoiceId);
        updateCmd.Parameters.AddWithValue(companyCode);
        updateCmd.Parameters.AddWithValue(blobPath);
        updateCmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("O"));
        await updateCmd.ExecuteNonQueryAsync();

        return blobPath;
    }

    /// <summary>
    /// 获取公司基本信息（用于请求书PDF）
    /// </summary>
    private static async Task<(string? companyName, string? companyAddress, string? companyRep, string? sealDataUrl,
        double? sealSize, double? sealOffsetX, double? sealOffsetY, double? sealOpacity,
        string? companyZip, string? companyTel, string? companyEmail, string? bankInfo)> 
        LoadCompanyBasicsForInvoiceAsync(NpgsqlDataSource ds, string companyCode)
    {
        string? companyName = null, companyAddress = null, companyRep = null;
        string? sealDataUrl = null;
        double? sealSize = null, sealOffsetX = null, sealOffsetY = null, sealOpacity = null;
        string? companyZip = null, companyTel = null, companyEmail = null, bankInfo = null;

        await using var conn = await ds.OpenConnectionAsync();

        // 获取公司设置
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT payload FROM company_settings WHERE company_code = $1 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);

            var payloadJson = (string?)await cmd.ExecuteScalarAsync();
            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    var root = doc.RootElement;

                    companyName = root.TryGetProperty("companyName", out var cn) && cn.ValueKind == JsonValueKind.String
                        ? cn.GetString() : null;
                    companyAddress = root.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.String
                        ? ad.GetString() : null;
                    companyRep = root.TryGetProperty("companyRep", out var cr) && cr.ValueKind == JsonValueKind.String
                        ? cr.GetString() : null;
                    if (string.IsNullOrWhiteSpace(companyRep))
                        companyRep = root.TryGetProperty("representative", out var rp) && rp.ValueKind == JsonValueKind.String
                            ? rp.GetString() : null;

                    companyZip = root.TryGetProperty("postalCode", out var pz) && pz.ValueKind == JsonValueKind.String
                        ? pz.GetString() : null;
                    companyTel = root.TryGetProperty("tel", out var tel) && tel.ValueKind == JsonValueKind.String
                        ? tel.GetString() : null;
                    companyEmail = root.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString() : null;
                    bankInfo = root.TryGetProperty("bankInfo", out var bi) && bi.ValueKind == JsonValueKind.String
                        ? bi.GetString() : null;

                    // 解密印章
                    if (root.TryGetProperty("seal", out var seal) && seal.ValueKind == JsonValueKind.Object)
                    {
                        var format = seal.TryGetProperty("format", out var fm) && fm.ValueKind == JsonValueKind.String
                            ? (fm.GetString() ?? "png") : "png";

                        if (seal.TryGetProperty("enc", out var enc) && enc.ValueKind == JsonValueKind.String)
                        {
                            var encString = enc.GetString();
                            if (!string.IsNullOrWhiteSpace(encString))
                            {
                                try
                                {
                                    string b64;
                                    if (OperatingSystem.IsWindows())
                                    {
                                        var bytes = ProtectedData.Unprotect(
                                            Convert.FromBase64String(encString!),
                                            null,
                                            DataProtectionScope.CurrentUser);
                                        b64 = Convert.ToBase64String(bytes);
                                    }
                                    else
                                    {
                                        b64 = encString!;
                                    }
                                    sealDataUrl = $"data:image/{format};base64,{b64}";
                                }
                                catch { }
                            }
                        }

                        if (seal.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                            if (sz.TryGetDouble(out var d)) sealSize = d;
                        if (seal.TryGetProperty("offsetX", out var ox) && ox.ValueKind == JsonValueKind.Number)
                            if (ox.TryGetDouble(out var d)) sealOffsetX = d;
                        if (seal.TryGetProperty("offsetY", out var oy) && oy.ValueKind == JsonValueKind.Number)
                            if (oy.TryGetDouble(out var d)) sealOffsetY = d;
                        if (seal.TryGetProperty("opacity", out var op) && op.ValueKind == JsonValueKind.Number)
                            if (op.TryGetDouble(out var d)) sealOpacity = d;
                    }
                }
                catch { }
            }
        }

        // 如果公司名称为空，从 companies 表获取
        if (string.IsNullOrWhiteSpace(companyName))
        {
            await using var q2 = conn.CreateCommand();
            q2.CommandText = "SELECT name FROM companies WHERE company_code = $1 LIMIT 1";
            q2.Parameters.AddWithValue(companyCode);
            var nm = await q2.ExecuteScalarAsync();
            companyName = nm as string;
        }

        return (companyName, companyAddress, companyRep, sealDataUrl, sealSize, sealOffsetX, sealOffsetY, sealOpacity,
            companyZip, companyTel, companyEmail, bankInfo);
    }
}

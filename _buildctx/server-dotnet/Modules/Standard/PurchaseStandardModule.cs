using Server.Infrastructure;
using Server.Infrastructure.Modules;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Server.Modules.Standard;

/// <summary>
/// 采购模块 - 标准版
/// </summary>
public class PurchaseStandardModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "purchase",
        Name = "采购管理",
        Description = "采购订单、供应商発票等功能",
        Category = ModuleCategory.Standard,
        Version = "1.0.0",
        Dependencies = new[] { "finance_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_purchase", Label = "menu.purchase", Icon = "ShoppingBag", Path = "", ParentId = null, Order = 500 },
            new MenuConfig { Id = "menu_purchase_orders", Label = "menu.purchaseOrders", Icon = "Document", Path = "/purchase-orders", ParentId = "menu_purchase", Order = 501 },
            new MenuConfig { Id = "menu_vendor_invoices", Label = "menu.vendorInvoices", Icon = "Tickets", Path = "/vendor-invoices", ParentId = "menu_purchase", Order = 502 },
            new MenuConfig { Id = "menu_goods_receipts", Label = "menu.goodsReceipts", Icon = "Box", Path = "/goods-receipts", ParentId = "menu_purchase", Order = 503 },
        }
    };

    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<InvoiceRegistryService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        // Helper endpoints for Purchase UI.

        // GET /purchase-orders/last-price?partnerCode=...&materialCode=...
        app.MapGet("/purchase-orders/last-price", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var partnerCode = req.Query["partnerCode"].FirstOrDefault();
            var materialCode = req.Query["materialCode"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(partnerCode) || string.IsNullOrWhiteSpace(materialCode))
                return Results.BadRequest(new { error = "partnerCode and materialCode required" });

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                  (line->>'unitPrice')::numeric AS unit_price
                FROM purchase_orders po,
                     jsonb_array_elements(COALESCE(po.payload->'lines','[]'::jsonb)) AS line
                WHERE po.company_code = $1
                  AND (po.payload->>'partnerCode') = $2
                  AND (line->>'materialCode') = $3
                ORDER BY po.created_at DESC
                LIMIT 1";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(partnerCode);
            cmd.Parameters.AddWithValue(materialCode);
            var val = await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            if (val is null || val is DBNull) return Results.Ok(new { found = false, unitPrice = 0m });
            return Results.Ok(new { found = true, unitPrice = Convert.ToDecimal(val) });
        }).RequireAuthorization();

        // GET /purchase-orders/{id}/progress
        // NOTE: If detailed receipts/vouchers linkage is not implemented in current backend snapshot, return empty structure (no 405).
        app.MapGet("/purchase-orders/{id:guid}/progress", (Guid id) =>
        {
            return Results.Ok(new { receipts = Array.Empty<object>(), vouchers = Array.Empty<object>(), receivedSummary = new { } });
        }).RequireAuthorization();

        // POST /vendor-invoice/recognize — upload PDF/image → AI recognition → return structured data
        app.MapPost("/vendor-invoice/recognize", async (HttpRequest req, AzureBlobService blobService, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, configuration);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest(new { error = "OpenAI API key not configured" });

            var formData = await req.ReadFormAsync(req.HttpContext.RequestAborted);
            var file = formData.Files.GetFile("file");
            if (file is null || file.Length <= 0)
                return Results.BadRequest(new { error = "file required" });

            var originalName = string.IsNullOrWhiteSpace(file.FileName) ? "invoice.bin" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
            var ext = Path.GetExtension(originalName)?.ToLowerInvariant() ?? string.Empty;
            var companyCode = cc.ToString().Trim();

            // Read file into memory
            await using var stream = file.OpenReadStream();
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, req.HttpContext.RequestAborted);
            var fileBytes = buffer.ToArray();

            // Upload to Azure Blob for archival
            string? blobName = null;
            string? blobUrl = null;
            if (blobService.IsConfigured)
            {
                blobName = $"{companyCode}/purchase/invoices/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}{ext}";
                buffer.Position = 0;
                await blobService.UploadAsync(buffer, blobName, contentType, req.HttpContext.RequestAborted);
                try { blobUrl = blobService.GetReadUri(blobName); } catch { }
            }

            // Build base64 for Vision API
            var base64 = fileBytes.Length > 0 ? Convert.ToBase64String(fileBytes) : null;

            // Extract text preview for PDF
            string? textPreview = null;
            if (ext == ".pdf" || contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Save to temp file for text extraction
                var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{ext}");
                try
                {
                    await File.WriteAllBytesAsync(tempPath, fileBytes, req.HttpContext.RequestAborted);
                    textPreview = AiFileHelpers.ExtractTextPreview(tempPath, contentType, 4000);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            // Build OpenAI Vision API request
            var userContent = new List<object>();
            if (!string.IsNullOrWhiteSpace(textPreview))
            {
                userContent.Add(new { type = "text", text = AiFileHelpers.SanitizePreview(textPreview, 4000) ?? "" });
            }
            if (!string.IsNullOrWhiteSpace(base64))
            {
                var mimeType = string.IsNullOrWhiteSpace(contentType) ? "image/png" : contentType;
                userContent.Add(new { type = "image_url", image_url = new { url = $"data:{mimeType};base64,{base64}" } });
            }

            var systemPrompt = @"あなたは仕入請求書（Vendor Invoice）の解析アシスタントです。
ユーザーが提供する請求書（画像またはPDF）に基づき、次の JSON を出力してください：
- vendorName: 請求元（仕入先）の会社名
- vendorInvoiceNo: 請求書番号（記載があれば）
- invoiceDate: 請求日（YYYY-MM-DD）
- dueDate: 支払期限（YYYY-MM-DD、記載があれば）
- currency: 通貨コード（既定は JPY）
- totalAmount: 合計金額（税込、数値）
- subtotal: 税抜合計（数値）
- taxAmount: 消費税額（数値）
- taxRate: 税率（パーセンテージ、整数）
- invoiceRegistrationNo: 適格請求書発行事業者番号（^T\d{13}$ に一致する番号があれば）
- items: 明細配列。各要素は以下を含む：
  - description: 品名・摘要
  - quantity: 数量（数値）
  - unitPrice: 単価（数値）
  - amount: 金額（数値）
  - taxRate: 税率（パーセンテージ、整数）
- bankInfo: 振込先情報（銀行名、支店名、口座種別、口座番号、口座名義）があれば記載
- memo: その他の補足情報

【重要】和暦から西暦への変換ルール：
- 令和元年 = 2019年（令和N年 = 2018 + N 年）
- 平成元年 = 1989年（平成N年 = 1988 + N 年）

判別できない項目は空文字または 0 を返し、決して推測で値を作らないこと。";

            var body = new
            {
                model = "gpt-4o",
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent.ToArray() }
                }
            };

            var http = httpClientFactory.CreateClient("openai");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            using var response = await http.PostAsync("chat/completions",
                new StringContent(JsonSerializer.Serialize(body, jsonOpts), Encoding.UTF8, "application/json"),
                req.HttpContext.RequestAborted);

            var responseText = await response.Content.ReadAsStringAsync(req.HttpContext.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return Results.Problem($"AI recognition failed: {response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseText);
            var aiContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(aiContent))
                return Results.Problem("AI returned empty result");

            // Parse AI response and return
            using var aiDoc = JsonDocument.Parse(aiContent);
            return Results.Ok(new
            {
                recognized = true,
                data = aiDoc.RootElement.Clone(),
                attachment = new
                {
                    blobName,
                    url = blobUrl,
                    fileName = originalName,
                    contentType,
                    size = fileBytes.Length
                }
            });
        }).RequireAuthorization().DisableAntiforgery();

        // POST /vendor-invoice/available-receipts — query uninvoiced receipt lines linked to a vendor's POs
        app.MapPost("/vendor-invoice/available-receipts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = doc.RootElement;
            var vendorCode = root.TryGetProperty("vendorCode", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() : null;
            if (string.IsNullOrWhiteSpace(vendorCode))
                return Results.BadRequest(new { error = "vendorCode required" });
            var beforeDate = root.TryGetProperty("beforeDate", out var bd) && bd.ValueKind == JsonValueKind.String ? bd.GetString() : null;

            var company = cc.ToString().Trim();

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);

            // 1) Get all IN-type inventory movements that reference this vendor's POs
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                WITH vendor_pos AS (
                    SELECT id, po_no,
                           jsonb_array_elements(COALESCE(payload->'lines','[]'::jsonb)) AS po_line
                    FROM purchase_orders
                    WHERE company_code = $1
                      AND partner_code = $2
                ),
                receipt_lines AS (
                    SELECT
                        il.id AS ledger_id,
                        im.id AS movement_id,
                        im.movement_date::text AS receipt_date,
                        im.reference_no AS po_no,
                        il.material_code,
                        il.quantity AS received_qty,
                        il.uom,
                        il.to_warehouse AS warehouse_code
                    FROM inventory_movements im
                    JOIN inventory_ledger il ON il.movement_id = im.id
                    WHERE im.company_code = $1
                      AND im.movement_type = 'IN'
                      AND im.reference_no IN (SELECT DISTINCT po_no FROM vendor_pos)
                ),
                invoiced AS (
                    SELECT
                        (line->>'matchedReceiptId') AS receipt_id,
                        COALESCE((line->>'quantity')::numeric, 0) AS invoiced_qty,
                        COALESCE((line->>'amount')::numeric, 0) AS invoiced_amount
                    FROM vendor_invoices vi,
                         jsonb_array_elements(COALESCE(vi.payload->'lines','[]'::jsonb)) AS line
                    WHERE vi.company_code = $1
                      AND vi.vendor_code = $2
                )
                SELECT
                    rl.ledger_id,
                    rl.receipt_date,
                    rl.po_no,
                    rl.material_code,
                    COALESCE(vp.po_line->>'materialName', '') AS material_name,
                    rl.received_qty,
                    COALESCE(SUM(inv.invoiced_qty), 0) AS total_invoiced_qty,
                    COALESCE((vp.po_line->>'unitPrice')::numeric, 0) AS unit_price,
                    rl.uom,
                    rl.warehouse_code
                FROM receipt_lines rl
                LEFT JOIN vendor_pos vp
                    ON vp.po_no = rl.po_no
                   AND (vp.po_line->>'materialCode') = rl.material_code
                LEFT JOIN invoiced inv ON inv.receipt_id = rl.ledger_id::text
                GROUP BY rl.ledger_id, rl.receipt_date, rl.po_no, rl.material_code,
                         vp.po_line->>'materialName', rl.received_qty, vp.po_line->>'unitPrice',
                         rl.uom, rl.warehouse_code
                HAVING rl.received_qty - COALESCE(SUM(inv.invoiced_qty), 0) > 0
                ORDER BY rl.receipt_date DESC, rl.po_no, rl.material_code";

            cmd.Parameters.AddWithValue(company);
            cmd.Parameters.AddWithValue(vendorCode);

            // Optional date filter
            if (!string.IsNullOrWhiteSpace(beforeDate))
            {
                // We'll filter in-memory since the CTE is already constrained
            }

            var receipts = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(req.HttpContext.RequestAborted);
            while (await reader.ReadAsync(req.HttpContext.RequestAborted))
            {
                var receiptDate = reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(beforeDate) && string.Compare(receiptDate, beforeDate, StringComparison.Ordinal) > 0)
                    continue;

                var receivedQty = reader.GetDecimal(5);
                var invoicedQty = reader.GetDecimal(6);
                var unitPrice = reader.GetDecimal(7);
                var uninvoicedQty = receivedQty - invoicedQty;
                var uninvoicedAmount = uninvoicedQty * unitPrice;

                receipts.Add(new
                {
                    id = reader.GetGuid(0).ToString(),
                    receiptDate,
                    poNo = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    materialCode = reader.GetString(3),
                    materialName = reader.GetString(4),
                    quantity = receivedQty,
                    uninvoicedQuantity = uninvoicedQty,
                    unitPrice,
                    uninvoicedAmount,
                    uom = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    warehouseCode = reader.IsDBNull(9) ? "" : reader.GetString(9)
                });
            }

            return Results.Ok(new { receipts });
        }).RequireAuthorization();

        // GET /blob/download-url?name=xxx — get fresh SAS URL for blob
        app.MapGet("/blob/download-url", (HttpRequest req, AzureBlobService blobService) =>
        {
            var name = req.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name required" });
            if (!blobService.IsConfigured)
                return Results.BadRequest(new { error = "Blob storage not configured" });
            try
            {
                var url = blobService.GetReadUri(name);
                return Results.Ok(new { url });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();
    }
}


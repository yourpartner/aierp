using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Domain;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Staffing;

/// <summary>
/// 受注管理モジュール
/// 顧客から受け取った注文（SES/派遣/請負）を管理する。
/// 受注明細（stf_juchuu_detail）でリソース・料金条件を複数管理。
/// 注文書PDF の OCR 解析（GPT-4o vision）に対応。
/// </summary>
public class StaffingJuchuuModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_juchuu",
        Name = "受注管理",
        Description = "顧客からの受注契約を管理（注文書PDF読み取り対応）",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_project" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_juchuu", Label = "menu.staffingJuchuu", Icon = "DocumentChecked", Path = "/staffing/juchuu", ParentId = "menu_staffing", Order = 252 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/staffing/juchuu")
            .WithTags("Staffing Juchuu")
            .RequireAuthorization();

        const string table = "stf_juchuu";
        const string detailTable = "stf_juchuu_detail";

        // ===== 一覧 =====
        group.MapGet("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var clientId = req.Query["clientId"].FirstOrDefault();
            var keyword = req.Query["keyword"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? Math.Max(0, o) : 0;

            await using var conn = await ds.OpenConnectionAsync();

            var where = new List<string> { "j.company_code = $1" };
            var args = new List<object?> { cc.ToString() };
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(status)) { where.Add($"j.status = ${idx++}"); args.Add(status); }
            if (!string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId, out var cid)) { where.Add($"j.client_partner_id = ${idx++}"); args.Add(cid); }
            if (!string.IsNullOrWhiteSpace(keyword)) { where.Add($"(j.juchuu_no ILIKE ${idx} OR bp.payload->>'name' ILIKE ${idx})"); args.Add($"%{keyword}%"); idx++; }

            var whereClause = string.Join(" AND ", where);

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {table} j LEFT JOIN businesspartners bp ON j.client_partner_id = bp.id WHERE {whereClause}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT j.id, j.juchuu_no, j.contract_type, j.status,
                       j.start_date, j.end_date,
                       j.attached_doc_url,
                       bp.partner_code as client_code,
                       bp.payload->>'name' as client_name,
                       p.payload->>'project_name' as project_name,
                       (SELECT COUNT(*) FROM {detailTable} d WHERE d.juchuu_id = j.id) as resource_count,
                       (SELECT string_agg(r2.payload->>'display_name', ', ' ORDER BY d2.sort_order)
                        FROM {detailTable} d2
                        LEFT JOIN stf_resources r2 ON d2.resource_id = r2.id
                        WHERE d2.juchuu_id = j.id) as resource_names
                FROM {table} j
                LEFT JOIN businesspartners bp ON j.client_partner_id = bp.id
                LEFT JOIN stf_projects p ON j.project_id = p.id
                WHERE {whereClause}
                ORDER BY j.start_date DESC NULLS LAST, j.created_at DESC
                LIMIT ${idx} OFFSET ${idx + 1}";
            for (var i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    id = reader.GetGuid(0),
                    juchuuNo = reader.IsDBNull(1) ? null : reader.GetString(1),
                    contractType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    status = reader.IsDBNull(3) ? null : reader.GetString(3),
                    startDate = reader.IsDBNull(4) ? null : (string?)reader.GetDateTime(4).ToString("yyyy-MM-dd"),
                    endDate = reader.IsDBNull(5) ? null : (string?)reader.GetDateTime(5).ToString("yyyy-MM-dd"),
                    hasAttachedDoc = !reader.IsDBNull(6),
                    clientCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                    clientName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    projectName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    resourceCount = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10)),
                    resourceNames = reader.IsDBNull(11) ? null : reader.GetString(11),
                });
            }
            return Results.Ok(new { data = rows, total });
        });

        // ===== 詳細 =====
        group.MapGet("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // ヘッダー取得
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT j.*,
                       bp.partner_code as client_code, bp.payload->>'name' as client_name,
                       p.payload->>'project_name' as project_name
                FROM {table} j
                LEFT JOIN businesspartners bp ON j.client_partner_id = bp.id
                LEFT JOIN stf_projects p ON j.project_id = p.id
                WHERE j.id = $1 AND j.company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            var result = BuildDetailObject(reader, blobService);
            await reader.CloseAsync();

            // 明細取得
            await using var detailCmd = conn.CreateCommand();
            detailCmd.CommandText = $@"
                SELECT d.id, d.resource_id, d.billing_rate, d.billing_rate_type,
                       d.overtime_rate_multiplier, d.settlement_type, d.settlement_lower_h, d.settlement_upper_h,
                       d.notes, d.sort_order,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       r.resource_type
                FROM {detailTable} d
                LEFT JOIN stf_resources r ON d.resource_id = r.id
                WHERE d.juchuu_id = $1
                ORDER BY d.sort_order, d.created_at";
            detailCmd.Parameters.AddWithValue(id);

            var details = new List<object>();
            await using var dr = await detailCmd.ExecuteReaderAsync();
            while (await dr.ReadAsync())
            {
                details.Add(new
                {
                    id = dr.GetGuid(0),
                    resourceId = dr.IsDBNull(1) ? null : dr.GetGuid(1).ToString(),
                    billingRate = dr.IsDBNull(2) ? (decimal?)null : dr.GetDecimal(2),
                    billingRateType = dr.IsDBNull(3) ? null : dr.GetString(3),
                    overtimeRateMultiplier = dr.IsDBNull(4) ? (decimal?)null : dr.GetDecimal(4),
                    settlementType = dr.IsDBNull(5) ? null : dr.GetString(5),
                    settlementLowerH = dr.IsDBNull(6) ? (decimal?)null : dr.GetDecimal(6),
                    settlementUpperH = dr.IsDBNull(7) ? (decimal?)null : dr.GetDecimal(7),
                    notes = dr.IsDBNull(8) ? null : dr.GetString(8),
                    sortOrder = dr.IsDBNull(9) ? 0 : dr.GetInt32(9),
                    resourceName = dr.IsDBNull(10) ? null : dr.GetString(10),
                    resourceCode = dr.IsDBNull(11) ? null : dr.GetString(11),
                    resourceType = dr.IsDBNull(12) ? null : dr.GetString(12),
                });
            }

            // ヘッダー + 明細を合成
            var resultDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(result))!;
            return Results.Json(new
            {
                id = resultDict.GetValueOrDefault("id"),
                juchuuNo = resultDict.GetValueOrDefault("juchuuNo"),
                contractType = resultDict.GetValueOrDefault("contractType"),
                status = resultDict.GetValueOrDefault("status"),
                startDate = resultDict.GetValueOrDefault("startDate"),
                endDate = resultDict.GetValueOrDefault("endDate"),
                clientPartnerId = resultDict.GetValueOrDefault("clientPartnerId"),
                clientCode = resultDict.GetValueOrDefault("clientCode"),
                clientName = resultDict.GetValueOrDefault("clientName"),
                projectId = resultDict.GetValueOrDefault("projectId"),
                projectName = resultDict.GetValueOrDefault("projectName"),
                workLocation = resultDict.GetValueOrDefault("workLocation"),
                workDays = resultDict.GetValueOrDefault("workDays"),
                workStartTime = resultDict.GetValueOrDefault("workStartTime"),
                workEndTime = resultDict.GetValueOrDefault("workEndTime"),
                monthlyWorkHours = resultDict.GetValueOrDefault("monthlyWorkHours"),
                attachedDocBlobName = resultDict.GetValueOrDefault("attachedDocBlobName"),
                attachedDocUrl = resultDict.GetValueOrDefault("attachedDocUrl"),
                ocrRawText = resultDict.GetValueOrDefault("ocrRawText"),
                notes = resultDict.GetValueOrDefault("notes"),
                details,
            });
        });

        // ===== 新規作成 =====
        group.MapPost("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var body = doc.RootElement;

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT nextval('seq_juchuu_no')";
            var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
            var juchuuNo = $"JU-{DateTime.Today:yyyy}-{seq:D4}";

            // ヘッダー挿入
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                INSERT INTO {table} (
                    company_code, juchuu_no, client_partner_id, project_id,
                    contract_type, status, start_date, end_date,
                    work_location, work_days, work_start_time, work_end_time, monthly_work_hours,
                    notes, payload
                ) VALUES (
                    $1, $2, $3, $4,
                    $5, $6, $7::date, $8::date,
                    $9, $10, $11, $12, $13,
                    $14, $15::jsonb
                ) RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(juchuuNo);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "clientPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "projectId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? "ses");
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? "active");
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workLocation") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workDays") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workStartTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workEndTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "monthlyWorkHours") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "notes") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("{}");

            var newId = (Guid)(await cmd.ExecuteScalarAsync())!;

            // 明細挿入
            if (body.TryGetProperty("details", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Array)
            {
                var sortOrder = 0;
                foreach (var detail in detailsEl.EnumerateArray())
                {
                    await using var dc = conn.CreateCommand();
                    dc.Transaction = tx;
                    dc.CommandText = $@"
                        INSERT INTO {detailTable} (
                            company_code, juchuu_id, resource_id,
                            billing_rate, billing_rate_type, overtime_rate_multiplier,
                            settlement_type, settlement_lower_h, settlement_upper_h,
                            notes, sort_order
                        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
                    dc.Parameters.AddWithValue(cc.ToString());
                    dc.Parameters.AddWithValue(newId);
                    dc.Parameters.AddWithValue(TryParseGuid(detail, "resourceId") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "billingRate") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "billingRateType") ?? "monthly");
                    dc.Parameters.AddWithValue(GetDecimal(detail, "overtimeRateMultiplier") ?? (object)1.25m);
                    dc.Parameters.AddWithValue(GetString(detail, "settlementType") ?? "range");
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementLowerH") ?? (object)140m);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementUpperH") ?? (object)180m);
                    dc.Parameters.AddWithValue(GetString(detail, "notes") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(sortOrder++);
                    await dc.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
            return Results.Ok(new { id = newId, juchuuNo });
        });

        // ===== 更新 =====
        group.MapPut("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var body = doc.RootElement;

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // ヘッダー更新
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                UPDATE {table} SET
                    client_partner_id = COALESCE($3, client_partner_id),
                    project_id        = COALESCE($4, project_id),
                    contract_type     = COALESCE($5, contract_type),
                    status            = COALESCE($6, status),
                    start_date        = COALESCE($7::date, start_date),
                    end_date          = COALESCE($8::date, end_date),
                    work_location     = COALESCE($9, work_location),
                    work_days         = COALESCE($10, work_days),
                    work_start_time   = COALESCE($11, work_start_time),
                    work_end_time     = COALESCE($12, work_end_time),
                    monthly_work_hours = COALESCE($13, monthly_work_hours),
                    notes             = COALESCE($14, notes),
                    updated_at        = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(TryParseGuid(body, "clientPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "projectId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workLocation") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workDays") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workStartTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workEndTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "monthlyWorkHours") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "notes") ?? (object)DBNull.Value);

            var updated = await cmd.ExecuteScalarAsync();
            if (updated == null) { await tx.RollbackAsync(); return Results.NotFound(); }

            // 明細が送られてきた場合は全置換
            if (body.TryGetProperty("details", out var detailsEl) && detailsEl.ValueKind == JsonValueKind.Array)
            {
                await using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = $"DELETE FROM {detailTable} WHERE juchuu_id = $1";
                delCmd.Parameters.AddWithValue(id);
                await delCmd.ExecuteNonQueryAsync();

                var sortOrder = 0;
                foreach (var detail in detailsEl.EnumerateArray())
                {
                    await using var dc = conn.CreateCommand();
                    dc.Transaction = tx;
                    dc.CommandText = $@"
                        INSERT INTO {detailTable} (
                            company_code, juchuu_id, resource_id,
                            billing_rate, billing_rate_type, overtime_rate_multiplier,
                            settlement_type, settlement_lower_h, settlement_upper_h,
                            notes, sort_order
                        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
                    dc.Parameters.AddWithValue(cc.ToString());
                    dc.Parameters.AddWithValue(id);
                    dc.Parameters.AddWithValue(TryParseGuid(detail, "resourceId") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "billingRate") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "billingRateType") ?? "monthly");
                    dc.Parameters.AddWithValue(GetDecimal(detail, "overtimeRateMultiplier") ?? (object)1.25m);
                    dc.Parameters.AddWithValue(GetString(detail, "settlementType") ?? "range");
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementLowerH") ?? (object)140m);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementUpperH") ?? (object)180m);
                    dc.Parameters.AddWithValue(GetString(detail, "notes") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(sortOrder++);
                    await dc.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
            return Results.Ok(new { id, updated = true });
        });

        // ===== 削除（ステータスを terminated に変更） =====
        group.MapDelete("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET status = 'terminated', updated_at = now() WHERE id = $1 AND company_code = $2 RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound();
            return Results.Ok(new { id, deleted = true });
        });

        // ===== 受注に紐づく発注一覧 =====
        group.MapGet("/{id:guid}/hatchuu", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT h.id, h.hatchuu_no, h.status, h.start_date, h.end_date,
                       h.doc_generated_url,
                       bp.payload->>'name' as supplier_name,
                       (SELECT COUNT(*) FROM stf_hatchuu_detail hd WHERE hd.hatchuu_id = h.id) as resource_count,
                       (SELECT string_agg(r2.payload->>'display_name', ', ' ORDER BY hd2.sort_order)
                        FROM stf_hatchuu_detail hd2
                        LEFT JOIN stf_resources r2 ON hd2.resource_id = r2.id
                        WHERE hd2.hatchuu_id = h.id) as resource_names
                FROM stf_hatchuu h
                LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id
                WHERE h.juchuu_id = $1 AND h.company_code = $2
                ORDER BY h.start_date ASC";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new
                {
                    id = reader.GetGuid(0),
                    hatchuuNo = reader.IsDBNull(1) ? null : reader.GetString(1),
                    status = reader.IsDBNull(2) ? null : reader.GetString(2),
                    startDate = reader.IsDBNull(3) ? null : (string?)reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                    endDate = reader.IsDBNull(4) ? null : (string?)reader.GetDateTime(4).ToString("yyyy-MM-dd"),
                    hasPdf = !reader.IsDBNull(5),
                    supplierName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    resourceCount = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
                    resourceNames = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }
            return Results.Ok(new { data = rows, total = rows.Count });
        });

        // ===== 注文書PDFアップロード + OCR解析 =====
        group.MapPost("/{id:guid}/upload-doc", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "file is required" });

            // 1. Blobストレージにアップロード
            var blobName = $"juchuu/{cc}/{id}/{DateTime.UtcNow:yyyyMMddHHmmss}_{file.FileName}";
            await using var stream = file.OpenReadStream();
            await blobService.UploadAsync(stream, blobName, file.ContentType, CancellationToken.None);
            var docUrl = blobService.GetReadUri(blobName);

            // 2. DBにURLを保存
            await using var conn = await ds.OpenConnectionAsync();
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = $"UPDATE {table} SET attached_doc_url = $3, updated_at = now() WHERE id = $1 AND company_code = $2";
            updateCmd.Parameters.AddWithValue(id);
            updateCmd.Parameters.AddWithValue(cc.ToString());
            updateCmd.Parameters.AddWithValue(blobName);
            await updateCmd.ExecuteNonQueryAsync();

            // 3. OCR解析（GPT-4o Vision）
            var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, config);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.Ok(new { blobName, docUrl, parsed = (object?)null, message = "OCR skipped: no API key" });

            var parsed = await ParseOrderDocumentAsync(file, apiKey, httpFactory);

            // 4. OCRテキストをDBに保存
            if (parsed != null)
            {
                await using var ocrCmd = conn.CreateCommand();
                ocrCmd.CommandText = $"UPDATE {table} SET ocr_raw_text = $3, updated_at = now() WHERE id = $1 AND company_code = $2";
                ocrCmd.Parameters.AddWithValue(id);
                ocrCmd.Parameters.AddWithValue(cc.ToString());
                ocrCmd.Parameters.AddWithValue(JsonSerializer.Serialize(parsed));
                await ocrCmd.ExecuteNonQueryAsync();
            }

            return Results.Ok(new { blobName, docUrl, parsed });
        });

        // ===== 注文書テキストのみOCR解析（ファイルアップロード済みの再解析）=====
        group.MapPost("/parse-doc", async (HttpRequest req, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Results.BadRequest(new { error = "file is required" });

            var apiKey = AiFileHelpers.ResolveOpenAIApiKey(req, config);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest(new { error = "API key not configured" });

            var parsed = await ParseOrderDocumentAsync(file, apiKey, httpFactory);
            return Results.Ok(new { parsed });
        });
    }

    /// <summary>
    /// GPT-4o Vision で注文書PDFを解析して受注フィールドを抽出
    /// </summary>
    private static async Task<object?> ParseOrderDocumentAsync(IFormFile file, string apiKey, IHttpClientFactory httpFactory)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var contentType = file.ContentType switch
        {
            "application/pdf" => "image/png",
            _ => file.ContentType
        };

        var systemPrompt = """
            あなたは日本語の注文書・発注書を解析する専門AIです。
            提供されたドキュメント（画像またはPDF）から、以下の項目を抽出してJSONで返してください。
            抽出できない項目は null にしてください。

            返却JSON形式:
            {
              "contractType": "ses|dispatch|contract",
              "clientName": "顧客会社名",
              "startDate": "YYYY-MM-DD",
              "endDate": "YYYY-MM-DD",
              "workLocation": "勤務地",
              "workDays": "勤務曜日（例: 月〜金）",
              "workStartTime": "HH:MM",
              "workEndTime": "HH:MM",
              "monthlyWorkHours": 数値（月間基準時間）,
              "notes": "その他特記事項",
              "resources": [
                {
                  "resourceName": "氏名",
                  "billingRate": 数値（円）,
                  "billingRateType": "monthly|daily|hourly",
                  "overtimeRateMultiplier": 数値（例: 1.25）,
                  "settlementType": "range|fixed",
                  "settlementLowerH": 数値,
                  "settlementUpperH": 数値
                }
              ],
              "rawText": "抽出した全テキスト"
            }

            判断基準:
            - SES（準委任）: 成果物より技術提供が主目的
            - 派遣: 派遣契約、偽装請負に注意
            - 請負: 成果物・納品物があり完成責任がある
            """;

        var userContent = new List<object>
        {
            new { type = "text", text = "この注文書を解析して、指定されたJSON形式で契約情報を抽出してください。" }
        };

        if (!string.IsNullOrWhiteSpace(base64))
        {
            var mimeType = contentType.StartsWith("image/") ? contentType : "image/png";
            userContent.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{mimeType};base64,{base64}", detail = "high" }
            });
        }

        var requestBody = new
        {
            model = "gpt-4o",
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = (object)systemPrompt },
                new { role = "user", content = (object)userContent }
            }
        };

        try
        {
            var http = httpFactory.CreateClient("openai");
            OpenAiApiHelper.SetOpenAiHeaders(http, apiKey);

            using var response = await http.PostAsync("chat/completions",
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return null;

            using var responseDoc = JsonDocument.Parse(responseText);
            var content = responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content)) return null;
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
        catch
        {
            return null;
        }
    }

    private static object BuildDetailObject(System.Data.Common.DbDataReader reader, AzureBlobService blobService)
    {
        static T? GetVal<T>(System.Data.Common.DbDataReader r, string col) where T : struct
        {
            try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? (T?)null : (T)r.GetValue(ord); }
            catch { return null; }
        }
        static string? GetStr(System.Data.Common.DbDataReader r, string col)
        {
            try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? null : r.GetString(ord); }
            catch { return null; }
        }

        var attachedBlobName = GetStr(reader, "attached_doc_url");
        string? attachedDocUrl = null;
        if (!string.IsNullOrWhiteSpace(attachedBlobName))
        {
            try { attachedDocUrl = blobService.GetReadUri(attachedBlobName); } catch { }
        }

        return new
        {
            id = reader.GetGuid(reader.GetOrdinal("id")),
            juchuuNo = GetStr(reader, "juchuu_no"),
            contractType = GetStr(reader, "contract_type"),
            status = GetStr(reader, "status"),
            startDate = GetVal<DateTime>(reader, "start_date")?.ToString("yyyy-MM-dd"),
            endDate = GetVal<DateTime>(reader, "end_date")?.ToString("yyyy-MM-dd"),
            clientPartnerId = GetStr(reader, "client_partner_id"),
            clientCode = GetStr(reader, "client_code"),
            clientName = GetStr(reader, "client_name"),
            projectId = GetStr(reader, "project_id"),
            projectName = GetStr(reader, "project_name"),
            workLocation = GetStr(reader, "work_location"),
            workDays = GetStr(reader, "work_days"),
            workStartTime = GetStr(reader, "work_start_time"),
            workEndTime = GetStr(reader, "work_end_time"),
            monthlyWorkHours = GetVal<decimal>(reader, "monthly_work_hours"),
            attachedDocBlobName = attachedBlobName,
            attachedDocUrl,
            ocrRawText = GetStr(reader, "ocr_raw_text"),
            notes = GetStr(reader, "notes"),
        };
    }

    private static Guid? TryParseGuid(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : (Guid?)null;
    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? GetDecimal(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : (decimal?)null;
}

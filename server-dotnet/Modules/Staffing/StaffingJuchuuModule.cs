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
                       j.billing_rate, j.billing_rate_type,
                       j.attached_doc_url,
                       bp.partner_code as client_code,
                       bp.payload->>'name' as client_name,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       p.payload->>'project_name' as project_name
                FROM {table} j
                LEFT JOIN businesspartners bp ON j.client_partner_id = bp.id
                LEFT JOIN stf_resources r ON j.resource_id = r.id
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
                    billingRate = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
                    billingRateType = reader.IsDBNull(7) ? null : reader.GetString(7),
                    hasAttachedDoc = !reader.IsDBNull(8),
                    clientCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                    clientName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    resourceName = reader.IsDBNull(11) ? null : reader.GetString(11),
                    resourceCode = reader.IsDBNull(12) ? null : reader.GetString(12),
                    projectName = reader.IsDBNull(13) ? null : reader.GetString(13),
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT j.*,
                       bp.partner_code as client_code, bp.payload->>'name' as client_name,
                       r.payload->>'display_name' as resource_name, r.payload->>'resource_code' as resource_code,
                       p.payload->>'project_name' as project_name
                FROM {table} j
                LEFT JOIN businesspartners bp ON j.client_partner_id = bp.id
                LEFT JOIN stf_resources r ON j.resource_id = r.id
                LEFT JOIN stf_projects p ON j.project_id = p.id
                WHERE j.id = $1 AND j.company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();

            var result = BuildDetailObject(reader, blobService);
            return Results.Json(result);
        });

        // ===== 新規作成 =====
        group.MapPost("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var body = doc.RootElement;

            await using var conn = await ds.OpenConnectionAsync();
            await using var seqCmd = conn.CreateCommand();
            seqCmd.CommandText = "SELECT nextval('seq_juchuu_no')";
            var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
            var juchuuNo = $"JU-{DateTime.Today:yyyy}-{seq:D4}";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {table} (
                    company_code, juchuu_no, client_partner_id, project_id, resource_id,
                    contract_type, status, start_date, end_date,
                    billing_rate, billing_rate_type, overtime_rate_multiplier,
                    settlement_type, settlement_lower_h, settlement_upper_h,
                    work_location, work_days, work_start_time, work_end_time, monthly_work_hours,
                    notes, payload
                ) VALUES (
                    $1, $2, $3, $4, $5,
                    $6, $7, $8::date, $9::date,
                    $10, $11, $12,
                    $13, $14, $15,
                    $16, $17, $18, $19, $20,
                    $21, $22::jsonb
                ) RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(juchuuNo);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "clientPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "projectId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "resourceId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? "ses");
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? "active");
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "billingRate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "billingRateType") ?? "monthly");
            cmd.Parameters.AddWithValue(GetDecimal(body, "overtimeRateMultiplier") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "settlementType") ?? "range");
            cmd.Parameters.AddWithValue(GetDecimal(body, "settlementLowerH") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "settlementUpperH") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workLocation") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workDays") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workStartTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workEndTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "monthlyWorkHours") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "notes") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("{}");

            var newId = (Guid)(await cmd.ExecuteScalarAsync())!;
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {table} SET
                    client_partner_id = COALESCE($3, client_partner_id),
                    project_id        = COALESCE($4, project_id),
                    resource_id       = COALESCE($5, resource_id),
                    contract_type     = COALESCE($6, contract_type),
                    status            = COALESCE($7, status),
                    start_date        = COALESCE($8::date, start_date),
                    end_date          = COALESCE($9::date, end_date),
                    billing_rate      = COALESCE($10, billing_rate),
                    billing_rate_type = COALESCE($11, billing_rate_type),
                    overtime_rate_multiplier = COALESCE($12, overtime_rate_multiplier),
                    settlement_type   = COALESCE($13, settlement_type),
                    settlement_lower_h = COALESCE($14, settlement_lower_h),
                    settlement_upper_h = COALESCE($15, settlement_upper_h),
                    work_location     = COALESCE($16, work_location),
                    work_days         = COALESCE($17, work_days),
                    work_start_time   = COALESCE($18, work_start_time),
                    work_end_time     = COALESCE($19, work_end_time),
                    monthly_work_hours = COALESCE($20, monthly_work_hours),
                    notes             = COALESCE($21, notes),
                    updated_at        = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(TryParseGuid(body, "clientPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "projectId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "resourceId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "billingRate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "billingRateType") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "overtimeRateMultiplier") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "settlementType") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "settlementLowerH") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "settlementUpperH") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workLocation") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workDays") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workStartTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "workEndTime") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "monthlyWorkHours") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "notes") ?? (object)DBNull.Value);

            var updated = await cmd.ExecuteScalarAsync();
            if (updated == null) return Results.NotFound();
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
                       h.cost_rate, h.cost_rate_type, h.doc_generated_url,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       bp.payload->>'name' as supplier_name
                FROM stf_hatchuu h
                LEFT JOIN stf_resources r ON h.resource_id = r.id
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
                    costRate = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5),
                    costRateType = reader.IsDBNull(6) ? null : reader.GetString(6),
                    hasPdf = !reader.IsDBNull(7),
                    resourceName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    resourceCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                    supplierName = reader.IsDBNull(10) ? null : reader.GetString(10),
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
        // ファイルをBase64に変換
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());
        var contentType = file.ContentType switch
        {
            "application/pdf" => "image/png", // PDFは一般的なmimeで送信
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
              "billingRate": 数値（円）,
              "billingRateType": "monthly|daily|hourly",
              "overtimeRateMultiplier": 数値（例: 1.25）,
              "settlementType": "range|fixed",
              "settlementLowerH": 数値（精算下限時間）,
              "settlementUpperH": 数値（精算上限時間）,
              "workLocation": "勤務地",
              "workDays": "勤務曜日（例: 月〜金）",
              "workStartTime": "HH:MM",
              "workEndTime": "HH:MM",
              "monthlyWorkHours": 数値（月間基準時間）,
              "notes": "その他特記事項",
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

        // PDFの場合は画像として送信
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
            resourceId = GetStr(reader, "resource_id"),
            resourceName = GetStr(reader, "resource_name"),
            resourceCode = GetStr(reader, "resource_code"),
            billingRate = GetVal<decimal>(reader, "billing_rate"),
            billingRateType = GetStr(reader, "billing_rate_type"),
            overtimeRateMultiplier = GetVal<decimal>(reader, "overtime_rate_multiplier"),
            settlementType = GetStr(reader, "settlement_type"),
            settlementLowerH = GetVal<decimal>(reader, "settlement_lower_h"),
            settlementUpperH = GetVal<decimal>(reader, "settlement_upper_h"),
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

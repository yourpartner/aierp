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
/// 発注管理モジュール
/// リソース・BP会社への発注（SES/派遣/請負）を管理する。
/// 発注明細（stf_hatchuu_detail）でリソース・原価条件を複数管理。
/// 発注書PDF の自動生成に対応。
/// </summary>
public class StaffingHatchuuModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_hatchuu",
        Name = "発注管理",
        Description = "リソース・BP会社への発注契約管理（発注書PDF生成対応）",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_juchuu" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_hatchuu", Label = "menu.staffingHatchuu", Icon = "DocumentAdd", Path = "/staffing/hatchuu", ParentId = "menu_staffing", Order = 253 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/staffing/hatchuu")
            .WithTags("Staffing Hatchuu")
            .RequireAuthorization();

        const string table = "stf_hatchuu";
        const string detailTable = "stf_hatchuu_detail";

        // ===== 一覧 =====
        group.MapGet("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var juchuuId = req.Query["juchuuId"].FirstOrDefault();
            var resourceId = req.Query["resourceId"].FirstOrDefault();
            var keyword = req.Query["keyword"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? Math.Max(0, o) : 0;

            await using var conn = await ds.OpenConnectionAsync();

            var where = new List<string> { "h.company_code = $1" };
            var args = new List<object?> { cc.ToString() };
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(status)) { where.Add($"h.status = ${idx++}"); args.Add(status); }
            if (!string.IsNullOrWhiteSpace(juchuuId) && Guid.TryParse(juchuuId, out var jid)) { where.Add($"h.juchuu_id = ${idx++}"); args.Add(jid); }
            if (!string.IsNullOrWhiteSpace(resourceId) && Guid.TryParse(resourceId, out var rid))
            {
                // resource_idは明細テーブルにある
                where.Add($"EXISTS (SELECT 1 FROM {detailTable} hd WHERE hd.hatchuu_id = h.id AND hd.resource_id = ${idx++})");
                args.Add(rid);
            }
            if (!string.IsNullOrWhiteSpace(keyword)) { where.Add($"(h.hatchuu_no ILIKE ${idx} OR bp.payload->>'name' ILIKE ${idx})"); args.Add($"%{keyword}%"); idx++; }

            var whereClause = string.Join(" AND ", where);

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {table} h LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id WHERE {whereClause}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT h.id, h.hatchuu_no, h.juchuu_id, h.contract_type, h.status,
                       h.start_date, h.end_date,
                       h.doc_generated_url, h.doc_generated_at,
                       bp.payload->>'name' as supplier_name,
                       j.juchuu_no,
                       jbp.payload->>'name' as client_name,
                       (SELECT COUNT(*) FROM {detailTable} hd WHERE hd.hatchuu_id = h.id) as resource_count,
                       (SELECT string_agg(COALESCE(hd2.resource_name, r2.payload->>'display_name'), ', ' ORDER BY hd2.sort_order)
                        FROM {detailTable} hd2
                        LEFT JOIN stf_resources r2 ON hd2.resource_id = r2.id
                        WHERE hd2.hatchuu_id = h.id) as resource_names
                FROM {table} h
                LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id
                LEFT JOIN stf_juchuu j ON h.juchuu_id = j.id
                LEFT JOIN businesspartners jbp ON j.client_partner_id = jbp.id
                WHERE {whereClause}
                ORDER BY h.start_date DESC NULLS LAST, h.created_at DESC
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
                    hatchuuNo = reader.IsDBNull(1) ? null : reader.GetString(1),
                    juchuuId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2),
                    contractType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    status = reader.IsDBNull(4) ? null : reader.GetString(4),
                    startDate = reader.IsDBNull(5) ? null : (string?)reader.GetDateTime(5).ToString("yyyy-MM-dd"),
                    endDate = reader.IsDBNull(6) ? null : (string?)reader.GetDateTime(6).ToString("yyyy-MM-dd"),
                    hasPdf = !reader.IsDBNull(7),
                    docGeneratedAt = reader.IsDBNull(8) ? null : (DateTime?)reader.GetDateTime(8),
                    supplierName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    juchuuNo = reader.IsDBNull(10) ? null : reader.GetString(10),
                    clientName = reader.IsDBNull(11) ? null : reader.GetString(11),
                    resourceCount = reader.IsDBNull(12) ? 0 : Convert.ToInt32(reader.GetValue(12)),
                    resourceNames = reader.IsDBNull(13) ? null : reader.GetString(13),
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
                SELECT h.*,
                       bp.payload->>'name' as supplier_name, bp.partner_code as supplier_code,
                       j.juchuu_no,
                       jbp.payload->>'name' as client_name
                FROM {table} h
                LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id
                LEFT JOIN stf_juchuu j ON h.juchuu_id = j.id
                LEFT JOIN businesspartners jbp ON j.client_partner_id = jbp.id
                WHERE h.id = $1 AND h.company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            var headerObj = BuildDetailObject(reader, blobService);
            await reader.CloseAsync();

            // 明細取得
            await using var detailCmd = conn.CreateCommand();
            detailCmd.CommandText = $@"
                SELECT d.id, d.resource_id, d.cost_rate, d.cost_rate_type,
                       d.settlement_type, d.settlement_lower_h, d.settlement_upper_h,
                       d.notes, d.sort_order,
                       COALESCE(d.resource_name, r.payload->>'display_name') as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       r.resource_type
                FROM {detailTable} d
                LEFT JOIN stf_resources r ON d.resource_id = r.id
                WHERE d.hatchuu_id = $1
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
                    costRate = dr.IsDBNull(2) ? (decimal?)null : dr.GetDecimal(2),
                    costRateType = dr.IsDBNull(3) ? null : dr.GetString(3),
                    settlementType = dr.IsDBNull(4) ? null : dr.GetString(4),
                    settlementLowerH = dr.IsDBNull(5) ? (decimal?)null : dr.GetDecimal(5),
                    settlementUpperH = dr.IsDBNull(6) ? (decimal?)null : dr.GetDecimal(6),
                    notes = dr.IsDBNull(7) ? null : dr.GetString(7),
                    sortOrder = dr.IsDBNull(8) ? 0 : dr.GetInt32(8),
                    resourceName = dr.IsDBNull(9) ? null : dr.GetString(9),
                    resourceCode = dr.IsDBNull(10) ? null : dr.GetString(10),
                    resourceType = dr.IsDBNull(11) ? null : dr.GetString(11),
                });
            }

            var headerDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(headerObj))!;
            return Results.Json(new
            {
                id = headerDict.GetValueOrDefault("id"),
                hatchuuNo = headerDict.GetValueOrDefault("hatchuuNo"),
                juchuuId = headerDict.GetValueOrDefault("juchuuId"),
                juchuuNo = headerDict.GetValueOrDefault("juchuuNo"),
                contractType = headerDict.GetValueOrDefault("contractType"),
                status = headerDict.GetValueOrDefault("status"),
                startDate = headerDict.GetValueOrDefault("startDate"),
                endDate = headerDict.GetValueOrDefault("endDate"),
                supplierPartnerId = headerDict.GetValueOrDefault("supplierPartnerId"),
                supplierName = headerDict.GetValueOrDefault("supplierName"),
                clientName = headerDict.GetValueOrDefault("clientName"),
                workLocation = headerDict.GetValueOrDefault("workLocation"),
                workDays = headerDict.GetValueOrDefault("workDays"),
                workStartTime = headerDict.GetValueOrDefault("workStartTime"),
                workEndTime = headerDict.GetValueOrDefault("workEndTime"),
                monthlyWorkHours = headerDict.GetValueOrDefault("monthlyWorkHours"),
                docBlobName = headerDict.GetValueOrDefault("docBlobName"),
                docUrl = headerDict.GetValueOrDefault("docUrl"),
                notes = headerDict.GetValueOrDefault("notes"),
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
            seqCmd.CommandText = "SELECT nextval('seq_hatchuu_no')";
            var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
            var hatchuuNo = $"HT-{DateTime.Today:yyyy}-{seq:D4}";

            // ヘッダー挿入
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                INSERT INTO {table} (
                    company_code, hatchuu_no, juchuu_id, supplier_partner_id,
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
            cmd.Parameters.AddWithValue(hatchuuNo);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "juchuuId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "supplierPartnerId") ?? (object)DBNull.Value);
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
                            company_code, hatchuu_id, resource_id, resource_name,
                            cost_rate, cost_rate_type,
                            settlement_type, settlement_lower_h, settlement_upper_h,
                            notes, sort_order
                        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
                    dc.Parameters.AddWithValue(cc.ToString());
                    dc.Parameters.AddWithValue(newId);
                    dc.Parameters.AddWithValue(TryParseGuid(detail, "resourceId") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "resourceName") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "costRate") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "costRateType") ?? "monthly");
                    dc.Parameters.AddWithValue(GetString(detail, "settlementType") ?? "range");
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementLowerH") ?? (object)140m);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "settlementUpperH") ?? (object)180m);
                    dc.Parameters.AddWithValue(GetString(detail, "notes") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(sortOrder++);
                    await dc.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
            return Results.Ok(new { id = newId, hatchuuNo });
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
                    juchuu_id           = COALESCE($3, juchuu_id),
                    supplier_partner_id = COALESCE($4, supplier_partner_id),
                    contract_type       = COALESCE($5, contract_type),
                    status              = COALESCE($6, status),
                    start_date          = COALESCE($7::date, start_date),
                    end_date            = COALESCE($8::date, end_date),
                    work_location       = COALESCE($9, work_location),
                    work_days           = COALESCE($10, work_days),
                    work_start_time     = COALESCE($11, work_start_time),
                    work_end_time       = COALESCE($12, work_end_time),
                    monthly_work_hours  = COALESCE($13, monthly_work_hours),
                    notes               = COALESCE($14, notes),
                    updated_at          = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(TryParseGuid(body, "juchuuId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "supplierPartnerId") ?? (object)DBNull.Value);
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
                delCmd.CommandText = $"DELETE FROM {detailTable} WHERE hatchuu_id = $1";
                delCmd.Parameters.AddWithValue(id);
                await delCmd.ExecuteNonQueryAsync();

                var sortOrder = 0;
                foreach (var detail in detailsEl.EnumerateArray())
                {
                    await using var dc = conn.CreateCommand();
                    dc.Transaction = tx;
                    dc.CommandText = $@"
                        INSERT INTO {detailTable} (
                            company_code, hatchuu_id, resource_id, resource_name,
                            cost_rate, cost_rate_type,
                            settlement_type, settlement_lower_h, settlement_upper_h,
                            notes, sort_order
                        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
                    dc.Parameters.AddWithValue(cc.ToString());
                    dc.Parameters.AddWithValue(id);
                    dc.Parameters.AddWithValue(TryParseGuid(detail, "resourceId") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "resourceName") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetDecimal(detail, "costRate") ?? (object)DBNull.Value);
                    dc.Parameters.AddWithValue(GetString(detail, "costRateType") ?? "monthly");
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

        // ===== 発注書PDF生成 =====
        group.MapPost("/{id:guid}/generate-pdf", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService, IConfiguration config) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // ヘッダー取得
            await using var queryCmd = conn.CreateCommand();
            queryCmd.CommandText = $@"
                SELECT h.*,
                       bp.payload->>'name' as supplier_name,
                       bp.partner_code as supplier_code,
                       j.juchuu_no,
                       jbp.payload->>'name' as client_name,
                       comp.payload->>'name' as company_name,
                       comp.payload->>'address' as company_address
                FROM {table} h
                LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id
                LEFT JOIN stf_juchuu j ON h.juchuu_id = j.id
                LEFT JOIN businesspartners jbp ON j.client_partner_id = jbp.id
                LEFT JOIN companies comp ON comp.company_code = h.company_code
                WHERE h.id = $1 AND h.company_code = $2";
            queryCmd.Parameters.AddWithValue(id);
            queryCmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await queryCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();

            static string? S(System.Data.Common.DbDataReader r, string col) {
                try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? null : r.GetString(ord); } catch { return null; }
            }
            static decimal? D(System.Data.Common.DbDataReader r, string col) {
                try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? (decimal?)null : r.GetDecimal(ord); } catch { return null; }
            }

            var headerData = new
            {
                HatchuuNo    = S(reader, "hatchuu_no"),
                IssuedDate   = DateTime.Today.ToString("yyyy年MM月dd日"),
                CompanyName  = S(reader, "company_name") ?? cc.ToString(),
                CompanyAddr  = S(reader, "company_address"),
                SupplierName = S(reader, "supplier_name"),
                ContractType = ContractTypeLabel(S(reader, "contract_type")),
                StartDate    = reader.IsDBNull(reader.GetOrdinal("start_date")) ? null : reader.GetDateTime(reader.GetOrdinal("start_date")).ToString("yyyy年MM月dd日"),
                EndDate      = reader.IsDBNull(reader.GetOrdinal("end_date")) ? null : reader.GetDateTime(reader.GetOrdinal("end_date")).ToString("yyyy年MM月dd日"),
                WorkLocation = S(reader, "work_location"),
                WorkDays     = S(reader, "work_days"),
                WorkStart    = S(reader, "work_start_time"),
                WorkEnd      = S(reader, "work_end_time"),
                MonthlyHours = D(reader, "monthly_work_hours"),
                JuchuuNo     = S(reader, "juchuu_no"),
                ClientName   = S(reader, "client_name"),
                Notes        = S(reader, "notes"),
            };
            await reader.CloseAsync();

            // 明細取得
            await using var detailCmd = conn.CreateCommand();
            detailCmd.CommandText = $@"
                SELECT COALESCE(d.resource_name, r.payload->>'display_name') as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       d.cost_rate, d.cost_rate_type,
                       d.settlement_type, d.settlement_lower_h, d.settlement_upper_h
                FROM {detailTable} d
                LEFT JOIN stf_resources r ON d.resource_id = r.id
                WHERE d.hatchuu_id = $1
                ORDER BY d.sort_order";
            detailCmd.Parameters.AddWithValue(id);

            var resourceRows = new List<(string? name, string? code, decimal? rate, string? rateType, string? settlType, decimal? lower, decimal? upper)>();
            await using var dr = await detailCmd.ExecuteReaderAsync();
            while (await dr.ReadAsync())
            {
                resourceRows.Add((
                    dr.IsDBNull(0) ? null : dr.GetString(0),
                    dr.IsDBNull(1) ? null : dr.GetString(1),
                    dr.IsDBNull(2) ? (decimal?)null : dr.GetDecimal(2),
                    dr.IsDBNull(3) ? null : dr.GetString(3),
                    dr.IsDBNull(4) ? null : dr.GetString(4),
                    dr.IsDBNull(5) ? (decimal?)null : dr.GetDecimal(5),
                    dr.IsDBNull(6) ? (decimal?)null : dr.GetDecimal(6)
                ));
            }

            var html = BuildHatchuuPdfHtml(headerData, resourceRows);
            var htmlBytes = Encoding.UTF8.GetBytes(html);

            var blobName = $"hatchuu/{cc}/{id}/{DateTime.UtcNow:yyyyMMddHHmmss}_hatchuu.html";
            using var htmlStream = new MemoryStream(htmlBytes);
            await blobService.UploadAsync(htmlStream, blobName, "text/html", CancellationToken.None);
            var docUrl = blobService.GetReadUri(blobName);

            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = $"UPDATE {table} SET doc_generated_url = $3, doc_generated_at = now(), updated_at = now() WHERE id = $1 AND company_code = $2";
            updateCmd.Parameters.AddWithValue(id);
            updateCmd.Parameters.AddWithValue(cc.ToString());
            updateCmd.Parameters.AddWithValue(blobName);
            await updateCmd.ExecuteNonQueryAsync();

            return Results.Json(new { blobName, docUrl, htmlContent = html, generated = true });
        });

        // ===== 発注書HTMLダウンロード =====
        group.MapGet("/{id:guid}/doc-html", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT doc_generated_url FROM {table} WHERE id = $1 AND company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            var blobName = await cmd.ExecuteScalarAsync() as string;
            if (string.IsNullOrWhiteSpace(blobName)) return Results.NotFound();

            var url = blobService.GetReadUri(blobName);
            return Results.Ok(new { url });
        });
    }

    private static string BuildHatchuuPdfHtml(dynamic d, List<(string? name, string? code, decimal? rate, string? rateType, string? settlType, decimal? lower, decimal? upper)> resources)
    {
        var resourceRows = new StringBuilder();
        foreach (var r in resources)
        {
            var settlRow = r.settlType == "range"
                ? $"幅精算（{r.lower}H〜{r.upper}H）"
                : "固定";
            resourceRows.Append($@"
            <tr>
              <td>{r.code} {r.name}</td>
              <td class='highlight'>¥{r.rate?.ToString("N0") ?? "−"} / {RateTypeLabel(r.rateType)}</td>
              <td>{settlRow}</td>
            </tr>");
        }

        return $@"<!DOCTYPE html>
<html lang='ja'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>業務委託発注書 {d.HatchuuNo}</title>
<style>
  @page {{ size: A4; margin: 20mm; }}
  body {{ font-family: 'Noto Sans JP', 'Hiragino Kaku Gothic Pro', sans-serif; font-size: 11pt; color: #222; }}
  h1 {{ text-align: center; font-size: 18pt; margin-bottom: 4px; border-bottom: 2px solid #333; padding-bottom: 8px; }}
  .doc-no {{ text-align: right; font-size: 10pt; color: #666; margin-bottom: 20px; }}
  .section {{ margin-bottom: 20px; }}
  .section-title {{ font-size: 12pt; font-weight: bold; background: #f0f4ff; padding: 4px 8px; border-left: 4px solid #4472c4; margin-bottom: 8px; }}
  table {{ width: 100%; border-collapse: collapse; }}
  td, th {{ padding: 6px 10px; border: 1px solid #ccc; vertical-align: top; }}
  td:first-child {{ background: #f8f8f8; font-weight: bold; width: 35%; }}
  th {{ background: #e8eeff; font-weight: bold; }}
  .footer {{ margin-top: 40px; display: flex; justify-content: space-between; }}
  .sign-box {{ border: 1px solid #ccc; width: 200px; padding: 10px; text-align: center; min-height: 60px; }}
  .highlight {{ color: #1a56db; font-weight: bold; font-size: 13pt; }}
  @media print {{ .no-print {{ display: none; }} }}
</style>
</head>
<body>
<div class='no-print' style='background:#fff3cd;padding:10px;margin-bottom:20px;border-radius:4px;'>
  ⚠️ このページを印刷または「PDFとして保存」でPDFを作成してください
  <button onclick='window.print()' style='margin-left:20px;padding:6px 16px;background:#1a56db;color:white;border:none;border-radius:4px;cursor:pointer;'>🖨️ 印刷/PDF保存</button>
</div>

<h1>業務委託発注書</h1>
<div class='doc-no'>発注番号：{d.HatchuuNo}　発行日：{d.IssuedDate}</div>

<div class='section'>
  <table>
    <tr><td>発注者</td><td><strong>{d.CompanyName}</strong>{(d.CompanyAddr != null ? $"<br><small>{d.CompanyAddr}</small>" : "")}</td></tr>
    <tr><td>受注者（宛先）</td><td><strong>{d.SupplierName ?? "（未設定）"}</strong></td></tr>
    <tr><td>受注番号（参照）</td><td>{d.JuchuuNo} {(d.ClientName != null ? $"（顧客：{d.ClientName}）" : "")}</td></tr>
  </table>
</div>

<div class='section'>
  <div class='section-title'>契約内容</div>
  <table>
    <tr><td>契約形態</td><td>{d.ContractType}</td></tr>
    <tr><td>業務開始日</td><td>{d.StartDate ?? "未定"}</td></tr>
    <tr><td>業務終了日</td><td>{d.EndDate ?? "未定"}</td></tr>
  </table>
</div>

<div class='section'>
  <div class='section-title'>勤務条件</div>
  <table>
    <tr><td>勤務地</td><td>{d.WorkLocation ?? "−"}</td></tr>
    <tr><td>勤務曜日</td><td>{d.WorkDays ?? "−"}</td></tr>
    <tr><td>勤務時間</td><td>{d.WorkStart ?? "−"} 〜 {d.WorkEnd ?? "−"}</td></tr>
    <tr><td>月間基準時間</td><td>{d.MonthlyHours?.ToString() ?? "160"}H</td></tr>
  </table>
</div>

<div class='section'>
  <div class='section-title'>要員・原価条件</div>
  <table>
    <tr>
      <th style='background:#e8eeff;'>要員名</th>
      <th style='background:#e8eeff;'>支払単価</th>
      <th style='background:#e8eeff;'>精算方式</th>
    </tr>
    {resourceRows}
  </table>
</div>

{(d.Notes != null ? $@"<div class='section'>
  <div class='section-title'>特記事項</div>
  <table><tr><td colspan='2'>{d.Notes}</td></tr></table>
</div>" : "")}

<div class='footer'>
  <div class='sign-box'>発注者　印<br><br></div>
  <div class='sign-box'>受注者　印<br><br></div>
</div>
</body>
</html>";
    }

    private static string ContractTypeLabel(string? type) => type switch
    {
        "ses" => "SES（準委任）",
        "dispatch" => "派遣",
        "contract" => "請負",
        _ => type ?? "−"
    };

    private static string RateTypeLabel(string? type) => type switch
    {
        "monthly" => "月額",
        "daily" => "日額",
        "hourly" => "時給",
        _ => type ?? "月額"
    };

    private static object BuildDetailObject(System.Data.Common.DbDataReader reader, AzureBlobService blobService)
    {
        static string? S(System.Data.Common.DbDataReader r, string col) {
            try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? null : r.GetString(ord); } catch { return null; }
        }
        static decimal? D(System.Data.Common.DbDataReader r, string col) {
            try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? (decimal?)null : r.GetDecimal(ord); } catch { return null; }
        }
        static Guid? G(System.Data.Common.DbDataReader r, string col) {
            try { var ord = r.GetOrdinal(col); return r.IsDBNull(ord) ? (Guid?)null : r.GetGuid(ord); } catch { return null; }
        }

        var docBlobName = S(reader, "doc_generated_url");
        string? docUrl = null;
        if (!string.IsNullOrWhiteSpace(docBlobName))
            try { docUrl = blobService.GetReadUri(docBlobName); } catch { }

        return new
        {
            id = G(reader, "id"),
            hatchuuNo = S(reader, "hatchuu_no"),
            juchuuId = G(reader, "juchuu_id")?.ToString(),
            juchuuNo = S(reader, "juchuu_no"),
            contractType = S(reader, "contract_type"),
            status = S(reader, "status"),
            startDate = reader.IsDBNull(reader.GetOrdinal("start_date")) ? null : reader.GetDateTime(reader.GetOrdinal("start_date")).ToString("yyyy-MM-dd"),
            endDate = reader.IsDBNull(reader.GetOrdinal("end_date")) ? null : reader.GetDateTime(reader.GetOrdinal("end_date")).ToString("yyyy-MM-dd"),
            supplierPartnerId = G(reader, "supplier_partner_id")?.ToString(),
            supplierName = S(reader, "supplier_name"),
            clientName = S(reader, "client_name"),
            workLocation = S(reader, "work_location"),
            workDays = S(reader, "work_days"),
            workStartTime = S(reader, "work_start_time"),
            workEndTime = S(reader, "work_end_time"),
            monthlyWorkHours = D(reader, "monthly_work_hours"),
            docBlobName,
            docUrl,
            notes = S(reader, "notes"),
        };
    }

    private static Guid? TryParseGuid(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : (Guid?)null;
    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? GetDecimal(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : (decimal?)null;
}

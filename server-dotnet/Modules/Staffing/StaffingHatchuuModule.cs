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
            if (!string.IsNullOrWhiteSpace(resourceId) && Guid.TryParse(resourceId, out var rid)) { where.Add($"h.resource_id = ${idx++}"); args.Add(rid); }
            if (!string.IsNullOrWhiteSpace(keyword)) { where.Add($"(h.hatchuu_no ILIKE ${idx} OR r.payload->>'display_name' ILIKE ${idx})"); args.Add($"%{keyword}%"); idx++; }

            var whereClause = string.Join(" AND ", where);

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {table} h LEFT JOIN stf_resources r ON h.resource_id = r.id WHERE {whereClause}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT h.id, h.hatchuu_no, h.juchuu_id, h.contract_type, h.status,
                       h.start_date, h.end_date, h.cost_rate, h.cost_rate_type,
                       h.doc_generated_url, h.doc_generated_at,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       bp.payload->>'name' as supplier_name,
                       j.juchuu_no,
                       jbp.payload->>'name' as client_name
                FROM {table} h
                LEFT JOIN stf_resources r ON h.resource_id = r.id
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
                    costRate = reader.IsDBNull(7) ? (decimal?)null : reader.GetDecimal(7),
                    costRateType = reader.IsDBNull(8) ? null : reader.GetString(8),
                    hasPdf = !reader.IsDBNull(9),
                    docGeneratedAt = reader.IsDBNull(10) ? null : (DateTime?)reader.GetDateTime(10),
                    resourceName = reader.IsDBNull(11) ? null : reader.GetString(11),
                    resourceCode = reader.IsDBNull(12) ? null : reader.GetString(12),
                    supplierName = reader.IsDBNull(13) ? null : reader.GetString(13),
                    juchuuNo = reader.IsDBNull(14) ? null : reader.GetString(14),
                    clientName = reader.IsDBNull(15) ? null : reader.GetString(15),
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
                SELECT h.*,
                       r.payload->>'display_name' as resource_name, r.payload->>'resource_code' as resource_code,
                       bp.payload->>'name' as supplier_name, bp.partner_code as supplier_code,
                       j.juchuu_no, j.billing_rate, j.billing_rate_type,
                       jbp.payload->>'name' as client_name
                FROM {table} h
                LEFT JOIN stf_resources r ON h.resource_id = r.id
                LEFT JOIN businesspartners bp ON h.supplier_partner_id = bp.id
                LEFT JOIN stf_juchuu j ON h.juchuu_id = j.id
                LEFT JOIN businesspartners jbp ON j.client_partner_id = jbp.id
                WHERE h.id = $1 AND h.company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            return Results.Json(BuildDetailObject(reader, blobService));
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
            seqCmd.CommandText = "SELECT nextval('seq_hatchuu_no')";
            var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
            var hatchuuNo = $"HT-{DateTime.Today:yyyy}-{seq:D4}";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {table} (
                    company_code, hatchuu_no, juchuu_id, resource_id, supplier_partner_id,
                    contract_type, status, start_date, end_date,
                    cost_rate, cost_rate_type,
                    settlement_type, settlement_lower_h, settlement_upper_h,
                    work_location, work_days, work_start_time, work_end_time, monthly_work_hours,
                    notes, payload
                ) VALUES (
                    $1, $2, $3, $4, $5,
                    $6, $7, $8::date, $9::date,
                    $10, $11,
                    $12, $13, $14,
                    $15, $16, $17, $18, $19,
                    $20, $21::jsonb
                ) RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(hatchuuNo);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "juchuuId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "resourceId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "supplierPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? "ses");
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? "active");
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "costRate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "costRateType") ?? "monthly");
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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {table} SET
                    juchuu_id           = COALESCE($3, juchuu_id),
                    resource_id         = COALESCE($4, resource_id),
                    supplier_partner_id = COALESCE($5, supplier_partner_id),
                    contract_type       = COALESCE($6, contract_type),
                    status              = COALESCE($7, status),
                    start_date          = COALESCE($8::date, start_date),
                    end_date            = COALESCE($9::date, end_date),
                    cost_rate           = COALESCE($10, cost_rate),
                    cost_rate_type      = COALESCE($11, cost_rate_type),
                    settlement_type     = COALESCE($12, settlement_type),
                    settlement_lower_h  = COALESCE($13, settlement_lower_h),
                    settlement_upper_h  = COALESCE($14, settlement_upper_h),
                    work_location       = COALESCE($15, work_location),
                    work_days           = COALESCE($16, work_days),
                    work_start_time     = COALESCE($17, work_start_time),
                    work_end_time       = COALESCE($18, work_end_time),
                    monthly_work_hours  = COALESCE($19, monthly_work_hours),
                    notes               = COALESCE($20, notes),
                    updated_at          = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(TryParseGuid(body, "juchuuId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "resourceId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(TryParseGuid(body, "supplierPartnerId") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "contractType") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "status") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "startDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "endDate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetDecimal(body, "costRate") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(GetString(body, "costRateType") ?? (object)DBNull.Value);
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

        // ===== 発注書PDF生成 =====
        group.MapPost("/{id:guid}/generate-pdf", async (Guid id, HttpRequest req, NpgsqlDataSource ds, AzureBlobService blobService, IConfiguration config) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var queryCmd = conn.CreateCommand();
            queryCmd.CommandText = $@"
                SELECT h.*,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code,
                       r.email as resource_email,
                       bp.payload->>'name' as supplier_name,
                       bp.partner_code as supplier_code,
                       j.juchuu_no, j.billing_rate,
                       jbp.payload->>'name' as client_name,
                       comp.payload->>'name' as company_name,
                       comp.payload->>'address' as company_address
                FROM {table} h
                LEFT JOIN stf_resources r ON h.resource_id = r.id
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

            var data = new
            {
                HatchuuNo    = S(reader, "hatchuu_no"),
                IssuedDate   = DateTime.Today.ToString("yyyy年MM月dd日"),
                CompanyName  = S(reader, "company_name") ?? cc.ToString(),
                CompanyAddr  = S(reader, "company_address"),
                SupplierName = S(reader, "supplier_name"),
                ResourceName = S(reader, "resource_name"),
                ResourceCode = S(reader, "resource_code"),
                ContractType = ContractTypeLabel(S(reader, "contract_type")),
                StartDate    = reader.IsDBNull(reader.GetOrdinal("start_date")) ? null : reader.GetDateTime(reader.GetOrdinal("start_date")).ToString("yyyy年MM月dd日"),
                EndDate      = reader.IsDBNull(reader.GetOrdinal("end_date")) ? null : reader.GetDateTime(reader.GetOrdinal("end_date")).ToString("yyyy年MM月dd日"),
                CostRate     = D(reader, "cost_rate"),
                CostRateType = RateTypeLabel(S(reader, "cost_rate_type")),
                SettlementType  = S(reader, "settlement_type"),
                SettlementLowerH = D(reader, "settlement_lower_h"),
                SettlementUpperH = D(reader, "settlement_upper_h"),
                WorkLocation = S(reader, "work_location"),
                WorkDays     = S(reader, "work_days"),
                WorkStart    = S(reader, "work_start_time"),
                WorkEnd      = S(reader, "work_end_time"),
                MonthlyHours = D(reader, "monthly_work_hours"),
                JuchuuNo     = S(reader, "juchuu_no"),
                ClientName   = S(reader, "client_name"),
                Notes        = S(reader, "notes"),
            };
            reader.Close();

            // HTMLテンプレートでPDF内容を生成
            var html = BuildHatchuuPdfHtml(data);
            var htmlBytes = Encoding.UTF8.GetBytes(html);

            // HTMLをBlobに保存（PDF生成はフロントエンドのブラウザ印刷で対応）
            var blobName = $"hatchuu/{cc}/{id}/{DateTime.UtcNow:yyyyMMddHHmmss}_hatchuu.html";
            using var htmlStream = new MemoryStream(htmlBytes);
            await blobService.UploadAsync(htmlStream, blobName, "text/html", CancellationToken.None);
            var docUrl = blobService.GetReadUri(blobName);

            // URLをDBに保存
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = $"UPDATE {table} SET doc_generated_url = $3, doc_generated_at = now(), updated_at = now() WHERE id = $1 AND company_code = $2";
            updateCmd.Parameters.AddWithValue(id);
            updateCmd.Parameters.AddWithValue(cc.ToString());
            updateCmd.Parameters.AddWithValue(blobName);
            await updateCmd.ExecuteNonQueryAsync();

            // HTMLコンテンツをそのまま返してフロントエンドで印刷させる
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

    private static string BuildHatchuuPdfHtml(dynamic d)
    {
        var settlementRow = d.SettlementType == "range"
            ? $"精算方式：幅精算（{d.SettlementLowerH}H〜{d.SettlementUpperH}H）"
            : "精算方式：固定";

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
  td {{ padding: 6px 10px; border: 1px solid #ccc; vertical-align: top; }}
  td:first-child {{ background: #f8f8f8; font-weight: bold; width: 35%; }}
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
    <tr><td>受注者（宛先）</td><td><strong>{d.SupplierName ?? d.ResourceName ?? "（未設定）"}</strong></td></tr>
    <tr><td>担当リソース</td><td>{d.ResourceCode} {d.ResourceName}</td></tr>
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
  <div class='section-title'>原価条件（支払条件）</div>
  <table>
    <tr><td>支払単価</td><td class='highlight'>¥{d.CostRate?.ToString("N0") ?? "−"} / {d.CostRateType}</td></tr>
    <tr><td>精算方式</td><td>{settlementRow}</td></tr>
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
            resourceId = G(reader, "resource_id")?.ToString(),
            resourceName = S(reader, "resource_name"),
            resourceCode = S(reader, "resource_code"),
            supplierPartnerId = G(reader, "supplier_partner_id")?.ToString(),
            supplierName = S(reader, "supplier_name"),
            clientName = S(reader, "client_name"),
            costRate = D(reader, "cost_rate"),
            costRateType = S(reader, "cost_rate_type"),
            settlementType = S(reader, "settlement_type"),
            settlementLowerH = D(reader, "settlement_lower_h"),
            settlementUpperH = D(reader, "settlement_upper_h"),
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

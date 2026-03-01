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
/// 勤怠サマリーモジュール - 契約別月次勤怠集計
/// </summary>
public class StaffingTimesheetModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_timesheet",
        Name = "勤怠連携",
        Description = "契約別の月次勤怠集計・精算計算",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_contract", "hr_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_timesheet_summary", Label = "menu.staffingTimesheet", Icon = "Clock", Path = "/staffing/timesheets", ParentId = "menu_staffing", Order = 257 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        // 勤怠サマリー一覧取得
        app.MapGet("/staffing/timesheets", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var query = req.Query;
            var yearMonth = query["yearMonth"].FirstOrDefault();
            var status = query["status"].FirstOrDefault();
            var contractId = query["contractId"].FirstOrDefault();

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            var cTable = Crud.TableFor("staffing_contract");
            var rTable = Crud.TableFor("resource");
            const string jTable = "stf_juchuu";
            const string jdTable = "stf_juchuu_detail";
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var sql = $@"
                SELECT ts.id, ts.contract_id, ts.resource_id, ts.year_month,
                       ts.scheduled_hours, ts.actual_hours, ts.overtime_hours,
                       fn_jsonb_numeric(ts.payload, 'billable_hours') as billable_hours,
                       fn_jsonb_numeric(ts.payload, 'settlement_hours') as settlement_hours,
                       fn_jsonb_numeric(ts.payload, 'deduction_hours') as deduction_hours,
                       fn_jsonb_numeric(ts.payload, 'excess_hours') as excess_hours,
                       fn_jsonb_numeric(ts.payload, 'base_amount') as base_amount,
                       fn_jsonb_numeric(ts.payload, 'overtime_amount') as overtime_amount,
                       fn_jsonb_numeric(ts.payload, 'adjustment_amount') as adjustment_amount,
                       ts.total_billing_amount, ts.total_cost_amount,
                       ts.status,
                       fn_jsonb_timestamptz(ts.payload, 'confirmed_at') as confirmed_at,
                       fn_jsonb_uuid(ts.payload, 'juchuu_detail_id') as juchuu_detail_id,
                       COALESCE(c.contract_no, ts.payload->>'juchuu_no') as contract_no,
                       COALESCE(c.billing_rate, fn_jsonb_numeric(ts.payload, 'billing_rate')) as billing_rate,
                       COALESCE(c.payload->>'billing_rate_type', ts.payload->>'billing_rate_type', 'monthly') as billing_rate_type,
                       COALESCE(c.payload->>'settlement_type', ts.payload->>'settlement_type', 'range') as settlement_type,
                       COALESCE(fn_jsonb_numeric(c.payload, 'settlement_lower_hours'), fn_jsonb_numeric(ts.payload, 'settlement_lower_hours')) as settlement_lower_hours,
                       COALESCE(fn_jsonb_numeric(c.payload, 'settlement_upper_hours'), fn_jsonb_numeric(ts.payload, 'settlement_upper_hours')) as settlement_upper_hours,
                       r.resource_code,
                       COALESCE(r.display_name, jd.resource_name, ts.payload->>'resource_name') as resource_name,
                       COALESCE(bp.payload->>'name', jbp.payload->>'name', ts.payload->>'client_name') as client_name,
                       COALESCE(ts.payload->>'resource_link_status', CASE WHEN ts.resource_id IS NULL THEN 'unresolved' ELSE 'resolved' END) as resource_link_status
                FROM {tsTable} ts
                LEFT JOIN {cTable} c ON ts.contract_id = c.id
                LEFT JOIN {rTable} r ON ts.resource_id = r.id
                LEFT JOIN businesspartners bp ON c.client_partner_id = bp.id
                LEFT JOIN {jdTable} jd ON fn_jsonb_uuid(ts.payload, 'juchuu_detail_id') = jd.id
                LEFT JOIN {jTable} j ON COALESCE(fn_jsonb_uuid(ts.payload, 'juchuu_id'), jd.juchuu_id) = j.id
                LEFT JOIN businesspartners jbp ON j.client_partner_id = jbp.id
                WHERE ts.company_code = $1";

            cmd.Parameters.AddWithValue(cc.ToString());
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(yearMonth))
            {
                sql += $" AND ts.year_month = ${idx}";
                cmd.Parameters.AddWithValue(yearMonth);
                idx++;
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                sql += $" AND ts.status = ${idx}";
                cmd.Parameters.AddWithValue(status);
                idx++;
            }

            if (!string.IsNullOrWhiteSpace(contractId) && Guid.TryParse(contractId, out var cid))
            {
                sql += $" AND ts.contract_id = ${idx}";
                cmd.Parameters.AddWithValue(cid);
                idx++;
            }

            sql += " ORDER BY ts.year_month DESC, r.display_name";
            cmd.CommandText = sql;

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    contractId = reader.IsDBNull(1) ? null : (Guid?)reader.GetGuid(1),
                    resourceId = reader.IsDBNull(2) ? null : (Guid?)reader.GetGuid(2),
                    yearMonth = reader.GetString(3),
                    scheduledHours = reader.IsDBNull(4) ? null : (decimal?)reader.GetDecimal(4),
                    actualHours = reader.IsDBNull(5) ? null : (decimal?)reader.GetDecimal(5),
                    overtimeHours = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                    billableHours = reader.IsDBNull(7) ? null : (decimal?)reader.GetDecimal(7),
                    settlementHours = reader.IsDBNull(8) ? null : (decimal?)reader.GetDecimal(8),
                    deductionHours = reader.IsDBNull(9) ? 0 : reader.GetDecimal(9),
                    excessHours = reader.IsDBNull(10) ? 0 : reader.GetDecimal(10),
                    baseAmount = reader.IsDBNull(11) ? null : (decimal?)reader.GetDecimal(11),
                    overtimeAmount = reader.IsDBNull(12) ? 0 : reader.GetDecimal(12),
                    adjustmentAmount = reader.IsDBNull(13) ? 0 : reader.GetDecimal(13),
                    totalBillingAmount = reader.IsDBNull(14) ? null : (decimal?)reader.GetDecimal(14),
                    totalCostAmount = reader.IsDBNull(15) ? null : (decimal?)reader.GetDecimal(15),
                    status = reader.GetString(16),
                    confirmedAt = reader.IsDBNull(17) ? null : (DateTime?)reader.GetDateTime(17),
                    juchuuDetailId = reader.IsDBNull(18) ? null : (Guid?)reader.GetGuid(18),
                    contractNo = reader.IsDBNull(19) ? null : reader.GetString(19),
                    billingRate = reader.IsDBNull(20) ? 0 : reader.GetDecimal(20),
                    billingRateType = reader.IsDBNull(21) ? "monthly" : reader.GetString(21),
                    settlementType = reader.IsDBNull(22) ? "range" : reader.GetString(22),
                    settlementLowerHours = reader.IsDBNull(23) ? null : (decimal?)reader.GetDecimal(23),
                    settlementUpperHours = reader.IsDBNull(24) ? null : (decimal?)reader.GetDecimal(24),
                    resourceCode = reader.IsDBNull(25) ? null : reader.GetString(25),
                    resourceName = reader.IsDBNull(26) ? null : reader.GetString(26),
                    clientName = reader.IsDBNull(27) ? null : reader.GetString(27),
                    resourceLinkStatus = reader.IsDBNull(28) ? "resolved" : reader.GetString(28)
                });
            }

            return Results.Ok(new { data = results, total = results.Count });
        }).RequireAuthorization();

        // 勤怠サマリー詳細取得
        app.MapGet("/staffing/timesheets/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT to_jsonb(ts) FROM {tsTable} ts WHERE id = $1 AND company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var json = await cmd.ExecuteScalarAsync() as string;
            if (json == null) return Results.NotFound();
            return Results.Text(json, "application/json");
        }).RequireAuthorization();

        // 月次勤怠サマリー自動生成（契約から）
        app.MapPost("/staffing/timesheets/generate", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
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

            // YYYY-MM形式チェック
            if (!System.Text.RegularExpressions.Regex.IsMatch(yearMonth, @"^\d{4}-\d{2}$"))
                return Results.BadRequest(new { error = "yearMonth must be YYYY-MM format" });

            var year = int.Parse(yearMonth.Substring(0, 4));
            var month = int.Parse(yearMonth.Substring(5, 2));
            var startDate = new DateTime(year, month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            await using var conn = await ds.OpenConnectionAsync();

            // 対象期間中に有効な受注明細を取得（staffing専用）
            await using var contractCmd = conn.CreateCommand();
            contractCmd.CommandText = @"
                SELECT d.id, d.resource_id, d.resource_name,
                       d.billing_rate, d.billing_rate_type,
                       d.settlement_type, d.settlement_lower_h, d.settlement_upper_h,
                       d.settlement_rate, d.deduction_rate,
                       j.id, j.juchuu_no, j.client_partner_id, j.monthly_work_hours
                FROM stf_juchuu_detail d
                JOIN stf_juchuu j ON d.juchuu_id = j.id
                WHERE d.company_code = $1
                  AND j.company_code = $1
                  AND j.status = 'active'
                  AND j.start_date <= $2
                  AND (j.end_date IS NULL OR j.end_date >= $3)
                ORDER BY j.juchuu_no, d.sort_order, d.created_at";
            contractCmd.Parameters.AddWithValue(cc.ToString());
            contractCmd.Parameters.AddWithValue(endDate);
            contractCmd.Parameters.AddWithValue(startDate);

            var generatedCount = 0;
            var skippedCount = 0;

            await using var contractReader = await contractCmd.ExecuteReaderAsync();
            var details = new List<(Guid detailId, Guid? resourceId, string? resourceName, decimal? billingRate, string? rateType,
                string? settlementType, decimal? lowerHours, decimal? upperHours, decimal? settlementRate, decimal? deductionRate,
                Guid juchuuId, string? juchuuNo, Guid? clientPartnerId, decimal? monthlyHours)>();

            while (await contractReader.ReadAsync())
            {
                details.Add((
                    contractReader.GetGuid(0),
                    contractReader.IsDBNull(1) ? null : contractReader.GetGuid(1),
                    contractReader.IsDBNull(2) ? null : contractReader.GetString(2),
                    contractReader.IsDBNull(3) ? null : contractReader.GetDecimal(3),
                    contractReader.IsDBNull(4) ? "monthly" : contractReader.GetString(4),
                    contractReader.IsDBNull(5) ? "range" : contractReader.GetString(5),
                    contractReader.IsDBNull(6) ? null : contractReader.GetDecimal(6),
                    contractReader.IsDBNull(7) ? null : contractReader.GetDecimal(7),
                    contractReader.IsDBNull(8) ? null : contractReader.GetDecimal(8),
                    contractReader.IsDBNull(9) ? null : contractReader.GetDecimal(9),
                    contractReader.GetGuid(10),
                    contractReader.IsDBNull(11) ? null : contractReader.GetString(11),
                    contractReader.IsDBNull(12) ? null : contractReader.GetGuid(12),
                    contractReader.IsDBNull(13) ? null : contractReader.GetDecimal(13)
                ));
            }
            await contractReader.CloseAsync();

            foreach (var d in details)
            {
                // 既存チェック
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = $@"SELECT id FROM {tsTable}
                                          WHERE company_code = $1
                                            AND year_month = $2
                                            AND fn_jsonb_uuid(payload, 'juchuu_detail_id') = $3
                                          LIMIT 1";
                checkCmd.Parameters.AddWithValue(cc.ToString());
                checkCmd.Parameters.AddWithValue(yearMonth);
                checkCmd.Parameters.AddWithValue(d.detailId);
                var existing = await checkCmd.ExecuteScalarAsync();
                if (existing != null)
                {
                    skippedCount++;
                    continue;
                }

                // デフォルト時間計算（月間所定時間 or 160時間）
                var scheduledHours = d.monthlyHours ?? 160m;
                var billingRate = d.billingRate ?? 0m;
                var rateType = string.IsNullOrWhiteSpace(d.rateType) ? "monthly" : d.rateType!;
                var baseAmount = rateType == "monthly" ? billingRate : billingRate * scheduledHours;
                var linkStatus = d.resourceId.HasValue ? "resolved" : "unresolved";

                var payload = new JsonObject
                {
                    ["contract_id"] = null,
                    ["resource_id"] = d.resourceId?.ToString(),
                    ["resource_name"] = d.resourceName,
                    ["resource_link_status"] = linkStatus,
                    ["juchuu_id"] = d.juchuuId.ToString(),
                    ["juchuu_no"] = d.juchuuNo,
                    ["juchuu_detail_id"] = d.detailId.ToString(),
                    ["client_partner_id"] = d.clientPartnerId?.ToString(),
                    ["year_month"] = yearMonth,
                    ["billing_rate"] = billingRate,
                    ["billing_rate_type"] = rateType,
                    ["settlement_type"] = d.settlementType ?? "range",
                    ["settlement_lower_hours"] = d.lowerHours,
                    ["settlement_upper_hours"] = d.upperHours,
                    ["settlement_rate"] = d.settlementRate,
                    ["deduction_rate"] = d.deductionRate,
                    ["monthly_work_hours"] = scheduledHours,
                    ["scheduled_hours"] = scheduledHours,
                    ["actual_hours"] = scheduledHours,
                    ["billable_hours"] = scheduledHours,
                    ["settlement_hours"] = scheduledHours,
                    ["base_amount"] = baseAmount,
                    ["overtime_amount"] = 0,
                    ["adjustment_amount"] = 0,
                    ["total_billing_amount"] = baseAmount,
                    ["total_cost_amount"] = null,
                    ["status"] = "open"
                };

                await using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = $"INSERT INTO {tsTable}(company_code, payload) VALUES ($1, $2::jsonb)";
                insertCmd.Parameters.AddWithValue(cc.ToString());
                insertCmd.Parameters.AddWithValue(payload.ToJsonString());
                await insertCmd.ExecuteNonQueryAsync();
                generatedCount++;
            }

            return Results.Ok(new { generated = generatedCount, skipped = skippedCount, yearMonth });
        }).RequireAuthorization();

        // 勤怠サマリー更新（時間入力）
        app.MapPut("/staffing/timesheets/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            var cTable = Crud.TableFor("staffing_contract");
            await using var conn = await ds.OpenConnectionAsync();

            // 計算情報取得（staffing契約があれば優先、無ければpayload）
            await using var contractCmd = conn.CreateCommand();
            contractCmd.CommandText = $@"
                SELECT COALESCE(c.billing_rate, fn_jsonb_numeric(ts.payload,'billing_rate'), 0) as billing_rate,
                       COALESCE(c.payload->>'billing_rate_type', ts.payload->>'billing_rate_type', 'monthly') as billing_rate_type,
                       COALESCE(c.cost_rate, fn_jsonb_numeric(ts.payload,'cost_rate'), 0) as cost_rate,
                       COALESCE(c.payload->>'settlement_type', ts.payload->>'settlement_type', 'range') as settlement_type,
                       COALESCE(fn_jsonb_numeric(c.payload,'settlement_lower_hours'), fn_jsonb_numeric(ts.payload,'settlement_lower_hours')) as settlement_lower_hours,
                       COALESCE(fn_jsonb_numeric(c.payload,'settlement_upper_hours'), fn_jsonb_numeric(ts.payload,'settlement_upper_hours')) as settlement_upper_hours,
                       COALESCE(fn_jsonb_numeric(ts.payload,'settlement_rate'), 0) as settlement_rate,
                       COALESCE(fn_jsonb_numeric(ts.payload,'deduction_rate'), 0) as deduction_rate,
                       COALESCE(fn_jsonb_numeric(c.payload,'monthly_work_hours'), fn_jsonb_numeric(ts.payload,'monthly_work_hours'), 160) as monthly_work_hours
                FROM {tsTable} ts
                LEFT JOIN {cTable} c ON ts.contract_id = c.id
                WHERE ts.id = $1 AND ts.company_code = $2";
            contractCmd.Parameters.AddWithValue(id);
            contractCmd.Parameters.AddWithValue(cc.ToString());

            decimal billingRate = 0, costRate = 0, monthlyHours = 160, settlementRate = 0, deductionRate = 0;
            string rateType = "monthly", settlementType = "range";
            decimal? lowerHours = null, upperHours = null;

            await using var cReader = await contractCmd.ExecuteReaderAsync();
            if (await cReader.ReadAsync())
            {
                billingRate = cReader.GetDecimal(0);
                rateType = cReader.IsDBNull(1) ? "monthly" : cReader.GetString(1);
                costRate = cReader.IsDBNull(2) ? 0 : cReader.GetDecimal(2);
                settlementType = cReader.IsDBNull(3) ? "range" : cReader.GetString(3);
                lowerHours = cReader.IsDBNull(4) ? null : cReader.GetDecimal(4);
                upperHours = cReader.IsDBNull(5) ? null : cReader.GetDecimal(5);
                settlementRate = cReader.IsDBNull(6) ? 0 : cReader.GetDecimal(6);
                deductionRate = cReader.IsDBNull(7) ? 0 : cReader.GetDecimal(7);
                monthlyHours = cReader.IsDBNull(8) ? 160 : cReader.GetDecimal(8);
            }
            else
            {
                return Results.NotFound();
            }
            await cReader.CloseAsync();

            // 入力値取得
            var scheduledHours = root.TryGetProperty("scheduledHours", out var sh) && sh.ValueKind == JsonValueKind.Number ? sh.GetDecimal() : monthlyHours;
            var actualHours = root.TryGetProperty("actualHours", out var ah) && ah.ValueKind == JsonValueKind.Number ? ah.GetDecimal() : scheduledHours;
            var overtimeHours = root.TryGetProperty("overtimeHours", out var oh) && oh.ValueKind == JsonValueKind.Number ? oh.GetDecimal() : 0;
            var adjustmentAmount = root.TryGetProperty("adjustmentAmount", out var adj) && adj.ValueKind == JsonValueKind.Number ? adj.GetDecimal() : 0;

            // 精算計算
            decimal billableHours = actualHours;
            decimal settlementHours = actualHours;
            decimal deductionHours = 0;
            decimal excessHours = 0;

            if (settlementType == "range" && lowerHours.HasValue && upperHours.HasValue)
            {
                if (actualHours < lowerHours.Value)
                {
                    deductionHours = lowerHours.Value - actualHours;
                    settlementHours = lowerHours.Value;
                }
                else if (actualHours > upperHours.Value)
                {
                    excessHours = actualHours - upperHours.Value;
                    settlementHours = upperHours.Value;
                }
            }

            // 金額計算
            decimal baseAmount;
            decimal overtimeAmount = 0;

            if (rateType == "monthly")
            {
                baseAmount = billingRate;
                // 幅精算の場合の控除/超過計算
                if (settlementType == "range" && lowerHours.HasValue && upperHours.HasValue && monthlyHours > 0)
                {
                    var hourlyRate = billingRate / monthlyHours;
                    var settleUnitRate = settlementRate > 0 ? settlementRate : hourlyRate;
                    var deducUnitRate = deductionRate > 0 ? deductionRate : hourlyRate;
                    if (deductionHours > 0)
                    {
                        baseAmount -= deducUnitRate * deductionHours;
                    }
                    if (excessHours > 0)
                    {
                        overtimeAmount = settleUnitRate * excessHours;
                    }
                }
                // 残業は別途計算
                if (overtimeHours > 0 && monthlyHours > 0)
                {
                    var hourlyRate = billingRate / monthlyHours;
                    var settleUnitRate = settlementRate > 0 ? settlementRate : hourlyRate;
                    overtimeAmount += settleUnitRate * overtimeHours;
                }
            }
            else
            {
                // 時給/日給の場合
                baseAmount = billingRate * settlementHours;
                var settleUnitRate = settlementRate > 0 ? settlementRate : billingRate;
                overtimeAmount = settleUnitRate * overtimeHours;
            }

            var totalBillingAmount = baseAmount + overtimeAmount + adjustmentAmount;
            var totalCostAmount = costRate > 0 
                ? (rateType == "monthly" ? costRate : costRate * actualHours)
                : (decimal?)null;

            // Load existing payload and update in-place (payload-as-source-of-truth)
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = $"SELECT payload FROM {tsTable} WHERE id=$1 AND company_code=$2";
            getCmd.Parameters.AddWithValue(id);
            getCmd.Parameters.AddWithValue(cc.ToString());
            var existing = (string?)await getCmd.ExecuteScalarAsync();
            if (string.IsNullOrWhiteSpace(existing)) return Results.NotFound();
            var obj = (JsonNode.Parse(existing) as JsonObject) ?? new JsonObject();

            obj["scheduled_hours"] = scheduledHours;
            obj["actual_hours"] = actualHours;
            obj["overtime_hours"] = overtimeHours;
            obj["billable_hours"] = billableHours;
            obj["settlement_hours"] = settlementHours;
            obj["deduction_hours"] = deductionHours;
            obj["excess_hours"] = excessHours;
            obj["base_amount"] = baseAmount;
            obj["overtime_amount"] = overtimeAmount;
            obj["adjustment_amount"] = adjustmentAmount;
            obj["total_billing_amount"] = totalBillingAmount;
            obj["total_cost_amount"] = totalCostAmount;
            if (root.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
                obj["notes"] = notesEl.GetString();

            await using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE {tsTable} SET payload=$3::jsonb, updated_at=now() WHERE id=$1 AND company_code=$2 RETURNING id";
            upd.Parameters.AddWithValue(id);
            upd.Parameters.AddWithValue(cc.ToString());
            upd.Parameters.AddWithValue(obj.ToJsonString());
            var result = await upd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound();
            return Results.Ok(new { id, updated = true, totalBillingAmount, totalCostAmount });
        }).RequireAuthorization();

        // 未解決リソースを主データにバインド（staffing専用）
        app.MapPost("/staffing/timesheets/{id:guid}/bind-resource", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var resourceId = TryParseGuid(root, "resourceId");
            if (!resourceId.HasValue) return Results.BadRequest(new { error = "resourceId is required" });

            await using var conn = await ds.OpenConnectionAsync();

            // resourceが存在するか検証
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = $"SELECT 1 FROM {Crud.TableFor("resource")} WHERE id=$1 AND company_code=$2 LIMIT 1";
                check.Parameters.AddWithValue(resourceId.Value);
                check.Parameters.AddWithValue(cc.ToString());
                var exists = await check.ExecuteScalarAsync();
                if (exists == null) return Results.BadRequest(new { error = "resource not found" });
            }

            // payloadのresource_id/resource_link_statusを更新
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {Crud.TableFor("staffing_timesheet_summary")}
                SET payload = payload
                    || jsonb_build_object(
                        'resource_id', $3::text,
                        'resource_link_status', 'resolved'
                    ),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId.Value.ToString());

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound();
            return Results.Ok(new { id, bound = true, resourceId });
        }).RequireAuthorization();

        // 勤怠サマリー確定
        app.MapPost("/staffing/timesheets/{id:guid}/confirm", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {tsTable}
                SET payload = payload
                    || jsonb_build_object('status','confirmed','confirmed_at',$3::text),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'open'
                RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("O"));

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return Results.NotFound(new { error = "Not found or already confirmed" });
            return Results.Ok(new { confirmed = true });
        }).RequireAuthorization();

        // 月次一括確定
        app.MapPost("/staffing/timesheets/confirm-all", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var yearMonth = root.TryGetProperty("yearMonth", out var ym) ? ym.GetString() : null;
            if (string.IsNullOrWhiteSpace(yearMonth))
                return Results.BadRequest(new { error = "yearMonth is required" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_timesheet_summary", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            var tsTable = Crud.TableFor("staffing_timesheet_summary");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {tsTable}
                SET payload = payload
                    || jsonb_build_object('status','confirmed','confirmed_at',$3::text),
                    updated_at = now()
                WHERE company_code = $1 AND year_month = $2 AND status = 'open'";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(yearMonth);
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToString("O"));

            var count = await cmd.ExecuteNonQueryAsync();
            return Results.Ok(new { confirmed = count, yearMonth });
        }).RequireAuthorization();
    }

    private static Guid? TryParseGuid(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : (Guid?)null;
}


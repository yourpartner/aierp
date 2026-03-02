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
                       c.contract_no, c.billing_rate,
                       COALESCE(c.payload->>'billing_rate_type','monthly') as billing_rate_type,
                       COALESCE(c.payload->>'settlement_type','range') as settlement_type,
                       fn_jsonb_numeric(c.payload, 'settlement_lower_hours') as settlement_lower_hours,
                       fn_jsonb_numeric(c.payload, 'settlement_upper_hours') as settlement_upper_hours,
                       r.resource_code, r.display_name as resource_name,
                       bp.payload->>'name' as client_name
                FROM {tsTable} ts
                JOIN {cTable} c ON ts.contract_id = c.id
                LEFT JOIN {rTable} r ON ts.resource_id = r.id
                LEFT JOIN businesspartners bp ON c.client_partner_id = bp.id
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
                    contractId = reader.GetGuid(1),
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
                    contractNo = reader.GetString(18),
                    billingRate = reader.GetDecimal(19),
                    billingRateType = reader.IsDBNull(20) ? "monthly" : reader.GetString(20),
                    settlementType = reader.IsDBNull(21) ? "range" : reader.GetString(21),
                    settlementLowerHours = reader.IsDBNull(22) ? null : (decimal?)reader.GetDecimal(22),
                    settlementUpperHours = reader.IsDBNull(23) ? null : (decimal?)reader.GetDecimal(23),
                    resourceCode = reader.IsDBNull(24) ? null : reader.GetString(24),
                    resourceName = reader.IsDBNull(25) ? null : reader.GetString(25),
                    clientName = reader.IsDBNull(26) ? null : reader.GetString(26)
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
            var cTable = Crud.TableFor("staffing_contract");
            await using var conn = await ds.OpenConnectionAsync();

            // 対象期間中に有効な契約を取得
            var contractsSql = $@"
                SELECT c.id, c.resource_id, c.billing_rate,
                       COALESCE(c.payload->>'billing_rate_type','monthly') as billing_rate_type,
                       c.cost_rate,
                       fn_jsonb_numeric(c.payload,'monthly_work_hours') as monthly_work_hours,
                       COALESCE(c.payload->>'settlement_type','range') as settlement_type,
                       fn_jsonb_numeric(c.payload,'settlement_lower_hours') as settlement_lower_hours,
                       fn_jsonb_numeric(c.payload,'settlement_upper_hours') as settlement_upper_hours,
                       COALESCE(fn_jsonb_numeric(c.payload,'overtime_rate_multiplier'), 1.25) as overtime_rate_multiplier
                FROM {cTable} c
                WHERE c.company_code = $1 
                  AND c.status = 'active'
                  AND c.start_date <= $2
                  AND (c.end_date IS NULL OR c.end_date >= $3)";

            await using var contractCmd = conn.CreateCommand();
            contractCmd.CommandText = contractsSql;
            contractCmd.Parameters.AddWithValue(cc.ToString());
            contractCmd.Parameters.AddWithValue(endDate);
            contractCmd.Parameters.AddWithValue(startDate);

            var generatedCount = 0;
            var skippedCount = 0;

            await using var contractReader = await contractCmd.ExecuteReaderAsync();
            var contracts = new List<(Guid id, Guid? resourceId, decimal billingRate, string rateType, decimal? costRate, 
                decimal? monthlyHours, string settlementType, decimal? lowerHours, decimal? upperHours, decimal overtimeMultiplier)>();

            while (await contractReader.ReadAsync())
            {
                contracts.Add((
                    contractReader.GetGuid(0),
                    contractReader.IsDBNull(1) ? null : contractReader.GetGuid(1),
                    contractReader.GetDecimal(2),
                    contractReader.IsDBNull(3) ? "monthly" : contractReader.GetString(3),
                    contractReader.IsDBNull(4) ? null : contractReader.GetDecimal(4),
                    contractReader.IsDBNull(5) ? null : contractReader.GetDecimal(5),
                    contractReader.IsDBNull(6) ? "range" : contractReader.GetString(6),
                    contractReader.IsDBNull(7) ? null : contractReader.GetDecimal(7),
                    contractReader.IsDBNull(8) ? null : contractReader.GetDecimal(8),
                    contractReader.IsDBNull(9) ? 1.25m : contractReader.GetDecimal(9)
                ));
            }
            await contractReader.CloseAsync();

            foreach (var contract in contracts)
            {
                // 既存チェック
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = $"SELECT id FROM {tsTable} WHERE company_code = $1 AND contract_id = $2 AND year_month = $3 LIMIT 1";
                checkCmd.Parameters.AddWithValue(cc.ToString());
                checkCmd.Parameters.AddWithValue(contract.id);
                checkCmd.Parameters.AddWithValue(yearMonth);
                var existing = await checkCmd.ExecuteScalarAsync();
                if (existing != null)
                {
                    skippedCount++;
                    continue;
                }

                // デフォルト時間計算（月間所定時間 or 160時間）
                var scheduledHours = contract.monthlyHours ?? 160m;
                var baseAmount = contract.rateType == "monthly" ? contract.billingRate : contract.billingRate * scheduledHours;
                var costAmount = contract.costRate.HasValue
                    ? (contract.rateType == "monthly" ? contract.costRate.Value : contract.costRate.Value * scheduledHours)
                    : (decimal?)null;

                var payload = new JsonObject
                {
                    ["contract_id"] = contract.id.ToString(),
                    ["resource_id"] = contract.resourceId?.ToString(),
                    ["year_month"] = yearMonth,
                    ["scheduled_hours"] = scheduledHours,
                    ["actual_hours"] = scheduledHours,
                    ["billable_hours"] = scheduledHours,
                    ["settlement_hours"] = scheduledHours,
                    ["base_amount"] = baseAmount,
                    ["overtime_amount"] = 0,
                    ["adjustment_amount"] = 0,
                    ["total_billing_amount"] = baseAmount,
                    ["total_cost_amount"] = costAmount,
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

            // 契約情報取得（精算計算用）
            await using var contractCmd = conn.CreateCommand();
            contractCmd.CommandText = $@"
                SELECT c.billing_rate,
                       COALESCE(c.payload->>'billing_rate_type','monthly') as billing_rate_type,
                       COALESCE(c.cost_rate, 0) as cost_rate,
                       COALESCE(c.payload->>'settlement_type','range') as settlement_type,
                       fn_jsonb_numeric(c.payload,'settlement_lower_hours') as settlement_lower_hours,
                       fn_jsonb_numeric(c.payload,'settlement_upper_hours') as settlement_upper_hours,
                       COALESCE(fn_jsonb_numeric(c.payload,'overtime_rate_multiplier'), 1.25) as overtime_rate_multiplier,
                       COALESCE(fn_jsonb_numeric(c.payload,'monthly_work_hours'), 160) as monthly_work_hours
                FROM {tsTable} ts
                JOIN {cTable} c ON ts.contract_id = c.id
                WHERE ts.id = $1 AND ts.company_code = $2";
            contractCmd.Parameters.AddWithValue(id);
            contractCmd.Parameters.AddWithValue(cc.ToString());

            decimal billingRate = 0, costRate = 0, overtimeMultiplier = 1.25m, monthlyHours = 160;
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
                overtimeMultiplier = cReader.IsDBNull(6) ? 1.25m : cReader.GetDecimal(6);
                monthlyHours = cReader.IsDBNull(7) ? 160 : cReader.GetDecimal(7);
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
                    if (deductionHours > 0)
                    {
                        baseAmount -= hourlyRate * deductionHours;
                    }
                    if (excessHours > 0)
                    {
                        overtimeAmount = hourlyRate * excessHours * overtimeMultiplier;
                    }
                }
                // 残業は別途計算
                if (overtimeHours > 0 && monthlyHours > 0)
                {
                    var hourlyRate = billingRate / monthlyHours;
                    overtimeAmount += hourlyRate * overtimeHours * overtimeMultiplier;
                }
            }
            else
            {
                // 時給/日給の場合
                baseAmount = billingRate * settlementHours;
                overtimeAmount = billingRate * overtimeHours * overtimeMultiplier;
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
}


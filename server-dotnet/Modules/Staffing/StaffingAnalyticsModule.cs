using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Staffing;

/// <summary>
/// 分析レポートモジュール - 稼働率・売上・利益分析
/// </summary>
public class StaffingAnalyticsModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_analytics",
        Name = "分析レポート",
        Description = "稼働率、売上、利益率などの分析レポート",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_contract", "staffing_billing" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_staffing_analytics", Label = "menu.staffingAnalytics", Icon = "DataAnalysis", Path = "/staffing/analytics", ParentId = "menu_staffing", Order = 259 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var resourceTable = Crud.TableFor("resource");
        var contractTable = Crud.TableFor("staffing_contract");
        var timesheetTable = Crud.TableFor("staffing_timesheet_summary");
        var invoiceTable = Crud.TableFor("staffing_invoice");
        var projectTable = Crud.TableFor("staffing_project");

        // ダッシュボード概要
        app.MapGet("/staffing/analytics/dashboard", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 基本統計
            var stats = new Dictionary<string, object>();

            // リソース数
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT 
                        COUNT(*) FILTER (WHERE status = 'active') as total_resources,
                        COUNT(*) FILTER (WHERE availability_status = 'assigned') as assigned_count,
                        COUNT(*) FILTER (WHERE availability_status = 'available') as available_count,
                        COUNT(*) FILTER (WHERE resource_type = 'employee') as employee_count,
                        COUNT(*) FILTER (WHERE resource_type = 'freelancer') as freelancer_count,
                        COUNT(*) FILTER (WHERE resource_type = 'bp') as bp_count
                    FROM {resourceTable}
                    WHERE company_code = $1";
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    stats["totalResources"] = reader.GetInt64(0);
                    stats["assignedCount"] = reader.GetInt64(1);
                    stats["availableCount"] = reader.GetInt64(2);
                    stats["employeeCount"] = reader.GetInt64(3);
                    stats["freelancerCount"] = reader.GetInt64(4);
                    stats["bpCount"] = reader.GetInt64(5);
                }
            }

            // 稼働率計算
            var totalResources = Convert.ToInt64(stats["totalResources"]);
            var assignedCount = Convert.ToInt64(stats["assignedCount"]);
            stats["utilizationRate"] = totalResources > 0 
                ? Math.Round((double)assignedCount / totalResources * 100, 1) 
                : 0;

            // 有効契約数
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT 
                        COUNT(*) as active_contracts,
                        COUNT(*) FILTER (WHERE payload->>'contract_type' = 'dispatch') as dispatch_count,
                        COUNT(*) FILTER (WHERE payload->>'contract_type' = 'ses') as ses_count,
                        COUNT(*) FILTER (WHERE payload->>'contract_type' = 'contract') as contract_count
                    FROM {contractTable}
                    WHERE company_code = $1 AND payload->>'status' = 'active'";
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    stats["activeContracts"] = reader.GetInt64(0);
                    stats["dispatchContracts"] = reader.GetInt64(1);
                    stats["sesContracts"] = reader.GetInt64(2);
                    stats["contractContracts"] = reader.GetInt64(3);
                }
            }

            // 今月の売上・原価
            var currentMonth = DateTime.Today.ToString("yyyy-MM");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT 
                        COALESCE(SUM(fn_jsonb_numeric(payload, 'total_billing_amount')), 0) as monthly_billing,
                        COALESCE(SUM(fn_jsonb_numeric(payload, 'total_cost_amount')), 0) as monthly_cost
                    FROM {timesheetTable}
                    WHERE company_code = $1 AND payload->>'year_month' = $2";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(currentMonth);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    stats["monthlyBilling"] = reader.GetDecimal(0);
                    stats["monthlyCost"] = reader.GetDecimal(1);
                    var billing = reader.GetDecimal(0);
                    var cost = reader.GetDecimal(1);
                    stats["monthlyProfit"] = billing - cost;
                    stats["monthlyProfitRate"] = billing > 0 
                        ? Math.Round((double)(billing - cost) / (double)billing * 100, 1) 
                        : 0;
                }
            }

            // 未入金請求額
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COALESCE(SUM(fn_jsonb_numeric(payload,'total_amount') - fn_jsonb_numeric(payload,'paid_amount')), 0)
                    FROM {invoiceTable}
                    WHERE company_code = $1 AND payload->>'status' NOT IN ('paid', 'cancelled')";
                cmd.Parameters.AddWithValue(cc.ToString());

                stats["unpaidAmount"] = await cmd.ExecuteScalarAsync() ?? 0m;
            }

            // オープン案件数
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT COUNT(*), COALESCE(SUM(fn_jsonb_numeric(payload,'headcount') - fn_jsonb_numeric(payload,'filled_count')), 0)
                    FROM {projectTable}
                    WHERE company_code = $1 AND payload->>'status' IN ('open', 'matching')";
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    stats["openProjects"] = reader.GetInt64(0);
                    stats["openPositions"] = reader.GetInt64(1);
                }
            }

            stats["currentMonth"] = currentMonth;

            return Results.Ok(stats);
        }).RequireAuthorization();

        // 月次売上推移
        app.MapGet("/staffing/analytics/monthly-revenue", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var months = int.TryParse(req.Query["months"].FirstOrDefault(), out var m) ? m : 12;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    payload->>'year_month' as year_month,
                    COALESCE(SUM(fn_jsonb_numeric(payload,'total_billing_amount')), 0) as billing,
                    COALESCE(SUM(fn_jsonb_numeric(payload,'total_cost_amount')), 0) as cost,
                    COUNT(DISTINCT payload->>'contract_id') as contract_count
                FROM {timesheetTable}
                WHERE company_code = $1 
                  AND payload->>'year_month' >= to_char(now() - interval '1 month' * $2, 'YYYY-MM')
                GROUP BY payload->>'year_month'
                ORDER BY payload->>'year_month'";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(months);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var billing = reader.GetDecimal(1);
                var cost = reader.GetDecimal(2);
                results.Add(new
                {
                    yearMonth = reader.GetString(0),
                    billing,
                    cost,
                    profit = billing - cost,
                    profitRate = billing > 0 ? Math.Round((double)(billing - cost) / (double)billing * 100, 1) : 0,
                    contractCount = reader.GetInt64(3)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 顧客別売上
        app.MapGet("/staffing/analytics/revenue-by-client", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var yearMonth = req.Query["yearMonth"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    c.payload->>'client_partner_id' as client_id,
                    bp.partner_code as client_code,
                    bp.payload->>'name' as client_name,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')), 0) as billing,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_cost_amount')), 0) as cost,
                    COUNT(DISTINCT ts.payload->>'contract_id') as contract_count,
                    COUNT(DISTINCT ts.payload->>'resource_id') as resource_count
                FROM {timesheetTable} ts
                JOIN {contractTable} c ON ts.payload->>'contract_id' = c.id::text
                LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                WHERE ts.company_code = $1 AND ts.payload->>'year_month' = $2
                GROUP BY c.payload->>'client_partner_id', bp.partner_code, bp.payload->>'name'
                ORDER BY SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')) DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(yearMonth);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var billing = reader.GetDecimal(3);
                var cost = reader.GetDecimal(4);
                results.Add(new
                {
                    clientId = reader.IsDBNull(0) ? (Guid?)null : Guid.Parse(reader.GetString(0)!),
                    clientCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    clientName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    billing,
                    cost,
                    profit = billing - cost,
                    profitRate = billing > 0 ? Math.Round((double)(billing - cost) / (double)billing * 100, 1) : 0,
                    contractCount = reader.GetInt64(5),
                    resourceCount = reader.GetInt64(6)
                });
            }

            return Results.Ok(new { data = results, yearMonth });
        }).RequireAuthorization();

        // リソース別稼働・売上
        app.MapGet("/staffing/analytics/revenue-by-resource", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var yearMonth = req.Query["yearMonth"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    ts.payload->>'resource_id' as resource_id,
                    r.payload->>'resource_code' as resource_code,
                    r.payload->>'display_name' as display_name,
                    r.payload->>'resource_type' as resource_type,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'actual_hours')), 0) as actual_hours,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'overtime_hours')), 0) as overtime_hours,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')), 0) as billing,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_cost_amount')), 0) as cost,
                    bp.payload->>'name' as client_name
                FROM {timesheetTable} ts
                JOIN {resourceTable} r ON ts.payload->>'resource_id' = r.id::text
                JOIN {contractTable} c ON ts.payload->>'contract_id' = c.id::text
                LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                WHERE ts.company_code = $1 AND ts.payload->>'year_month' = $2
                GROUP BY ts.payload->>'resource_id', r.payload->>'resource_code', r.payload->>'display_name', r.payload->>'resource_type', bp.payload->>'name'
                ORDER BY SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')) DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(yearMonth);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var billing = reader.GetDecimal(6);
                var cost = reader.GetDecimal(7);
                results.Add(new
                {
                    resourceId = reader.IsDBNull(0) ? (Guid?)null : Guid.Parse(reader.GetString(0)!),
                    resourceCode = reader.IsDBNull(1) ? null : reader.GetString(1),
                    displayName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    resourceType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    actualHours = reader.GetDecimal(4),
                    overtimeHours = reader.GetDecimal(5),
                    billing,
                    cost,
                    profit = billing - cost,
                    profitRate = billing > 0 ? Math.Round((double)(billing - cost) / (double)billing * 100, 1) : 0,
                    clientName = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }

            return Results.Ok(new { data = results, yearMonth });
        }).RequireAuthorization();

        // 稼働率推移
        app.MapGet("/staffing/analytics/utilization-trend", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var months = int.TryParse(req.Query["months"].FirstOrDefault(), out var m) ? m : 12;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            
            // 月別の稼働リソース数と総リソース数を計算
            cmd.CommandText = $@"
                WITH monthly_data AS (
                    SELECT 
                        ts.payload->>'year_month' as year_month,
                        COUNT(DISTINCT ts.payload->>'resource_id') as active_resources
                    FROM {timesheetTable} ts
                    WHERE ts.company_code = $1 
                      AND ts.payload->>'year_month' >= to_char(now() - interval '1 month' * $2, 'YYYY-MM')
                    GROUP BY ts.payload->>'year_month'
                ),
                total_resources AS (
                    SELECT COUNT(*) as total
                    FROM {resourceTable}
                    WHERE company_code = $1 AND status = 'active' AND resource_type != 'candidate'
                )
                SELECT 
                    md.year_month,
                    md.active_resources,
                    tr.total as total_resources
                FROM monthly_data md
                CROSS JOIN total_resources tr
                ORDER BY md.year_month";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(months);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var active = reader.GetInt64(1);
                var total = reader.GetInt64(2);
                results.Add(new
                {
                    yearMonth = reader.GetString(0),
                    activeResources = active,
                    totalResources = total,
                    utilizationRate = total > 0 ? Math.Round((double)active / total * 100, 1) : 0
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 契約終了予定
        app.MapGet("/staffing/analytics/expiring-contracts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var days = int.TryParse(req.Query["days"].FirstOrDefault(), out var d) ? d : 30;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    c.id,
                    c.payload,
                    r.payload as resource_payload,
                    bp.payload as client_payload,
                    (c.payload->>'end_date')::date - CURRENT_DATE as days_remaining
                FROM {contractTable} c
                LEFT JOIN {resourceTable} r ON c.payload->>'resource_id' = r.id::text
                LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                WHERE c.company_code = $1 
                  AND c.payload->>'status' = 'active'
                  AND c.payload->>'end_date' IS NOT NULL
                  AND (c.payload->>'end_date')::date <= CURRENT_DATE + $2
                ORDER BY (c.payload->>'end_date')::date";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(days);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var contractPayloadDoc = JsonDocument.Parse(reader.GetString(1));
                var contractPayload = contractPayloadDoc.RootElement;
                using var resourcePayloadDoc = JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
                var resourcePayload = resourcePayloadDoc.RootElement;
                using var clientPayloadDoc = JsonDocument.Parse(reader.IsDBNull(3) ? "{}" : reader.GetString(3));
                var clientPayload = clientPayloadDoc.RootElement;

                results.Add(new
                {
                    id = reader.GetGuid(0),
                    contractNo = contractPayload.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                    contractType = contractPayload.TryGetProperty("contract_type", out var ct) ? ct.GetString() : null,
                    endDate = contractPayload.TryGetProperty("end_date", out var ed) ? ed.GetDateTime() : (DateTime?)null,
                    daysRemaining = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    billingRate = contractPayload.TryGetProperty("billing_rate", out var br) ? br.GetDecimal() : (decimal?)null,
                    resourceCode = resourcePayload.TryGetProperty("resource_code", out var rcode) ? rcode.GetString() : null,
                    resourceName = resourcePayload.TryGetProperty("display_name", out var rname) ? rname.GetString() : null,
                    clientName = clientPayload.TryGetProperty("name", out var cname) ? cname.GetString() : null
                });
            }

            return Results.Ok(new { data = results, withinDays = days });
        }).RequireAuthorization();

        // 契約タイプ別統計
        app.MapGet("/staffing/analytics/contract-type-stats", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var yearMonth = req.Query["yearMonth"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    c.payload->>'contract_type' as contract_type,
                    COUNT(DISTINCT c.id) as contract_count,
                    COUNT(DISTINCT ts.payload->>'resource_id') as resource_count,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')), 0) as billing,
                    COALESCE(SUM(fn_jsonb_numeric(ts.payload,'total_cost_amount')), 0) as cost
                FROM {contractTable} c
                LEFT JOIN {timesheetTable} ts ON c.id::text = ts.payload->>'contract_id' AND ts.payload->>'year_month' = $2
                WHERE c.company_code = $1 AND c.payload->>'status' = 'active'
                GROUP BY c.payload->>'contract_type'
                ORDER BY SUM(fn_jsonb_numeric(ts.payload,'total_billing_amount')) DESC NULLS LAST";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(yearMonth);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var billing = reader.GetDecimal(3);
                var cost = reader.GetDecimal(4);
                results.Add(new
                {
                    contractType = reader.IsDBNull(0) ? null : reader.GetString(0),
                    contractCount = reader.GetInt64(1),
                    resourceCount = reader.GetInt64(2),
                    billing,
                    cost,
                    profit = billing - cost,
                    profitRate = billing > 0 ? Math.Round((double)(billing - cost) / (double)billing * 100, 1) : 0
                });
            }

            return Results.Ok(new { data = results, yearMonth });
        }).RequireAuthorization();
    }
}


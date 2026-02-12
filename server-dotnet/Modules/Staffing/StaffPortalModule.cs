using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;
using Server.Infrastructure.Skills;

namespace Server.Modules.Staffing;

/// <summary>
/// 员工自主门户模块 - 员工/个人事业主的自助服务
/// </summary>
public class StaffPortalModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_portal",
        Name = "員工門戶",
        Description = "员工自主门户：工时填写、工资查看、证明书申请；个人事业主：注文书、请求书、入金確認",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "hr_core", "staffing_contract" },
        Menus = new[]
        {
            // 员工门户菜单（独立入口，非管理后台）
            new MenuConfig { Id = "menu_portal", Label = "menu.portal", Icon = "User", Path = "/portal", Order = 900 },
            new MenuConfig { Id = "menu_portal_dashboard", Label = "menu.portalDashboard", Icon = "Odometer", Path = "/portal/dashboard", ParentId = "menu_portal", Order = 901 },
            new MenuConfig { Id = "menu_portal_timesheet", Label = "menu.portalTimesheet", Icon = "Calendar", Path = "/portal/timesheet", ParentId = "menu_portal", Order = 902 },
            new MenuConfig { Id = "menu_portal_payslip", Label = "menu.portalPayslip", Icon = "Money", Path = "/portal/payslip", ParentId = "menu_portal", Order = 903 },
            new MenuConfig { Id = "menu_portal_certificates", Label = "menu.portalCertificates", Icon = "Document", Path = "/portal/certificates", ParentId = "menu_portal", Order = 904 },
            // 个人事业主专属
            new MenuConfig { Id = "menu_portal_orders", Label = "menu.portalOrders", Icon = "Tickets", Path = "/portal/orders", ParentId = "menu_portal", Order = 910, Permission = "portal.freelancer" },
            new MenuConfig { Id = "menu_portal_invoices", Label = "menu.portalInvoices", Icon = "List", Path = "/portal/invoices", ParentId = "menu_portal", Order = 911, Permission = "portal.freelancer" },
            new MenuConfig { Id = "menu_portal_payments", Label = "menu.portalPayments", Icon = "Wallet", Path = "/portal/payments", ParentId = "menu_portal", Order = 912, Permission = "portal.freelancer" },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var resourceTable = Crud.TableFor("resource");
        var contractTable = Crud.TableFor("staffing_contract");
        var timesheetTable = Crud.TableFor("staffing_timesheet_summary");
        var purchaseOrderTable = Crud.TableFor("staffing_purchase_order");
        var freelancerInvoiceTable = Crud.TableFor("staffing_freelancer_invoice");

        // ========== 门户首页/仪表盘 ==========
        app.MapGet("/portal/dashboard", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            // 从JWT获取员工ID/资源ID（员工场景通常只有 employee_id，需要反查 resource）
            var employeeIdStr = req.HttpContext.User.FindFirst("employee_id")?.Value;
            var resourceIdStr = req.HttpContext.User.FindFirst("resource_id")?.Value;
            
            if (string.IsNullOrEmpty(employeeIdStr) && string.IsNullOrEmpty(resourceIdStr))
                return Results.BadRequest(new { error = "No employee or resource linked to this user" });

            await using var conn = await ds.OpenConnectionAsync();
            var dashboard = new Dictionary<string, object?>();

            // 统一得到 resourceId（员工通过 employee_id 反查）
            Guid? resourceId = null;
            if (!string.IsNullOrEmpty(resourceIdStr) && Guid.TryParse(resourceIdStr, out var rid)) resourceId = rid;

            if (resourceId == null && !string.IsNullOrEmpty(employeeIdStr) && Guid.TryParse(employeeIdStr, out var eid))
            {
                await using var cmdFind = conn.CreateCommand();
                cmdFind.CommandText = $@"SELECT id FROM {resourceTable} WHERE company_code = $1 AND employee_id = $2 LIMIT 1";
                cmdFind.Parameters.AddWithValue(cc.ToString());
                cmdFind.Parameters.AddWithValue(eid);
                var found = await cmdFind.ExecuteScalarAsync();
                if (found is Guid g) resourceId = g;
            }

            if (resourceId == null)
                return Results.BadRequest(new { error = "No resource linked to this user (employee may not be registered in resource pool)" });

            // 获取资源信息
            {
                await using var cmdRes = conn.CreateCommand();
                cmdRes.CommandText = $@"SELECT payload FROM {resourceTable} WHERE id = $1 AND company_code = $2";
                cmdRes.Parameters.AddWithValue(resourceId.Value);
                cmdRes.Parameters.AddWithValue(cc.ToString());

                var json = await cmdRes.ExecuteScalarAsync() as string;
                if (!string.IsNullOrEmpty(json))
                {
                    using var payloadDoc = JsonDocument.Parse(json);
                    var p = payloadDoc.RootElement;
                    dashboard["resourceId"] = resourceId;
                    dashboard["resourceCode"] = p.TryGetProperty("resource_code", out var rc) ? rc.GetString() : null;
                    dashboard["displayName"] = p.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                    dashboard["resourceType"] = p.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null;
                    dashboard["availabilityStatus"] = p.TryGetProperty("availability_status", out var av) ? av.GetString() : null;
                }
            }

            // 当前有效契约
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT c.id, c.payload, bp.payload->>'name' as client_name
                    FROM {contractTable} c
                    LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                    WHERE c.company_code = $1 AND c.payload->>'resource_id' = $2 AND c.payload->>'status' = 'active'
                    ORDER BY c.payload->>'start_date' DESC
                    LIMIT 5";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(resourceId.Value.ToString());

                var contracts = new List<object>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    using var payloadDoc = JsonDocument.Parse(reader.GetString(1));
                    var p = payloadDoc.RootElement;
                    contracts.Add(new
                    {
                        id = reader.GetGuid(0),
                        contractNo = p.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                        contractType = p.TryGetProperty("contract_type", out var ct) ? ct.GetString() : null,
                        startDate = p.TryGetProperty("start_date", out var sd) && sd.ValueKind == JsonValueKind.String ? sd.GetString() : null,
                        endDate = p.TryGetProperty("end_date", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString() : null,
                        clientName = reader.IsDBNull(2) ? null : reader.GetString(2)
                    });
                }
                dashboard["activeContracts"] = contracts;
            }

            // 本月工时状态
            var currentMonth = DateTime.Today.ToString("yyyy-MM");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT payload->>'year_month', fn_jsonb_numeric(payload,'actual_hours'), payload->>'status'
                    FROM {timesheetTable}
                    WHERE company_code = $1 AND payload->>'resource_id' = $2 AND payload->>'year_month' = $3";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(resourceId.Value.ToString());
                cmd.Parameters.AddWithValue(currentMonth);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    dashboard["currentMonthHours"] = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                    dashboard["currentMonthStatus"] = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
                }
                else
                {
                    dashboard["currentMonthHours"] = 0;
                    dashboard["currentMonthStatus"] = "not_submitted";
                }
            }

            // 待处理事项
            var pendingItems = new List<object>();
            
            // 检查是否有待签收的注文书（个人事业主）
            if (dashboard.TryGetValue("resourceType", out var resourceTypeVal) && resourceTypeVal?.ToString() == "freelancer")
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT COUNT(*) FROM {purchaseOrderTable}
                    WHERE company_code = $1 AND payload->>'resource_id' = $2 AND payload->>'status' = 'sent'";
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(resourceId.Value.ToString());
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    pendingItems.Add(new { type = "order_pending", count, message = $"{count}件の注文書が届いています" });
                }
            }

            dashboard["pendingItems"] = pendingItems;
            dashboard["currentMonth"] = currentMonth;

            return Results.Ok(dashboard);
        }).RequireAuthorization();

        // ========== 工时管理 ==========
        // 获取本人的工时记录
        app.MapGet("/portal/timesheets", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            cmd.CommandText = $@"
                SELECT ts.id, ts.payload, c.payload as contract_payload, bp.payload->>'name' as client_name
                FROM {timesheetTable} ts
                LEFT JOIN {contractTable} c ON ts.payload->>'contract_id' = c.id::text
                LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                WHERE ts.company_code = $1 AND ts.payload->>'resource_id' = $2 AND ts.payload->>'year_month' LIKE $3
                ORDER BY ts.payload->>'year_month' DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId.Value.ToString());
            cmd.Parameters.AddWithValue($"{year}-%");

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var tsDoc = JsonDocument.Parse(reader.GetString(1));
                var ts = tsDoc.RootElement;
                using var cDoc = JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
                var c = cDoc.RootElement;

                results.Add(new
                {
                    id = reader.GetGuid(0),
                    yearMonth = ts.TryGetProperty("year_month", out var ym) ? ym.GetString() : null,
                    scheduledHours = ts.TryGetProperty("scheduled_hours", out var sh) && sh.ValueKind == JsonValueKind.Number ? sh.GetDecimal() : 0m,
                    actualHours = ts.TryGetProperty("actual_hours", out var ah) && ah.ValueKind == JsonValueKind.Number ? ah.GetDecimal() : 0m,
                    overtimeHours = ts.TryGetProperty("overtime_hours", out var oh) && oh.ValueKind == JsonValueKind.Number ? oh.GetDecimal() : 0m,
                    status = ts.TryGetProperty("status", out var st) ? st.GetString() : null,
                    submittedAt = ts.TryGetProperty("submitted_at", out var sa) ? sa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    confirmedAt = ts.TryGetProperty("confirmed_at", out var ca) ? ca.GetDateTimeOffset() : (DateTimeOffset?)null,
                    contractNo = c.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                    clientName = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 填写/更新工时
        app.MapPost("/portal/timesheets", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var body = await req.ReadFromJsonAsync<JsonElement>();
            var yearMonth = body.GetProperty("yearMonth").GetString()!;
            var contractId = body.TryGetProperty("contractId", out var cid) && cid.TryGetGuid(out var cidVal) ? cidVal : (Guid?)null;
            if (contractId == null) return Results.BadRequest(new { error = "contractId is required" });

            await using var conn = await ds.OpenConnectionAsync();

            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            var scheduledHours = body.TryGetProperty("scheduledHours", out var sh) && sh.ValueKind == JsonValueKind.Number ? sh.GetDecimal() : 0m;
            var actualHours = body.TryGetProperty("actualHours", out var ah) && ah.ValueKind == JsonValueKind.Number ? ah.GetDecimal() : 0m;
            var overtimeHours = body.TryGetProperty("overtimeHours", out var oh) && oh.ValueKind == JsonValueKind.Number ? oh.GetDecimal() : 0m;

            var payload = JsonSerializer.Serialize(new
            {
                contract_id = contractId.Value,
                resource_id = resourceId.Value,
                year_month = yearMonth,
                scheduled_hours = scheduledHours,
                actual_hours = actualHours,
                overtime_hours = overtimeHours,
                status = "open"
            });

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {timesheetTable} (company_code, payload)
                VALUES ($1, $2::jsonb)
                ON CONFLICT (company_code, contract_id, year_month)
                DO UPDATE SET
                    payload = {timesheetTable}.payload || EXCLUDED.payload || jsonb_build_object('updated_at', now()),
                    updated_at = now()
                RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payload);

            var id = await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id, message = "Saved" });
        }).RequireAuthorization();

        // 提交工时审批
        app.MapPost("/portal/timesheets/{id:guid}/submit", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {timesheetTable}
                SET payload = payload || jsonb_build_object('status','submitted','submitted_at',now()), updated_at = now()
                WHERE id = $1 AND company_code = $2 AND payload->>'status' = 'open'";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            
            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                return Results.BadRequest(new { error = "Cannot submit - already submitted or not found" });

            return Results.Ok(new { message = "Submitted for approval" });
        }).RequireAuthorization();

        // ========== 工资明细 ==========
        app.MapGet("/portal/payslips", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, pay_period, gross_salary, total_deductions, net_salary, 
                       status, paid_at, payload
                FROM payroll_results
                WHERE company_code = $1 AND employee_id = $2 AND pay_period LIKE $3
                ORDER BY pay_period DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));
            cmd.Parameters.AddWithValue($"{year}-%");

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    payPeriod = reader.GetString(1),
                    grossSalary = reader.GetDecimal(2),
                    totalDeductions = reader.GetDecimal(3),
                    netSalary = reader.GetDecimal(4),
                    status = reader.GetString(5),
                    paidAt = reader.IsDBNull(6) ? null : (DateTime?)reader.GetDateTime(6),
                    details = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 获取单个工资明细
        app.MapGet("/portal/payslips/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, pay_period, gross_salary, total_deductions, net_salary, 
                       status, paid_at, payload
                FROM payroll_results
                WHERE id = $1 AND company_code = $2 AND employee_id = $3";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return Results.NotFound(new { error = "Payslip not found" });

            return Results.Ok(new
            {
                id = reader.GetGuid(0),
                payPeriod = reader.GetString(1),
                grossSalary = reader.GetDecimal(2),
                totalDeductions = reader.GetDecimal(3),
                netSalary = reader.GetDecimal(4),
                status = reader.GetString(5),
                paidAt = reader.IsDBNull(6) ? null : (DateTime?)reader.GetDateTime(6),
                details = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }).RequireAuthorization();

        // ========== 证明书申请 ==========
        app.MapGet("/portal/certificates", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, request_type, status, requested_at, completed_at, document_url, notes
                FROM certificate_requests
                WHERE company_code = $1 AND employee_id = $2
                ORDER BY requested_at DESC
                LIMIT 50";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    requestType = reader.GetString(1),
                    status = reader.GetString(2),
                    requestedAt = reader.GetDateTime(3),
                    completedAt = reader.IsDBNull(4) ? null : (DateTime?)reader.GetDateTime(4),
                    documentUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                    notes = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        app.MapPost("/portal/certificates", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            var body = await req.ReadFromJsonAsync<JsonElement>();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO certificate_requests 
                (company_code, employee_id, request_type, purpose, notes, status, requested_at)
                VALUES ($1, $2, $3, $4, $5, 'pending', now())
                RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));
            cmd.Parameters.AddWithValue(body.GetProperty("requestType").GetString()!);
            cmd.Parameters.AddWithValue(body.TryGetProperty("purpose", out var p) ? p.GetString()! : (object)DBNull.Value);
            cmd.Parameters.AddWithValue(body.TryGetProperty("notes", out var n) ? n.GetString()! : (object)DBNull.Value);

            var id = await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id, message = "Certificate request submitted" });
        }).RequireAuthorization();

        // ========== 个人事业主：注文书 ==========
        app.MapGet("/portal/orders", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var resourceId = req.HttpContext.User.FindFirst("resource_id")?.Value;
            if (string.IsNullOrEmpty(resourceId))
                return Results.BadRequest(new { error = "No resource linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT po.id, po.payload, c.payload as contract_payload, bp.payload->>'name' as client_name
                FROM {purchaseOrderTable} po
                LEFT JOIN {contractTable} c ON po.payload->>'contract_id' = c.id::text
                LEFT JOIN businesspartners bp ON c.payload->>'client_partner_id' = bp.id::text
                WHERE po.company_code = $1 AND po.payload->>'resource_id' = $2
                ORDER BY po.payload->>'order_date' DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var poDoc = JsonDocument.Parse(reader.GetString(1));
                var po = poDoc.RootElement;
                using var cDoc = JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
                var c = cDoc.RootElement;

                results.Add(new
                {
                    id = reader.GetGuid(0),
                    orderNo = po.TryGetProperty("order_no", out var on) ? on.GetString() : null,
                    contractId = po.TryGetProperty("contract_id", out var cid2) && cid2.ValueKind == JsonValueKind.String ? cid2.GetString() : null,
                    orderDate = po.TryGetProperty("order_date", out var od) ? od.GetString() : null,
                    periodStart = po.TryGetProperty("period_start", out var ps) ? ps.GetString() : null,
                    periodEnd = po.TryGetProperty("period_end", out var pe) ? pe.GetString() : null,
                    unitPrice = po.TryGetProperty("unit_price", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetDecimal() : (decimal?)null,
                    settlementType = po.TryGetProperty("settlement_type", out var st) ? st.GetString() : null,
                    status = po.TryGetProperty("status", out var s) ? s.GetString() : null,
                    acceptedAt = po.TryGetProperty("accepted_at", out var aa) ? aa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    contractNo = c.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                    clientName = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 签收注文书
        app.MapPost("/portal/orders/{id:guid}/accept", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var resourceId = req.HttpContext.User.FindFirst("resource_id")?.Value;
            if (string.IsNullOrEmpty(resourceId))
                return Results.BadRequest(new { error = "No resource linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {purchaseOrderTable}
                SET payload = payload || jsonb_build_object('status','accepted','accepted_at',now()), updated_at = now()
                WHERE id = $1 AND company_code = $2 AND payload->>'resource_id' = $3 AND payload->>'status' = 'sent'";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0)
                return Results.BadRequest(new { error = "Order not found or already accepted" });

            return Results.Ok(new { message = "Order accepted" });
        }).RequireAuthorization();

        // ========== 个人事业主：請求書 ==========
        app.MapGet("/portal/invoices", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var resourceId = req.HttpContext.User.FindFirst("resource_id")?.Value;
            if (string.IsNullOrEmpty(resourceId))
                return Results.BadRequest(new { error = "No resource linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, payload
                FROM {freelancerInvoiceTable}
                WHERE company_code = $1 AND payload->>'resource_id' = $2
                ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var fiDoc = JsonDocument.Parse(reader.GetString(1));
                var fi = fiDoc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    invoiceNo = fi.TryGetProperty("invoice_no", out var ino) ? ino.GetString() : null,
                    periodStart = fi.TryGetProperty("period_start", out var ps) ? ps.GetString() : null,
                    periodEnd = fi.TryGetProperty("period_end", out var pe) ? pe.GetString() : null,
                    subtotal = fi.TryGetProperty("subtotal", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetDecimal() : 0m,
                    taxAmount = fi.TryGetProperty("tax_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDecimal() : 0m,
                    totalAmount = fi.TryGetProperty("total_amount", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetDecimal() : 0m,
                    status = fi.TryGetProperty("status", out var s) ? s.GetString() : null,
                    submittedAt = fi.TryGetProperty("submitted_at", out var sa) ? sa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    approvedAt = fi.TryGetProperty("approved_at", out var aa) ? aa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    paidAt = fi.TryGetProperty("paid_at", out var pa) ? pa.GetDateTimeOffset() : (DateTimeOffset?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 提交请求书
        app.MapPost("/portal/invoices", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var resourceId = req.HttpContext.User.FindFirst("resource_id")?.Value;
            if (string.IsNullOrEmpty(resourceId))
                return Results.BadRequest(new { error = "No resource linked" });

            var body = await req.ReadFromJsonAsync<JsonElement>();

            await using var conn = await ds.OpenConnectionAsync();
            
            // 生成请求书编号
            await using var cmdSeq = conn.CreateCommand();
            cmdSeq.CommandText = "SELECT nextval('seq_freelancer_invoice')";
            var seq = Convert.ToInt64(await cmdSeq.ExecuteScalarAsync());
            var invoiceNo = $"FI-{DateTime.Today:yyyyMM}-{seq:D4}";
            
            var subtotal = body.GetProperty("subtotal").GetDecimal();
            var taxRate = body.TryGetProperty("taxRate", out var tr) ? tr.GetDecimal() : 0.10m;
            var taxAmount = subtotal * taxRate;

            var payload = JsonSerializer.Serialize(new
            {
                invoice_no = invoiceNo,
                resource_id = Guid.Parse(resourceId),
                period_start = body.GetProperty("periodStart").GetString(),
                period_end = body.GetProperty("periodEnd").GetString(),
                subtotal = subtotal,
                tax_rate = taxRate,
                tax_amount = taxAmount,
                total_amount = subtotal + taxAmount,
                status = "submitted",
                submitted_at = DateTimeOffset.UtcNow,
                timesheet_summary_id = body.TryGetProperty("timesheetSummaryId", out var tsid) && tsid.TryGetGuid(out var tsidVal) ? tsidVal : (Guid?)null,
                payload = body.TryGetProperty("payload", out var pl) ? pl : (JsonElement?)null
            });

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"INSERT INTO {freelancerInvoiceTable} (company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payload);

            var id = await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id, invoiceNo, message = "Invoice submitted" });
        }).RequireAuthorization();

        // ========== 个人事业主：入金確認 ==========
        app.MapGet("/portal/payments", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            var resourceId = req.HttpContext.User.FindFirst("resource_id")?.Value;
            if (string.IsNullOrEmpty(resourceId))
                return Results.BadRequest(new { error = "No resource linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT fi.id, fi.payload
                FROM {freelancerInvoiceTable} fi
                WHERE fi.company_code = $1 AND fi.payload->>'resource_id' = $2 AND fi.payload->>'status' IN ('approved', 'paid')
                ORDER BY fi.payload->>'period_end' DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var fiDoc = JsonDocument.Parse(reader.GetString(1));
                var fi = fiDoc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    invoiceNo = fi.TryGetProperty("invoice_no", out var ino) ? ino.GetString() : null,
                    periodStart = fi.TryGetProperty("period_start", out var ps) ? ps.GetString() : null,
                    periodEnd = fi.TryGetProperty("period_end", out var pe) ? pe.GetString() : null,
                    totalAmount = fi.TryGetProperty("total_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDecimal() : 0m,
                    status = fi.TryGetProperty("status", out var s) ? s.GetString() : null,
                    paidAt = fi.TryGetProperty("paid_at", out var pa) ? pa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    paidAmount = fi.TryGetProperty("paid_amount", out var pam) && pam.ValueKind == JsonValueKind.Number ? pam.GetDecimal() : (decimal?)null
                });
            }

            // 统计
            decimal totalPaid = 0, totalPending = 0;
            foreach (var item in results.Cast<dynamic>())
            {
                if (item.status == "paid")
                    totalPaid += (decimal)(item.paidAmount ?? item.totalAmount);
                else
                    totalPending += (decimal)item.totalAmount;
            }

            return Results.Ok(new { 
                data = results,
                summary = new { totalPaid, totalPending }
            });
        }).RequireAuthorization();

        // ========== Timesheet 每日工时明细（周视图） ==========

        // 获取某月的每日工时明细
        app.MapGet("/portal/timesheet-daily", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            var month = req.Query["month"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");
            var year = int.Parse(month.Split('-')[0]);
            var mon = int.Parse(month.Split('-')[1]);
            var startDate = new DateTime(year, mon, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, entry_date, start_time, end_time, break_minutes, 
                       regular_hours, overtime_hours, holiday_flag, source, notes
                FROM timesheet_daily_entries
                WHERE company_code = $1 AND resource_id = $2 
                  AND entry_date >= $3 AND entry_date <= $4
                ORDER BY entry_date";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId.Value);
            cmd.Parameters.AddWithValue(startDate);
            cmd.Parameters.AddWithValue(endDate);

            var entries = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new
                {
                    id = reader.GetGuid(0),
                    entryDate = reader.GetDateTime(1).ToString("yyyy-MM-dd"),
                    dayOfWeek = reader.GetDateTime(1).ToString("ddd"),
                    startTime = reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2).ToString(@"hh\:mm"),
                    endTime = reader.IsDBNull(3) ? null : reader.GetFieldValue<TimeSpan>(3).ToString(@"hh\:mm"),
                    breakMinutes = reader.GetInt32(4),
                    regularHours = reader.GetDecimal(5),
                    overtimeHours = reader.GetDecimal(6),
                    isHoliday = reader.GetBoolean(7),
                    source = reader.IsDBNull(8) ? "manual" : reader.GetString(8),
                    notes = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            // 汇总
            var totalRegular = entries.Sum(e => (decimal)((dynamic)e).regularHours);
            var totalOvertime = entries.Sum(e => (decimal)((dynamic)e).overtimeHours);

            return Results.Ok(new
            {
                month,
                data = entries,
                summary = new { totalRegular, totalOvertime, entryCount = entries.Count }
            });
        }).RequireAuthorization();

        // 保存每日工时明细（单日或批量）
        app.MapPost("/portal/timesheet-daily", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            var body = await req.ReadFromJsonAsync<JsonElement>();

            // 支持单条或批量
            var entries = new List<JsonElement>();
            if (body.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in body.EnumerateArray()) entries.Add(e);
            }
            else
            {
                entries.Add(body);
            }

            // 获取有效合约 ID
            Guid? contractId = null;
            if (entries.Count > 0 && entries[0].TryGetProperty("contractId", out var cidEl) && cidEl.TryGetGuid(out var cid))
            {
                contractId = cid;
            }
            else
            {
                // 查找有效合约
                await using var cmdContract = conn.CreateCommand();
                cmdContract.CommandText = @"
                    SELECT id FROM staffing_contracts 
                    WHERE company_code = $1 AND resource_id = $2 AND status = 'active'
                    ORDER BY start_date DESC LIMIT 1";
                cmdContract.Parameters.AddWithValue(cc.ToString());
                cmdContract.Parameters.AddWithValue(resourceId.Value);
                var found = await cmdContract.ExecuteScalarAsync();
                contractId = found is Guid g ? g : null;
            }

            var savedCount = 0;
            foreach (var entry in entries)
            {
                var dateStr = entry.GetProperty("entryDate").GetString()!;
                var date = DateTime.Parse(dateStr);
                var startTime = entry.TryGetProperty("startTime", out var st) && st.ValueKind == JsonValueKind.String
                    ? (object)TimeSpan.Parse(st.GetString()!) : DBNull.Value;
                var endTime = entry.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String
                    ? (object)TimeSpan.Parse(et.GetString()!) : DBNull.Value;
                var breakMins = entry.TryGetProperty("breakMinutes", out var bm) ? bm.GetInt32() : 60;
                var regularHours = entry.TryGetProperty("regularHours", out var rh) && rh.ValueKind == JsonValueKind.Number
                    ? rh.GetDecimal() : 0m;
                var overtimeHours = entry.TryGetProperty("overtimeHours", out var oh) && oh.ValueKind == JsonValueKind.Number
                    ? oh.GetDecimal() : 0m;
                var isHoliday = entry.TryGetProperty("isHoliday", out var ih) && ih.GetBoolean();
                var notes = entry.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null;

                // 如果有起止时间但没有工时数据，自动计算
                if (regularHours == 0 && startTime is TimeSpan ts && endTime is TimeSpan te)
                {
                    var workMinutes = (te - ts).TotalMinutes - breakMins;
                    regularHours = Math.Min((decimal)workMinutes / 60, 8);
                    overtimeHours = Math.Max((decimal)workMinutes / 60 - 8, 0);
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO timesheet_daily_entries 
                    (company_code, resource_id, contract_id, entry_date, start_time, end_time, 
                     break_minutes, regular_hours, overtime_hours, holiday_flag, source, notes)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'manual', $11)
                    ON CONFLICT (company_code, resource_id, entry_date, contract_id)
                    DO UPDATE SET 
                        start_time = EXCLUDED.start_time,
                        end_time = EXCLUDED.end_time,
                        break_minutes = EXCLUDED.break_minutes,
                        regular_hours = EXCLUDED.regular_hours,
                        overtime_hours = EXCLUDED.overtime_hours,
                        holiday_flag = EXCLUDED.holiday_flag,
                        notes = EXCLUDED.notes,
                        updated_at = now()";
                cmd.Parameters.AddWithValue(cc.ToString());                          // $1
                cmd.Parameters.AddWithValue(resourceId.Value);                        // $2
                cmd.Parameters.AddWithValue(contractId.HasValue ? (object)contractId.Value : DBNull.Value); // $3
                cmd.Parameters.AddWithValue(date);                                    // $4
                cmd.Parameters.AddWithValue(startTime);                               // $5
                cmd.Parameters.AddWithValue(endTime);                                 // $6
                cmd.Parameters.AddWithValue(breakMins);                               // $7
                cmd.Parameters.AddWithValue(regularHours);                            // $8
                cmd.Parameters.AddWithValue(overtimeHours);                           // $9
                cmd.Parameters.AddWithValue(isHoliday);                               // $10
                cmd.Parameters.AddWithValue(notes ?? (object)DBNull.Value);           // $11

                await cmd.ExecuteNonQueryAsync();
                savedCount++;
            }

            return Results.Ok(new { saved = savedCount, message = $"Saved {savedCount} entries" });
        }).RequireAuthorization();

        // 删除每日工时条目
        app.MapDelete("/portal/timesheet-daily/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM timesheet_daily_entries 
                WHERE id = $1 AND company_code = $2 AND resource_id = $3";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId.Value);

            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0
                ? Results.Ok(new { message = "Deleted" })
                : Results.NotFound(new { error = "Entry not found" });
        }).RequireAuthorization();

        // 从上月复制工时数据
        app.MapPost("/portal/timesheet-daily/copy-from-previous", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var targetMonth = body.TryGetProperty("targetMonth", out var tm) ? tm.GetString() : DateTime.Today.ToString("yyyy-MM");
            if (string.IsNullOrEmpty(targetMonth))
                return Results.BadRequest(new { error = "targetMonth is required" });

            // 计算上月
            var targetDate = DateTime.Parse(targetMonth + "-01");
            var prevDate = targetDate.AddMonths(-1);
            var prevStart = new DateTime(prevDate.Year, prevDate.Month, 1);
            var prevEnd = prevStart.AddMonths(1).AddDays(-1);

            // 读取上月数据
            await using var cmdPrev = conn.CreateCommand();
            cmdPrev.CommandText = @"
                SELECT entry_date, start_time, end_time, break_minutes, 
                       regular_hours, overtime_hours, holiday_flag, contract_id, notes
                FROM timesheet_daily_entries
                WHERE company_code = $1 AND resource_id = $2 
                  AND entry_date >= $3 AND entry_date <= $4";
            cmdPrev.Parameters.AddWithValue(cc.ToString());
            cmdPrev.Parameters.AddWithValue(resourceId.Value);
            cmdPrev.Parameters.AddWithValue(prevStart);
            cmdPrev.Parameters.AddWithValue(prevEnd);

            var prevEntries = new List<(int day, TimeSpan? start, TimeSpan? end, int breakMin, decimal regular, decimal overtime, bool holiday, Guid? contractId, string? notes)>();
            await using var reader = await cmdPrev.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                prevEntries.Add((
                    reader.GetDateTime(0).Day,
                    reader.IsDBNull(1) ? null : reader.GetFieldValue<TimeSpan>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2),
                    reader.GetInt32(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
                ));
            }

            if (prevEntries.Count == 0)
                return Results.BadRequest(new { error = "先月のデータがありません" });

            // 目标月的天数
            var targetDaysInMonth = DateTime.DaysInMonth(targetDate.Year, targetDate.Month);
            var copied = 0;

            foreach (var pe in prevEntries)
            {
                // 匹配相同日号（如上月15号 → 本月15号）
                if (pe.day > targetDaysInMonth) continue;

                var newDate = new DateTime(targetDate.Year, targetDate.Month, pe.day);
                // 检查目标日期是否同为工作日/休日（周几相同性不检查，简单复制）
                
                await using var cmdInsert = conn.CreateCommand();
                cmdInsert.CommandText = @"
                    INSERT INTO timesheet_daily_entries 
                    (company_code, resource_id, contract_id, entry_date, start_time, end_time, 
                     break_minutes, regular_hours, overtime_hours, holiday_flag, source, notes)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'copy', $11)
                    ON CONFLICT (company_code, resource_id, entry_date, contract_id)
                    DO NOTHING";
                cmdInsert.Parameters.AddWithValue(cc.ToString());
                cmdInsert.Parameters.AddWithValue(resourceId.Value);
                cmdInsert.Parameters.AddWithValue(pe.contractId.HasValue ? (object)pe.contractId.Value : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(newDate);
                cmdInsert.Parameters.AddWithValue(pe.start.HasValue ? (object)pe.start.Value : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(pe.end.HasValue ? (object)pe.end.Value : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(pe.breakMin);
                cmdInsert.Parameters.AddWithValue(pe.regular);
                cmdInsert.Parameters.AddWithValue(pe.overtime);
                cmdInsert.Parameters.AddWithValue(pe.holiday);
                cmdInsert.Parameters.AddWithValue(pe.notes ?? (object)DBNull.Value);

                var rows = await cmdInsert.ExecuteNonQueryAsync();
                if (rows > 0) copied++;
            }

            return Results.Ok(new { copied, message = $"Copied {copied} entries from {prevDate:yyyy-MM}" });
        }).RequireAuthorization();

        // ========== Timesheet 文件上传 + AI 解析 ==========

        app.MapPost("/portal/timesheet-upload", async (HttpRequest req, NpgsqlDataSource ds, TimesheetAiParser parser) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            if (!req.HasFormContentType || req.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            var file = req.Form.Files[0];
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

            // 读取文件内容
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var fileBytes = ms.ToArray();

            // 保存文件记录
            await using var cmdUpload = conn.CreateCommand();
            cmdUpload.CommandText = @"
                INSERT INTO timesheet_uploads 
                (company_code, resource_id, file_name, file_url, file_type, file_size, parse_status)
                VALUES ($1, $2, $3, $4, $5, $6, 'parsing')
                RETURNING id";
            cmdUpload.Parameters.AddWithValue(cc.ToString());
            cmdUpload.Parameters.AddWithValue(resourceId.Value);
            cmdUpload.Parameters.AddWithValue(file.FileName);
            cmdUpload.Parameters.AddWithValue($"/uploads/timesheets/{Guid.NewGuid()}{ext}");
            cmdUpload.Parameters.AddWithValue(ext.TrimStart('.'));
            cmdUpload.Parameters.AddWithValue((int)file.Length);

            var uploadId = (Guid)(await cmdUpload.ExecuteScalarAsync())!;

            // AI 解析
            TimesheetAiParser.ParseResult parseResult;
            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
            {
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                parseResult = await parser.ParseImageAsync(fileBytes, mimeType, req.HttpContext.RequestAborted);
            }
            else if (ext is ".csv")
            {
                var csvContent = Encoding.UTF8.GetString(fileBytes);
                parseResult = await parser.ParseCsvAsync(csvContent, req.HttpContext.RequestAborted);
            }
            else if (ext is ".xlsx" or ".xls")
            {
                // 简单处理：将 Excel 前几 KB 作为文本发送给 LLM
                // 实际生产中应使用 EPPlus / ClosedXML 解析
                var textContent = Encoding.UTF8.GetString(fileBytes.Take(Math.Min(fileBytes.Length, 8000)).ToArray());
                parseResult = await parser.ParseExcelTextAsync(textContent, req.HttpContext.RequestAborted);
            }
            else
            {
                parseResult = new TimesheetAiParser.ParseResult
                {
                    Success = false,
                    ErrorMessage = $"Unsupported file type: {ext}"
                };
            }

            // 更新解析结果
            await using var cmdUpdate = conn.CreateCommand();
            cmdUpdate.CommandText = @"
                UPDATE timesheet_uploads 
                SET parse_status = $2, 
                    parsed_data = $3::jsonb, 
                    parse_errors = $4::jsonb,
                    confidence = $5,
                    updated_at = now()
                WHERE id = $1";
            cmdUpdate.Parameters.AddWithValue(uploadId);
            cmdUpdate.Parameters.AddWithValue(parseResult.Success ? "parsed" : "failed");
            cmdUpdate.Parameters.AddWithValue(JsonSerializer.Serialize(parseResult.Entries));
            cmdUpdate.Parameters.AddWithValue(parseResult.Warnings.Count > 0
                ? JsonSerializer.Serialize(parseResult.Warnings)
                : "[]");
            cmdUpdate.Parameters.AddWithValue(parseResult.Confidence);

            await cmdUpdate.ExecuteNonQueryAsync();

            return Results.Ok(new
            {
                uploadId,
                success = parseResult.Success,
                entries = parseResult.Entries,
                confidence = parseResult.Confidence,
                warnings = parseResult.Warnings,
                summary = parseResult.Summary,
                error = parseResult.ErrorMessage
            });
        }).RequireAuthorization().DisableAntiforgery();

        // 确认并导入解析后的工时数据
        app.MapPost("/portal/timesheet-upload/{uploadId:guid}/apply", async (
            Guid uploadId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);
            if (resourceId == null) return Results.BadRequest(new { error = "No resource linked" });

            // 读取解析数据
            await using var cmdRead = conn.CreateCommand();
            cmdRead.CommandText = @"
                SELECT parsed_data, applied 
                FROM timesheet_uploads 
                WHERE id = $1 AND company_code = $2 AND resource_id = $3";
            cmdRead.Parameters.AddWithValue(uploadId);
            cmdRead.Parameters.AddWithValue(cc.ToString());
            cmdRead.Parameters.AddWithValue(resourceId.Value);

            await using var reader = await cmdRead.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return Results.NotFound(new { error = "Upload not found" });

            if (!reader.IsDBNull(1) && reader.GetBoolean(1))
                return Results.BadRequest(new { error = "Already applied" });

            var parsedJson = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
            await reader.CloseAsync();

            using var doc = JsonDocument.Parse(parsedJson);
            var savedCount = 0;

            // 获取合约
            Guid? contractId = null;
            await using var cmdContract = conn.CreateCommand();
            cmdContract.CommandText = @"
                SELECT id FROM staffing_contracts 
                WHERE company_code = $1 AND resource_id = $2 AND status = 'active'
                ORDER BY start_date DESC LIMIT 1";
            cmdContract.Parameters.AddWithValue(cc.ToString());
            cmdContract.Parameters.AddWithValue(resourceId.Value);
            var found = await cmdContract.ExecuteScalarAsync();
            contractId = found is Guid g ? g : null;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var dateStr = entry.GetProperty("date").GetString();
                if (string.IsNullOrEmpty(dateStr)) continue;
                var date = DateTime.Parse(dateStr);
                var startStr = entry.TryGetProperty("startTime", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
                var endStr = entry.TryGetProperty("endTime", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null;
                var breakMins = entry.TryGetProperty("breakMinutes", out var bm) ? bm.GetInt32() : 60;
                var regularHours = entry.TryGetProperty("regularHours", out var rh) && rh.ValueKind == JsonValueKind.Number ? rh.GetDecimal() : 0m;
                var overtimeHours = entry.TryGetProperty("overtimeHours", out var oh) && oh.ValueKind == JsonValueKind.Number ? oh.GetDecimal() : 0m;
                var isHoliday = entry.TryGetProperty("isHoliday", out var ih) && ih.GetBoolean();

                await using var cmdInsert = conn.CreateCommand();
                cmdInsert.CommandText = @"
                    INSERT INTO timesheet_daily_entries 
                    (company_code, resource_id, contract_id, entry_date, start_time, end_time, 
                     break_minutes, regular_hours, overtime_hours, holiday_flag, source, source_file_url)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'excel_upload', $11)
                    ON CONFLICT (company_code, resource_id, entry_date, contract_id)
                    DO UPDATE SET 
                        start_time = EXCLUDED.start_time,
                        end_time = EXCLUDED.end_time,
                        break_minutes = EXCLUDED.break_minutes,
                        regular_hours = EXCLUDED.regular_hours,
                        overtime_hours = EXCLUDED.overtime_hours,
                        holiday_flag = EXCLUDED.holiday_flag,
                        source = 'excel_upload',
                        updated_at = now()";
                cmdInsert.Parameters.AddWithValue(cc.ToString());
                cmdInsert.Parameters.AddWithValue(resourceId.Value);
                cmdInsert.Parameters.AddWithValue(contractId.HasValue ? (object)contractId.Value : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(date);
                cmdInsert.Parameters.AddWithValue(!string.IsNullOrEmpty(startStr) ? (object)TimeSpan.Parse(startStr) : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(!string.IsNullOrEmpty(endStr) ? (object)TimeSpan.Parse(endStr) : DBNull.Value);
                cmdInsert.Parameters.AddWithValue(breakMins);
                cmdInsert.Parameters.AddWithValue(regularHours);
                cmdInsert.Parameters.AddWithValue(overtimeHours);
                cmdInsert.Parameters.AddWithValue(isHoliday);
                cmdInsert.Parameters.AddWithValue($"upload:{uploadId}");

                await cmdInsert.ExecuteNonQueryAsync();
                savedCount++;
            }

            // 标记为已导入
            await using var cmdApplied = conn.CreateCommand();
            cmdApplied.CommandText = @"
                UPDATE timesheet_uploads SET applied = TRUE, updated_at = now() WHERE id = $1";
            cmdApplied.Parameters.AddWithValue(uploadId);
            await cmdApplied.ExecuteNonQueryAsync();

            return Results.Ok(new { imported = savedCount, message = $"Imported {savedCount} entries from upload" });
        }).RequireAuthorization();

        // ========== Timesheet 审批 API（管理者用） ==========

        // 获取待审批列表
        app.MapGet("/portal/timesheet-approvals", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var month = req.Query["month"].FirstOrDefault() ?? DateTime.Today.ToString("yyyy-MM");
            var status = req.Query["status"].FirstOrDefault() ?? "submitted";

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ts.id, ts.year_month, ts.actual_hours, ts.overtime_hours,
                       ts.approval_status, ts.submitted_at,
                       r.payload->>'display_name' as resource_name,
                       r.payload->>'resource_code' as resource_code
                FROM staffing_timesheet_summary ts
                LEFT JOIN resource_pool r ON ts.resource_id = r.id
                WHERE ts.company_code = $1 AND ts.year_month = $2 AND ts.approval_status = $3
                ORDER BY ts.submitted_at DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(month);
            cmd.Parameters.AddWithValue(status);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    yearMonth = reader.IsDBNull(1) ? null : reader.GetString(1),
                    actualHours = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                    overtimeHours = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                    approvalStatus = reader.IsDBNull(4) ? "draft" : reader.GetString(4),
                    submittedAt = reader.IsDBNull(5) ? null : (DateTimeOffset?)reader.GetFieldValue<DateTimeOffset>(5),
                    resourceName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    resourceCode = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 审批工时
        app.MapPost("/portal/timesheet-approvals/{id:guid}/approve", async (
            Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var userId = req.HttpContext.User.FindFirst("sub")?.Value;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE staffing_timesheet_summary 
                SET approval_status = 'approved', 
                    approved_at = now(), 
                    approved_by = $3,
                    approval_history = COALESCE(approval_history, '[]'::jsonb) || 
                        jsonb_build_array(jsonb_build_object('action','approved','at',now(),'by',$4)),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND approval_status = 'submitted'";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(userId != null ? (object)Guid.Parse(userId) : DBNull.Value);
            cmd.Parameters.AddWithValue(userId ?? "system");

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0) return Results.BadRequest(new { error = "Not found or already processed" });

            // 发送企业微信通知给员工
            try
            {
                var wecomService = req.HttpContext.RequestServices.GetService<WeComNotificationService>();
                if (wecomService?.IsConfigured == true)
                {
                    // 查找员工的企业微信 userId
                    await using var cmdEmp = conn.CreateCommand();
                    cmdEmp.CommandText = @"
                        SELECT r.payload->>'display_name', e.payload->>'wecom_user_id', ts.year_month
                        FROM staffing_timesheet_summary ts
                        LEFT JOIN resource_pool r ON ts.resource_id = r.id
                        LEFT JOIN employees e ON r.employee_id = e.id
                        WHERE ts.id = $1";
                    cmdEmp.Parameters.AddWithValue(id);
                    await using var empReader = await cmdEmp.ExecuteReaderAsync();
                    if (await empReader.ReadAsync())
                    {
                        var empName = empReader.IsDBNull(0) ? "従業員" : empReader.GetString(0);
                        var wecomUid = empReader.IsDBNull(1) ? null : empReader.GetString(1);
                        var yearMonth = empReader.IsDBNull(2) ? "" : empReader.GetString(2);
                        if (!string.IsNullOrEmpty(wecomUid))
                        {
                            await wecomService.SendTextMessageAsync(
                                $"✅ {yearMonth} の勤怠が承認されました。お疲れ様です！", wecomUid);
                        }
                    }
                }
            }
            catch { /* 通知失败不影响审批结果 */ }

            return Results.Ok(new { message = "Approved" });
        }).RequireAuthorization();

        // 退回工时
        app.MapPost("/portal/timesheet-approvals/{id:guid}/reject", async (
            Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var userId = req.HttpContext.User.FindFirst("sub")?.Value;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE staffing_timesheet_summary 
                SET approval_status = 'rejected', 
                    rejection_reason = $3,
                    approval_history = COALESCE(approval_history, '[]'::jsonb) || 
                        jsonb_build_array(jsonb_build_object('action','rejected','at',now(),'by',$4,'reason',$3)),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2 AND approval_status = 'submitted'";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(reason ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(userId ?? "system");

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows == 0) return Results.BadRequest(new { error = "Not found or already processed" });

            // 发送企业微信退回通知
            try
            {
                var wecomService = req.HttpContext.RequestServices.GetService<WeComNotificationService>();
                if (wecomService?.IsConfigured == true)
                {
                    await using var cmdEmp = conn.CreateCommand();
                    cmdEmp.CommandText = @"
                        SELECT r.payload->>'display_name', e.payload->>'wecom_user_id', ts.year_month
                        FROM staffing_timesheet_summary ts
                        LEFT JOIN resource_pool r ON ts.resource_id = r.id
                        LEFT JOIN employees e ON r.employee_id = e.id
                        WHERE ts.id = $1";
                    cmdEmp.Parameters.AddWithValue(id);
                    await using var empReader = await cmdEmp.ExecuteReaderAsync();
                    if (await empReader.ReadAsync())
                    {
                        var wecomUid = empReader.IsDBNull(1) ? null : empReader.GetString(1);
                        var yearMonth = empReader.IsDBNull(2) ? "" : empReader.GetString(2);
                        if (!string.IsNullOrEmpty(wecomUid))
                        {
                            var msg = $"❌ {yearMonth} の勤怠が差し戻されました。";
                            if (!string.IsNullOrEmpty(reason)) msg += $"\n理由：{reason}";
                            msg += "\n修正後、再度提出してください。";
                            await wecomService.SendTextMessageAsync(msg, wecomUid);
                        }
                    }
                }
            }
            catch { /* 通知失败不影响退回结果 */ }

            return Results.Ok(new { message = "Rejected" });
        }).RequireAuthorization();

        // ========== 休暇管理 ==========

        // 获取本人的休暇申请
        app.MapGet("/portal/leaves", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            var year = req.Query["year"].FirstOrDefault() ?? DateTime.Today.Year.ToString();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, leave_type, start_date, end_date, days, reason, status, 
                       approved_at, rejection_reason, created_at
                FROM leave_requests
                WHERE company_code = $1 AND employee_id = $2 
                  AND EXTRACT(YEAR FROM start_date) = $3
                ORDER BY start_date DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));
            cmd.Parameters.AddWithValue(int.Parse(year));

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    leaveType = reader.GetString(1),
                    startDate = reader.GetDateTime(2).ToString("yyyy-MM-dd"),
                    endDate = reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                    days = reader.GetDecimal(4),
                    reason = reader.IsDBNull(5) ? null : reader.GetString(5),
                    status = reader.GetString(6),
                    approvedAt = reader.IsDBNull(7) ? null : (DateTimeOffset?)reader.GetFieldValue<DateTimeOffset>(7),
                    rejectionReason = reader.IsDBNull(8) ? null : reader.GetString(8),
                    createdAt = reader.GetFieldValue<DateTimeOffset>(9)
                });
            }

            // 统计使用情况
            var totalPaid = results.Where(r => ((dynamic)r).leaveType == "paid" && ((dynamic)r).status != "cancelled")
                .Sum(r => (decimal)((dynamic)r).days);

            return Results.Ok(new
            {
                data = results,
                summary = new { totalPaidUsed = totalPaid, totalPaidAllowed = 20 }
            });
        }).RequireAuthorization();

        // 提交休暇申请
        app.MapPost("/portal/leaves", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            await using var conn = await ds.OpenConnectionAsync();
            var resourceId = await ResolveResourceId(req.HttpContext, conn, cc.ToString(), resourceTable);

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var leaveType = body.GetProperty("leaveType").GetString() ?? "paid";
            var startDate = DateTime.Parse(body.GetProperty("startDate").GetString()!);
            var endDate = DateTime.Parse(body.GetProperty("endDate").GetString()!);
            var days = body.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDecimal() : (decimal)(endDate - startDate).TotalDays + 1;
            var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO leave_requests 
                (company_code, employee_id, resource_id, leave_type, start_date, end_date, days, reason, source)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 'web')
                RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));
            cmd.Parameters.AddWithValue(resourceId.HasValue ? (object)resourceId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue(leaveType);
            cmd.Parameters.AddWithValue(startDate);
            cmd.Parameters.AddWithValue(endDate);
            cmd.Parameters.AddWithValue(days);
            cmd.Parameters.AddWithValue(reason ?? (object)DBNull.Value);

            var id = await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id, message = "Leave request submitted" });
        }).RequireAuthorization();

        // 取消休暇申请
        app.MapPost("/portal/leaves/{id:guid}/cancel", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var employeeId = req.HttpContext.User.FindFirst("employee_id")?.Value;
            if (string.IsNullOrEmpty(employeeId))
                return Results.BadRequest(new { error = "No employee linked" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE leave_requests SET status = 'cancelled', updated_at = now()
                WHERE id = $1 AND company_code = $2 AND employee_id = $3 AND status = 'pending'";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(employeeId));

            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0
                ? Results.Ok(new { message = "Cancelled" })
                : Results.BadRequest(new { error = "Not found or cannot cancel" });
        }).RequireAuthorization();

        // ========== 企业微信员工消息测试端点 ==========

        app.MapPost("/portal/wecom-employee-message", async (
            HttpRequest req, NpgsqlDataSource ds, WeComEmployeeGateway gateway) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var message = new WeComMessage
            {
                MsgId = Guid.NewGuid().ToString(),
                MsgType = root.TryGetProperty("msgType", out var mt) ? mt.GetString()! : "text",
                FromUser = root.TryGetProperty("userId", out var uid) ? uid.GetString()! : "",
                Content = root.TryGetProperty("content", out var ct2) ? ct2.GetString() : null,
                MediaId = root.TryGetProperty("mediaId", out var mi) ? mi.GetString() : null,
                CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (string.IsNullOrEmpty(message.FromUser))
                return Results.BadRequest(new { error = "userId is required" });

            var response = await gateway.HandleEmployeeMessageAsync(cc.ToString(), message, req.HttpContext.RequestAborted);
            return Results.Ok(new
            {
                intent = response.Intent,
                reply = response.Reply,
                sessionId = response.SessionId
            });
        }).RequireAuthorization();
    }

    private static async Task<Guid?> ResolveResourceId(HttpContext ctx, NpgsqlConnection conn, string companyCode, string resourceTable)
    {
        var resourceIdStr = ctx.User.FindFirst("resource_id")?.Value;
        if (!string.IsNullOrEmpty(resourceIdStr) && Guid.TryParse(resourceIdStr, out var rid)) return rid;

        var employeeIdStr = ctx.User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrEmpty(employeeIdStr) && Guid.TryParse(employeeIdStr, out var eid))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT id FROM {resourceTable} WHERE company_code = $1 AND employee_id = $2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(eid);
            var result = await cmd.ExecuteScalarAsync();
            return result is Guid g ? g : null;
        }

        return null;
    }
}


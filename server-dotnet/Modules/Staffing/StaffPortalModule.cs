using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

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


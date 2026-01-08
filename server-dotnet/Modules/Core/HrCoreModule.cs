using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Core;

/// <summary>
/// 人事核心模块 - 包含员工、部门等基础人事功能
/// </summary>
public class HrCoreModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "hr_core",
        Name = "人事核心",
        Description = "员工管理、部门管理、考勤等核心人事功能",
        Category = ModuleCategory.Core,
        Version = "1.0.0",
        Dependencies = Array.Empty<string>(),
        Menus = new[]
        {
            new MenuConfig { Id = "menu_hr", Label = "menu.hr", Icon = "User", Path = "", ParentId = null, Order = 200 },
            new MenuConfig { Id = "menu_employees", Label = "menu.employees", Icon = "UserFilled", Path = "/hr/employees", ParentId = "menu_hr", Order = 201 },
            new MenuConfig { Id = "menu_departments", Label = "menu.departments", Icon = "OfficeBuilding", Path = "/hr/departments", ParentId = "menu_hr", Order = 202 },
            new MenuConfig { Id = "menu_timesheets", Label = "menu.timesheets", Icon = "Timer", Path = "/timesheets", ParentId = "menu_hr", Order = 203 },
        }
    };
    
    public override void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<HrCrudService>();
    }

    public override void MapEndpoints(WebApplication app)
    {
        MapEmployeeEndpoints(app);
        MapDepartmentOperationEndpoints(app);
        MapTimesheetOperationEndpoints(app);
    }

    private static void MapEmployeeEndpoints(WebApplication app)
    {
        // Minimal employee lookup endpoint for staffing UI (ResourcePoolList.vue):
        // GET /hr/employees?keyword=...&limit=20  -> { data: [...] }
        app.MapGet("/hr/employees", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var keyword = req.Query["keyword"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT to_jsonb(t)
                FROM (
                    SELECT id, employee_code, payload, created_at, updated_at
                    FROM employees
                    WHERE company_code = $1
                      AND (
                        $2::text IS NULL
                        OR employee_code ILIKE $3
                        OR (payload->>'name') ILIKE $3
                        OR (payload->>'nameKanji') ILIKE $3
                      )
                    ORDER BY updated_at DESC
                    LIMIT $4
                ) t";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(keyword) ? (object)DBNull.Value : keyword);
            cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(keyword) ? (object)DBNull.Value : $"%{keyword}%");
            cmd.Parameters.AddWithValue(limit);

            var rows = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync(req.HttpContext.RequestAborted);
            while (await rd.ReadAsync(req.HttpContext.RequestAborted))
            {
                rows.Add(rd.GetFieldValue<string>(0));
            }

            return Results.Ok(new { data = rows.Select(r => JsonDocument.Parse(r).RootElement).ToArray() });
        }).RequireAuthorization();
    }

    private static void MapDepartmentOperationEndpoints(WebApplication app)
    {
        // Restore-compatible endpoint (was previously in Program.cs.bak):
        // POST /operations/department/reparent { departmentId, newParentCode|null, newOrder? }
        app.MapPost("/operations/department/reparent", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            try
            {
                if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                    return Results.BadRequest(new { error = "Missing x-company-code" });

                using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
                var root = body.RootElement;
                if (!root.TryGetProperty("departmentId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "departmentId required" });

                var depId = Guid.Parse(idEl.GetString()!);
                var newParentCode = root.TryGetProperty("newParentCode", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var newOrder = root.TryGetProperty("newOrder", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : (int?)null;

                await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
                await using var tx = await conn.BeginTransactionAsync(req.HttpContext.RequestAborted);

                // Load target department & lock
                string? code;
                string? oldPath;
                await using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = "SELECT department_code, payload->>'path' FROM departments WHERE company_code=$1 AND id=$2 FOR UPDATE";
                    q.Parameters.AddWithValue(cc.ToString());
                    q.Parameters.AddWithValue(depId);
                    await using var rd = await q.ExecuteReaderAsync(req.HttpContext.RequestAborted);
                    if (!await rd.ReadAsync(req.HttpContext.RequestAborted))
                    {
                        await tx.RollbackAsync(req.HttpContext.RequestAborted);
                        return Results.NotFound(new { error = "department not found" });
                    }

                    code = rd.IsDBNull(0) ? null : rd.GetString(0);
                    oldPath = rd.IsDBNull(1) ? null : rd.GetString(1);
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    await tx.RollbackAsync(req.HttpContext.RequestAborted);
                    return Results.BadRequest(new { error = "department code missing" });
                }

                string newParentPath = string.Empty;
                if (!string.IsNullOrEmpty(newParentCode))
                {
                    await using var qp = conn.CreateCommand();
                    qp.Transaction = tx;
                    qp.CommandText = "SELECT department_code, payload->>'path' FROM departments WHERE company_code=$1 AND department_code=$2 LIMIT 1";
                    qp.Parameters.AddWithValue(cc.ToString());
                    qp.Parameters.AddWithValue(newParentCode!);
                    await using var pr = await qp.ExecuteReaderAsync(req.HttpContext.RequestAborted);
                    if (!await pr.ReadAsync(req.HttpContext.RequestAborted))
                    {
                        await tx.RollbackAsync(req.HttpContext.RequestAborted);
                        return Results.BadRequest(new { error = "parent not found" });
                    }

                    var pCode = pr.IsDBNull(0) ? (string?)null : pr.GetString(0);
                    var pPath = pr.IsDBNull(1) ? (string?)null : pr.GetString(1);
                    newParentPath = string.IsNullOrWhiteSpace(pPath) ? (pCode ?? newParentCode!) : pPath;
                }

                var newPath = string.IsNullOrEmpty(newParentPath) ? code! : (newParentPath + "/" + code);
                if (string.IsNullOrWhiteSpace(oldPath)) oldPath = code;

                // Write parentCode & path
                await using (var up1 = conn.CreateCommand())
                {
                    up1.Transaction = tx;
                    up1.CommandText = @"
                        UPDATE departments
                        SET payload = jsonb_set(
                                      jsonb_set(payload, '{parentCode}', to_jsonb($3::text), true),
                                      '{path}', to_jsonb($4::text), true
                                    ),
                            updated_at = now()
                        WHERE company_code=$1 AND id=$2";
                    up1.Parameters.AddWithValue(cc.ToString());
                    up1.Parameters.AddWithValue(depId);
                    up1.Parameters.AddWithValue((object?)newParentCode ?? DBNull.Value);
                    up1.Parameters.AddWithValue(newPath);
                    await up1.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                }

                if (newOrder.HasValue)
                {
                    await using var up2 = conn.CreateCommand();
                    up2.Transaction = tx;
                    up2.CommandText = @"UPDATE departments SET payload = jsonb_set(payload, '{order}', to_jsonb($3::int), true), updated_at = now() WHERE company_code=$1 AND id=$2";
                    up2.Parameters.AddWithValue(cc.ToString());
                    up2.Parameters.AddWithValue(depId);
                    up2.Parameters.AddWithValue(newOrder.Value);
                    await up2.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                }

                // Cascade update descendant paths
                if (!string.IsNullOrWhiteSpace(oldPath) && oldPath != newPath)
                {
                    await using var upChildren = conn.CreateCommand();
                    upChildren.Transaction = tx;
                    upChildren.CommandText = @"
                        UPDATE departments
                           SET payload = jsonb_set(payload, '{path}', to_jsonb(regexp_replace(payload->>'path', '^' || $3, $4)))
                         WHERE company_code=$1 AND (payload->>'path') LIKE $2";
                    upChildren.Parameters.AddWithValue(cc.ToString());
                    upChildren.Parameters.AddWithValue(oldPath + "/%");
                    upChildren.Parameters.AddWithValue(oldPath!);
                    upChildren.Parameters.AddWithValue(newPath);
                    await upChildren.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                }

                await tx.CommitAsync(req.HttpContext.RequestAborted);
                return Results.Ok(new { ok = true, path = newPath });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        }).RequireAuthorization();
    }

    private static void MapTimesheetOperationEndpoints(WebApplication app)
    {
        // Backward-compatible submit endpoint (was previously in Program.cs.bak):
        // POST /operations/timesheet/submit { timesheetId }
        //
        // Current frontend uses:
        // POST /operations/timesheet/submit-month { month: 'YYYY-MM' }
        // This implementation creates/updates timesheet_submissions and corresponding approval_tasks (entity=timesheet_submission).

        app.MapPost("/operations/timesheet/submit", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timesheetId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "timesheetId required" });
            var tsId = Guid.Parse(idEl.GetString()!);

            var steps = new[] { new { stepNo = 1, role = "manager", name = "经理" }, new { stepNo = 2, role = "hr_manager", name = "HR" } };

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(req.HttpContext.RequestAborted);

            await using (var u = conn.CreateCommand())
            {
                u.Transaction = tx;
                u.CommandText = "UPDATE timesheets SET payload = jsonb_set(payload, '{status}', to_jsonb('submitted'::text), true), updated_at = now() WHERE id=$1 AND company_code=$2";
                u.Parameters.AddWithValue(tsId);
                u.Parameters.AddWithValue(cc.ToString());
                var n = await u.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
                if (n == 0)
                {
                    await tx.RollbackAsync(req.HttpContext.RequestAborted);
                    return Results.NotFound(new { error = "timesheet not found" });
                }
            }

            var submitter = Auth.GetUserCtx(req);
            foreach (var s in steps)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO approval_tasks(company_code, entity, object_id, step_no, step_name, approver_user_id, status)
                                    VALUES ($1,$2,$3,$4,$5,$6,'pending')";
                ins.Parameters.AddWithValue(cc.ToString());
                ins.Parameters.AddWithValue("timesheet");
                ins.Parameters.AddWithValue(tsId);
                ins.Parameters.AddWithValue(s.stepNo);
                ins.Parameters.AddWithValue(s.name);
                ins.Parameters.AddWithValue(submitter.UserId ?? string.Empty);
                await ins.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
            }

            await tx.CommitAsync(req.HttpContext.RequestAborted);
            return Results.Ok(new { ok = true, steps = steps.Select(x => new { x.stepNo, x.name }) });
        }).RequireAuthorization();

        app.MapPost("/operations/timesheet/submit-month", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var root = doc.RootElement;
            var month = root.TryGetProperty("month", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(month)) return Results.BadRequest(new { error = "month required (YYYY-MM)" });

            var user = Auth.GetUserCtx(req);
            if (string.IsNullOrWhiteSpace(user.UserId)) return Results.BadRequest(new { error = "Missing user id" });

            // Create or update timesheet_submissions as submitted.
            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(req.HttpContext.RequestAborted);

            Guid submissionId;
            await using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = @"
                    INSERT INTO timesheet_submissions(company_code, payload, created_by, month, status)
                    VALUES ($1, jsonb_set(COALESCE($2::jsonb,'{}'::jsonb), '{status}', to_jsonb('submitted'::text), true), $3, $4, 'submitted')
                    ON CONFLICT (company_code, created_by, month)
                    DO UPDATE SET
                        payload = jsonb_set(COALESCE(timesheet_submissions.payload,'{}'::jsonb), '{status}', to_jsonb('submitted'::text), true),
                        status = 'submitted',
                        updated_at = now()
                    RETURNING id";
                upsert.Parameters.AddWithValue(cc.ToString());
                upsert.Parameters.AddWithValue(root.GetRawText());
                upsert.Parameters.AddWithValue(user.UserId);
                upsert.Parameters.AddWithValue(month);
                submissionId = (Guid)(await upsert.ExecuteScalarAsync(req.HttpContext.RequestAborted) ?? Guid.Empty);
                if (submissionId == Guid.Empty)
                {
                    await tx.RollbackAsync(req.HttpContext.RequestAborted);
                    return Results.Problem("failed to create timesheet submission");
                }
            }

            // Create approval tasks for this submission (entity=timesheet_submission)
            var steps = new[] { new { stepNo = 1, role = "manager", name = "经理" }, new { stepNo = 2, role = "hr_manager", name = "HR" } };
            foreach (var s in steps)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO approval_tasks(company_code, entity, object_id, step_no, step_name, approver_user_id, status)
                                    VALUES ($1,$2,$3,$4,$5,$6,'pending')
                                    ON CONFLICT DO NOTHING";
                ins.Parameters.AddWithValue(cc.ToString());
                ins.Parameters.AddWithValue("timesheet_submission");
                ins.Parameters.AddWithValue(submissionId);
                ins.Parameters.AddWithValue(s.stepNo);
                ins.Parameters.AddWithValue(s.name);
                ins.Parameters.AddWithValue(user.UserId);
                await ins.ExecuteNonQueryAsync(req.HttpContext.RequestAborted);
            }

            await tx.CommitAsync(req.HttpContext.RequestAborted);
            return Results.Ok(new { ok = true, submissionId, month, steps = steps.Select(x => new { x.stepNo, x.name }) });
        }).RequireAuthorization();
    }
}


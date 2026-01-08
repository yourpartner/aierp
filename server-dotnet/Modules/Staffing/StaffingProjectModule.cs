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
/// 案件管理モジュール
/// 
/// 基本 CRUD 通过通用端点 /api/staffing_project 提供
/// 本模块只提供候选人管理等业务逻辑端点
/// </summary>
public class StaffingProjectModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_project",
        Name = "案件管理",
        Description = "顧客からの要員依頼・案件管理、マッチング",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_resource_pool" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_projects", Label = "menu.staffingProjects", Icon = "Briefcase", Path = "/staffing/projects", ParentId = "menu_staffing", Order = 253 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        // NOTE: Do NOT prefix with "/api" because Program.cs rewrites "/api/*" -> "/*" when bypassing the dev proxy.
        var group = app.MapGroup("/staffing/projects")
            .WithTags("Staffing Project")
            .RequireAuthorization();

        var projectTable = Crud.TableFor("staffing_project");

        // ========== UI CRUD endpoints (for current frontend pages) ==========
        // 列表
        group.MapGet("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var contractType = req.Query["contractType"].FirstOrDefault();
            var clientId = req.Query["clientId"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 100;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? Math.Max(0, o) : 0;

            await using var conn = await ds.OpenConnectionAsync();

            var whereSql = "p.company_code = $1";
            var args = new List<object?> { cc.ToString() };
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(status))
            {
                whereSql += $" AND p.status = ${idx}";
                args.Add(status);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(contractType))
            {
                whereSql += $" AND p.contract_type = ${idx}";
                args.Add(contractType);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId, out var cid))
            {
                whereSql += $" AND p.client_partner_id = ${idx}";
                args.Add(cid);
                idx++;
            }

            // total
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {projectTable} p WHERE {whereSql}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            // data
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT p.id, p.payload, bp.partner_code as client_code, bp.payload->>'name' as client_name
                FROM {projectTable} p
                LEFT JOIN businesspartners bp ON p.client_partner_id = bp.id
                WHERE {whereSql}
                ORDER BY p.updated_at DESC
                LIMIT ${idx} OFFSET ${idx + 1}";
            for (var i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                using var payloadDoc = JsonDocument.Parse(reader.GetString(1));
                var p = payloadDoc.RootElement;
                rows.Add(new
                {
                    id,
                    projectCode = p.TryGetProperty("project_code", out var pc) ? pc.GetString() : null,
                    projectName = p.TryGetProperty("project_name", out var pn) ? pn.GetString() : null,
                    jobCategory = p.TryGetProperty("job_category", out var jc) ? jc.GetString() : null,
                    contractType = p.TryGetProperty("contract_type", out var ct) ? ct.GetString() : null,
                    headcount = p.TryGetProperty("headcount", out var hc) && hc.ValueKind == JsonValueKind.Number ? hc.GetInt32() : 1,
                    filledCount = p.TryGetProperty("filled_count", out var fc) && fc.ValueKind == JsonValueKind.Number ? fc.GetInt32() : 0,
                    expectedStartDate = p.TryGetProperty("expected_start_date", out var esd) ? esd.GetString() : null,
                    expectedEndDate = p.TryGetProperty("expected_end_date", out var eed) ? eed.GetString() : null,
                    workLocation = p.TryGetProperty("work_location", out var wl) ? wl.GetString() : null,
                    billingRateMin = p.TryGetProperty("billing_rate_min", out var brmin) && brmin.ValueKind == JsonValueKind.Number ? brmin.GetDecimal() : (decimal?)null,
                    billingRateMax = p.TryGetProperty("billing_rate_max", out var brmax) && brmax.ValueKind == JsonValueKind.Number ? brmax.GetDecimal() : (decimal?)null,
                    rateType = p.TryGetProperty("rate_type", out var rt) ? rt.GetString() : "monthly",
                    status = p.TryGetProperty("status", out var st) ? st.GetString() : null,
                    priority = p.TryGetProperty("priority", out var pr) ? pr.GetString() : "normal",
                    clientPartnerId = p.TryGetProperty("client_partner_id", out var cpid) && cpid.ValueKind == JsonValueKind.String ? cpid.GetString() : null,
                    clientCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    clientName = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }

            return Results.Ok(new { data = rows, total });
        });

        // 详情（snake_case，供编辑对话框使用）
        group.MapGet("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var json = await Crud.GetDetailJson(ds, projectTable, id, cc.ToString(), "", Array.Empty<object?>());
            if (json == null) return Results.NotFound();

            using var rowDoc = JsonDocument.Parse(json);
            var row = rowDoc.RootElement;
            var payload = row.GetProperty("payload");
            var obj = new JsonObject { ["id"] = id.ToString() };
            foreach (var prop in payload.EnumerateObject())
            {
                obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
            return Results.Json(obj);
        });

        // 新增（camelCase 输入 -> snake_case payload）
        group.MapPost("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var body = doc.RootElement;

            await using var conn = await ds.OpenConnectionAsync();
            // 编号
            string code;
            await using (var seqCmd = conn.CreateCommand())
            {
                seqCmd.CommandText = "SELECT nextval('seq_project_code')";
                var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
                code = $"PJ-{seq:D4}";
            }

            var payload = JsonSerializer.Serialize(new
            {
                project_code = code,
                project_name = body.TryGetProperty("projectName", out var pn) ? pn.GetString() : null,
                job_category = body.TryGetProperty("jobCategory", out var jc) ? jc.GetString() : null,
                job_description = body.TryGetProperty("jobDescription", out var jd) ? jd.GetString() : null,
                client_partner_id = body.TryGetProperty("clientPartnerId", out var cpid) ? cpid.GetString() : null,
                contract_type = body.TryGetProperty("contractType", out var ct) ? ct.GetString() : null,
                headcount = body.TryGetProperty("headcount", out var hc) && hc.ValueKind == JsonValueKind.Number ? hc.GetInt32() : 1,
                filled_count = 0,
                experience_years_min = body.TryGetProperty("experienceYearsMin", out var eym) && eym.ValueKind == JsonValueKind.Number ? eym.GetInt32() : (int?)null,
                expected_start_date = body.TryGetProperty("expectedStartDate", out var esd) ? esd.GetString() : null,
                expected_end_date = body.TryGetProperty("expectedEndDate", out var eed) ? eed.GetString() : null,
                work_location = body.TryGetProperty("workLocation", out var wl) ? wl.GetString() : null,
                work_days = body.TryGetProperty("workDays", out var wd) ? wd.GetString() : null,
                work_hours = body.TryGetProperty("workHours", out var wh) ? wh.GetString() : null,
                remote_work_ratio = body.TryGetProperty("remoteWorkRatio", out var rwr) && rwr.ValueKind == JsonValueKind.Number ? rwr.GetInt32() : 0,
                billing_rate_min = body.TryGetProperty("billingRateMin", out var brmin) && brmin.ValueKind == JsonValueKind.Number ? brmin.GetDecimal() : (decimal?)null,
                billing_rate_max = body.TryGetProperty("billingRateMax", out var brmax) && brmax.ValueKind == JsonValueKind.Number ? brmax.GetDecimal() : (decimal?)null,
                rate_type = body.TryGetProperty("rateType", out var rt) ? rt.GetString() : "monthly",
                priority = body.TryGetProperty("priority", out var pr) ? pr.GetString() : "normal",
                notes = body.TryGetProperty("notes", out var n) ? n.GetString() : null,
                status = "open"
            });

            var inserted = await Crud.InsertRawJson(ds, projectTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create project");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, projectCode = code });
        });

        // 更新（camelCase 输入 -> merge into payload）
        group.MapPut("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var body = doc.RootElement;

            var patch = JsonSerializer.Serialize(new
            {
                project_name = body.TryGetProperty("projectName", out var pn) ? pn.GetString() : null,
                job_category = body.TryGetProperty("jobCategory", out var jc) ? jc.GetString() : null,
                job_description = body.TryGetProperty("jobDescription", out var jd) ? jd.GetString() : null,
                client_partner_id = body.TryGetProperty("clientPartnerId", out var cpid) ? cpid.GetString() : null,
                contract_type = body.TryGetProperty("contractType", out var ct) ? ct.GetString() : null,
                headcount = body.TryGetProperty("headcount", out var hc) && hc.ValueKind == JsonValueKind.Number ? hc.GetInt32() : (int?)null,
                experience_years_min = body.TryGetProperty("experienceYearsMin", out var eym) && eym.ValueKind == JsonValueKind.Number ? eym.GetInt32() : (int?)null,
                expected_start_date = body.TryGetProperty("expectedStartDate", out var esd) ? esd.GetString() : null,
                expected_end_date = body.TryGetProperty("expectedEndDate", out var eed) ? eed.GetString() : null,
                work_location = body.TryGetProperty("workLocation", out var wl) ? wl.GetString() : null,
                work_days = body.TryGetProperty("workDays", out var wd) ? wd.GetString() : null,
                work_hours = body.TryGetProperty("workHours", out var wh) ? wh.GetString() : null,
                remote_work_ratio = body.TryGetProperty("remoteWorkRatio", out var rwr) && rwr.ValueKind == JsonValueKind.Number ? rwr.GetInt32() : (int?)null,
                billing_rate_min = body.TryGetProperty("billingRateMin", out var brmin) && brmin.ValueKind == JsonValueKind.Number ? brmin.GetDecimal() : (decimal?)null,
                billing_rate_max = body.TryGetProperty("billingRateMax", out var brmax) && brmax.ValueKind == JsonValueKind.Number ? brmax.GetDecimal() : (decimal?)null,
                rate_type = body.TryGetProperty("rateType", out var rt) ? rt.GetString() : null,
                priority = body.TryGetProperty("priority", out var pr) ? pr.GetString() : null,
                notes = body.TryGetProperty("notes", out var n) ? n.GetString() : null
            });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {projectTable} SET payload = payload || $3::jsonb, updated_at = now() WHERE id = $1 AND company_code = $2 RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(patch);
            var updated = await cmd.ExecuteScalarAsync();
            if (updated == null) return Results.NotFound();
            return Results.Ok(new { id, updated = true });
        });

        // ========== 候选人管理 ==========

        // 获取案件的候选人列表
        group.MapGet("/{projectId:guid}/candidates", async (Guid projectId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project_candidate", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var candTable = Crud.TableFor("staffing_project_candidate");
            var resTable = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.resource_id, c.status, c.proposed_rate, c.final_rate,
                       c.recommended_at, c.payload,
                       r.resource_code, r.display_name, r.resource_type, r.availability_status,
                       r.payload->'skills' as skills
                FROM {candTable} c
                JOIN {resTable} r ON c.resource_id = r.id
                WHERE c.company_code = $1 AND c.project_id = $2
                ORDER BY c.recommended_at DESC NULLS LAST, c.updated_at DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(projectId);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var candPayload = reader.IsDBNull(6) ? "{}" : reader.GetString(6);
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    resourceId = reader.GetGuid(1),
                    status = reader.IsDBNull(2) ? null : reader.GetString(2),
                    proposedRate = reader.IsDBNull(3) ? null : (decimal?)reader.GetDecimal(3),
                    finalRate = reader.IsDBNull(4) ? null : (decimal?)reader.GetDecimal(4),
                    recommendedAt = reader.IsDBNull(5) ? null : (DateTime?)reader.GetDateTime(5),
                    candidatePayload = candPayload,
                    resourceCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                    displayName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    resourceType = reader.IsDBNull(9) ? null : reader.GetString(9),
                    availabilityStatus = reader.IsDBNull(10) ? null : reader.GetString(10),
                    skills = reader.IsDBNull(11) ? "[]" : reader.GetString(11)
                });
            }

            return Results.Ok(new { data = results });
        });

        // 添加候选人
        group.MapPost("/{projectId:guid}/candidates", async (Guid projectId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project_candidate", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "create", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var resourceId = root.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String
                ? Guid.TryParse(rid.GetString(), out var g) ? g : (Guid?)null : null;

            if (resourceId == null)
                return Results.BadRequest(new { error = "resourceId is required" });

            await using var conn = await ds.OpenConnectionAsync();
            var candTable = Crud.TableFor("staffing_project_candidate");

            // 检查是否已添加
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT id FROM {candTable} WHERE company_code = $1 AND project_id = $2 AND resource_id = $3 LIMIT 1";
            checkCmd.Parameters.AddWithValue(cc.ToString());
            checkCmd.Parameters.AddWithValue(projectId);
            checkCmd.Parameters.AddWithValue(resourceId.Value);
            var existing = await checkCmd.ExecuteScalarAsync();
            if (existing != null)
            {
                return Results.Conflict(new { error = "Candidate already added", existingId = existing });
            }

            var payload = new JsonObject
            {
                ["project_id"] = projectId.ToString(),
                ["resource_id"] = resourceId.Value.ToString(),
                ["status"] = "recommended",
                ["recommended_at"] = DateTimeOffset.UtcNow.ToString("O")
            };
            if (root.TryGetProperty("proposedRate", out var pr) && pr.ValueKind == JsonValueKind.Number)
                payload["proposed_rate"] = pr.GetDecimal();
            if (root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                payload["notes"] = n.GetString();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO {candTable}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(payload.ToJsonString());
            var newIdObj = await cmd.ExecuteScalarAsync();
            if (newIdObj is not Guid newId) return Results.Problem("Failed to add candidate");

            // 更新案件状态为 matching
            await using var updateCmd = conn.CreateCommand();
            var projectTable = Crud.TableFor("staffing_project");
            updateCmd.CommandText = $@"
                UPDATE {projectTable}
                SET payload = jsonb_set(payload, '{{status}}', to_jsonb('matching'::text), true),
                    updated_at = now()
                WHERE company_code = $1 AND id = $2 AND status = 'open'";
            updateCmd.Parameters.AddWithValue(cc.ToString());
            updateCmd.Parameters.AddWithValue(projectId);
            await updateCmd.ExecuteNonQueryAsync();

            return Results.Ok(new { id = newId });
        });

        // 更新候选人状态
        group.MapPut("/candidates/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project_candidate", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            var candTable = Crud.TableFor("staffing_project_candidate");
            await using var conn = await ds.OpenConnectionAsync();

            // Load current payload
            await using var getCmd = conn.CreateCommand();
            getCmd.CommandText = $"SELECT payload FROM {candTable} WHERE id = $1 AND company_code = $2";
            getCmd.Parameters.AddWithValue(id);
            getCmd.Parameters.AddWithValue(cc.ToString());
            var existingPayload = (string?)await getCmd.ExecuteScalarAsync();
            if (string.IsNullOrWhiteSpace(existingPayload)) return Results.NotFound();

            var obj = (JsonNode.Parse(existingPayload) as JsonObject) ?? new JsonObject();
            void SetString(string inputKey, string payloadKey)
            {
                if (root.TryGetProperty(inputKey, out var v) && v.ValueKind == JsonValueKind.String)
                    obj[payloadKey] = v.GetString();
            }
            void SetDecimal(string inputKey, string payloadKey)
            {
                if (root.TryGetProperty(inputKey, out var v) && v.ValueKind == JsonValueKind.Number)
                    obj[payloadKey] = v.GetDecimal();
            }
            SetString("status", "status");
            SetDecimal("proposedRate", "proposed_rate");
            SetDecimal("finalRate", "final_rate");
            SetString("interviewDate", "interview_date");
            SetString("interviewFeedback", "interview_feedback");
            SetString("rejectionReason", "rejection_reason");
            SetString("resultNote", "result_note");
            SetString("notes", "notes");

            await using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE {candTable} SET payload=$3::jsonb, updated_at=now() WHERE id=$1 AND company_code=$2 RETURNING project_id, resource_id, status";
            upd.Parameters.AddWithValue(id);
            upd.Parameters.AddWithValue(cc.ToString());
            upd.Parameters.AddWithValue(obj.ToJsonString());

            await using var reader = await upd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            var projectId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            var resourceId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            var newStatus = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (string.Equals(newStatus, "accepted", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("createContract", out var cc2) && cc2.ValueKind == JsonValueKind.True)
            {
                return Results.Ok(new { id, updated = true, projectId, resourceId, shouldCreateContract = true });
            }

            return Results.Ok(new { id, updated = true });
        });

        // 删除候选人
        group.MapDelete("/candidates/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project_candidate", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "delete", user))
                    return Results.StatusCode(403);
            }

            var candTable = Crud.TableFor("staffing_project_candidate");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {candTable} WHERE id = $1 AND company_code = $2 RETURNING project_id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var projectId = await cmd.ExecuteScalarAsync();
            if (projectId == null) return Results.NotFound();

            return Results.Ok(new { deleted = true, projectId });
        });

        // ========== 案件状态管理 ==========

        // 更新案件状态
        group.MapPost("/{id:guid}/status", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var newStatus = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (string.IsNullOrWhiteSpace(newStatus))
                return Results.BadRequest(new { error = "status is required" });

            var projectTable = Crud.TableFor("staffing_project");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {projectTable}
                SET payload = jsonb_set(payload, '{{status}}', to_jsonb($3::text), true),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id, status";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(newStatus);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Results.Ok(new { id = reader.GetGuid(0), status = reader.GetString(1) });
            }
            return Results.NotFound();
        });

        // 获取案件统计
        group.MapGet("/stats", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_project", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var projectTable = Crud.TableFor("staffing_project");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    COALESCE(status,'') as status,
                    COUNT(*) as count,
                    COALESCE(SUM(fn_jsonb_numeric(payload,'headcount')),0) as total_headcount,
                    COALESCE(SUM(fn_jsonb_numeric(payload,'filled_count')),0) as total_filled
                FROM {projectTable}
                WHERE company_code = $1
                GROUP BY status";
            cmd.Parameters.AddWithValue(cc.ToString());

            var stats = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stats.Add(new
                {
                    status = reader.GetString(0),
                    count = reader.GetInt64(1),
                    totalHeadcount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    totalFilled = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
                });
            }

            return Results.Ok(new { stats });
        });
    }

    // (helpers removed; payload-based tables are used)
}

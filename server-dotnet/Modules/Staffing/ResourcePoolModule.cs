using System.Text.Json;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Domain;
using Server.Infrastructure;
using Server.Infrastructure.Modules;
using System.Text.Json.Nodes;

namespace Server.Modules.Staffing;

/// <summary>
/// リソースプール管理モジュール
/// 
/// 基本 CRUD 通过通用端点 /api/resource 提供
/// 本模块只提供业务逻辑端点
/// </summary>
public class ResourcePoolModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_resource_pool",
        Name = "リソースプール",
        Description = "要員の統合管理（自社社員・個人事業主・BP要員・候補者）",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "hr_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_staffing", Label = "menu.staffing", Icon = "UserFilled", Path = "", ParentId = null, Order = 250 },
            new MenuConfig { Id = "menu_resource_pool", Label = "menu.resourcePool", Icon = "User", Path = "/staffing/resources", ParentId = "menu_staffing", Order = 251 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        // NOTE: Do NOT prefix with "/api" because Program.cs rewrites "/api/*" -> "/*" when bypassing the dev proxy.
        var group = app.MapGroup("/staffing/resources")
            .WithTags("Staffing Resource Pool")
            .RequireAuthorization();

        var table = Crud.TableFor("resource");

        // ========== UI CRUD endpoints (for current frontend pages) ==========
        // 列表
        group.MapGet("", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var keyword = req.Query["keyword"].FirstOrDefault();
            var type = req.Query["type"].FirstOrDefault();
            var status = req.Query["status"].FirstOrDefault(); // availability_status
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? Math.Max(0, o) : 0;

            await using var conn = await ds.OpenConnectionAsync();

            var whereSql = $"company_code = $1 AND status = 'active'";
            var args = new List<object?> { cc.ToString() };
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(type))
            {
                whereSql += $" AND resource_type = ${idx}";
                args.Add(type);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                whereSql += $" AND availability_status = ${idx}";
                args.Add(status);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                whereSql += $" AND (resource_code ILIKE ${idx} OR display_name ILIKE ${idx})";
                args.Add($"%{keyword}%");
                idx++;
            }

            // total
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereSql}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            // data
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, payload FROM {table} WHERE {whereSql} ORDER BY updated_at DESC LIMIT ${idx} OFFSET ${idx + 1}";
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
                    resourceCode = p.TryGetProperty("resource_code", out var rc) ? rc.GetString() : null,
                    displayName = p.TryGetProperty("display_name", out var dn) ? dn.GetString() : null,
                    displayNameKana = p.TryGetProperty("display_name_kana", out var dnk) ? dnk.GetString() : null,
                    resourceType = p.TryGetProperty("resource_type", out var rt) ? rt.GetString() : null,
                    email = p.TryGetProperty("email", out var em) ? em.GetString() : null,
                    phone = p.TryGetProperty("phone", out var ph) ? ph.GetString() : null,
                    primarySkillCategory = p.TryGetProperty("primary_skill_category", out var psc) ? psc.GetString() : null,
                    experienceYears = p.TryGetProperty("experience_years", out var ey) && ey.ValueKind == JsonValueKind.Number ? ey.GetInt32() : (int?)null,
                    defaultBillingRate = p.TryGetProperty("default_billing_rate", out var dbr) && dbr.ValueKind == JsonValueKind.Number ? dbr.GetDecimal() : (decimal?)null,
                    defaultCostRate = p.TryGetProperty("default_cost_rate", out var dcr) && dcr.ValueKind == JsonValueKind.Number ? dcr.GetDecimal() : (decimal?)null,
                    rateType = p.TryGetProperty("rate_type", out var rt2) ? rt2.GetString() : null,
                    availabilityStatus = p.TryGetProperty("availability_status", out var av) ? av.GetString() : null,
                    availableFrom = p.TryGetProperty("available_from", out var af) ? af.GetString() : null
                });
            }

            return Results.Ok(new { data = rows, total });
        });

        // 详情（snake_case，供编辑对话框使用）
        group.MapGet("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT payload FROM {table} WHERE id = $1 AND company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            var json = await cmd.ExecuteScalarAsync() as string;
            if (json == null) return Results.NotFound();

            using var payloadDoc = JsonDocument.Parse(json);
            var root = payloadDoc.RootElement;
            var obj = new JsonObject { ["id"] = id.ToString() };
            foreach (var prop in root.EnumerateObject())
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
                seqCmd.CommandText = "SELECT nextval('seq_resource_code')";
                var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
                code = $"RS-{seq:D4}";
            }

            var payload = JsonSerializer.Serialize(new
            {
                resource_code = code,
                display_name = body.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                display_name_kana = body.TryGetProperty("displayNameKana", out var dnk) ? dnk.GetString() : null,
                resource_type = body.TryGetProperty("resourceType", out var rt) ? rt.GetString() : null,
                employee_id = body.TryGetProperty("employeeId", out var eid) && eid.ValueKind == JsonValueKind.String && Guid.TryParse(eid.GetString(), out var eg) ? eg : (Guid?)null,
                supplier_partner_id = body.TryGetProperty("supplierPartnerId", out var sp) && sp.ValueKind == JsonValueKind.String && Guid.TryParse(sp.GetString(), out var spg) ? spg : (Guid?)null,
                email = body.TryGetProperty("email", out var em) ? em.GetString() : null,
                phone = body.TryGetProperty("phone", out var ph) ? ph.GetString() : null,
                primary_skill_category = body.TryGetProperty("primarySkillCategory", out var psc) ? psc.GetString() : null,
                experience_years = body.TryGetProperty("experienceYears", out var ey) && ey.ValueKind == JsonValueKind.Number ? ey.GetInt32() : (int?)null,
                default_billing_rate = body.TryGetProperty("defaultBillingRate", out var dbr) && dbr.ValueKind == JsonValueKind.Number ? dbr.GetDecimal() : (decimal?)null,
                default_cost_rate = body.TryGetProperty("defaultCostRate", out var dcr) && dcr.ValueKind == JsonValueKind.Number ? dcr.GetDecimal() : (decimal?)null,
                rate_type = body.TryGetProperty("rateType", out var rt2) ? rt2.GetString() : null,
                availability_status = body.TryGetProperty("availabilityStatus", out var av) ? av.GetString() : "available",
                available_from = body.TryGetProperty("availableFrom", out var af) ? af.GetString() : null,
                internal_notes = body.TryGetProperty("internalNotes", out var inn) ? inn.GetString() : null,
                status = "active"
            });

            var inserted = await Crud.InsertRawJson(ds, table, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create resource");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, resourceCode = code });
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
                display_name = body.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                display_name_kana = body.TryGetProperty("displayNameKana", out var dnk) ? dnk.GetString() : null,
                resource_type = body.TryGetProperty("resourceType", out var rt) ? rt.GetString() : null,
                supplier_partner_id = body.TryGetProperty("supplierPartnerId", out var sp) && sp.ValueKind == JsonValueKind.String && Guid.TryParse(sp.GetString(), out var spg) ? spg : (Guid?)null,
                email = body.TryGetProperty("email", out var em) ? em.GetString() : null,
                phone = body.TryGetProperty("phone", out var ph) ? ph.GetString() : null,
                primary_skill_category = body.TryGetProperty("primarySkillCategory", out var psc) ? psc.GetString() : null,
                experience_years = body.TryGetProperty("experienceYears", out var ey) && ey.ValueKind == JsonValueKind.Number ? ey.GetInt32() : (int?)null,
                default_billing_rate = body.TryGetProperty("defaultBillingRate", out var dbr) && dbr.ValueKind == JsonValueKind.Number ? dbr.GetDecimal() : (decimal?)null,
                default_cost_rate = body.TryGetProperty("defaultCostRate", out var dcr) && dcr.ValueKind == JsonValueKind.Number ? dcr.GetDecimal() : (decimal?)null,
                rate_type = body.TryGetProperty("rateType", out var rt2) ? rt2.GetString() : null,
                availability_status = body.TryGetProperty("availabilityStatus", out var av) ? av.GetString() : null,
                available_from = body.TryGetProperty("availableFrom", out var af) ? af.GetString() : null,
                internal_notes = body.TryGetProperty("internalNotes", out var inn) ? inn.GetString() : null
            });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {table} SET payload = payload || $3::jsonb, updated_at = now() WHERE id = $1 AND company_code = $2 RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(patch);
            var updated = await cmd.ExecuteScalarAsync();
            if (updated == null) return Results.NotFound();
            return Results.Ok(new { id, updated = true });
        });

        // ========== 业务逻辑端点 ==========

        // 从自社社员创建资源
        group.MapPost("/from-employee/{employeeId:guid}", async (Guid employeeId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // 权限检查
            var schemaDoc = await SchemasService.GetActiveSchema(ds, "resource", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "create", user))
                    return Results.StatusCode(403);
            }

            await using var conn = await ds.OpenConnectionAsync();

            // 获取社员信息
            await using var empCmd = conn.CreateCommand();
            empCmd.CommandText = "SELECT payload FROM employees WHERE id = $1 AND company_code = $2";
            empCmd.Parameters.AddWithValue(employeeId);
            empCmd.Parameters.AddWithValue(cc.ToString());
            var empJson = await empCmd.ExecuteScalarAsync() as string;
            if (empJson == null) return Results.NotFound(new { error = "Employee not found" });

            using var empDoc = JsonDocument.Parse(empJson);
            var emp = empDoc.RootElement;

            var name = emp.TryGetProperty("nameKanji", out var nk) ? nk.GetString() :
                       emp.TryGetProperty("name", out var nn) ? nn.GetString() : "Unknown";
            var nameKana = emp.TryGetProperty("nameKana", out var nkk) ? nkk.GetString() : null;
            var email = emp.TryGetProperty("email", out var em) ? em.GetString() : null;

            // 检查是否已注册
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT id FROM {table} WHERE company_code = $1 AND employee_id = $2 LIMIT 1";
            checkCmd.Parameters.AddWithValue(cc.ToString());
            checkCmd.Parameters.AddWithValue(employeeId);
            var existing = await checkCmd.ExecuteScalarAsync();
            if (existing != null)
            {
                return Results.Ok(new { id = (Guid)existing, alreadyExists = true });
            }

            // 生成编号
            string resourceCode;
            await using (var seqCmd = conn.CreateCommand())
            {
                seqCmd.CommandText = "SELECT nextval('seq_resource_code')";
                var seq = await seqCmd.ExecuteScalarAsync();
                resourceCode = $"RS-{seq:D4}";
            }

            // 创建资源（payload 作为事实来源）
            var payload = new JsonObject
            {
                ["resource_code"] = resourceCode,
                ["display_name"] = name ?? "Unknown",
                ["display_name_kana"] = string.IsNullOrWhiteSpace(nameKana) ? null : nameKana,
                ["resource_type"] = "employee",
                ["employee_id"] = employeeId.ToString(),
                ["email"] = string.IsNullOrWhiteSpace(email) ? null : email,
                ["availability_status"] = "available",
                ["status"] = "active",
                ["skills"] = new JsonArray()
            };

            await using var ins = conn.CreateCommand();
            ins.CommandText = $"INSERT INTO {table}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
            ins.Parameters.AddWithValue(cc.ToString());
            ins.Parameters.AddWithValue(payload.ToJsonString());
            var newIdObj = await ins.ExecuteScalarAsync();
            if (newIdObj is not Guid newId) return Results.Problem("Failed to create resource from employee");
            return Results.Ok(new { id = newId, resourceCode, created = true });
        });

        // 批量更新可用状态
        group.MapPost("/batch-availability", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var ids = root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
                ? idsEl.EnumerateArray().Select(e => Guid.TryParse(e.GetString(), out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g!.Value).ToList()
                : new List<Guid>();
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (ids.Count == 0 || string.IsNullOrWhiteSpace(status))
                return Results.BadRequest(new { error = "ids and status are required" });

            var table = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {table}
                SET payload = jsonb_set(payload, '{{availability_status}}', to_jsonb($2::text), true),
                    updated_at = now()
                WHERE company_code = $1 AND id = ANY($3::uuid[])
                RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(ids.ToArray());

            var updatedIds = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                updatedIds.Add(reader.GetGuid(0));
            }

            return Results.Ok(new { updated = updatedIds.Count, ids = updatedIds });
        });

        // 获取可用资源统计
        group.MapGet("/stats", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var table = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT 
                    COALESCE(resource_type, '') as resource_type,
                    COALESCE(availability_status, '') as availability_status,
                    COUNT(*) as count
                FROM {table}
                WHERE company_code = $1 AND COALESCE(status, 'active') = 'active'
                GROUP BY resource_type, availability_status";
            cmd.Parameters.AddWithValue(cc.ToString());

            var stats = new Dictionary<string, Dictionary<string, long>>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var type = reader.GetString(0);
                var avail = reader.GetString(1);
                var count = reader.GetInt64(2);

                if (!stats.ContainsKey(type))
                    stats[type] = new Dictionary<string, long>();
                stats[type][avail] = count;
            }

            return Results.Ok(new { stats });
        });

        // 技能搜索
        group.MapGet("/search-by-skills", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var skills = req.Query["skills"].FirstOrDefault()?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            var availableOnly = req.Query["availableOnly"].FirstOrDefault() == "true";
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 50;

            if (skills.Length == 0)
                return Results.BadRequest(new { error = "skills parameter is required" });

            var table = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var sql = $@"
                SELECT id, resource_code, display_name, resource_type, availability_status,
                       payload->'skills' as skills,
                       fn_jsonb_numeric(payload, 'default_billing_rate') as default_billing_rate,
                       fn_jsonb_numeric(payload, 'default_cost_rate') as default_cost_rate
                FROM {table}
                WHERE company_code = $1 AND COALESCE(status, 'active') = 'active'";

            if (availableOnly)
                sql += " AND availability_status IN ('available', 'ending_soon')";

            // 技能匹配：payload.skills 为数组时做 ?| 检查
            sql += " AND (payload->'skills') ?| $2";
            sql += " ORDER BY updated_at DESC LIMIT $3";

            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(skills);
            cmd.Parameters.AddWithValue(limit);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    resourceCode = reader.GetString(1),
                    displayName = reader.GetString(2),
                    resourceType = reader.GetString(3),
                    availabilityStatus = reader.IsDBNull(4) ? null : reader.GetString(4),
                    skills = reader.IsDBNull(5) ? "[]" : reader.GetString(5),
                    defaultBillingRate = reader.IsDBNull(6) ? null : (decimal?)reader.GetDecimal(6),
                    defaultCostRate = reader.IsDBNull(7) ? null : (decimal?)reader.GetDecimal(7)
                });
            }

            return Results.Ok(new { data = results, searchedSkills = skills });
        });
    }
}

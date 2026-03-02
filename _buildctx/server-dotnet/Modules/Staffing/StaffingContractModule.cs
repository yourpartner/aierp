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
/// 契約管理モジュール
/// 
/// 基本 CRUD 通过通用端点 /api/staffing_contract 提供
/// 本模块只提供契约终止、续签等业务逻辑端点
/// </summary>
public class StaffingContractModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_contract",
        Name = "契約管理",
        Description = "派遣契約・業務委託契約の管理",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "staffing_project" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_contracts", Label = "menu.staffingContracts", Icon = "Document", Path = "/staffing/contracts", ParentId = "menu_staffing", Order = 255 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        // NOTE: Do NOT prefix with "/api" because Program.cs rewrites "/api/*" -> "/*" when bypassing the dev proxy.
        var group = app.MapGroup("/staffing/contracts")
            .WithTags("Staffing Contract")
            .RequireAuthorization();

        var contractTable = Crud.TableFor("staffing_contract");
        var resourceTable = Crud.TableFor("resource");

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

            var whereSql = "c.company_code = $1";
            var args = new List<object?> { cc.ToString() };
            var idx = 2;

            if (!string.IsNullOrWhiteSpace(status))
            {
                whereSql += $" AND c.status = ${idx}";
                args.Add(status);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(contractType))
            {
                whereSql += $" AND c.contract_type = ${idx}";
                args.Add(contractType);
                idx++;
            }
            if (!string.IsNullOrWhiteSpace(clientId) && Guid.TryParse(clientId, out var cid))
            {
                whereSql += $" AND c.client_partner_id = ${idx}";
                args.Add(cid);
                idx++;
            }

            // total
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {contractTable} c WHERE {whereSql}";
            for (var i = 0; i < args.Count; i++) countCmd.Parameters.AddWithValue(args[i] ?? DBNull.Value);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());

            // data
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.payload,
                       r.payload->>'resource_code' as resource_code,
                       r.payload->>'display_name' as resource_name,
                       bp.partner_code as client_code,
                       bp.payload->>'name' as client_name
                FROM {contractTable} c
                LEFT JOIN {resourceTable} r ON c.resource_id = r.id
                LEFT JOIN businesspartners bp ON c.client_partner_id = bp.id
                WHERE {whereSql}
                ORDER BY c.start_date DESC NULLS LAST, c.updated_at DESC
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
                    contractNo = p.TryGetProperty("contract_no", out var cn) ? cn.GetString() : null,
                    contractType = p.TryGetProperty("contract_type", out var ct) ? ct.GetString() : null,
                    status = p.TryGetProperty("status", out var st) ? st.GetString() : null,
                    startDate = p.TryGetProperty("start_date", out var sd) ? sd.GetString() : null,
                    endDate = p.TryGetProperty("end_date", out var ed) ? ed.GetString() : null,
                    billingRate = p.TryGetProperty("billing_rate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : 0m,
                    billingRateType = p.TryGetProperty("billing_rate_type", out var brt) ? brt.GetString() : "monthly",
                    resourceId = p.TryGetProperty("resource_id", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null,
                    resourceCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    resourceName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    clientPartnerId = p.TryGetProperty("client_partner_id", out var cpid) && cpid.ValueKind == JsonValueKind.String ? cpid.GetString() : null,
                    clientCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    clientName = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }

            return Results.Ok(new { data = rows, total });
        });

        // 详情（snake_case，供编辑对话框使用）
        group.MapGet("/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var json = await Crud.GetDetailJson(ds, contractTable, id, cc.ToString(), "", Array.Empty<object?>());
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
            string contractNo;
            await using (var seqCmd = conn.CreateCommand())
            {
                seqCmd.CommandText = "SELECT nextval('seq_contract_code')";
                var seq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync());
                contractNo = $"CT-{DateTime.Today:yyyy}-{seq:D4}";
            }

            var payload = JsonSerializer.Serialize(new
            {
                contract_no = contractNo,
                contract_type = body.TryGetProperty("contractType", out var ct) ? ct.GetString() : null,
                client_partner_id = body.TryGetProperty("clientPartnerId", out var cpid) ? cpid.GetString() : null,
                resource_id = body.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null,
                project_id = body.TryGetProperty("projectId", out var pid) ? pid.GetString() : null,
                start_date = body.TryGetProperty("startDate", out var sd) ? sd.GetString() : null,
                end_date = body.TryGetProperty("endDate", out var ed) ? ed.GetString() : null,
                work_location = body.TryGetProperty("workLocation", out var wl) ? wl.GetString() : null,
                work_days = body.TryGetProperty("workDays", out var wd) ? wd.GetString() : null,
                work_start_time = body.TryGetProperty("workStartTime", out var wst) ? wst.GetString() : null,
                work_end_time = body.TryGetProperty("workEndTime", out var wet) ? wet.GetString() : null,
                monthly_work_hours = body.TryGetProperty("monthlyWorkHours", out var mwh) && mwh.ValueKind == JsonValueKind.Number ? mwh.GetDecimal() : (decimal?)null,
                billing_rate = body.TryGetProperty("billingRate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : 0m,
                billing_rate_type = body.TryGetProperty("billingRateType", out var brt) ? brt.GetString() : "monthly",
                overtime_rate_multiplier = body.TryGetProperty("overtimeRateMultiplier", out var orm) && orm.ValueKind == JsonValueKind.Number ? orm.GetDecimal() : (decimal?)null,
                settlement_type = body.TryGetProperty("settlementType", out var st) ? st.GetString() : "range",
                settlement_lower_hours = body.TryGetProperty("settlementLowerHours", out var slh) && slh.ValueKind == JsonValueKind.Number ? slh.GetDecimal() : (decimal?)null,
                settlement_upper_hours = body.TryGetProperty("settlementUpperHours", out var suh) && suh.ValueKind == JsonValueKind.Number ? suh.GetDecimal() : (decimal?)null,
                cost_rate = body.TryGetProperty("costRate", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetDecimal() : (decimal?)null,
                cost_rate_type = body.TryGetProperty("costRateType", out var crt) ? crt.GetString() : null,
                notes = body.TryGetProperty("notes", out var n) ? n.GetString() : null,
                status = "active"
            });

            var inserted = await Crud.InsertRawJson(ds, contractTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create contract");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, contractNo });
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
                contract_type = body.TryGetProperty("contractType", out var ct) ? ct.GetString() : null,
                client_partner_id = body.TryGetProperty("clientPartnerId", out var cpid) ? cpid.GetString() : null,
                resource_id = body.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null,
                project_id = body.TryGetProperty("projectId", out var pid) ? pid.GetString() : null,
                start_date = body.TryGetProperty("startDate", out var sd) ? sd.GetString() : null,
                end_date = body.TryGetProperty("endDate", out var ed) ? ed.GetString() : null,
                work_location = body.TryGetProperty("workLocation", out var wl) ? wl.GetString() : null,
                work_days = body.TryGetProperty("workDays", out var wd) ? wd.GetString() : null,
                work_start_time = body.TryGetProperty("workStartTime", out var wst) ? wst.GetString() : null,
                work_end_time = body.TryGetProperty("workEndTime", out var wet) ? wet.GetString() : null,
                monthly_work_hours = body.TryGetProperty("monthlyWorkHours", out var mwh) && mwh.ValueKind == JsonValueKind.Number ? mwh.GetDecimal() : (decimal?)null,
                billing_rate = body.TryGetProperty("billingRate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetDecimal() : (decimal?)null,
                billing_rate_type = body.TryGetProperty("billingRateType", out var brt) ? brt.GetString() : null,
                overtime_rate_multiplier = body.TryGetProperty("overtimeRateMultiplier", out var orm) && orm.ValueKind == JsonValueKind.Number ? orm.GetDecimal() : (decimal?)null,
                settlement_type = body.TryGetProperty("settlementType", out var st) ? st.GetString() : null,
                settlement_lower_hours = body.TryGetProperty("settlementLowerHours", out var slh) && slh.ValueKind == JsonValueKind.Number ? slh.GetDecimal() : (decimal?)null,
                settlement_upper_hours = body.TryGetProperty("settlementUpperHours", out var suh) && suh.ValueKind == JsonValueKind.Number ? suh.GetDecimal() : (decimal?)null,
                cost_rate = body.TryGetProperty("costRate", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetDecimal() : (decimal?)null,
                cost_rate_type = body.TryGetProperty("costRateType", out var crt) ? crt.GetString() : null,
                notes = body.TryGetProperty("notes", out var n) ? n.GetString() : null
            });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE {contractTable} SET payload = payload || $3::jsonb, updated_at = now() WHERE id = $1 AND company_code = $2 RETURNING id";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(patch);
            var updated = await cmd.ExecuteScalarAsync();
            if (updated == null) return Results.NotFound();
            return Results.Ok(new { id, updated = true });
        });

        // ========== 契约终止 ==========
        group.MapPost("/{id:guid}/terminate", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_contract", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var terminationDate = root.TryGetProperty("terminationDate", out var td) && td.ValueKind == JsonValueKind.String
                ? DateTime.TryParse(td.GetString(), out var d) ? d : DateTime.Today : DateTime.Today;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    UPDATE {contractTable}
                    SET payload = payload
                        || jsonb_build_object(
                            'status','terminated',
                            'end_date',$3::text,
                            'termination_reason',$4::text
                        ),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2
                    RETURNING resource_id";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(cc.ToString());
                cmd.Parameters.AddWithValue(terminationDate.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue(reason ?? string.Empty);

                var resourceId = await cmd.ExecuteScalarAsync() as Guid?;
                if (resourceId == null)
                {
                    await tx.RollbackAsync();
                    return Results.NotFound();
                }

                // 将资源状态改为可用
                if (resourceId.HasValue)
                {
                    await using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = $@"
                        UPDATE {resourceTable}
                        SET payload = (payload - 'current_assignment_end')
                          || jsonb_build_object('availability_status','available'),
                            updated_at = now()
                        WHERE id = $1 AND company_code = $2";
                    updateCmd.Parameters.AddWithValue(resourceId.Value);
                    updateCmd.Parameters.AddWithValue(cc.ToString());
                    await updateCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Results.Ok(new { terminated = true, resourceId });
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // ========== 契约续签（延长） ==========
        group.MapPost("/{id:guid}/renew", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var newEndDate = root.TryGetProperty("newEndDate", out var ned) && ned.ValueKind == JsonValueKind.String
                ? DateTime.TryParse(ned.GetString(), out var d) ? d : (DateTime?)null : null;
            var newBillingRate = root.TryGetProperty("newBillingRate", out var nbr) && nbr.ValueKind == JsonValueKind.Number
                ? nbr.GetDecimal() : (decimal?)null;

            if (newEndDate == null)
                return Results.BadRequest(new { error = "newEndDate is required" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_contract", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "update", user))
                    return Results.StatusCode(403);
            }

            var contractTable = Crud.TableFor("staffing_contract");
            var resourceTable = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();

            // Update payload
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {contractTable}
                SET payload = payload
                    || jsonb_build_object('end_date',$3::text)
                    || CASE WHEN $4::numeric IS NULL THEN '{{}}'::jsonb ELSE jsonb_build_object('billing_rate',$4) END,
                    updated_at = now()
                WHERE id = $1 AND company_code = $2
                RETURNING id, resource_id, billing_rate";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(newEndDate.Value.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue(newBillingRate.HasValue ? (object)newBillingRate.Value : DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return Results.NotFound();
            var resourceId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            var billingRate = reader.IsDBNull(2) ? null : (decimal?)reader.GetDecimal(2);

            if (resourceId.HasValue)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = $@"
                    UPDATE {resourceTable}
                    SET payload = payload || jsonb_build_object('current_assignment_end',$2::text),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $3";
                updateCmd.Parameters.AddWithValue(resourceId.Value);
                updateCmd.Parameters.AddWithValue(newEndDate.Value.ToString("yyyy-MM-dd"));
                updateCmd.Parameters.AddWithValue(cc.ToString());
                await updateCmd.ExecuteNonQueryAsync();
            }

            return Results.Ok(new { id, newEndDate, billingRate, renewed = true });
        });

        // ========== 从候选人创建契约 ==========
        group.MapPost("/from-candidate/{candidateId:guid}", async (Guid candidateId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_contract", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "create", user))
                    return Results.StatusCode(403);
            }

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var candTable = Crud.TableFor("staffing_project_candidate");
            var projTable = Crud.TableFor("staffing_project");
            var contractTable = Crud.TableFor("staffing_contract");
            var resTable = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                Guid projectId, resourceId, clientPartnerId;
                decimal billingRate;
                string contractType;

                // candidate
                await using (var candCmd = conn.CreateCommand())
                {
                    candCmd.CommandText = $@"
                        SELECT project_id, resource_id,
                               COALESCE(final_rate, proposed_rate, 0) as rate
                        FROM {candTable}
                        WHERE id = $1 AND company_code = $2";
                    candCmd.Parameters.AddWithValue(candidateId);
                    candCmd.Parameters.AddWithValue(cc.ToString());
                    await using var candReader = await candCmd.ExecuteReaderAsync();
                    if (!await candReader.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound(new { error = "Candidate not found" });
                    }
                    projectId = candReader.GetGuid(0);
                    resourceId = candReader.GetGuid(1);
                    billingRate = candReader.GetDecimal(2);
                }

                // project (client + contract type)
                await using (var projCmd = conn.CreateCommand())
                {
                    projCmd.CommandText = $@"
                        SELECT client_partner_id, COALESCE(contract_type,'ses') as contract_type
                        FROM {projTable}
                        WHERE id = $1 AND company_code = $2";
                    projCmd.Parameters.AddWithValue(projectId);
                    projCmd.Parameters.AddWithValue(cc.ToString());
                    await using var projReader = await projCmd.ExecuteReaderAsync();
                    if (!await projReader.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound(new { error = "Project not found" });
                    }
                    clientPartnerId = projReader.GetGuid(0);
                    contractType = projReader.IsDBNull(1) ? "ses" : projReader.GetString(1);
                }

                // 从请求体覆盖
                if (root.TryGetProperty("billingRate", out var br) && br.ValueKind == JsonValueKind.Number)
                    billingRate = br.GetDecimal();
                if (root.TryGetProperty("contractType", out var ct) && ct.ValueKind == JsonValueKind.String)
                    contractType = ct.GetString() ?? contractType;

                var startDate = root.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String
                    ? DateTime.TryParse(sd.GetString(), out var d) ? d : DateTime.Today.AddDays(1)
                    : DateTime.Today.AddDays(1);
                var endDate = root.TryGetProperty("endDate", out var ed) && ed.ValueKind == JsonValueKind.String
                    ? DateTime.TryParse(ed.GetString(), out var d2) ? (DateTime?)d2 : null
                    : null;

                // 生成契约编号
                string contractNo;
                await using (var seqCmd = conn.CreateCommand())
                {
                    seqCmd.CommandText = "SELECT nextval('seq_contract_code')";
                    var seq = await seqCmd.ExecuteScalarAsync();
                    contractNo = $"CT-{DateTime.Now:yyyy}-{seq:D4}";
                }

                var contractPayload = new JsonObject
                {
                    ["contract_no"] = contractNo,
                    ["project_id"] = projectId.ToString(),
                    ["resource_id"] = resourceId.ToString(),
                    ["client_partner_id"] = clientPartnerId.ToString(),
                    ["contract_type"] = contractType,
                    ["status"] = "active",
                    ["start_date"] = startDate.ToString("yyyy-MM-dd"),
                    ["end_date"] = endDate.HasValue ? endDate.Value.ToString("yyyy-MM-dd") : null,
                    ["billing_rate"] = billingRate,
                    ["billing_rate_type"] = "monthly"
                };

                Guid contractId;
                await using (var createCmd = conn.CreateCommand())
                {
                    createCmd.CommandText = $"INSERT INTO {contractTable}(company_code, payload) VALUES ($1, $2::jsonb) RETURNING id";
                    createCmd.Parameters.AddWithValue(cc.ToString());
                    createCmd.Parameters.AddWithValue(contractPayload.ToJsonString());
                    var idObj = await createCmd.ExecuteScalarAsync();
                    if (idObj is not Guid gid)
                    {
                        await tx.RollbackAsync();
                        return Results.Problem("Failed to create contract");
                    }
                    contractId = gid;
                }

                // 更新候选人状态
                await using var updateCandCmd = conn.CreateCommand();
                updateCandCmd.CommandText = $@"
                    UPDATE {candTable}
                    SET payload = payload || jsonb_build_object('status','accepted','contract_id',$2::text),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $3";
                updateCandCmd.Parameters.AddWithValue(candidateId);
                updateCandCmd.Parameters.AddWithValue(contractId.ToString());
                updateCandCmd.Parameters.AddWithValue(cc.ToString());
                await updateCandCmd.ExecuteNonQueryAsync();

                // 更新资源状态
                await using var updateResCmd = conn.CreateCommand();
                updateResCmd.CommandText = $@"
                    UPDATE {resTable}
                    SET payload = (payload
                        || jsonb_build_object('availability_status','assigned')
                        || CASE WHEN $2::text IS NULL OR $2::text = '' THEN '{{}}'::jsonb ELSE jsonb_build_object('current_assignment_end',$2::text) END),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $3";
                updateResCmd.Parameters.AddWithValue(resourceId);
                updateResCmd.Parameters.AddWithValue(endDate.HasValue ? endDate.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
                updateResCmd.Parameters.AddWithValue(cc.ToString());
                await updateResCmd.ExecuteNonQueryAsync();

                // 更新案件 filled_count
                await using var updateProjCmd = conn.CreateCommand();
                updateProjCmd.CommandText = $@"
                    UPDATE {projTable}
                    SET payload = jsonb_set(
                        payload,
                        '{{filled_count}}',
                        to_jsonb((COALESCE(fn_jsonb_numeric(payload,'filled_count'),0) + 1)::int),
                        true
                    ),
                        updated_at = now()
                    WHERE id = $1 AND company_code = $2";
                updateProjCmd.Parameters.AddWithValue(projectId);
                updateProjCmd.Parameters.AddWithValue(cc.ToString());
                await updateProjCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();

                return Results.Ok(new
                {
                    contractId,
                    contractNo,
                    projectId,
                    resourceId,
                    created = true
                });
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // ========== 契约历史查询 ==========
        group.MapGet("/by-resource/{resourceId:guid}", async (Guid resourceId, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_contract", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var contractTable = Crud.TableFor("staffing_contract");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.contract_no, c.contract_type, c.start_date, c.end_date,
                       c.billing_rate, c.cost_rate, c.status,
                       bp.payload->>'name' as client_name
                FROM {contractTable} c
                LEFT JOIN businesspartners bp ON c.client_partner_id = bp.id
                WHERE c.company_code = $1 AND c.resource_id = $2
                ORDER BY c.start_date DESC NULLS LAST, c.created_at DESC";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(resourceId);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    contractNo = reader.GetString(1),
                    contractType = reader.GetString(2),
                    startDate = reader.GetDateTime(3),
                    endDate = reader.IsDBNull(4) ? null : (DateTime?)reader.GetDateTime(4),
                    billingRate = reader.GetDecimal(5),
                    costRate = reader.IsDBNull(6) ? null : (decimal?)reader.GetDecimal(6),
                    status = reader.GetString(7),
                    clientName = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }

            return Results.Ok(new { data = results });
        });

        // ========== 即将到期契约查询 ==========
        group.MapGet("/expiring", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var days = int.TryParse(req.Query["days"].FirstOrDefault(), out var d) ? d : 30;

            var schemaDoc = await SchemasService.GetActiveSchema(ds, "staffing_contract", cc.ToString());
            if (schemaDoc is not null)
            {
                var user = Auth.GetUserCtx(req);
                if (!Auth.IsActionAllowed(schemaDoc, "read", user))
                    return Results.StatusCode(403);
            }

            var contractTable = Crud.TableFor("staffing_contract");
            var resTable = Crud.TableFor("resource");
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT c.id, c.contract_no, c.contract_type, c.start_date, c.end_date,
                       c.billing_rate, c.status,
                       r.resource_code, r.display_name as resource_name,
                       bp.payload->>'name' as client_name,
                       c.end_date - CURRENT_DATE as days_remaining
                FROM {contractTable} c
                LEFT JOIN {resTable} r ON c.resource_id = r.id
                LEFT JOIN businesspartners bp ON c.client_partner_id = bp.id
                WHERE c.company_code = $1 
                  AND c.status = 'active'
                  AND c.end_date IS NOT NULL
                  AND c.end_date <= CURRENT_DATE + $2
                ORDER BY c.end_date";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(days);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    contractNo = reader.GetString(1),
                    contractType = reader.GetString(2),
                    startDate = reader.GetDateTime(3),
                    endDate = reader.GetDateTime(4),
                    billingRate = reader.GetDecimal(5),
                    status = reader.GetString(6),
                    resourceCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                    resourceName = reader.IsDBNull(8) ? null : reader.GetString(8),
                    clientName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    daysRemaining = reader.GetInt32(10)
                });
            }

            return Results.Ok(new { data = results, withinDays = days });
        });
    }
}

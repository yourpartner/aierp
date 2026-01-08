using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using Server.Infrastructure.Modules;

namespace Server.Modules.Staffing;

/// <summary>
/// 邮件引擎模块 - 自动收发邮件、模板管理、队列处理
/// </summary>
public class EmailEngineModule : ModuleBase
{
    public override ModuleInfo GetInfo() => new()
    {
        Id = "staffing_email",
        Name = "邮件自动化",
        Description = "自动收取和发送邮件、AI内容识别、邮件模板管理",
        Category = ModuleCategory.Staffing,
        Version = "1.0.0",
        Dependencies = new[] { "ai_core" },
        Menus = new[]
        {
            new MenuConfig { Id = "menu_staffing_email", Label = "menu.staffingEmail", Icon = "Message", Path = "/staffing/email", ParentId = "menu_staffing", Order = 270 },
            new MenuConfig { Id = "menu_staffing_email_inbox", Label = "menu.staffingEmailInbox", Icon = "ChatLineSquare", Path = "/staffing/email/inbox", ParentId = "menu_staffing_email", Order = 271 },
            new MenuConfig { Id = "menu_staffing_email_templates", Label = "menu.staffingEmailTemplates", Icon = "Document", Path = "/staffing/email/templates", ParentId = "menu_staffing_email", Order = 272 },
            new MenuConfig { Id = "menu_staffing_email_rules", Label = "menu.staffingEmailRules", Icon = "Setting", Path = "/staffing/email/rules", ParentId = "menu_staffing_email", Order = 273 },
        }
    };

    public override void MapEndpoints(WebApplication app)
    {
        var emailAccountTable = Crud.TableFor("staffing_email_account");
        var emailTemplateTable = Crud.TableFor("staffing_email_template");
        var emailMessageTable = Crud.TableFor("staffing_email_message");
        var emailQueueTable = Crud.TableFor("staffing_email_queue");
        var emailRuleTable = Crud.TableFor("staffing_email_rule");

        // ========== 邮件账户配置 ==========
        app.MapGet("/staffing/email/accounts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, payload
                FROM {emailAccountTable}
                WHERE company_code = $1
                ORDER BY (payload->>'is_default') DESC, payload->>'account_name'";
            cmd.Parameters.AddWithValue(cc.ToString());

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var p = doc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    accountName = p.TryGetProperty("account_name", out var an) ? an.GetString() : null,
                    emailAddress = p.TryGetProperty("email_address", out var ea) ? ea.GetString() : null,
                    accountType = p.TryGetProperty("account_type", out var at) ? at.GetString() : null,
                    isDefault = p.TryGetProperty("is_default", out var def) && def.ValueKind == JsonValueKind.True,
                    isActive = p.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.True,
                    lastSyncAt = p.TryGetProperty("last_sync_at", out var lsa) ? lsa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    createdAt = p.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : (DateTimeOffset?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        app.MapPost("/staffing/email/accounts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();

            var password = body.TryGetProperty("password", out var pw) ? pw.GetString() : null;
            string? passwordEnc = null;
            if (!string.IsNullOrEmpty(password))
            {
                try { passwordEnc = SecretProtector.ProtectToBase64(password); }
                catch (Exception ex) { return Results.Problem($"Failed to protect password: {ex.Message}"); }
            }

            var payload = JsonSerializer.Serialize(new
            {
                account_name = body.GetProperty("accountName").GetString()!,
                email_address = body.GetProperty("emailAddress").GetString()!,
                account_type = body.TryGetProperty("accountType", out var at) ? at.GetString()! : "imap",
                imap_host = body.TryGetProperty("imapHost", out var ih) ? ih.GetString()! : "",
                imap_port = body.TryGetProperty("imapPort", out var ip) ? ip.GetInt32() : 993,
                smtp_host = body.TryGetProperty("smtpHost", out var sh) ? sh.GetString()! : "",
                smtp_port = body.TryGetProperty("smtpPort", out var sp) ? sp.GetInt32() : 587,
                username = body.TryGetProperty("username", out var un) ? un.GetString()! : "",
                password_enc = passwordEnc,
                is_default = body.TryGetProperty("isDefault", out var def) && def.GetBoolean(),
                is_active = true
            });

            var inserted = await Crud.InsertRawJson(ds, emailAccountTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create email account");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, message = "Created" });
        }).RequireAuthorization();

        // ========== 邮件模板管理 ==========
        app.MapGet("/staffing/email/templates", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, payload
                FROM {emailTemplateTable}
                WHERE company_code = $1
                ORDER BY payload->>'category', payload->>'template_name'";
            cmd.Parameters.AddWithValue(cc.ToString());

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var p = doc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    templateCode = p.TryGetProperty("template_code", out var tc) ? tc.GetString() : null,
                    templateName = p.TryGetProperty("template_name", out var tn) ? tn.GetString() : null,
                    category = p.TryGetProperty("category", out var cat) ? cat.GetString() : null,
                    subjectTemplate = p.TryGetProperty("subject_template", out var st) ? st.GetString() : null,
                    bodyTemplate = p.TryGetProperty("body_template", out var bt) ? bt.GetString() : null,
                    variables = p.TryGetProperty("variables", out var vars) ? vars.GetRawText() : "[]",
                    isActive = p.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.True,
                    createdAt = p.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : (DateTimeOffset?)null,
                    updatedAt = p.TryGetProperty("updated_at", out var ua) ? ua.GetDateTimeOffset() : (DateTimeOffset?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        app.MapPost("/staffing/email/templates", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var payload = JsonSerializer.Serialize(new
            {
                template_code = body.GetProperty("templateCode").GetString()!,
                template_name = body.GetProperty("templateName").GetString()!,
                category = body.TryGetProperty("category", out var cat) ? cat.GetString()! : "general",
                subject_template = body.GetProperty("subjectTemplate").GetString()!,
                body_template = body.GetProperty("bodyTemplate").GetString()!,
                variables = body.TryGetProperty("variables", out var vars) ? vars : JsonDocument.Parse("[]").RootElement,
                is_active = true
            });

            var inserted = await Crud.InsertRawJson(ds, emailTemplateTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create email template");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, message = "Created" });
        }).RequireAuthorization();

        app.MapPut("/staffing/email/templates/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var payload = JsonSerializer.Serialize(new
            {
                template_name = body.GetProperty("templateName").GetString()!,
                category = body.TryGetProperty("category", out var cat) ? cat.GetString()! : "general",
                subject_template = body.GetProperty("subjectTemplate").GetString()!,
                body_template = body.GetProperty("bodyTemplate").GetString()!,
                variables = body.TryGetProperty("variables", out var vars) ? vars : JsonDocument.Parse("[]").RootElement,
                is_active = body.TryGetProperty("isActive", out var ia) ? ia.GetBoolean() : true
            });

            var updated = await Crud.UpdateRawJson(ds, emailTemplateTable, id, cc.ToString(), payload);
            if (updated is null) return Results.NotFound();
            return Results.Ok(new { message = "Updated" });
        }).RequireAuthorization();

        // ========== 收件箱 ==========
        app.MapGet("/staffing/email/inbox", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var folder = req.Query["folder"].FirstOrDefault() ?? "inbox";
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? o : 0;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var sql = $@"
                SELECT id, payload
                FROM {emailMessageTable}
                WHERE company_code = $1 AND payload->>'folder' = $2";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(folder);
            var idx = 3;
            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND payload->>'status' = ${idx}";
                cmd.Parameters.AddWithValue(status);
                idx++;
            }
            sql += $" ORDER BY payload->>'received_at' DESC LIMIT ${idx} OFFSET ${idx + 1}";
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);
            cmd.CommandText = sql;

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var p = doc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    messageId = p.TryGetProperty("message_id", out var mid) ? mid.GetString() : null,
                    fromAddress = p.TryGetProperty("from_address", out var fa) ? fa.GetString() : null,
                    fromName = p.TryGetProperty("from_name", out var fn) ? fn.GetString() : null,
                    toAddresses = p.TryGetProperty("to_addresses", out var ta) ? ta.GetString() : null,
                    subject = p.TryGetProperty("subject", out var s) ? s.GetString() : null,
                    bodyText = p.TryGetProperty("body_text", out var bt) ? bt.GetString() : null,
                    bodyHtml = p.TryGetProperty("body_html", out var bh) ? bh.GetString() : null,
                    receivedAt = p.TryGetProperty("received_at", out var ra) ? ra.GetDateTimeOffset() : (DateTimeOffset?)null,
                    status = p.TryGetProperty("status", out var st) ? st.GetString() : null,
                    isRead = p.TryGetProperty("is_read", out var ir) && ir.ValueKind == JsonValueKind.True,
                    parsedIntent = p.TryGetProperty("parsed_intent", out var pi) ? pi.GetString() : null,
                    parsedData = p.TryGetProperty("parsed_data", out var pd) ? pd.GetRawText() : null,
                    linkedEntityType = p.TryGetProperty("linked_entity_type", out var let) ? let.GetString() : null,
                    linkedEntityId = p.TryGetProperty("linked_entity_id", out var lei) && lei.ValueKind == JsonValueKind.String
                        ? Guid.TryParse(lei.GetString(), out var g) ? g : (Guid?)null
                        : (Guid?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 标记已读
        app.MapPut("/staffing/email/inbox/{id:guid}/read", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                UPDATE {emailMessageTable}
                SET payload = payload || jsonb_build_object('is_read', true), updated_at = now()
                WHERE id = $1 AND company_code = $2";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());
            await cmd.ExecuteNonQueryAsync();
            return Results.Ok(new { message = "Marked as read" });
        }).RequireAuthorization();

        // AI解析邮件（简化版：意图识别 + 取引先匹配）
        app.MapPost("/staffing/email/inbox/{id:guid}/parse", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            await using var cmdGet = conn.CreateCommand();
            cmdGet.CommandText = $@"SELECT payload FROM {emailMessageTable} WHERE id = $1 AND company_code = $2";
            cmdGet.Parameters.AddWithValue(id);
            cmdGet.Parameters.AddWithValue(cc.ToString());
            var json = await cmdGet.ExecuteScalarAsync() as string;
            if (json == null) return Results.NotFound(new { error = "Email not found" });

            using var emailDoc = JsonDocument.Parse(json);
            var p = emailDoc.RootElement;
            var fromAddress = p.TryGetProperty("from_address", out var fa) ? fa.GetString() : null;
            var subject = p.TryGetProperty("subject", out var s) ? s.GetString() : null;

            // 匹配取引先（简化：按 email 模糊）
            Guid? partnerId = null;
            string? partnerName = null;
            if (!string.IsNullOrEmpty(fromAddress))
            {
                await using var cmdMatch = conn.CreateCommand();
                cmdMatch.CommandText = @"
                    SELECT id, payload->>'name'
                    FROM businesspartners
                    WHERE company_code = $1 AND payload->>'email' ILIKE $2
                    LIMIT 1";
                cmdMatch.Parameters.AddWithValue(cc.ToString());
                cmdMatch.Parameters.AddWithValue($"%{fromAddress}%");
                await using var r = await cmdMatch.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    partnerId = r.GetGuid(0);
                    partnerName = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }

            var intent = "unknown";
            if (!string.IsNullOrEmpty(subject))
            {
                var subjectLower = subject.ToLower();
                if (subjectLower.Contains("要員") || subjectLower.Contains("募集") || subjectLower.Contains("案件")) intent = "project_request";
                else if (subjectLower.Contains("契約") || subjectLower.Contains("確認")) intent = "contract_confirm";
                else if (subjectLower.Contains("請求") || subjectLower.Contains("invoice")) intent = "invoice_related";
                else if (subjectLower.Contains("入金") || subjectLower.Contains("支払")) intent = "payment_confirm";
            }

            var parsedData = JsonSerializer.Serialize(new { matchedPartnerId = partnerId, matchedPartnerName = partnerName });
            await using var cmdUpdate = conn.CreateCommand();
            cmdUpdate.CommandText = $@"
                UPDATE {emailMessageTable}
                SET payload = payload
                    || jsonb_build_object('parsed_intent', $3::text)
                    || jsonb_build_object('parsed_data', $4::jsonb)
                    || jsonb_build_object('status', 'parsed'),
                    updated_at = now()
                WHERE id = $1 AND company_code = $2";
            cmdUpdate.Parameters.AddWithValue(id);
            cmdUpdate.Parameters.AddWithValue(cc.ToString());
            cmdUpdate.Parameters.AddWithValue(intent);
            cmdUpdate.Parameters.AddWithValue(parsedData);
            await cmdUpdate.ExecuteNonQueryAsync();

            return Results.Ok(new
            {
                intent,
                matchedPartner = partnerId != null ? new { id = partnerId, name = partnerName } : null
            });
        }).RequireAuthorization();

        // ========== 发件箱/队列 ==========
        app.MapGet("/staffing/email/outbox", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            var offset = int.TryParse(req.Query["offset"].FirstOrDefault(), out var o) ? o : 0;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var sql = $@"
                SELECT id, payload
                FROM {emailQueueTable}
                WHERE company_code = $1";
            cmd.Parameters.AddWithValue(cc.ToString());
            var idx = 2;
            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND payload->>'status' = ${idx}";
                cmd.Parameters.AddWithValue(status);
                idx++;
            }
            sql += $" ORDER BY payload->>'created_at' DESC LIMIT ${idx} OFFSET ${idx + 1}";
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);
            cmd.CommandText = sql;

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var p = doc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    toAddresses = p.TryGetProperty("to_addresses", out var ta) ? ta.GetString() : null,
                    ccAddresses = p.TryGetProperty("cc_addresses", out var cca) ? cca.GetString() : null,
                    subject = p.TryGetProperty("subject", out var s) ? s.GetString() : null,
                    bodyHtml = p.TryGetProperty("body_html", out var bh) ? bh.GetString() : null,
                    templateId = p.TryGetProperty("template_id", out var tid) && tid.ValueKind == JsonValueKind.String
                        ? Guid.TryParse(tid.GetString(), out var g) ? g : (Guid?)null
                        : (Guid?)null,
                    templateData = p.TryGetProperty("template_data", out var td) ? td.GetRawText() : null,
                    status = p.TryGetProperty("status", out var st) ? st.GetString() : null,
                    scheduledAt = p.TryGetProperty("scheduled_at", out var sa) ? sa.GetDateTimeOffset() : (DateTimeOffset?)null,
                    sentAt = p.TryGetProperty("sent_at", out var sea) ? sea.GetDateTimeOffset() : (DateTimeOffset?)null,
                    errorMessage = p.TryGetProperty("error_message", out var em) ? em.GetString() : null,
                    linkedEntityType = p.TryGetProperty("linked_entity_type", out var let) ? let.GetString() : null,
                    linkedEntityId = p.TryGetProperty("linked_entity_id", out var lei) && lei.ValueKind == JsonValueKind.String
                        ? Guid.TryParse(lei.GetString(), out var g2) ? g2 : (Guid?)null
                        : (Guid?)null,
                    createdAt = p.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : (DateTimeOffset?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        // 创建发送任务
        app.MapPost("/staffing/email/send", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var payload = JsonSerializer.Serialize(new
            {
                to_addresses = body.GetProperty("toAddresses").GetString()!,
                cc_addresses = body.TryGetProperty("ccAddresses", out var cca) ? cca.GetString() : null,
                subject = body.GetProperty("subject").GetString()!,
                body_html = body.TryGetProperty("bodyHtml", out var bh) ? bh.GetString() : null,
                template_id = body.TryGetProperty("templateId", out var tid) && tid.TryGetGuid(out var tidVal) ? tidVal : (Guid?)null,
                template_data = body.TryGetProperty("templateData", out var td) ? td : (JsonElement?)null,
                status = "pending",
                scheduled_at = body.TryGetProperty("scheduledAt", out var sa) && sa.ValueKind == JsonValueKind.String ? sa.GetString() : null,
                linked_entity_type = body.TryGetProperty("linkedEntityType", out var let) ? let.GetString() : null,
                linked_entity_id = body.TryGetProperty("linkedEntityId", out var lei) && lei.TryGetGuid(out var leiVal) ? leiVal : (Guid?)null
            });

            var inserted = await Crud.InsertRawJson(ds, emailQueueTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to queue email");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, message = "Queued for sending" });
        }).RequireAuthorization();

        // 使用模板发送
        app.MapPost("/staffing/email/send-template", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var templateCode = body.GetProperty("templateCode").GetString()!;
            var toAddresses = body.GetProperty("toAddresses").GetString()!;
            var variables = body.TryGetProperty("variables", out var vars) ? vars : default;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmdTpl = conn.CreateCommand();
            cmdTpl.CommandText = $@"
                SELECT id, payload
                FROM {emailTemplateTable}
                WHERE company_code = $1 AND payload->>'template_code' = $2 AND payload->>'is_active' = 'true'
                LIMIT 1";
            cmdTpl.Parameters.AddWithValue(cc.ToString());
            cmdTpl.Parameters.AddWithValue(templateCode);

            Guid? templateId = null;
            string? subjectTpl = null;
            string? bodyTpl = null;
            await using (var reader = await cmdTpl.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    templateId = reader.GetGuid(0);
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    var p = doc.RootElement;
                    subjectTpl = p.TryGetProperty("subject_template", out var st) ? st.GetString() : null;
                    bodyTpl = p.TryGetProperty("body_template", out var bt) ? bt.GetString() : null;
                }
            }
            if (templateId == null) return Results.NotFound(new { error = "Template not found" });

            var finalSubject = subjectTpl ?? "";
            var finalBody = bodyTpl ?? "";
            if (variables.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in variables.EnumerateObject())
                {
                    var placeholder = "{{" + prop.Name + "}}";
                    var value = prop.Value.GetString() ?? "";
                    finalSubject = finalSubject.Replace(placeholder, value);
                    finalBody = finalBody.Replace(placeholder, value);
                }
            }

            var payload = JsonSerializer.Serialize(new
            {
                to_addresses = toAddresses,
                subject = finalSubject,
                body_html = finalBody,
                template_id = templateId,
                template_data = variables.ValueKind == JsonValueKind.Object ? variables : JsonDocument.Parse("{}").RootElement,
                status = "pending",
                linked_entity_type = body.TryGetProperty("linkedEntityType", out var let) ? let.GetString() : null,
                linked_entity_id = body.TryGetProperty("linkedEntityId", out var lei) && lei.TryGetGuid(out var leiVal) ? leiVal : (Guid?)null
            });

            var inserted = await Crud.InsertRawJson(ds, emailQueueTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to queue email from template");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, subject = finalSubject, message = "Queued for sending" });
        }).RequireAuthorization();

        // ========== 自动化规则 ==========
        app.MapGet("/staffing/email/rules", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, payload
                FROM {emailRuleTable}
                WHERE company_code = $1
                ORDER BY payload->>'rule_name'";
            cmd.Parameters.AddWithValue(cc.ToString());

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var doc = JsonDocument.Parse(reader.GetString(1));
                var p = doc.RootElement;
                results.Add(new
                {
                    id = reader.GetGuid(0),
                    ruleName = p.TryGetProperty("rule_name", out var rn) ? rn.GetString() : null,
                    triggerType = p.TryGetProperty("trigger_type", out var tt) ? tt.GetString() : null,
                    triggerConditions = p.TryGetProperty("trigger_conditions", out var tc) ? tc.GetRawText() : null,
                    actionType = p.TryGetProperty("action_type", out var at) ? at.GetString() : null,
                    actionConfig = p.TryGetProperty("action_config", out var ac) ? ac.GetRawText() : null,
                    templateId = p.TryGetProperty("template_id", out var tid) && tid.ValueKind == JsonValueKind.String
                        ? Guid.TryParse(tid.GetString(), out var g) ? g : (Guid?)null
                        : (Guid?)null,
                    isActive = p.TryGetProperty("is_active", out var ia) && ia.ValueKind == JsonValueKind.True,
                    createdAt = p.TryGetProperty("created_at", out var ca) ? ca.GetDateTimeOffset() : (DateTimeOffset?)null
                });
            }

            return Results.Ok(new { data = results });
        }).RequireAuthorization();

        app.MapPost("/staffing/email/rules", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var body = await req.ReadFromJsonAsync<JsonElement>();
            var payload = JsonSerializer.Serialize(new
            {
                rule_name = body.GetProperty("ruleName").GetString()!,
                trigger_type = body.GetProperty("triggerType").GetString()!,
                trigger_conditions = body.TryGetProperty("triggerConditions", out var tc) ? tc : JsonDocument.Parse("{}").RootElement,
                action_type = body.GetProperty("actionType").GetString()!,
                action_config = body.TryGetProperty("actionConfig", out var ac) ? ac : JsonDocument.Parse("{}").RootElement,
                template_id = body.TryGetProperty("templateId", out var tid) && tid.TryGetGuid(out var tidVal) ? tidVal : (Guid?)null,
                is_active = true
            });

            var inserted = await Crud.InsertRawJson(ds, emailRuleTable, cc.ToString(), payload);
            if (inserted is null) return Results.Problem("Failed to create email rule");
            var id = JsonDocument.Parse(inserted).RootElement.GetProperty("id").GetGuid();
            return Results.Ok(new { id, message = "Created" });
        }).RequireAuthorization();

        // Update rule
        app.MapPut("/staffing/email/rules/{id:guid}", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: req.HttpContext.RequestAborted);
            var payload = body.RootElement.GetRawText();

            await using var conn = await ds.OpenConnectionAsync(req.HttpContext.RequestAborted);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE stf_email_rules
                   SET payload = $1::jsonb,
                       updated_at = now()
                 WHERE company_code = $2 AND id = $3
                 RETURNING to_jsonb(stf_email_rules)";
            cmd.Parameters.AddWithValue(payload);
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(id);
            var json = (string?)await cmd.ExecuteScalarAsync(req.HttpContext.RequestAborted);
            if (json is null) return Results.NotFound(new { error = "not found" });
            return Results.Text(json, "application/json");
        }).RequireAuthorization();
    }
}


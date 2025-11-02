using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Server.Infrastructure;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Server.Modules;

public static class CrmModule
{
    public static void MapCrmModule(this WebApplication app)
    {
        // 报价确认 -> 生成受注（SO），并将报价状态置为 accepted
        app.MapPost("/crm/quote/{id:guid}/confirm", async (HttpRequest req, Guid id, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            // 读取报价
            await using var conn = await ds.OpenConnectionAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload FROM quotes WHERE id=$1 AND company_code=$2 LIMIT 1";
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(cc.ToString());
                await using var rd = await cmd.ExecuteReaderAsync();
                if (!await rd.ReadAsync()) return Results.NotFound(new { error = "quote not found" });
                using var qDoc = JsonDocument.Parse(rd.GetFieldValue<string>(0));
                var root = qDoc.RootElement;
                var quoteNo = root.TryGetProperty("quoteNo", out var qn) ? qn.GetString() : null;
                var partnerCode = root.TryGetProperty("partnerCode", out var pc) ? pc.GetString() : null;
                var amountTotal = root.TryGetProperty("amountTotal", out var at) ? (decimal?)at.GetDecimal() : null;
                var lines = root.TryGetProperty("lines", out var ls) ? ls : default;

                // 生成 SO 编号：优先使用 quoteNo 衍生
                var soNo = !string.IsNullOrWhiteSpace(quoteNo) ? $"SO-{quoteNo}" : $"SO-{DateTime.UtcNow:yyyyMMddHHmmss}";
                using var soDoc = BuildSalesOrderPayload(soNo, partnerCode, amountTotal, lines);
                var inserted = await Crud.InsertRawJson(ds, "sales_orders", cc.ToString(), soDoc.RootElement.GetRawText());
                if (inserted is null) return Results.Problem("create sales order failed");

                // 更新报价状态为 accepted
                using var updatedQuote = SetQuoteStatus(root, "accepted");
                var ok = await Crud.UpdateRawJson(ds, "quotes", id, cc.ToString(), updatedQuote.RootElement.GetRawText());
                return Results.Text(inserted, "application/json");
            }

            static JsonDocument BuildSalesOrderPayload(string soNo, string? partnerCode, decimal? amountTotal, JsonElement lines)
            {
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("soNo", soNo);
                    if (!string.IsNullOrWhiteSpace(partnerCode)) writer.WriteString("partnerCode", partnerCode);
                    writer.WriteString("orderDate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
                    writer.WritePropertyName("lines");
                    if (lines.ValueKind == JsonValueKind.Array) lines.WriteTo(writer); else { writer.WriteStartArray(); writer.WriteEndArray(); }
                    if (amountTotal.HasValue) writer.WriteNumber("amountTotal", amountTotal.Value);
                    writer.WriteString("status", "confirmed");
                    writer.WriteEndObject();
                }
                return JsonDocument.Parse(stream.ToArray());
            }

            static JsonDocument SetQuoteStatus(JsonElement root, string status)
            {
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.NameEquals("status")) continue;
                        p.WriteTo(writer);
                    }
                    writer.WriteString("status", status);
                    writer.WriteEndObject();
                }
                return JsonDocument.Parse(stream.ToArray());
            }
        }).RequireAuthorization();

        // Gmail 导入邮件为活动：仅创建 activity（type=email），不强制绑定联系人/伙伴
        app.MapPost("/crm/gmail/import", async (HttpRequest req, NpgsqlDataSource ds, IHttpClientFactory httpFactory) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("accessToken", out var at) ? at.GetString() : null;
            var query = root.TryGetProperty("query", out var q) ? q.GetString() : "newer_than:30d";
            var maxResults = root.TryGetProperty("maxResults", out var mr) ? Math.Clamp(mr.GetInt32(), 1, 100) : 20;
            if (string.IsNullOrWhiteSpace(accessToken)) return Results.BadRequest(new { error = "accessToken required" });

            var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var listUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q={Uri.EscapeDataString(query!)}&maxResults={maxResults}";
            var listResp = await http.GetAsync(listUrl);
            if (!listResp.IsSuccessStatusCode) return Results.StatusCode((int)listResp.StatusCode);
            var listJson = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
            var messages = listJson.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Take(maxResults).ToList() : new List<JsonElement>();

            foreach (var m in messages)
            {
                var id = m.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (await HasActivityByPayloadField(ds, cc.ToString(), "gmailMessageId", id)) continue;
                var msgResp = await http.GetAsync($"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=Subject&metadataHeaders=From");
                if (!msgResp.IsSuccessStatusCode) continue;
                var msgJson = JsonDocument.Parse(await msgResp.Content.ReadAsStringAsync());
                var headers = msgJson.RootElement.GetProperty("payload").GetProperty("headers").EnumerateArray();
                string? subject = null; string? from = null;
                foreach (var h in headers)
                {
                    var name = h.GetProperty("name").GetString();
                    var value = h.GetProperty("value").GetString();
                    if (string.Equals(name, "Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                    else if (string.Equals(name, "From", StringComparison.OrdinalIgnoreCase)) from = value;
                }

                // 生成 activity
                using var act = BuildEmailActivity(subject, from, id);
                await Crud.InsertRawJson(ds, "activities", cc.ToString(), act.RootElement.GetRawText());
            }

            return Results.Ok(new { imported = messages.Count });

            static JsonDocument BuildEmailActivity(string? subject, string? from, string? messageId)
            {
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "email");
                    if (!string.IsNullOrWhiteSpace(subject)) writer.WriteString("subject", subject);
                    if (!string.IsNullOrWhiteSpace(from)) writer.WriteString("content", $"From: {from}");
                    if (!string.IsNullOrWhiteSpace(messageId)) writer.WriteString("gmailMessageId", messageId);
                    writer.WriteString("status", "open");
                    writer.WriteEndObject();
                }
                return JsonDocument.Parse(stream.ToArray());
            }
        }).RequireAuthorization();

        // 报表：商谈漏斗（按阶段汇总金额）
        app.MapGet("/crm/reports/deals-funnel", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT stage, COALESCE(SUM(expected_amount),0) AS amount FROM deals WHERE company_code=$1 GROUP BY stage ORDER BY stage";
            cmd.Parameters.AddWithValue(cc.ToString());
            var list = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) list.Add(new { stage = rd.IsDBNull(0)? null : rd.GetString(0), amount = rd.IsDBNull(1)? 0m : rd.GetDecimal(1) });
            return Results.Json(list);
        }).RequireAuthorization();

        // 报表：每月商谈数（按预计成约月）
        app.MapGet("/crm/reports/deals-monthly", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT to_char(expected_close_date, 'YYYY-MM') AS ym, COUNT(1) FROM deals WHERE company_code=$1 GROUP BY ym ORDER BY ym";
            cmd.Parameters.AddWithValue(cc.ToString());
            var list = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) list.Add(new { ym = rd.IsDBNull(0)? null : rd.GetString(0), count = rd.IsDBNull(1)? 0 : rd.GetInt64(1) });
            return Results.Json(list);
        }).RequireAuthorization();

        // 报表：成约率（won / all），最近 N 月（默认 6）
        app.MapGet("/crm/reports/win-rate", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            var n = 6;
            if (req.Query.TryGetValue("months", out var qs) && int.TryParse(qs.ToString(), out var m)) n = Math.Clamp(m, 1, 24);
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"WITH base AS (
  SELECT to_char(expected_close_date, 'YYYY-MM') AS ym, stage
  FROM deals
  WHERE company_code=$1 AND expected_close_date >= (date_trunc('month', now()) - ($2||' months')::interval)
)
SELECT ym,
       SUM(CASE WHEN stage='won' THEN 1 ELSE 0 END)::decimal AS won,
       COUNT(1)::decimal AS total,
       CASE WHEN COUNT(1)=0 THEN 0 ELSE SUM(CASE WHEN stage='won' THEN 1 ELSE 0 END)::decimal / COUNT(1) END AS rate
FROM base GROUP BY ym ORDER BY ym;";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(n);
            var list = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var ym = rd.IsDBNull(0) ? null : rd.GetString(0);
                var won = rd.IsDBNull(1) ? 0m : rd.GetDecimal(1);
                var total = rd.IsDBNull(2) ? 0m : rd.GetDecimal(2);
                var rate = rd.IsDBNull(3) ? 0m : rd.GetDecimal(3);
                list.Add(new { ym, won, total, rate });
            }
            return Results.Json(list);
        }).RequireAuthorization();

        // 通过 Gmail IMAP 拉取最近 N 封邮件并写入 activities（type=email）
        app.MapPost("/crm/gmail/imap-import", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var appPassword = root.TryGetProperty("appPassword", out var p) ? p.GetString() : null;
            var maxCount = root.TryGetProperty("maxCount", out var mc) ? Math.Clamp(mc.GetInt32(), 1, 50) : 10;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(appPassword))
                return Results.BadRequest(new { error = "email/appPassword required" });

            using var imap = new ImapClient();
            await imap.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect);
            await imap.AuthenticateAsync(email, appPassword);
            var inbox = imap.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            var uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-7)));
            var items = uids.TakeLast(maxCount).ToList();
            foreach (var uid in items)
            {
                var msg = await inbox.GetMessageAsync(uid);
                var subject = msg.Subject ?? string.Empty;
                var from = msg.From?.ToString() ?? string.Empty;
                var text = msg.TextBody ?? msg.HtmlBody ?? string.Empty;
                var uidString = uid.Id.ToString();
                if (await HasActivityByPayloadField(ds, cc.ToString(), "imapUid", uidString)) continue;
                using var act = BuildEmailActivity(subject, $"From: {from}\n\n{text}", uidString);
                await Crud.InsertRawJson(ds, "activities", cc.ToString(), act.RootElement.GetRawText());
            }
            await imap.DisconnectAsync(true);
            return Results.Ok(new { imported = items.Count });

            static JsonDocument BuildEmailActivity(string? subject, string? content, string? uid)
            {
                using var stream = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "email");
                    if (!string.IsNullOrWhiteSpace(subject)) writer.WriteString("subject", subject);
                    if (!string.IsNullOrWhiteSpace(content)) writer.WriteString("content", content);
                    if (!string.IsNullOrWhiteSpace(uid)) writer.WriteString("imapUid", uid);
                    writer.WriteString("status", "open");
                    writer.WriteEndObject();
                }
                return JsonDocument.Parse(stream.ToArray());
            }
        }).RequireAuthorization();

        static async Task<bool> HasActivityByPayloadField(NpgsqlDataSource ds, string companyCode, string jsonField, string value)
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT 1 FROM activities
WHERE company_code=$1 AND payload #>> string_to_array($3, '.') = $2 LIMIT 1";
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(value);
            cmd.Parameters.AddWithValue(jsonField);
            var obj = await cmd.ExecuteScalarAsync();
            return obj is not null;
        }
    }
}



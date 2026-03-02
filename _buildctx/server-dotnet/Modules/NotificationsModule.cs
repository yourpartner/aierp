using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

public static class NotificationsModule
{
    public static void MapNotificationsModule(this WebApplication app)
    {
        // Register device token (idempotent by company+user+platform+deviceId).
        app.MapPost("/notifications/register-device", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : null;
            if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var platform = root.GetProperty("platform").GetString();
            var bundleId = root.TryGetProperty("bundleId", out var pb) ? pb.GetString() : null;
            var deviceId = root.TryGetProperty("deviceId", out var pd) ? pd.GetString() : null;
            var token = root.GetProperty("token").GetString();
            var environment = root.TryGetProperty("environment", out var pe) ? pe.GetString() : "sandbox";

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO device_tokens(company_code,user_id,platform,bundle_id,device_id,token,environment)
VALUES($1,$2::uuid,$3,$4,$5,$6,$7)
ON CONFLICT (company_code,user_id,platform,device_id) DO UPDATE SET token=excluded.token, bundle_id=excluded.bundle_id, environment=excluded.environment, updated_at=now()
RETURNING id";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(uid!));
            cmd.Parameters.AddWithValue(platform!);
            cmd.Parameters.AddWithValue((object?)bundleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)deviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(token!);
            cmd.Parameters.AddWithValue((object?)environment ?? DBNull.Value);
            var id = (Guid?)await cmd.ExecuteScalarAsync();
            return Results.Ok(new { id });
        }).RequireAuthorization();

        // Send a test notification to all iOS devices of the current user (for debugging).
        app.MapPost("/notifications/test", async (HttpRequest req, NpgsqlDataSource ds, ApnsService apns) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });
            var uid = req.Headers.TryGetValue("x-user-id", out var u) ? u.ToString() : null;
            if (string.IsNullOrWhiteSpace(uid)) return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : "通知";
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : "テスト通知";
            var sandbox = root.TryGetProperty("sandbox", out var sb) && sb.ValueKind == JsonValueKind.True;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT bundle_id, token, COALESCE(environment,'sandbox')
FROM device_tokens WHERE company_code=$1 AND user_id=$2::uuid AND platform='ios'";
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(Guid.Parse(uid!));
            await using var rd = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await rd.ReadAsync())
            {
                var bundleId = rd.IsDBNull(0) ? null : rd.GetString(0);
                var token = rd.GetString(1);
                var env = rd.IsDBNull(2) ? "sandbox" : rd.GetString(2);
                var useSandbox = sandbox || string.Equals(env, "sandbox", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(bundleId)) { results.Add(new { ok=false, error="missing bundleId"}); continue; }
                var (ok, id, error) = await apns.SendAsync(bundleId!, token, title!, body!, useSandbox);
                results.Add(new { ok, id, error });
            }
            return Results.Ok(results);
        }).RequireAuthorization();

        // Approval timeout/risk reminder: scan according to business rules and push alerts.
        app.MapPost("/maintenance/notifications/pending-approvals", async (HttpRequest req, NpgsqlDataSource ds, ApnsService apns) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc)) return Results.BadRequest(new { error = "Missing x-company-code" });

            // Fetch pending tasks older than 24 hours.
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT approver_user_id, count(*) AS cnt
FROM approval_tasks WHERE company_code=$1 AND status='pending' AND created_at < now() - interval '24 hours'
GROUP BY approver_user_id";
            cmd.Parameters.AddWithValue(cc.ToString());
            var pending = new List<(string userId,int count)>();
            await using (var rd = await cmd.ExecuteReaderAsync())
            {
                while (await rd.ReadAsync()) pending.Add((rd.GetString(0), rd.GetInt32(1)));
            }

            var sent = new List<object>();
            foreach (var (userId, cnt) in pending)
            {
                // Locate the user's iOS devices and send notifications.
                await using var c2 = await ds.OpenConnectionAsync();
                await using var q = c2.CreateCommand();
                q.CommandText = @"SELECT bundle_id, token, COALESCE(environment,'sandbox') FROM device_tokens WHERE company_code=$1 AND user_id=$2::uuid AND platform='ios'";
                q.Parameters.AddWithValue(cc.ToString());
                q.Parameters.AddWithValue(Guid.Parse(userId));
                await using var r2 = await q.ExecuteReaderAsync();
                while (await r2.ReadAsync())
                {
                    var bundleId = r2.IsDBNull(0) ? null : r2.GetString(0);
                    var token = r2.GetString(1);
                    var env = r2.IsDBNull(2) ? "sandbox" : r2.GetString(2);
                    if (string.IsNullOrWhiteSpace(bundleId)) { sent.Add(new { userId, ok=false, error="missing bundleId"}); continue; }
                    var (ok, id, error) = await apns.SendAsync(bundleId!, token, "待办提醒", $"您有 {cnt} 个待审批任务超时未处理。", string.Equals(env, "sandbox", StringComparison.OrdinalIgnoreCase));
                    sent.Add(new { userId, ok, id, error });
                }
            }
            return Results.Ok(sent);
        }).RequireAuthorization();
    }
}



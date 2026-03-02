using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using OpenAI.Chat;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// 销售告警模块 - 管理销售监控告警和任务
/// </summary>
public static class SalesAlertModule
{
    public static void MapSalesAlertModule(this WebApplication app)
    {
        // AI 解析自然语言监控规则
        app.MapPost("/sales-alerts/parse-rule", async (HttpRequest req, IConfiguration config) =>
        {
            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;
            
            if (!root.TryGetProperty("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.String)
                return Results.BadRequest(new { error = "query required" });
            
            var query = queryEl.GetString()!;
            var ruleType = root.TryGetProperty("ruleType", out var rtEl) ? rtEl.GetString() : "customer_churn";

            try
            {
                var apiKey = config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
                var modelId = config["OpenAI:ChatModel"] ?? "gpt-4";
                var chatClient = new ChatClient(modelId, apiKey);

                var systemPrompt = @"
あなたは販売監視ルールの設定アシスタントです。
ユーザーの自然言語クエリを解析し、以下のJSON形式でルールパラメータを返してください。

顧客離脱検知 (customer_churn) の場合:
{
  ""inactiveDays"": 非活動日数（例：30）,
  ""minOrdersInPeriod"": 期間内最小注文数（例：3）,
  ""lookbackDays"": 過去参照期間（例：180）,
  ""description"": ""ルールの説明文""
}

入金超過検知 (overdue_payment) の場合:
{
  ""thresholdWorkDays"": 超過営業日数（例：1）,
  ""skipWeekends"": true/false,
  ""skipHolidays"": true/false,
  ""description"": ""ルールの説明文""
}

在庫不足検知 (inventory_shortage) の場合:
{
  ""lookAheadDays"": 先読み日数（例：14）,
  ""avgInboundDays"": 入庫平均計算日数（例：30）,
  ""description"": ""ルールの説明文""
}

JSONのみを返してください。
";

                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage($"ルールタイプ: {ruleType}\nクエリ: {query}")
                };

                var chatOptions = new ChatCompletionOptions
                {
                    Temperature = 0.2f,
                    MaxOutputTokenCount = 500,
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                };

                var completion = await chatClient.CompleteChatAsync(messages, chatOptions);
                var responseContent = completion.Value.Content[0].Text;

                var parsedParams = JsonNode.Parse(responseContent);
                return Results.Ok(new
                {
                    success = true,
                    ruleType,
                    originalQuery = query,
                    parsedParams
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    success = false,
                    error = ex.Message,
                    ruleType,
                    originalQuery = query
                });
            }
        }).RequireAuthorization();

        // 获取监控规则列表
        app.MapGet("/sales-monitor-rules", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var sql = @"
                SELECT id, rule_type, rule_name, is_active, params, notification_channels, notification_users, created_at
                FROM sales_monitor_rules
                WHERE company_code = $1
                ORDER BY rule_type";

            var rows = await Crud.QueryJsonRows(ds, sql, new object?[] { cc.ToString() });
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 更新监控规则
        app.MapPost("/sales-monitor-rules/update", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var root = body.RootElement;

            var ruleType = root.TryGetProperty("ruleType", out var rtEl) ? rtEl.GetString() : null;
            var paramsNode = root.TryGetProperty("params", out var pEl) ? pEl.Clone() : default;
            var nlQuery = root.TryGetProperty("naturalLanguageQuery", out var nlEl) ? nlEl.GetString() : null;

            if (string.IsNullOrEmpty(ruleType))
                return Results.BadRequest(new { error = "ruleType required" });

            await using var conn = await ds.OpenConnectionAsync();

            // 如果有自然语言查询，将其合并到 params 中
            var paramsObj = new JsonObject();
            if (paramsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in paramsNode.EnumerateObject())
                {
                    paramsObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                }
            }
            if (!string.IsNullOrEmpty(nlQuery))
            {
                paramsObj["naturalLanguageQuery"] = nlQuery;
            }

            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_monitor_rules 
                SET params = $3::jsonb, updated_at = now()
                WHERE company_code = $1 AND rule_type = $2", conn);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(ruleType);
            cmd.Parameters.AddWithValue(paramsObj.ToJsonString());

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 切换规则状态
        app.MapPost("/sales-monitor-rules/{id:guid}/toggle", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var isActive = body.RootElement.TryGetProperty("isActive", out var iaEl) && iaEl.GetBoolean();

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_monitor_rules 
                SET is_active = $3, updated_at = now()
                WHERE id = $1 AND company_code = $2", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(isActive);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 获取告警列表
        app.MapGet("/sales-alerts", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var type = req.Query["type"].FirstOrDefault();
            var status = req.Query["status"].FirstOrDefault() ?? "open";

            var sql = @"
                SELECT id, alert_type, severity, status, so_no, delivery_no, invoice_no,
                       customer_code, customer_name, material_code, material_name,
                       title, description, amount, due_date, overdue_days,
                       assigned_to, task_id, resolved_at, resolved_by, resolution_note,
                       notified_wecom, created_at
                FROM sales_alerts
                WHERE company_code = $1";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(type))
            {
                sql += $" AND alert_type = ${paramIndex}";
                parameters.Add(type);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND status = ${paramIndex}";
                parameters.Add(status);
                paramIndex++;
            }

            sql += " ORDER BY CASE severity WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END, created_at DESC LIMIT 100";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 确认告警
        app.MapPost("/sales-alerts/{id:guid}/acknowledge", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_alerts 
                SET status = 'acknowledged', updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'open'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 解决告警
        app.MapPost("/sales-alerts/{id:guid}/resolve", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var note = body.RootElement.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;
            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_alerts 
                SET status = 'resolved', resolved_at = now(), resolved_by = $3, resolution_note = $4, updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status IN ('open', 'acknowledged')", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(currentUser);
            cmd.Parameters.AddWithValue(note ?? (object)DBNull.Value);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 拒绝告警
        app.MapPost("/sales-alerts/{id:guid}/dismiss", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE sales_alerts 
                SET status = 'dismissed', updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'open'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 获取告警统计
        app.MapGet("/sales-alerts/stats", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取汇总数据
            int openCritical = 0, openHigh = 0, pendingTasks = 0, resolvedToday = 0;

            await using (var cmd = new NpgsqlCommand(@"
                SELECT 
                    COUNT(*) FILTER (WHERE status = 'open' AND severity = 'critical'),
                    COUNT(*) FILTER (WHERE status = 'open' AND severity = 'high'),
                    (SELECT COUNT(*) FROM alert_tasks WHERE company_code = $1 AND status = 'pending'),
                    COUNT(*) FILTER (WHERE status = 'resolved' AND resolved_at::date = CURRENT_DATE)
                FROM sales_alerts WHERE company_code = $1", conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    openCritical = reader.GetInt32(0);
                    openHigh = reader.GetInt32(1);
                    pendingTasks = reader.GetInt32(2);
                    resolvedToday = reader.GetInt32(3);
                }
            }

            // 按类型统计
            var byType = new List<object>();
            await using (var cmd = new NpgsqlCommand(@"
                SELECT alert_type,
                       COUNT(*) FILTER (WHERE status = 'open') as open_count,
                       COUNT(*) FILTER (WHERE status = 'resolved') as resolved_count,
                       COUNT(*) as total_count
                FROM sales_alerts WHERE company_code = $1
                GROUP BY alert_type", conn))
            {
                cmd.Parameters.AddWithValue(cc.ToString()!);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    byType.Add(new
                    {
                        alert_type = reader.GetString(0),
                        open_count = reader.GetInt64(1),
                        resolved_count = reader.GetInt64(2),
                        total_count = reader.GetInt64(3)
                    });
                }
            }

            return Results.Ok(new
            {
                summary = new { openCritical, openHigh, pendingTasks, resolvedToday },
                byType
            });
        }).RequireAuthorization();

        // 获取任务列表
        app.MapGet("/alert-tasks", async (HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].FirstOrDefault();
            var priority = req.Query["priority"].FirstOrDefault();

            var sql = @"
                SELECT id, alert_id, task_type, title, description, priority, status,
                       assigned_to, due_date, completed_at, completed_by, completion_note, created_at
                FROM alert_tasks
                WHERE company_code = $1";

            var parameters = new List<object?> { cc.ToString() };
            int paramIndex = 2;

            if (!string.IsNullOrEmpty(status))
            {
                sql += $" AND status = ${paramIndex}";
                parameters.Add(status);
                paramIndex++;
            }

            if (!string.IsNullOrEmpty(priority))
            {
                sql += $" AND priority = ${paramIndex}";
                parameters.Add(priority);
                paramIndex++;
            }

            sql += " ORDER BY CASE priority WHEN 'urgent' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 ELSE 4 END, due_date ASC NULLS LAST LIMIT 100";

            var rows = await Crud.QueryJsonRows(ds, sql, parameters.ToArray());
            return Results.Text($"[{string.Join(",", rows)}]", "application/json");
        }).RequireAuthorization();

        // 开始任务
        app.MapPost("/alert-tasks/{id:guid}/start", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE alert_tasks 
                SET status = 'in_progress', assigned_to = $3, updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status = 'pending'", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(currentUser);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 完成任务
        app.MapPost("/alert-tasks/{id:guid}/complete", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var body = await JsonDocument.ParseAsync(req.Body);
            var note = body.RootElement.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : null;
            var currentUser = req.HttpContext.User.FindFirst("employee_code")?.Value ?? "system";

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE alert_tasks 
                SET status = 'completed', completed_at = now(), completed_by = $3, completion_note = $4, updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status IN ('pending', 'in_progress')", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);
            cmd.Parameters.AddWithValue(currentUser);
            cmd.Parameters.AddWithValue(note ?? (object)DBNull.Value);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // 取消任务
        app.MapPost("/alert-tasks/{id:guid}/cancel", async (Guid id, HttpRequest req, NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(@"
                UPDATE alert_tasks 
                SET status = 'cancelled', updated_at = now()
                WHERE id = $1 AND company_code = $2 AND status IN ('pending', 'in_progress')", conn);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString()!);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected == 0) return Results.NotFound();
            return Results.Ok(new { success = true });
        }).RequireAuthorization();
    }
}


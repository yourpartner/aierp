using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 企业微信AI客服管理模块
/// 提供客户映射管理、AI学习统计等API
/// </summary>
public static class WeComChatbotModule
{
    public static void MapWeComChatbotModule(this WebApplication app)
    {
        #region 客户映射管理

        // 获取客户映射列表
        app.MapGet("/wecom/customer-mappings", async (
            HttpRequest req,
            WeChatCustomerMappingService mappingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var confirmed = req.Query.TryGetValue("confirmed", out var c) && bool.TryParse(c, out var cv) ? cv : (bool?)null;
            var limit = req.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv) ? Math.Min(lv, 100) : 50;
            var offset = req.Query.TryGetValue("offset", out var o) && int.TryParse(o, out var ov) ? ov : 0;

            var mappings = await mappingService.GetMappingsAsync(cc.ToString(), confirmed, limit, offset, req.HttpContext.RequestAborted);
            return Results.Ok(mappings);
        }).RequireAuthorization();

        // 手动关联客户
        app.MapPost("/wecom/customer-mappings/{id:guid}/link", async (
            Guid id,
            HttpRequest req,
            WeChatCustomerMappingService mappingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var partnerCode = doc.RootElement.TryGetProperty("partnerCode", out var pc) ? pc.GetString() : null;
            
            if (string.IsNullOrEmpty(partnerCode))
                return Results.BadRequest(new { error = "partnerCode is required" });

            var success = await mappingService.LinkCustomerAsync(cc.ToString(), id, partnerCode, req.HttpContext.RequestAborted);
            return success 
                ? Results.Ok(new { success = true, message = "关联成功" })
                : Results.NotFound(new { error = "映射或客户不存在" });
        }).RequireAuthorization();

        // 确认自动匹配
        app.MapPost("/wecom/customer-mappings/{id:guid}/confirm", async (
            Guid id,
            HttpRequest req,
            WeChatCustomerMappingService mappingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var success = await mappingService.ConfirmMappingAsync(cc.ToString(), id, req.HttpContext.RequestAborted);
            return success
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "映射不存在" });
        }).RequireAuthorization();

        // 解除关联
        app.MapDelete("/wecom/customer-mappings/{id:guid}/link", async (
            Guid id,
            HttpRequest req,
            WeChatCustomerMappingService mappingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var success = await mappingService.UnlinkCustomerAsync(cc.ToString(), id, req.HttpContext.RequestAborted);
            return success
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "映射不存在" });
        }).RequireAuthorization();

        // 搜索可匹配的客户
        app.MapGet("/wecom/customer-mappings/suggest-partners", async (
            HttpRequest req,
            WeChatCustomerMappingService mappingService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var search = req.Query["search"].ToString();
            if (string.IsNullOrEmpty(search))
                return Results.BadRequest(new { error = "search parameter is required" });

            var limit = req.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv) ? Math.Min(lv, 20) : 10;

            var suggestions = await mappingService.SuggestPartnersAsync(cc.ToString(), search, limit, req.HttpContext.RequestAborted);
            return Results.Ok(suggestions);
        }).RequireAuthorization();

        #endregion

        #region AI学习管理

        // 获取学习统计
        app.MapGet("/wecom/ai-learning/statistics", async (
            HttpRequest req,
            AiLearningService learningService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var stats = await learningService.GetStatisticsAsync(cc.ToString(), req.HttpContext.RequestAborted);
            return Results.Ok(stats);
        }).RequireAuthorization();

        // 分析失败案例
        app.MapGet("/wecom/ai-learning/analyze", async (
            HttpRequest req,
            AiLearningService learningService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var days = req.Query.TryGetValue("days", out var d) && int.TryParse(d, out var dv) ? Math.Min(dv, 30) : 7;

            var analysis = await learningService.AnalyzeFailedCasesAsync(cc.ToString(), days, req.HttpContext.RequestAborted);
            return Results.Ok(analysis);
        }).RequireAuthorization();

        // 手动添加训练样本
        app.MapPost("/wecom/ai-learning/samples", async (
            HttpRequest req,
            AiLearningService learningService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var sampleType = root.TryGetProperty("sampleType", out var st) ? st.GetString() : "other";
            var inputText = root.TryGetProperty("inputText", out var it) ? it.GetString() : null;
            var expectedIntent = root.TryGetProperty("expectedIntent", out var ei) ? ei.GetString() : null;
            var expectedResponse = root.TryGetProperty("expectedResponse", out var er) ? er.GetString() : null;
            
            JsonObject? expectedEntities = null;
            if (root.TryGetProperty("expectedEntities", out var ee))
            {
                expectedEntities = JsonNode.Parse(ee.GetRawText())?.AsObject();
            }

            if (string.IsNullOrEmpty(inputText))
                return Results.BadRequest(new { error = "inputText is required" });

            var success = await learningService.AddTrainingSampleAsync(
                cc.ToString(), sampleType!, inputText, expectedIntent ?? "other", 
                expectedEntities, expectedResponse, req.HttpContext.RequestAborted);

            return success
                ? Results.Ok(new { success = true, message = "样本已添加" })
                : Results.Problem("添加样本失败");
        }).RequireAuthorization();

        // 触发训练样本生成
        app.MapPost("/wecom/ai-learning/generate-samples", async (
            HttpRequest req,
            AiLearningService learningService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var days = root.TryGetProperty("days", out var d) ? d.GetInt32() : 30;
            var maxSamples = root.TryGetProperty("maxSamples", out var m) ? m.GetInt32() : 50;

            var count = await learningService.GenerateTrainingSamplesAsync(
                cc.ToString(), days, maxSamples, req.HttpContext.RequestAborted);

            return Results.Ok(new { generated = count });
        }).RequireAuthorization();

        // 记录用户反馈
        app.MapPost("/wecom/ai-learning/feedback", async (
            HttpRequest req,
            AiLearningService learningService) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var sessionId = root.TryGetProperty("sessionId", out var si) && Guid.TryParse(si.GetString(), out var sid) ? sid : Guid.Empty;
            var satisfaction = root.TryGetProperty("satisfaction", out var s) ? s.GetInt32() : 0;
            var note = root.TryGetProperty("note", out var n) ? n.GetString() : null;

            if (sessionId == Guid.Empty)
                return Results.BadRequest(new { error = "sessionId is required" });

            await learningService.RecordUserFeedbackAsync(cc.ToString(), sessionId, satisfaction, note, req.HttpContext.RequestAborted);
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        #endregion

        #region 对话会话管理

        // 获取会话列表
        app.MapGet("/wecom/chat-sessions", async (
            HttpRequest req,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var status = req.Query["status"].ToString();
            var limit = req.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv) ? Math.Min(lv, 100) : 50;
            var offset = req.Query.TryGetValue("offset", out var o) && int.TryParse(o, out var ov) ? ov : 0;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var whereClause = string.IsNullOrEmpty(status) ? "" : "AND status = @status";
            cmd.CommandText = $"""
                SELECT id, user_id, user_type, chat_id, partner_code, partner_name,
                       status, intent, sales_order_no, message_count,
                       created_at, updated_at, completed_at
                FROM wecom_chat_sessions
                WHERE company_code = $1 {whereClause}
                ORDER BY created_at DESC
                LIMIT $2 OFFSET $3
                """;
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(limit);
            cmd.Parameters.AddWithValue(offset);
            if (!string.IsNullOrEmpty(status))
                cmd.Parameters.AddWithValue(status);

            var sessions = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new
                {
                    id = reader.GetGuid(0),
                    userId = reader.GetString(1),
                    userType = reader.GetString(2),
                    chatId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    partnerCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    partnerName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    status = reader.GetString(6),
                    intent = reader.IsDBNull(7) ? null : reader.GetString(7),
                    salesOrderNo = reader.IsDBNull(8) ? null : reader.GetString(8),
                    messageCount = reader.GetInt32(9),
                    createdAt = reader.GetDateTime(10),
                    updatedAt = reader.GetDateTime(11),
                    completedAt = reader.IsDBNull(12) ? null : (DateTime?)reader.GetDateTime(12)
                });
            }

            return Results.Ok(sessions);
        }).RequireAuthorization();

        // 获取会话详情和消息
        app.MapGet("/wecom/chat-sessions/{id:guid}", async (
            Guid id,
            HttpRequest req,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();

            // 获取会话
            object? session = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, user_id, user_type, chat_id, partner_code, partner_name,
                           status, intent, pending_order_data, sales_order_id, sales_order_no,
                           message_count, created_at, updated_at, completed_at
                    FROM wecom_chat_sessions
                    WHERE id = $1 AND company_code = $2
                    """;
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(cc.ToString());

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    session = new
                    {
                        id = reader.GetGuid(0),
                        userId = reader.GetString(1),
                        userType = reader.GetString(2),
                        chatId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        partnerCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                        partnerName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        status = reader.GetString(6),
                        intent = reader.IsDBNull(7) ? null : reader.GetString(7),
                        pendingOrderData = reader.IsDBNull(8) ? null : JsonNode.Parse(reader.GetString(8)),
                        salesOrderId = reader.IsDBNull(9) ? null : (Guid?)reader.GetGuid(9),
                        salesOrderNo = reader.IsDBNull(10) ? null : reader.GetString(10),
                        messageCount = reader.GetInt32(11),
                        createdAt = reader.GetDateTime(12),
                        updatedAt = reader.GetDateTime(13),
                        completedAt = reader.IsDBNull(14) ? null : (DateTime?)reader.GetDateTime(14)
                    };
                }
            }

            if (session == null)
                return Results.NotFound(new { error = "会话不存在" });

            // 获取消息
            var messages = new List<object>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, msg_type, direction, sender_id, sender_name, 
                           content, ai_analysis, created_at
                    FROM wecom_chat_messages
                    WHERE session_id = $1
                    ORDER BY created_at
                    """;
                cmd.Parameters.AddWithValue(id);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    messages.Add(new
                    {
                        id = reader.GetGuid(0),
                        msgType = reader.GetString(1),
                        direction = reader.GetString(2),
                        senderId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        senderName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        content = reader.IsDBNull(5) ? null : reader.GetString(5),
                        aiAnalysis = reader.IsDBNull(6) ? null : JsonNode.Parse(reader.GetString(6)),
                        createdAt = reader.GetDateTime(7)
                    });
                }
            }

            return Results.Ok(new { session, messages });
        }).RequireAuthorization();

        #endregion

        #region 商品别名管理

        // 获取商品别名列表
        app.MapGet("/wecom/product-aliases", async (
            HttpRequest req,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            var materialCode = req.Query["materialCode"].ToString();
            var limit = req.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv) ? Math.Min(lv, 100) : 50;

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();

            var whereClause = string.IsNullOrEmpty(materialCode) ? "" : "AND material_code = $3";
            cmd.CommandText = $"""
                SELECT id, material_code, material_name, alias, alias_type, 
                       customer_code, priority, match_count, is_active, created_at
                FROM product_aliases
                WHERE company_code = $1 AND is_active = true {whereClause}
                ORDER BY match_count DESC, priority DESC
                LIMIT $2
                """;
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(limit);
            if (!string.IsNullOrEmpty(materialCode))
                cmd.Parameters.AddWithValue(materialCode);

            var aliases = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                aliases.Add(new
                {
                    id = reader.GetGuid(0),
                    materialCode = reader.GetString(1),
                    materialName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    alias = reader.GetString(3),
                    aliasType = reader.GetString(4),
                    customerCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    priority = reader.GetInt32(6),
                    matchCount = reader.GetInt32(7),
                    isActive = reader.GetBoolean(8),
                    createdAt = reader.GetDateTime(9)
                });
            }

            return Results.Ok(aliases);
        }).RequireAuthorization();

        // 添加商品别名
        app.MapPost("/wecom/product-aliases", async (
            HttpRequest req,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var materialCode = root.TryGetProperty("materialCode", out var mc) ? mc.GetString() : null;
            var materialName = root.TryGetProperty("materialName", out var mn) ? mn.GetString() : null;
            var alias = root.TryGetProperty("alias", out var a) ? a.GetString() : null;
            var aliasType = root.TryGetProperty("aliasType", out var at) ? at.GetString() : "common";
            var customerCode = root.TryGetProperty("customerCode", out var cuc) ? cuc.GetString() : null;
            var priority = root.TryGetProperty("priority", out var p) ? p.GetInt32() : 0;

            if (string.IsNullOrEmpty(materialCode) || string.IsNullOrEmpty(alias))
                return Results.BadRequest(new { error = "materialCode and alias are required" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO product_aliases
                (company_code, material_code, material_name, alias, alias_type, customer_code, priority)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT DO NOTHING
                RETURNING id
                """;
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(materialCode);
            cmd.Parameters.AddWithValue((object?)materialName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(alias);
            cmd.Parameters.AddWithValue(aliasType ?? "common");
            cmd.Parameters.AddWithValue((object?)customerCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(priority);

            var id = await cmd.ExecuteScalarAsync();
            return id != null
                ? Results.Ok(new { id, success = true })
                : Results.Conflict(new { error = "别名已存在" });
        }).RequireAuthorization();

        // 删除商品别名
        app.MapDelete("/wecom/product-aliases/{id:guid}", async (
            Guid id,
            HttpRequest req,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE product_aliases
                SET is_active = false
                WHERE id = $1 AND company_code = $2
                """;
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(cc.ToString());

            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "别名不存在" });
        }).RequireAuthorization();

        #endregion

        #region 发货通知

        // 手动发送发货通知
        app.MapPost("/wecom/notifications/shipment", async (
            HttpRequest req,
            WeComNotificationService wecomService,
            WeChatCustomerMappingService mappingService,
            NpgsqlDataSource ds) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });

            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            var salesOrderNo = root.TryGetProperty("salesOrderNo", out var so) ? so.GetString() : null;
            var trackingNumber = root.TryGetProperty("trackingNumber", out var tn) ? tn.GetString() : null;

            if (string.IsNullOrEmpty(salesOrderNo))
                return Results.BadRequest(new { error = "salesOrderNo is required" });

            // 获取订单信息
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT payload->>'partnerCode' as partner_code,
                       payload->>'partnerName' as partner_name
                FROM sales_orders
                WHERE company_code = $1 AND payload->>'soNo' = $2
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(cc.ToString());
            cmd.Parameters.AddWithValue(salesOrderNo);

            string? partnerCode = null;
            string? partnerName = null;
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    partnerCode = reader.IsDBNull(0) ? null : reader.GetString(0);
                    partnerName = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (string.IsNullOrEmpty(partnerCode))
                return Results.NotFound(new { error = "订单不存在" });

            // 查找客户的微信映射
            string? toUser = null;
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = """
                    SELECT external_user_id, wecom_user_id
                    FROM wecom_customer_mappings
                    WHERE company_code = $1 AND partner_code = $2
                    LIMIT 1
                    """;
                cmd2.Parameters.AddWithValue(cc.ToString());
                cmd2.Parameters.AddWithValue(partnerCode);

                await using var reader = await cmd2.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    toUser = reader.IsDBNull(0) ? (reader.IsDBNull(1) ? null : reader.GetString(1)) : reader.GetString(0);
                }
            }

            // 发送通知
            await wecomService.SendShipmentNotificationAsync(
                cc.ToString(), salesOrderNo, partnerName, toUser, trackingNumber, req.HttpContext.RequestAborted);

            return Results.Ok(new { success = true, message = "发货通知已发送", toUser });
        }).RequireAuthorization();

        #endregion
    }
}


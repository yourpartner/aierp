using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure;

namespace Server.Modules;

/// <summary>
/// AI客服核心服务
/// 负责消息意图识别、订单信息提取、对话管理和订单创建
/// </summary>
public class AiChatbotService
{
    private readonly ILogger<AiChatbotService> _logger;
    private readonly NpgsqlDataSource _ds;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly WeComNotificationService _wecomService;
    private readonly WeChatCustomerMappingService _mappingService;
    
    private readonly string _apiKey;
    private readonly string _defaultModel;
    
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AiChatbotService(
        ILogger<AiChatbotService> logger,
        NpgsqlDataSource ds,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        WeComNotificationService wecomService,
        WeChatCustomerMappingService mappingService)
    {
        _logger = logger;
        _ds = ds;
        _httpClient = httpClientFactory.CreateClient("openai");
        _config = config;
        _wecomService = wecomService;
        _mappingService = mappingService;
        
        _apiKey = config["OpenAI:ApiKey"] ?? "";
        _defaultModel = "gpt-4o";
    }

    /// <summary>
    /// 处理收到的微信消息
    /// </summary>
    public async Task<ChatbotResponse> HandleIncomingMessageAsync(
        string companyCode,
        WeComMessage message,
        CancellationToken ct)
    {
        _logger.LogInformation("[Chatbot] Processing message from {User}: {Content}", 
            message.FromUser, message.Content?.Substring(0, Math.Min(50, message.Content?.Length ?? 0)));
        
        // 1. 获取或创建会话
        var session = await GetOrCreateSessionAsync(companyCode, message, ct);
        
        // 2. 保存收到的消息
        await SaveMessageAsync(session.Id, companyCode, message, "in", ct);
        
        // 3. 获取客户映射信息
        var mapping = await _mappingService.GetOrCreateMappingAsync(
            companyCode, 
            message.FromUser, 
            message.FromUserName,
            message.IsGroup ? "internal" : "external",
            ct);
        
        // 4. 如果是语音消息且没有识别结果，暂时回复无法处理
        if (message.MsgType == "voice" && string.IsNullOrEmpty(message.Content))
        {
            var voiceReply = "抱歉，我暂时无法识别语音消息。请使用文字发送您的需求。";
            await SendReplyAsync(companyCode, session, message, voiceReply, ct);
            return new ChatbotResponse("voice_not_supported", voiceReply, null);
        }
        
        // 5. 获取对话历史
        var history = await GetConversationHistoryAsync(session.Id, 10, ct);
        
        // 6. 获取训练样本作为示例
        var samples = await GetTrainingSamplesAsync(companyCode, ct);
        
        // 7. 调用AI分析意图和提取信息
        var analysis = await AnalyzeMessageAsync(
            companyCode, 
            message.Content ?? "", 
            history, 
            session.PendingOrderData,
            mapping,
            samples,
            ct);
        
        // 8. 保存AI分析结果到消息记录
        await UpdateMessageAnalysisAsync(session.Id, message.MsgId, analysis, ct);
        
        // 9. 根据分析结果处理
        var response = await ProcessAnalysisResultAsync(companyCode, session, message, analysis, mapping, ct);
        
        // 10. 发送回复
        if (!string.IsNullOrEmpty(response.Reply))
        {
            await SendReplyAsync(companyCode, session, message, response.Reply, ct);
            await SaveMessageAsync(session.Id, companyCode, new WeComMessage
            {
                MsgId = Guid.NewGuid().ToString(),
                MsgType = "text",
                Content = response.Reply,
                FromUser = "AI_ASSISTANT"
            }, "out", ct);
        }
        
        return response;
    }

    /// <summary>
    /// 调用AI分析消息
    /// </summary>
    private async Task<MessageAnalysis> AnalyzeMessageAsync(
        string companyCode,
        string content,
        List<ConversationMessage> history,
        JsonObject? pendingOrder,
        CustomerMappingInfo? mapping,
        List<TrainingSample> samples,
        CancellationToken ct)
    {
        var systemPrompt = BuildSystemPrompt(companyCode, samples);
        var messages = BuildConversationMessages(history, content, pendingOrder, mapping);
        
        // 定义工具
        var tools = new[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "analyze_order_intent",
                    description = "分析用户消息，识别意图并提取订单相关信息",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            intent = new
                            {
                                type = "string",
                                @enum = new[] { "create_order", "modify_order", "cancel_order", "inquiry_delivery", "inquiry_price", "inquiry_stock", "greeting", "other" },
                                description = "用户意图"
                            },
                            confidence = new
                            {
                                type = "number",
                                description = "意图识别置信度 0-1"
                            },
                            customer_name = new
                            {
                                type = "string",
                                description = "客户名称/店铺名称"
                            },
                            items = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string", description = "商品名称/别名" },
                                        quantity = new { type = "number", description = "数量" },
                                        unit = new { type = "string", description = "单位（箱/瓶/件等）" }
                                    }
                                },
                                description = "商品明细"
                            },
                            delivery_date = new
                            {
                                type = "string",
                                description = "希望送货日期（今天/明天/具体日期）"
                            },
                            delivery_time = new
                            {
                                type = "string",
                                description = "希望送货时间段"
                            },
                            note = new
                            {
                                type = "string",
                                description = "备注信息"
                            },
                            missing_fields = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "缺少的必要信息：customer/items/quantity"
                            },
                            is_complete = new
                            {
                                type = "boolean",
                                description = "订单信息是否完整（有客户、商品、数量）"
                            },
                            suggested_reply = new
                            {
                                type = "string",
                                description = "建议的回复内容"
                            }
                        },
                        required = new[] { "intent", "confidence", "is_complete", "suggested_reply" }
                    }
                }
            }
        };
        
        var allMessages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        allMessages.AddRange(messages);
        
        var response = await OpenAiApiHelper.CallOpenAiWithToolsAsync(
            _httpClient,
            _apiKey,
            _defaultModel,
            allMessages,
            tools,
            0.1,
            2048,
            ct);
        
        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            _logger.LogWarning("[Chatbot] AI analysis failed: No tool call");
            return new MessageAnalysis
            {
                Intent = "other",
                Confidence = 0,
                SuggestedReply = response.Content ?? "抱歉，我没有理解您的意思。请问您需要什么帮助？"
            };
        }
        
        // 解析工具调用结果
        var toolCall = response.ToolCalls[0];
        try
        {
            var result = JsonSerializer.Deserialize<MessageAnalysis>(toolCall.Arguments, JsonOptions);
            return result ?? new MessageAnalysis { Intent = "other", Confidence = 0 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chatbot] Failed to parse AI analysis result");
            return new MessageAnalysis { Intent = "other", Confidence = 0 };
        }
    }

    /// <summary>
    /// 处理AI分析结果
    /// </summary>
    private async Task<ChatbotResponse> ProcessAnalysisResultAsync(
        string companyCode,
        ChatSession session,
        WeComMessage message,
        MessageAnalysis analysis,
        CustomerMappingInfo? mapping,
        CancellationToken ct)
    {
        switch (analysis.Intent)
        {
            case "create_order":
                return await HandleCreateOrderAsync(companyCode, session, analysis, mapping, ct);
                
            case "modify_order":
                return new ChatbotResponse("modify_order", 
                    analysis.SuggestedReply ?? "好的，请告诉我需要修改什么？", null);
                
            case "cancel_order":
                return new ChatbotResponse("cancel_order",
                    analysis.SuggestedReply ?? "好的，请问是取消哪个订单呢？", null);
                
            case "inquiry_delivery":
                return await HandleDeliveryInquiryAsync(companyCode, session, mapping, ct);
                
            case "inquiry_price":
            case "inquiry_stock":
                return new ChatbotResponse(analysis.Intent,
                    analysis.SuggestedReply ?? "让我帮您查询一下...", null);
                
            case "greeting":
                return new ChatbotResponse("greeting",
                    analysis.SuggestedReply ?? "您好！我是AI客服，可以帮您下单或查询订单。请问有什么可以帮您的？", null);
                
            default:
                return new ChatbotResponse("other",
                    analysis.SuggestedReply ?? "请问您需要什么帮助呢？可以直接告诉我要订什么货。", null);
        }
    }

    /// <summary>
    /// 处理创建订单意图
    /// </summary>
    private async Task<ChatbotResponse> HandleCreateOrderAsync(
        string companyCode,
        ChatSession session,
        MessageAnalysis analysis,
        CustomerMappingInfo? mapping,
        CancellationToken ct)
    {
        // 合并已有的待处理订单信息
        var orderData = session.PendingOrderData ?? new JsonObject();
        
        // 更新客户信息
        if (!string.IsNullOrEmpty(analysis.CustomerName))
        {
            orderData["customerName"] = analysis.CustomerName;
        }
        else if (mapping != null && !string.IsNullOrEmpty(mapping.PartnerName))
        {
            orderData["customerName"] = mapping.PartnerName;
            orderData["customerCode"] = mapping.PartnerCode;
        }
        
        // 更新商品信息
        if (analysis.Items != null && analysis.Items.Count > 0)
        {
            var itemsArray = new JsonArray();
            foreach (var item in analysis.Items)
            {
                itemsArray.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["quantity"] = item.Quantity,
                    ["unit"] = item.Unit
                });
            }
            orderData["items"] = itemsArray;
        }
        
        // 更新配送信息
        if (!string.IsNullOrEmpty(analysis.DeliveryDate))
        {
            orderData["deliveryDate"] = ParseDeliveryDate(analysis.DeliveryDate);
        }
        if (!string.IsNullOrEmpty(analysis.DeliveryTime))
        {
            orderData["deliveryTime"] = analysis.DeliveryTime;
        }
        if (!string.IsNullOrEmpty(analysis.Note))
        {
            orderData["note"] = analysis.Note;
        }
        
        // 保存待处理订单数据
        await UpdateSessionOrderDataAsync(session.Id, orderData, ct);
        
        // 检查是否信息完整
        if (analysis.IsComplete)
        {
            // 尝试创建订单
            var createResult = await CreateSalesOrderAsync(companyCode, session, orderData, ct);
            if (createResult.Success)
            {
                // 更新会话状态
                await CompleteSessionAsync(session.Id, createResult.OrderId, createResult.OrderNo, ct);
                
                // 记录成功反馈用于学习
                await RecordFeedbackAsync(companyCode, session.Id, null, "success", 
                    session.LastUserMessage, analysis, 1.0m, ct);
                
                return new ChatbotResponse("order_created", 
                    $"好的，已为您创建订单 {createResult.OrderNo}。\n{FormatOrderSummary(orderData)}\n如需修改请随时告诉我。",
                    createResult.OrderId);
            }
            else
            {
                return new ChatbotResponse("order_failed",
                    $"抱歉，创建订单时遇到问题：{createResult.Error}。请稍后再试或联系客服。", null);
            }
        }
        else
        {
            // 信息不完整，询问缺少的信息
            var reply = analysis.SuggestedReply;
            if (string.IsNullOrEmpty(reply) && analysis.MissingFields != null)
            {
                reply = BuildMissingFieldsPrompt(analysis.MissingFields);
            }
            
            return new ChatbotResponse("order_incomplete", reply ?? "请补充订单信息。", null);
        }
    }

    /// <summary>
    /// 处理发货查询
    /// </summary>
    private async Task<ChatbotResponse> HandleDeliveryInquiryAsync(
        string companyCode,
        ChatSession session,
        CustomerMappingInfo? mapping,
        CancellationToken ct)
    {
        if (mapping == null || string.IsNullOrEmpty(mapping.PartnerCode))
        {
            return new ChatbotResponse("inquiry_delivery", 
                "请问您是哪位客户呢？我帮您查询订单状态。", null);
        }
        
        // 查询最近的订单
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, payload->>'soNo' as so_no, payload->>'status' as status,
                   payload->>'requestedDeliveryDate' as delivery_date
            FROM sales_orders
            WHERE company_code = $1 
              AND payload->>'partnerCode' = $2
            ORDER BY created_at DESC
            LIMIT 3
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(mapping.PartnerCode);
        
        var orders = new List<(Guid Id, string? SoNo, string? Status, string? DeliveryDate)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orders.Add((
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)
            ));
        }
        
        if (orders.Count == 0)
        {
            return new ChatbotResponse("inquiry_delivery",
                "暂时没有查询到您的订单记录。请问需要下单吗？", null);
        }
        
        var sb = new StringBuilder("您最近的订单情况如下：\n");
        foreach (var order in orders)
        {
            var statusText = order.Status switch
            {
                "draft" => "待确认",
                "confirmed" => "已确认，待发货",
                "shipped" => "已发货",
                "delivered" => "已送达",
                "invoiced" => "已结算",
                _ => order.Status ?? "未知"
            };
            sb.AppendLine($"· {order.SoNo}: {statusText}");
        }
        
        return new ChatbotResponse("inquiry_delivery", sb.ToString(), null);
    }

    /// <summary>
    /// 创建销售订单
    /// </summary>
    private async Task<(bool Success, Guid? OrderId, string? OrderNo, string? Error)> CreateSalesOrderAsync(
        string companyCode,
        ChatSession session,
        JsonObject orderData,
        CancellationToken ct)
    {
        try
        {
            // 查找客户
            var customerName = orderData["customerName"]?.GetValue<string>();
            var customerCode = orderData["customerCode"]?.GetValue<string>();
            
            if (string.IsNullOrEmpty(customerCode) && !string.IsNullOrEmpty(customerName))
            {
                customerCode = await FindCustomerCodeAsync(companyCode, customerName, ct);
            }
            
            if (string.IsNullOrEmpty(customerCode))
            {
                return (false, null, null, $"未找到客户 '{customerName}'，请确认客户名称");
            }
            
            // 解析商品
            var items = orderData["items"]?.AsArray();
            if (items == null || items.Count == 0)
            {
                return (false, null, null, "订单没有商品明细");
            }
            
            var lines = new JsonArray();
            var lineNo = 0;
            decimal total = 0;
            
            foreach (var item in items)
            {
                var obj = item?.AsObject();
                if (obj == null) continue;
                
                lineNo++;
                var productName = obj["name"]?.GetValue<string>() ?? "";
                var quantity = obj["quantity"]?.GetValue<decimal>() ?? 0;
                var unit = obj["unit"]?.GetValue<string>() ?? "个";
                
                // 查找商品和价格
                var (materialCode, materialName, unitPrice) = await FindProductAsync(companyCode, productName, customerCode, ct);
                
                if (string.IsNullOrEmpty(materialCode))
                {
                    // 使用用户输入的名称
                    materialCode = $"TEMP-{Guid.NewGuid():N}"[..20];
                    materialName = productName;
                    unitPrice = 0; // 价格待定
                }
                
                var amount = quantity * unitPrice;
                total += amount;
                
                lines.Add(new JsonObject
                {
                    ["lineNo"] = lineNo,
                    ["materialCode"] = materialCode,
                    ["materialName"] = materialName,
                    ["quantity"] = quantity,
                    ["uom"] = unit,
                    ["unitPrice"] = unitPrice,
                    ["amount"] = amount,
                    ["taxRate"] = 10,
                    ["taxAmount"] = Math.Round(amount * 0.1m, 0)
                });
            }
            
            // 生成订单号
            var soNo = await GenerateSalesOrderNoAsync(companyCode, ct);
            
            // 构建订单数据
            var deliveryDate = orderData["deliveryDate"]?.GetValue<string>() ?? DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
            
            var orderPayload = new JsonObject
            {
                ["soNo"] = soNo,
                ["partnerCode"] = customerCode,
                ["partnerName"] = customerName,
                ["orderDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
                ["requestedDeliveryDate"] = deliveryDate,
                ["currency"] = "JPY",
                ["amountTotal"] = Math.Round(total * 1.1m, 0), // 含税
                ["taxAmountTotal"] = Math.Round(total * 0.1m, 0),
                ["status"] = "confirmed", // AI创建的订单直接确认
                ["lines"] = lines,
                ["note"] = $"通过微信AI客服创建 - 会话ID: {session.Id}",
                ["source"] = "wecom_ai_chatbot",
                ["sessionId"] = session.Id.ToString()
            };
            
            if (orderData["note"] != null)
            {
                orderPayload["note"] = orderPayload["note"]?.GetValue<string>() + "\n" + orderData["note"]?.GetValue<string>();
            }
            
            // 插入订单
            var insertedJson = await Crud.InsertRawJson(_ds, "sales_orders", companyCode, orderPayload.ToJsonString());
            if (string.IsNullOrEmpty(insertedJson))
            {
                return (false, null, null, "保存订单失败");
            }
            
            using var insertedDoc = JsonDocument.Parse(insertedJson);
            var orderId = insertedDoc.RootElement.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var id)
                ? id
                : Guid.Empty;
            
            _logger.LogInformation("[Chatbot] Created sales order {SoNo} for customer {Customer}", soNo, customerName);
            
            return (true, orderId, soNo, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Chatbot] Failed to create sales order");
            return (false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// 查找客户代码
    /// </summary>
    private async Task<string?> FindCustomerCodeAsync(string companyCode, string customerName, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // 首先精确匹配
        cmd.CommandText = """
            SELECT payload->>'code' as code
            FROM businesspartners
            WHERE company_code = $1 
              AND (payload->>'name' = $2 OR payload->>'code' = $2)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(customerName);
        
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result != null && result != DBNull.Value)
        {
            return result.ToString();
        }
        
        // 模糊匹配
        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT payload->>'code' as code
            FROM businesspartners
            WHERE company_code = $1 
              AND (payload->>'name' ILIKE '%' || $2 || '%' 
                   OR payload->>'nameKana' ILIKE '%' || $2 || '%')
            ORDER BY 
                CASE WHEN payload->>'name' = $2 THEN 0 
                     WHEN payload->>'name' ILIKE $2 || '%' THEN 1
                     ELSE 2 END
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(customerName);
        
        result = await cmd.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value ? result.ToString() : null;
    }

    /// <summary>
    /// 查找商品
    /// </summary>
    private async Task<(string? Code, string? Name, decimal Price)> FindProductAsync(
        string companyCode, string productName, string? customerCode, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 首先检查别名表
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT material_code, material_name
                FROM product_aliases
                WHERE company_code = $1 
                  AND alias ILIKE '%' || $2 || '%'
                  AND is_active = true
                ORDER BY 
                    CASE WHEN alias = $2 THEN 0
                         WHEN alias ILIKE $2 || '%' THEN 1
                         ELSE 2 END,
                    priority DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(productName);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var code = reader.IsDBNull(0) ? null : reader.GetString(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.IsNullOrEmpty(code))
                {
                    // 获取价格
                    var price = await GetProductPriceAsync(conn, companyCode, code, customerCode, ct);
                    return (code, name, price);
                }
            }
        }
        
        // 直接搜索商品主数据
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT payload->>'materialCode' as code, payload->>'materialName' as name
                FROM materials
                WHERE company_code = $1 
                  AND (payload->>'materialName' ILIKE '%' || $2 || '%'
                       OR payload->>'materialCode' ILIKE '%' || $2 || '%')
                ORDER BY 
                    CASE WHEN payload->>'materialName' = $2 THEN 0
                         WHEN payload->>'materialName' ILIKE $2 || '%' THEN 1
                         ELSE 2 END
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(productName);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var code = reader.IsDBNull(0) ? null : reader.GetString(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.IsNullOrEmpty(code))
                {
                    var price = await GetProductPriceAsync(conn, companyCode, code, customerCode, ct);
                    return (code, name, price);
                }
            }
        }
        
        return (null, null, 0);
    }

    /// <summary>
    /// 获取商品价格
    /// </summary>
    private async Task<decimal> GetProductPriceAsync(
        NpgsqlConnection conn, string companyCode, string materialCode, string? customerCode, CancellationToken ct)
    {
        // 简化实现：返回0，实际应该查询价格表
        // TODO: 实现价格查询逻辑
        return 0;
    }

    /// <summary>
    /// 生成销售订单号
    /// </summary>
    private async Task<string> GenerateSalesOrderNoAsync(string companyCode, CancellationToken ct)
    {
        var prefix = $"SO{DateTime.Today:yyyyMMdd}";
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) + 1 
            FROM sales_orders 
            WHERE company_code = $1 
              AND payload->>'soNo' LIKE $2 || '%'
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(prefix);
        
        var seq = await cmd.ExecuteScalarAsync(ct);
        var seqNo = Convert.ToInt32(seq ?? 1);
        
        return $"{prefix}-{seqNo:D4}";
    }

    #region 会话管理

    private async Task<ChatSession> GetOrCreateSessionAsync(string companyCode, WeComMessage message, CancellationToken ct)
    {
        var userId = message.FromUser;
        var chatId = message.ChatId;
        var userType = message.IsGroup ? "group" : "external";
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        
        // 查找活跃会话
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, user_id, user_type, chat_id, partner_code, partner_name, 
                       status, intent, pending_order_data, sales_order_id, sales_order_no,
                       message_count, created_at
                FROM wecom_chat_sessions
                WHERE company_code = $1 
                  AND user_id = $2 
                  AND status = 'active'
                  AND ($3::text IS NULL OR chat_id = $3)
                  AND created_at > now() - interval '2 hours'
                ORDER BY created_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(chatId) ? (object)DBNull.Value : chatId);
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new ChatSession
                {
                    Id = reader.GetGuid(0),
                    UserId = reader.GetString(1),
                    UserType = reader.GetString(2),
                    ChatId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    PartnerCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    PartnerName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Status = reader.GetString(6),
                    Intent = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PendingOrderData = reader.IsDBNull(8) ? null : JsonNode.Parse(reader.GetString(8))?.AsObject(),
                    SalesOrderId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    SalesOrderNo = reader.IsDBNull(10) ? null : reader.GetString(10),
                    MessageCount = reader.GetInt32(11),
                    CreatedAt = reader.GetDateTime(12)
                };
            }
        }
        
        // 创建新会话
        var newId = Guid.NewGuid();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO wecom_chat_sessions 
                (id, company_code, user_id, user_type, chat_id, status)
                VALUES ($1, $2, $3, $4, $5, 'active')
                """;
            cmd.Parameters.AddWithValue(newId);
            cmd.Parameters.AddWithValue(companyCode);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(userType);
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(chatId) ? (object)DBNull.Value : chatId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        
        return new ChatSession
        {
            Id = newId,
            UserId = userId,
            UserType = userType,
            ChatId = chatId,
            Status = "active",
            MessageCount = 0,
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task UpdateSessionOrderDataAsync(Guid sessionId, JsonObject orderData, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_chat_sessions
            SET pending_order_data = $2::jsonb,
                intent = 'create_order',
                message_count = message_count + 1,
                updated_at = now()
            WHERE id = $1
            """;
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(orderData.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CompleteSessionAsync(Guid sessionId, Guid? orderId, string? orderNo, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_chat_sessions
            SET status = 'completed',
                sales_order_id = $2,
                sales_order_no = $3,
                completed_at = now(),
                updated_at = now()
            WHERE id = $1
            """;
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(orderId.HasValue ? (object)orderId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(orderNo) ? (object)DBNull.Value : orderNo);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region 消息管理

    private async Task SaveMessageAsync(Guid sessionId, string companyCode, WeComMessage message, string direction, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wecom_chat_messages
            (session_id, company_code, msg_id, msg_type, direction, sender_id, sender_name, content, media_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """;
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(message.MsgId);
        cmd.Parameters.AddWithValue(message.MsgType);
        cmd.Parameters.AddWithValue(direction);
        cmd.Parameters.AddWithValue(message.FromUser);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(message.FromUserName) ? (object)DBNull.Value : message.FromUserName);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(message.Content) ? (object)DBNull.Value : message.Content);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(message.MediaId) ? (object)DBNull.Value : message.MediaId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateMessageAnalysisAsync(Guid sessionId, string msgId, MessageAnalysis analysis, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE wecom_chat_messages
            SET ai_analysis = $3::jsonb
            WHERE session_id = $1 AND msg_id = $2
            """;
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(msgId);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(analysis, JsonOptions));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<ConversationMessage>> GetConversationHistoryAsync(Guid sessionId, int limit, CancellationToken ct)
    {
        var messages = new List<ConversationMessage>();
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT direction, content, created_at
            FROM wecom_chat_messages
            WHERE session_id = $1 AND content IS NOT NULL
            ORDER BY created_at DESC
            LIMIT $2
            """;
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(limit);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new ConversationMessage
            {
                Role = reader.GetString(0) == "in" ? "user" : "assistant",
                Content = reader.GetString(1),
                Timestamp = reader.GetDateTime(2)
            });
        }
        
        messages.Reverse();
        return messages;
    }

    #endregion

    #region 训练样本和反馈

    private async Task<List<TrainingSample>> GetTrainingSamplesAsync(string companyCode, CancellationToken ct)
    {
        var samples = new List<TrainingSample>();
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sample_type, input_text, expected_intent, expected_entities, expected_response
            FROM wecom_ai_training_samples
            WHERE company_code = $1 AND is_active = true
            ORDER BY quality_score DESC, usage_count DESC
            LIMIT 10
            """;
        cmd.Parameters.AddWithValue(companyCode);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new TrainingSample
            {
                Type = reader.GetString(0),
                Input = reader.GetString(1),
                Intent = reader.IsDBNull(2) ? null : reader.GetString(2),
                Entities = reader.IsDBNull(3) ? null : reader.GetString(3),
                Response = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        
        return samples;
    }

    private async Task RecordFeedbackAsync(
        string companyCode, Guid sessionId, Guid? messageId,
        string feedbackType, string? userInput, MessageAnalysis analysis,
        decimal accuracyScore, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO wecom_ai_feedback
            (company_code, session_id, message_id, feedback_type, 
             user_input, ai_output, ai_intent, ai_entities, accuracy_score)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9)
            """;
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(messageId.HasValue ? (object)messageId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(feedbackType);
        cmd.Parameters.AddWithValue(string.IsNullOrEmpty(userInput) ? (object)DBNull.Value : userInput);
        cmd.Parameters.AddWithValue(analysis.SuggestedReply ?? "");
        cmd.Parameters.AddWithValue(analysis.Intent ?? "");
        
        var entitiesJson = new JsonObject();
        if (!string.IsNullOrEmpty(analysis.CustomerName))
            entitiesJson["customerName"] = analysis.CustomerName;
        if (analysis.Items != null)
            entitiesJson["items"] = JsonSerializer.SerializeToNode(analysis.Items);
        cmd.Parameters.AddWithValue(entitiesJson.ToJsonString());
        
        cmd.Parameters.AddWithValue(accuracyScore);
        
        await cmd.ExecuteNonQueryAsync(ct);
    }

    #endregion

    #region 辅助方法

    private async Task SendReplyAsync(string companyCode, ChatSession session, WeComMessage originalMessage, string reply, CancellationToken ct)
    {
        // 如果是群消息，需要@发送者
        if (session.IsGroup && !string.IsNullOrEmpty(originalMessage.FromUserName))
        {
            reply = $"@{originalMessage.FromUserName} {reply}";
        }
        
        await _wecomService.SendMessageAsync(reply, originalMessage.FromUser, ct);
    }

    private string BuildSystemPrompt(string companyCode, List<TrainingSample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个智能销售订单助手，帮助客户和销售人员快速下单。");
        sb.AppendLine();
        sb.AppendLine("你的主要任务：");
        sb.AppendLine("1. 识别用户是否要下单、查询订单或其他需求");
        sb.AppendLine("2. 从消息中提取订单信息：客户名称、商品、数量、送货日期等");
        sb.AppendLine("3. 判断订单信息是否完整，如果不完整要询问缺少的信息");
        sb.AppendLine("4. 使用友好、专业的语气与客户交流");
        sb.AppendLine();
        sb.AppendLine("重要规则：");
        sb.AppendLine("- 一个完整的订单必须有：客户名称（或能识别客户）、至少一个商品、商品数量");
        sb.AppendLine("- 如果用户说的是日期（如'明天'、'后天'、'下周一'），要正确解析");
        sb.AppendLine("- 商品名称可能是俗称或简称，要尽量理解");
        sb.AppendLine("- 数量单位如果用户没说，默认使用'个'或根据商品类型推断");
        sb.AppendLine();
        
        if (samples.Count > 0)
        {
            sb.AppendLine("以下是一些示例对话：");
            sb.AppendLine();
            foreach (var sample in samples)
            {
                sb.AppendLine($"用户: {sample.Input}");
                sb.AppendLine($"意图: {sample.Intent}");
                if (!string.IsNullOrEmpty(sample.Entities))
                {
                    sb.AppendLine($"提取信息: {sample.Entities}");
                }
                if (!string.IsNullOrEmpty(sample.Response))
                {
                    sb.AppendLine($"回复: {sample.Response}");
                }
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }

    private List<object> BuildConversationMessages(
        List<ConversationMessage> history,
        string currentMessage,
        JsonObject? pendingOrder,
        CustomerMappingInfo? mapping)
    {
        var messages = new List<object>();
        
        // 添加上下文信息
        var context = new StringBuilder();
        if (mapping != null && !string.IsNullOrEmpty(mapping.PartnerName))
        {
            context.AppendLine($"已识别客户: {mapping.PartnerName} (代码: {mapping.PartnerCode})");
        }
        if (pendingOrder != null && pendingOrder.Count > 0)
        {
            context.AppendLine($"当前订单草稿: {pendingOrder.ToJsonString()}");
        }
        
        if (context.Length > 0)
        {
            messages.Add(new { role = "user", content = $"[系统上下文]\n{context}" });
            messages.Add(new { role = "assistant", content = "好的，我了解了上下文信息。" });
        }
        
        // 添加历史对话
        foreach (var msg in history)
        {
            messages.Add(new { role = msg.Role, content = msg.Content });
        }
        
        // 添加当前消息
        messages.Add(new { role = "user", content = currentMessage });
        
        return messages;
    }

    private static string ParseDeliveryDate(string dateStr)
    {
        var today = DateTime.Today;
        
        return dateStr.ToLower() switch
        {
            "今天" or "今日" => today.ToString("yyyy-MM-dd"),
            "明天" or "明日" => today.AddDays(1).ToString("yyyy-MM-dd"),
            "后天" => today.AddDays(2).ToString("yyyy-MM-dd"),
            "大后天" => today.AddDays(3).ToString("yyyy-MM-dd"),
            "下周" or "下周一" => GetNextWeekday(today, DayOfWeek.Monday).ToString("yyyy-MM-dd"),
            "下周二" => GetNextWeekday(today, DayOfWeek.Tuesday).ToString("yyyy-MM-dd"),
            "下周三" => GetNextWeekday(today, DayOfWeek.Wednesday).ToString("yyyy-MM-dd"),
            "下周四" => GetNextWeekday(today, DayOfWeek.Thursday).ToString("yyyy-MM-dd"),
            "下周五" => GetNextWeekday(today, DayOfWeek.Friday).ToString("yyyy-MM-dd"),
            _ => DateTime.TryParse(dateStr, out var parsed) ? parsed.ToString("yyyy-MM-dd") : today.AddDays(1).ToString("yyyy-MM-dd")
        };
    }

    private static DateTime GetNextWeekday(DateTime start, DayOfWeek day)
    {
        int daysToAdd = ((int)day - (int)start.DayOfWeek + 7) % 7;
        if (daysToAdd == 0) daysToAdd = 7;
        return start.AddDays(daysToAdd);
    }

    private static string BuildMissingFieldsPrompt(List<string> missingFields)
    {
        var parts = new List<string>();
        foreach (var field in missingFields)
        {
            var prompt = field switch
            {
                "customer" => "请问是给哪位客户的订单",
                "items" => "请问需要订什么商品",
                "quantity" => "请问需要多少数量",
                _ => $"请提供{field}"
            };
            parts.Add(prompt);
        }
        return string.Join("？", parts) + "？";
    }

    private static string FormatOrderSummary(JsonObject orderData)
    {
        var sb = new StringBuilder();
        
        if (orderData["customerName"] != null)
            sb.AppendLine($"客户: {orderData["customerName"]}");
        
        if (orderData["items"] is JsonArray items)
        {
            sb.AppendLine("商品:");
            foreach (var item in items)
            {
                var name = item?["name"]?.GetValue<string>() ?? "";
                var qty = item?["quantity"]?.GetValue<decimal>() ?? 0;
                var unit = item?["unit"]?.GetValue<string>() ?? "个";
                sb.AppendLine($"  · {name} x {qty}{unit}");
            }
        }
        
        if (orderData["deliveryDate"] != null)
            sb.AppendLine($"送货日期: {orderData["deliveryDate"]}");
        
        return sb.ToString();
    }

    #endregion
}

#region 数据模型

public class ChatSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string UserType { get; set; } = "";
    public string? ChatId { get; set; }
    public string? PartnerCode { get; set; }
    public string? PartnerName { get; set; }
    public string Status { get; set; } = "active";
    public string? Intent { get; set; }
    public JsonObject? PendingOrderData { get; set; }
    public Guid? SalesOrderId { get; set; }
    public string? SalesOrderNo { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? LastUserMessage { get; set; }
    
    public bool IsGroup => !string.IsNullOrEmpty(ChatId);
}

public class ConversationMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

public class MessageAnalysis
{
    public string Intent { get; set; } = "";
    public double Confidence { get; set; }
    public string? CustomerName { get; set; }
    public List<OrderItem>? Items { get; set; }
    public string? DeliveryDate { get; set; }
    public string? DeliveryTime { get; set; }
    public string? Note { get; set; }
    public List<string>? MissingFields { get; set; }
    public bool IsComplete { get; set; }
    public string? SuggestedReply { get; set; }
}

public class OrderItem
{
    public string Name { get; set; } = "";
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
}

public class TrainingSample
{
    public string Type { get; set; } = "";
    public string Input { get; set; } = "";
    public string? Intent { get; set; }
    public string? Entities { get; set; }
    public string? Response { get; set; }
}

public class CustomerMappingInfo
{
    public Guid Id { get; set; }
    public string? WeComUserId { get; set; }
    public string? WeComName { get; set; }
    public string? PartnerCode { get; set; }
    public string? PartnerName { get; set; }
    public bool IsConfirmed { get; set; }
}

public record ChatbotResponse(string Intent, string? Reply, Guid? OrderId);

#endregion


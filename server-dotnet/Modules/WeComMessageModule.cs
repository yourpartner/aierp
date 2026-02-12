using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Modules;

/// <summary>
/// 企业微信消息接收模块
/// 处理来自企业微信的回调消息，包括客户消息和群消息
/// </summary>
public static class WeComMessageModule
{
    public static void MapWeComMessageModule(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<WeComMessageService>>();
        var config = app.Services.GetRequiredService<IConfiguration>();
        
        // 企业微信回调验证（GET请求）
        app.MapGet("/wecom/callback", (HttpRequest req) =>
        {
            var section = config.GetSection("WeComCallback");
            var token = section["Token"] ?? "";
            var encodingAesKey = section["EncodingAESKey"] ?? "";
            
            var msgSignature = req.Query["msg_signature"].ToString();
            var timestamp = req.Query["timestamp"].ToString();
            var nonce = req.Query["nonce"].ToString();
            var echoStr = req.Query["echostr"].ToString();
            
            logger.LogInformation("[WeCom Callback] Verification request: timestamp={Timestamp}, nonce={Nonce}", 
                timestamp, nonce);
            
            // 验证签名
            if (!VerifySignature(token, timestamp, nonce, echoStr, msgSignature))
            {
                logger.LogWarning("[WeCom Callback] Signature verification failed");
                return Results.BadRequest("Invalid signature");
            }
            
            // 解密 echostr
            try
            {
                var decrypted = DecryptMessage(encodingAesKey, echoStr);
                logger.LogInformation("[WeCom Callback] Verification successful");
                return Results.Text(decrypted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WeCom Callback] Failed to decrypt echostr");
                return Results.BadRequest("Decryption failed");
            }
        });
        
        // 企业微信消息回调（POST请求）
        app.MapPost("/wecom/callback", async (HttpRequest req, NpgsqlDataSource ds, IServiceProvider sp) =>
        {
            var section = config.GetSection("WeComCallback");
            var token = section["Token"] ?? "";
            var encodingAesKey = section["EncodingAESKey"] ?? "";
            var companyCode = section["DefaultCompanyCode"] ?? "JP01";
            
            var msgSignature = req.Query["msg_signature"].ToString();
            var timestamp = req.Query["timestamp"].ToString();
            var nonce = req.Query["nonce"].ToString();
            
            // 读取请求体
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            
            logger.LogDebug("[WeCom Callback] Received message: {Body}", body);
            
            try
            {
                // 解析 XML
                var xml = XDocument.Parse(body);
                var encryptedMsg = xml.Root?.Element("Encrypt")?.Value;
                
                if (string.IsNullOrEmpty(encryptedMsg))
                {
                    logger.LogWarning("[WeCom Callback] No encrypted message found");
                    return Results.Ok();
                }
                
                // 验证签名
                if (!VerifySignature(token, timestamp, nonce, encryptedMsg, msgSignature))
                {
                    logger.LogWarning("[WeCom Callback] Message signature verification failed");
                    return Results.BadRequest("Invalid signature");
                }
                
                // 解密消息
                var decryptedXml = DecryptMessage(encodingAesKey, encryptedMsg);
                logger.LogDebug("[WeCom Callback] Decrypted message: {Message}", decryptedXml);
                
                // 解析消息内容
                var msgXml = XDocument.Parse(decryptedXml);
                var message = ParseMessage(msgXml);
                
                if (message != null)
                {
                    // 处理消息
                    var chatbotService = sp.GetRequiredService<AiChatbotService>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await chatbotService.HandleIncomingMessageAsync(companyCode, message, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[WeCom Callback] Failed to process message");
                        }
                    });
                }
                
                // 立即返回空响应（异步处理消息）
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WeCom Callback] Failed to process callback");
                return Results.Ok(); // 仍然返回200避免重试
            }
        });
        
        // ========== 员工自建应用回调（独立路径） ==========
        
        // 员工自建应用 - 回调验证（GET请求）
        app.MapGet("/wecom/employee-callback", (HttpRequest req) =>
        {
            var section = config.GetSection("WeComEmployee");
            var token = section["Token"] ?? config.GetSection("WeComCallback")["Token"] ?? "";
            var encodingAesKey = section["EncodingAESKey"] ?? config.GetSection("WeComCallback")["EncodingAESKey"] ?? "";
            
            var msgSignature = req.Query["msg_signature"].ToString();
            var timestamp = req.Query["timestamp"].ToString();
            var nonce = req.Query["nonce"].ToString();
            var echoStr = req.Query["echostr"].ToString();
            
            logger.LogInformation("[WeCom Employee Callback] Verification request");
            
            if (!VerifySignature(token, timestamp, nonce, echoStr, msgSignature))
            {
                logger.LogWarning("[WeCom Employee Callback] Signature verification failed");
                return Results.BadRequest("Invalid signature");
            }
            
            try
            {
                var decrypted = DecryptMessage(encodingAesKey, echoStr);
                return Results.Text(decrypted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WeCom Employee Callback] Decrypt failed");
                return Results.BadRequest("Decryption failed");
            }
        });
        
        // 员工自建应用 - 消息回调（POST请求）→ 路由到 WeComEmployeeGateway
        app.MapPost("/wecom/employee-callback", async (HttpRequest req, NpgsqlDataSource ds, IServiceProvider sp) =>
        {
            var section = config.GetSection("WeComEmployee");
            var token = section["Token"] ?? config.GetSection("WeComCallback")["Token"] ?? "";
            var encodingAesKey = section["EncodingAESKey"] ?? config.GetSection("WeComCallback")["EncodingAESKey"] ?? "";
            var companyCode = section["DefaultCompanyCode"] ?? config.GetSection("WeComCallback")["DefaultCompanyCode"] ?? "JP01";
            
            var msgSignature = req.Query["msg_signature"].ToString();
            var timestamp = req.Query["timestamp"].ToString();
            var nonce = req.Query["nonce"].ToString();
            
            using var bodyReader = new StreamReader(req.Body);
            var body = await bodyReader.ReadToEndAsync();
            
            try
            {
                var xml = XDocument.Parse(body);
                var encryptedMsg = xml.Root?.Element("Encrypt")?.Value;
                
                if (string.IsNullOrEmpty(encryptedMsg)) return Results.Ok();
                
                if (!VerifySignature(token, timestamp, nonce, encryptedMsg, msgSignature))
                    return Results.BadRequest("Invalid signature");
                
                var decryptedXml = DecryptMessage(encodingAesKey, encryptedMsg);
                var msgXml = XDocument.Parse(decryptedXml);
                var message = ParseMessage(msgXml);
                
                if (message != null)
                {
                    // 路由到员工 Gateway（异步处理，立即返回）
                    var gateway = sp.GetRequiredService<WeComEmployeeGateway>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await gateway.HandleEmployeeMessageAsync(companyCode, message, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[WeCom Employee Callback] Failed to process employee message");
                        }
                    });
                }
                
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WeCom Employee Callback] Failed to process callback");
                return Results.Ok();
            }
        });

        // ========== 智能路由：现有回调自动区分内部/外部消息 ==========
        // 如果只配置了一个回调 URL，可以通过用户类型自动路由
        
        // 测试端点：模拟接收消息（开发时使用）
        app.MapPost("/wecom/test-message", async (HttpRequest req, NpgsqlDataSource ds, IServiceProvider sp) =>
        {
            if (!req.Headers.TryGetValue("x-company-code", out var cc) || string.IsNullOrWhiteSpace(cc))
                return Results.BadRequest(new { error = "Missing x-company-code" });
            
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            
            var isEmployee = root.TryGetProperty("isEmployee", out var ie) && ie.GetBoolean();
            
            var message = new WeComMessage
            {
                MsgId = (root.TryGetProperty("msgId", out var mid) ? mid.GetString() : null) ?? Guid.NewGuid().ToString(),
                MsgType = (root.TryGetProperty("msgType", out var mt) ? mt.GetString() : null) ?? "text",
                FromUser = (root.TryGetProperty("fromUser", out var fu) ? fu.GetString() : null) ?? "",
                FromUserName = root.TryGetProperty("fromUserName", out var fn) ? fn.GetString() : null,
                ChatId = root.TryGetProperty("chatId", out var ci) ? ci.GetString() : null,
                Content = root.TryGetProperty("content", out var ct) ? ct.GetString() : null,
                MediaId = root.TryGetProperty("mediaId", out var mi) ? mi.GetString() : null,
                CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsGroup = root.TryGetProperty("isGroup", out var ig) && ig.GetBoolean()
            };
            
            if (isEmployee)
            {
                // 路由到员工 Gateway
                var gateway = sp.GetRequiredService<WeComEmployeeGateway>();
                var response = await gateway.HandleEmployeeMessageAsync(cc.ToString(), message, req.HttpContext.RequestAborted);
                return Results.Ok(new { received = message, response = new { intent = response.Intent, reply = response.Reply, sessionId = response.SessionId } });
            }
            else
            {
                // 路由到客户 AI Chatbot
                var chatbotService = sp.GetRequiredService<AiChatbotService>();
                var response = await chatbotService.HandleIncomingMessageAsync(cc.ToString(), message, req.HttpContext.RequestAborted);
                return Results.Ok(new { received = message, response });
            }
        }).RequireAuthorization();
    }
    
    private static bool VerifySignature(string token, string timestamp, string nonce, string encrypted, string signature)
    {
        var arr = new[] { token, timestamp, nonce, encrypted };
        Array.Sort(arr, StringComparer.Ordinal);
        var str = string.Concat(arr);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(str));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return computed == signature;
    }
    
    private static string DecryptMessage(string encodingAesKey, string encrypted)
    {
        // 补齐 Base64 编码
        var key = Convert.FromBase64String(encodingAesKey + "=");
        var iv = key[..16];
        
        var encryptedBytes = Convert.FromBase64String(encrypted);
        
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        
        // 去除 PKCS7 padding
        var pad = decrypted[^1];
        if (pad > 0 && pad <= 32)
        {
            decrypted = decrypted[..^pad];
        }
        
        // 前16字节是随机数，接下来4字节是消息长度
        var msgLen = BitConverter.ToInt32(decrypted.AsSpan(16, 4).ToArray().Reverse().ToArray(), 0);
        var msg = Encoding.UTF8.GetString(decrypted, 20, msgLen);
        
        return msg;
    }
    
    private static WeComMessage? ParseMessage(XDocument xml)
    {
        var root = xml.Root;
        if (root == null) return null;
        
        var msgType = root.Element("MsgType")?.Value;
        if (string.IsNullOrEmpty(msgType)) return null;
        
        var message = new WeComMessage
        {
            MsgId = root.Element("MsgId")?.Value ?? Guid.NewGuid().ToString(),
            MsgType = msgType,
            FromUser = root.Element("FromUserName")?.Value ?? "",
            ToUser = root.Element("ToUserName")?.Value,
            CreateTime = long.TryParse(root.Element("CreateTime")?.Value, out var ct) ? ct : DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        
        // 判断是否为群消息
        var chatId = root.Element("ChatId")?.Value;
        if (!string.IsNullOrEmpty(chatId))
        {
            message.ChatId = chatId;
            message.IsGroup = true;
        }
        
        // 根据消息类型解析内容
        switch (msgType)
        {
            case "text":
                message.Content = root.Element("Content")?.Value;
                break;
            case "voice":
                message.MediaId = root.Element("MediaId")?.Value;
                // 如果开启了语音识别，这里会有识别结果
                message.Content = root.Element("Recognition")?.Value;
                break;
            case "image":
                message.MediaId = root.Element("MediaId")?.Value;
                message.PicUrl = root.Element("PicUrl")?.Value;
                break;
            case "event":
                message.Event = root.Element("Event")?.Value;
                message.EventKey = root.Element("EventKey")?.Value;
                break;
        }
        
        return message;
    }
}

/// <summary>
/// 企业微信消息实体
/// </summary>
public class WeComMessage
{
    public string MsgId { get; set; } = "";
    public string MsgType { get; set; } = "text";
    public string FromUser { get; set; } = "";
    public string? FromUserName { get; set; }
    public string? ToUser { get; set; }
    public string? ChatId { get; set; }
    public bool IsGroup { get; set; }
    public string? Content { get; set; }
    public string? MediaId { get; set; }
    public string? PicUrl { get; set; }
    public string? Event { get; set; }
    public string? EventKey { get; set; }
    public long CreateTime { get; set; }
}

/// <summary>
/// 企业微信消息服务（用于日志标记）
/// </summary>
public class WeComMessageService { }


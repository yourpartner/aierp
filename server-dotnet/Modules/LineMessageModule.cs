using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Server.Infrastructure.Skills;

namespace Server.Modules;

/// <summary>
/// LINE Messaging API Webhook 端点
/// 
/// 配置:
///   "LineMessaging": {
///     "ChannelSecret": "xxx",
///     "ChannelAccessToken": "xxx",
///     "DefaultCompanyCode": "JP01"
///   }
/// </summary>
public static class LineMessageModule
{
    public static void MapLineMessageModule(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<LineChannelAdapter>>();
        var config = app.Configuration;

        // LINE Webhook 端点
        app.MapPost("/line/callback", async (HttpRequest req, NpgsqlDataSource ds, IServiceProvider sp) =>
        {
            var section = config.GetSection("LineMessaging");
            var channelSecret = section["ChannelSecret"] ?? "";
            var companyCode = section["DefaultCompanyCode"] ?? "JP01";

            // 1. 读取请求体
            req.EnableBuffering();
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            // 2. 验证签名
            var signature = req.Headers["X-Line-Signature"].FirstOrDefault() ?? "";
            var adapter = sp.GetRequiredService<LineChannelAdapter>();
            if (!string.IsNullOrEmpty(channelSecret) && !adapter.VerifySignature(body, signature))
            {
                logger.LogWarning("[LINE Callback] Signature verification failed");
                return Results.Unauthorized();
            }

            // 3. 解析事件
            try
            {
                using var doc = JsonDocument.Parse(body);
                var events = doc.RootElement.GetProperty("events");

                foreach (var evt in events.EnumerateArray())
                {
                    var eventType = evt.GetProperty("type").GetString();
                    if (eventType != "message") continue;

                    try
                    {
                        // 解析统一消息
                        var message = await adapter.ParseIncomingAsync(evt, req.HttpContext.RequestAborted);
                        if (message == null) continue;

                        message.CompanyCode = companyCode;

                        // 路由到 WeComEmployeeGateway（复用员工网关逻辑）
                        // LINE 用户通过 employee_channel_bindings 表绑定到系统用户
                        var gateway = sp.GetRequiredService<WeComEmployeeGateway>();

                        // 构造 WeComMessage 格式（复用现有处理逻辑）
                        var wecomMsg = new WeComMessage
                        {
                            FromUser = message.ChannelUserId,
                            MsgType = message.MsgType,
                            Content = message.Content,
                            MediaId = message.MediaUrl
                        };

                        var result = await gateway.HandleEmployeeMessageAsync(
                            companyCode, wecomMsg, req.HttpContext.RequestAborted);

                        // 通过 LINE 回复（而不是 WeChat）
                        if (adapter.IsConfigured)
                        {
                            var reply = new UnifiedReply { Text = result.Reply };
                            await adapter.SendReplyAsync(message.ChannelUserId, reply, req.HttpContext.RequestAborted);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[LINE Callback] Failed to process event");
                    }
                }

                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LINE Callback] Failed to parse webhook");
                return Results.Ok(); // LINE 需要200响应，否则会重试
            }
        });

        logger.LogInformation("[LINE] Webhook endpoint registered at /line/callback");
    }
}

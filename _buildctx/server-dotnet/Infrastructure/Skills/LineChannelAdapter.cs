using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// LINE Messaging API Channel Adapter
/// 
/// 配置 (appsettings.json):
///   "LineMessaging": {
///     "ChannelSecret": "xxx",
///     "ChannelAccessToken": "xxx"
///   }
/// </summary>
public class LineChannelAdapter : IChannelAdapter
{
    private readonly ILogger<LineChannelAdapter> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _channelAccessToken;
    private readonly string _channelSecret;

    private const string LINE_API_BASE = "https://api.line.me/v2/bot";
    private const string LINE_DATA_API = "https://api-data.line.me/v2/bot";

    public string ChannelName => "line";

    public bool IsConfigured => !string.IsNullOrEmpty(_channelAccessToken);

    public LineChannelAdapter(
        ILogger<LineChannelAdapter> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var section = config.GetSection("LineMessaging");
        _channelAccessToken = section["ChannelAccessToken"] ?? "";
        _channelSecret = section["ChannelSecret"] ?? "";

        if (IsConfigured)
            _logger.LogInformation("[LINE] Channel Adapter initialized");
        else
            _logger.LogWarning("[LINE] Channel Adapter not configured (missing ChannelAccessToken)");
    }

    /// <summary>解析 LINE Webhook 事件为统一消息</summary>
    public Task<UnifiedMessage?> ParseIncomingAsync(object rawMessage, CancellationToken ct)
    {
        if (rawMessage is not JsonElement evt)
            return Task.FromResult<UnifiedMessage?>(null);

        var type = evt.GetProperty("type").GetString();
        if (type != "message") return Task.FromResult<UnifiedMessage?>(null);

        var source = evt.GetProperty("source");
        var userId = source.GetProperty("userId").GetString() ?? "";
        var message = evt.GetProperty("message");
        var msgType = message.GetProperty("type").GetString() ?? "text";

        var unified = new UnifiedMessage
        {
            Channel = "line",
            ChannelUserId = userId,
            MsgType = MapMsgType(msgType),
        };

        switch (msgType)
        {
            case "text":
                unified.Content = message.GetProperty("text").GetString();
                break;
            case "image":
            case "video":
            case "audio":
            case "file":
                unified.MediaUrl = message.GetProperty("id").GetString();
                if (message.TryGetProperty("fileName", out var fn))
                    unified.FileName = fn.GetString();
                if (message.TryGetProperty("fileSize", out var fs))
                    unified.FileSize = fs.GetInt64();
                break;
        }

        unified.RawMessage = JsonNode.Parse(evt.GetRawText()) as JsonObject;
        return Task.FromResult<UnifiedMessage?>(unified);
    }

    /// <summary>通过 LINE Messaging API 发送回复</summary>
    public async Task SendReplyAsync(string channelUserId, UnifiedReply reply, CancellationToken ct)
    {
        if (!IsConfigured) return;

        var messages = BuildLineMessages(reply);
        await SendPushAsync(channelUserId, messages, ct);
    }

    public Task PushMessageAsync(string channelUserId, UnifiedReply reply, CancellationToken ct)
    {
        return SendReplyAsync(channelUserId, reply, ct);
    }

    /// <summary>下载 LINE 媒体内容</summary>
    public async Task<(byte[] Data, string FileName, string ContentType)?> DownloadMediaAsync(
        string mediaId, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _channelAccessToken);

            var response = await client.GetAsync($"{LINE_DATA_API}/message/{mediaId}/content", ct);
            if (!response.IsSuccessStatusCode) return null;

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return (data, $"line_media_{mediaId}", contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LINE] Failed to download media {MediaId}", mediaId);
            return null;
        }
    }

    // ==================== LINE Messaging API 调用 ====================

    /// <summary>发送 Push Message</summary>
    private async Task SendPushAsync(string userId, List<object> messages, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _channelAccessToken);

        var payload = new { to = userId, messages };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var response = await client.PostAsync(
            $"{LINE_API_BASE}/message/push",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[LINE] Push failed: {Status} {Body}", response.StatusCode, body);
        }
    }

    /// <summary>构建 LINE 消息列表（支持文字+快捷回复）</summary>
    private static List<object> BuildLineMessages(UnifiedReply reply)
    {
        var messages = new List<object>();

        // 主文本消息
        if (!string.IsNullOrEmpty(reply.Text))
        {
            if (reply.QuickActions?.Count > 0)
            {
                // 使用 Quick Reply 按钮
                var quickReplyItems = reply.QuickActions.Select(a => new
                {
                    type = "action",
                    action = new
                    {
                        type = "message",
                        label = a.Label.Length > 20 ? a.Label[..20] : a.Label,
                        text = a.Label
                    }
                }).ToList();

                messages.Add(new
                {
                    type = "text",
                    text = reply.Text,
                    quickReply = new { items = quickReplyItems }
                });
            }
            else
            {
                messages.Add(new { type = "text", text = reply.Text });
            }
        }

        // 卡片消息（Flex Message）
        if (reply.Card != null)
        {
            var card = reply.Card;
            var bodyContents = new List<object>
            {
                new { type = "text", text = card.Title, weight = "bold", size = "lg" }
            };

            if (!string.IsNullOrEmpty(card.Description))
                bodyContents.Add(new { type = "text", text = card.Description, size = "sm", color = "#666666", wrap = true });

            if (card.Fields?.Count > 0)
            {
                foreach (var field in card.Fields)
                {
                    bodyContents.Add(new
                    {
                        type = "box",
                        layout = "horizontal",
                        contents = new object[]
                        {
                            new { type = "text", text = field.Label, size = "sm", color = "#aaaaaa", flex = 2 },
                            new { type = "text", text = field.Value, size = "sm", flex = 3 }
                        }
                    });
                }
            }

            messages.Add(new
            {
                type = "flex",
                altText = card.Title,
                contents = new
                {
                    type = "bubble",
                    body = new { type = "box", layout = "vertical", contents = bodyContents }
                }
            });
        }

        // 链接
        if (!string.IsNullOrEmpty(reply.LinkUrl))
        {
            messages.Add(new { type = "text", text = $"🔗 {reply.LinkUrl}" });
        }

        return messages;
    }

    /// <summary>验证 LINE Webhook 签名</summary>
    public bool VerifySignature(string body, string signature)
    {
        if (string.IsNullOrEmpty(_channelSecret)) return false;

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(_channelSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computed = Convert.ToBase64String(hash);
        return string.Equals(computed, signature, StringComparison.Ordinal);
    }

    private static string MapMsgType(string lineMsgType)
    {
        return lineMsgType switch
        {
            "text" => "text",
            "image" => "image",
            "video" => "file",
            "audio" => "voice",
            "file" => "file",
            "location" => "location",
            "sticker" => "text", // 表情转文字
            _ => "text"
        };
    }
}

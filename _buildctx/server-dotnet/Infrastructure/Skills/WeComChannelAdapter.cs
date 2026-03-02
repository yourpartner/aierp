using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 企业微信 Channel Adapter
/// 将 WeComMessage 转为 UnifiedMessage，将 UnifiedReply 转为企业微信消息
/// </summary>
public class WeComChannelAdapter : IChannelAdapter
{
    private readonly ILogger<WeComChannelAdapter> _logger;
    private readonly Server.Modules.WeComNotificationService _wecomService;

    public string ChannelName => "wecom";

    public WeComChannelAdapter(
        ILogger<WeComChannelAdapter> logger,
        Server.Modules.WeComNotificationService wecomService)
    {
        _logger = logger;
        _wecomService = wecomService;
    }

    public Task<UnifiedMessage?> ParseIncomingAsync(object rawMessage, CancellationToken ct)
    {
        if (rawMessage is not Server.Modules.WeComMessage wecomMsg)
            return Task.FromResult<UnifiedMessage?>(null);

        var unified = new UnifiedMessage
        {
            Channel = "wecom",
            ChannelUserId = wecomMsg.FromUser,
            MsgType = MapMsgType(wecomMsg.MsgType),
            Content = wecomMsg.Content,
            MediaUrl = wecomMsg.MediaId,
            RawMessage = new JsonObject
            {
                ["fromUser"] = wecomMsg.FromUser,
                ["msgType"] = wecomMsg.MsgType,
                ["content"] = wecomMsg.Content,
                ["mediaId"] = wecomMsg.MediaId
            }
        };

        return Task.FromResult<UnifiedMessage?>(unified);
    }

    public async Task SendReplyAsync(string channelUserId, UnifiedReply reply, CancellationToken ct)
    {
        if (!_wecomService.IsConfigured) return;

        // 构建回复文本（合并正文+快捷操作提示）
        var text = reply.Text;
        if (reply.QuickActions?.Count > 0)
        {
            text += "\n\n💡 快捷操作：";
            foreach (var action in reply.QuickActions)
            {
                text += $"\n  · {action.Label}";
            }
        }

        if (!string.IsNullOrEmpty(reply.LinkUrl))
        {
            text += $"\n\n🔗 详情：{reply.LinkUrl}";
        }

        await _wecomService.SendTextMessageAsync(text, channelUserId, ct);
    }

    public Task PushMessageAsync(string channelUserId, UnifiedReply reply, CancellationToken ct)
    {
        return SendReplyAsync(channelUserId, reply, ct);
    }

    public async Task<(byte[] Data, string FileName, string ContentType)?> DownloadMediaAsync(
        string mediaId, CancellationToken ct)
    {
        try
        {
            var result = await _wecomService.DownloadMediaAsync(mediaId, ct);
            if (result != null)
                return (result.Value.data, result.Value.fileName ?? "media_file", result.Value.mimeType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WeComAdapter] 下载媒体文件失败: {MediaId}", mediaId);
        }
        return null;
    }

    private static string MapMsgType(string wecomMsgType)
    {
        return wecomMsgType switch
        {
            "text" => "text",
            "image" => "image",
            "voice" => "voice",
            "video" => "file",
            "file" => "file",
            "location" => "location",
            "event" => "event",
            _ => "text"
        };
    }
}

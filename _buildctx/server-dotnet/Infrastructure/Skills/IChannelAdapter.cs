using System.Text.Json.Nodes;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 统一消息模型 — 所有渠道（WeChat/LINE/Web）的消息归一化格式
/// </summary>
public class UnifiedMessage
{
    /// <summary>渠道类型：wecom / line / web / email</summary>
    public string Channel { get; set; } = "web";

    /// <summary>渠道内用户ID</summary>
    public string ChannelUserId { get; set; } = "";

    /// <summary>消息类型：text / image / file / voice / location / event</summary>
    public string MsgType { get; set; } = "text";

    /// <summary>文本内容</summary>
    public string? Content { get; set; }

    /// <summary>文件/图片 URL 或 MediaId</summary>
    public string? MediaUrl { get; set; }

    /// <summary>文件名（文件消息时）</summary>
    public string? FileName { get; set; }

    /// <summary>文件大小（字节）</summary>
    public long? FileSize { get; set; }

    /// <summary>MIME 类型</summary>
    public string? ContentType { get; set; }

    /// <summary>渠道原始消息（存档用）</summary>
    public JsonObject? RawMessage { get; set; }

    /// <summary>企业代码（已解析）</summary>
    public string? CompanyCode { get; set; }

    /// <summary>系统用户ID（已绑定时）</summary>
    public Guid? SystemUserId { get; set; }
}

/// <summary>
/// 统一回复模型 — 渠道适配层将此转为渠道特定格式
/// </summary>
public class UnifiedReply
{
    /// <summary>纯文字回复</summary>
    public string Text { get; set; } = "";

    /// <summary>快捷操作按钮</summary>
    public List<QuickAction>? QuickActions { get; set; }

    /// <summary>卡片消息（结构化展示）</summary>
    public ReplyCard? Card { get; set; }

    /// <summary>跳转链接</summary>
    public string? LinkUrl { get; set; }

    /// <summary>附件文件（需要发送的文件）</summary>
    public ReplyAttachment? Attachment { get; set; }
}

/// <summary>卡片消息</summary>
public class ReplyCard
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? LinkUrl { get; set; }
    public List<CardField>? Fields { get; set; }
}

public record CardField(string Label, string Value);

/// <summary>回复附件</summary>
public record ReplyAttachment(string FileName, string ContentType, byte[] Data);

/// <summary>
/// Channel Adapter 接口 — 每个渠道实现此接口
/// </summary>
public interface IChannelAdapter
{
    /// <summary>渠道名称</summary>
    string ChannelName { get; }

    /// <summary>将渠道原始消息转为统一格式</summary>
    Task<UnifiedMessage?> ParseIncomingAsync(object rawMessage, CancellationToken ct);

    /// <summary>将统一回复转为渠道格式并发送</summary>
    Task SendReplyAsync(string channelUserId, UnifiedReply reply, CancellationToken ct);

    /// <summary>主动推送消息</summary>
    Task PushMessageAsync(string channelUserId, UnifiedReply reply, CancellationToken ct);

    /// <summary>下载渠道中的媒体文件（图片/文件）</summary>
    Task<(byte[] Data, string FileName, string ContentType)?> DownloadMediaAsync(
        string mediaId, CancellationToken ct);
}

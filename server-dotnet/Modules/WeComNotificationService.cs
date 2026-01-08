using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Server.Modules;

/// <summary>
/// 企业微信通知服务 - 发送销售告警到企业微信
/// 需要配置：WeComNotification:CorpId, AgentId, Secret 或 WebhookUrl
/// </summary>
public class WeComNotificationService
{
    private readonly ILogger<WeComNotificationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    
    // 配置缓存
    private string? _corpId;
    private string? _agentId;
    private string? _secret;
    private string? _webhookUrl;
    
    // Token 缓存
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public WeComNotificationService(
        ILogger<WeComNotificationService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("WeCom");
        _config = config;
        
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        var section = _config.GetSection("WeComNotification");
        _corpId = section["CorpId"] ?? Environment.GetEnvironmentVariable("WECOM_CORP_ID");
        _agentId = section["AgentId"] ?? Environment.GetEnvironmentVariable("WECOM_AGENT_ID");
        _secret = section["Secret"] ?? Environment.GetEnvironmentVariable("WECOM_SECRET");
        _webhookUrl = section["WebhookUrl"] ?? Environment.GetEnvironmentVariable("WECOM_WEBHOOK_URL");
    }

    /// <summary>
    /// 检查服务是否已配置
    /// </summary>
    public bool IsConfigured => 
        !string.IsNullOrEmpty(_webhookUrl) || 
        (!string.IsNullOrEmpty(_corpId) && !string.IsNullOrEmpty(_agentId) && !string.IsNullOrEmpty(_secret));

    /// <summary>
    /// 发送销售告警
    /// </summary>
    public async Task SendAlertAsync(
        string companyCode,
        SalesMonitorBackgroundService.AlertInfo alert,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping notification");
            return;
        }

        var markdown = BuildAlertMarkdown(alert);

        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            await SendWebhookMessageAsync(markdown, ct);
        }
        else
        {
            await SendAppMessageAsync(markdown, null, ct);
        }
    }

    /// <summary>
    /// 发送自定义消息
    /// </summary>
    public async Task SendMessageAsync(
        string content,
        string? toUser = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping notification");
            return;
        }

        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            await SendWebhookMessageAsync(content, ct);
        }
        else
        {
            await SendAppMessageAsync(content, toUser, ct);
        }
    }

    private string BuildAlertMarkdown(SalesMonitorBackgroundService.AlertInfo alert)
    {
        var severityIcon = alert.Severity switch
        {
            "critical" => "🔴",
            "high" => "🟠",
            "medium" => "🟡",
            _ => "🟢"
        };

        var typeLabel = alert.AlertType switch
        {
            "overdue_delivery" => "納期超過",
            "overdue_payment" => "入金超過",
            "customer_churn" => "顧客離脱",
            "inventory_shortage" => "在庫不足",
            _ => alert.AlertType
        };

        var parts = new List<string>
        {
            $"## {severityIcon} {typeLabel}アラート",
            "",
            $"**{alert.Title}**",
            "",
            alert.Description ?? ""
        };

        if (!string.IsNullOrEmpty(alert.CustomerName))
            parts.Add($"> 顧客: {alert.CustomerName}");
        
        if (alert.Amount.HasValue)
            parts.Add($"> 金額: ¥{alert.Amount.Value:N0}");
        
        if (alert.OverdueDays.HasValue)
            parts.Add($"> 超過日数: {alert.OverdueDays.Value} 日");

        parts.Add("");
        parts.Add($"---");
        parts.Add($"[詳細を確認する](http://erp.example.com/sales-alerts)");

        return string.Join("\n", parts);
    }

    #region Webhook 方式
    private async Task SendWebhookMessageAsync(string content, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                msgtype = "markdown",
                markdown = new { content }
            };

            var response = await _httpClient.PostAsJsonAsync(_webhookUrl, payload, ct);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

            if (result?["errcode"]?.GetValue<int>() != 0)
            {
                var errMsg = result?["errmsg"]?.GetValue<string>() ?? "unknown error";
                _logger.LogWarning("[WeCom] Webhook send failed: {Error}", errMsg);
            }
            else
            {
                _logger.LogInformation("[WeCom] Webhook message sent successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WeCom] Failed to send webhook message");
        }
    }
    #endregion

    #region 应用消息方式
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _accessToken;
            }

            var url = $"https://qyapi.weixin.qq.com/cgi-bin/gettoken?corpid={_corpId}&corpsecret={_secret}";
            var response = await _httpClient.GetFromJsonAsync<JsonObject>(url, ct);

            if (response?["errcode"]?.GetValue<int>() != 0)
            {
                var errMsg = response?["errmsg"]?.GetValue<string>() ?? "unknown error";
                _logger.LogError("[WeCom] Failed to get access token: {Error}", errMsg);
                return null;
            }

            _accessToken = response?["access_token"]?.GetValue<string>();
            var expiresIn = response?["expires_in"]?.GetValue<int>() ?? 7200;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 300); // 提前5分钟过期

            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task SendAppMessageAsync(string content, string? toUser = null, CancellationToken ct = default)
    {
        try
        {
            var token = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("[WeCom] No access token, skipping app message");
                return;
            }

            var url = $"https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token={token}";
            var payload = new
            {
                touser = toUser ?? "@all",
                msgtype = "markdown",
                agentid = int.Parse(_agentId ?? "0"),
                markdown = new { content }
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload, ct);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

            if (result?["errcode"]?.GetValue<int>() != 0)
            {
                var errMsg = result?["errmsg"]?.GetValue<string>() ?? "unknown error";
                _logger.LogWarning("[WeCom] App message send failed: {Error}", errMsg);
            }
            else
            {
                _logger.LogInformation("[WeCom] App message sent successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WeCom] Failed to send app message");
        }
    }
    #endregion

    #region 发货通知
    /// <summary>
    /// 发送发货通知给客户
    /// </summary>
    public async Task SendShipmentNotificationAsync(
        string companyCode,
        string salesOrderNo,
        string? customerName,
        string? toUser,
        string? trackingNumber = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping shipment notification");
            return;
        }

        var parts = new List<string>
        {
            "## 📦 发货通知",
            "",
            $"尊敬的 **{customerName ?? "客户"}**，您好！",
            "",
            $"您的订单 **{salesOrderNo}** 已发货。"
        };

        if (!string.IsNullOrEmpty(trackingNumber))
        {
            parts.Add("");
            parts.Add($"快递单号: **{trackingNumber}**");
        }

        parts.Add("");
        parts.Add("如有疑问，请随时联系我们。");
        parts.Add("");
        parts.Add("---");
        parts.Add("此消息由AI客服系统自动发送");

        var markdown = string.Join("\n", parts);

        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            await SendWebhookMessageAsync(markdown, ct);
        }
        else if (!string.IsNullOrEmpty(toUser))
        {
            await SendAppMessageAsync(markdown, toUser, ct);
        }
        else
        {
            _logger.LogWarning("[WeCom] Cannot send shipment notification: no recipient specified");
        }
    }

    /// <summary>
    /// 发送订单确认通知给客户
    /// </summary>
    public async Task SendOrderConfirmationAsync(
        string companyCode,
        string salesOrderNo,
        string? customerName,
        string orderSummary,
        string? toUser,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping order confirmation");
            return;
        }

        var parts = new List<string>
        {
            "## ✅ 订单确认",
            "",
            $"尊敬的 **{customerName ?? "客户"}**，您好！",
            "",
            $"您的订单 **{salesOrderNo}** 已确认。",
            "",
            "**订单详情:**",
            orderSummary,
            "",
            "我们会尽快为您安排发货。",
            "",
            "---",
            "此消息由AI客服系统自动发送"
        };

        var markdown = string.Join("\n", parts);

        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            await SendWebhookMessageAsync(markdown, ct);
        }
        else if (!string.IsNullOrEmpty(toUser))
        {
            await SendAppMessageAsync(markdown, toUser, ct);
        }
    }

    /// <summary>
    /// 发送文本消息（支持指定接收人）
    /// </summary>
    public async Task SendTextMessageAsync(
        string content,
        string? toUser = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping text message");
            return;
        }

        if (!string.IsNullOrEmpty(_webhookUrl))
        {
            var payload = new
            {
                msgtype = "text",
                text = new { content }
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync(_webhookUrl, payload, ct);
                var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

                if (result?["errcode"]?.GetValue<int>() != 0)
                {
                    var errMsg = result?["errmsg"]?.GetValue<string>() ?? "unknown error";
                    _logger.LogWarning("[WeCom] Webhook text send failed: {Error}", errMsg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WeCom] Failed to send webhook text message");
            }
        }
        else
        {
            // 使用应用消息发送文本
            try
            {
                var token = await GetAccessTokenAsync(ct);
                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("[WeCom] No access token, skipping text message");
                    return;
                }

                var url = $"https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token={token}";
                var payload = new
                {
                    touser = toUser ?? "@all",
                    msgtype = "text",
                    agentid = int.Parse(_agentId ?? "0"),
                    text = new { content }
                };

                var response = await _httpClient.PostAsJsonAsync(url, payload, ct);
                var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

                if (result?["errcode"]?.GetValue<int>() != 0)
                {
                    var errMsg = result?["errmsg"]?.GetValue<string>() ?? "unknown error";
                    _logger.LogWarning("[WeCom] App text send failed: {Error}", errMsg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WeCom] Failed to send app text message");
            }
        }
    }

    /// <summary>
    /// 发送群消息并@指定用户
    /// </summary>
    public async Task SendGroupMessageWithMentionAsync(
        string chatId,
        string content,
        string? mentionUserId,
        string? mentionUserName,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("[WeCom] Service not configured, skipping group message");
            return;
        }

        // 如果需要@某人，在消息前添加@
        if (!string.IsNullOrEmpty(mentionUserName))
        {
            content = $"@{mentionUserName} {content}";
        }

        // 使用应用消息发送到群聊
        try
        {
            var token = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("[WeCom] No access token, skipping group message");
                return;
            }

            var url = $"https://qyapi.weixin.qq.com/cgi-bin/appchat/send?access_token={token}";
            var payload = new
            {
                chatid = chatId,
                msgtype = "text",
                text = new { content }
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload, ct);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

            if (result?["errcode"]?.GetValue<int>() != 0)
            {
                var errMsg = result?["errmsg"]?.GetValue<string>() ?? "unknown error";
                _logger.LogWarning("[WeCom] Group message send failed: {Error}", errMsg);
            }
            else
            {
                _logger.LogInformation("[WeCom] Group message sent to {ChatId}", chatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WeCom] Failed to send group message");
        }
    }
    #endregion

    #region 测试连接
    /// <summary>
    /// 测试企业微信连接
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return (false, "企業微信が設定されていません。appsettings.json の WeComNotification セクションを確認してください。");
        }

        try
        {
            if (!string.IsNullOrEmpty(_webhookUrl))
            {
                // 测试 Webhook
                var testPayload = new
                {
                    msgtype = "text",
                    text = new { content = "ERP系统连接测试 - 請忽略此消息" }
                };

                var response = await _httpClient.PostAsJsonAsync(_webhookUrl, testPayload, ct);
                var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);

                if (result?["errcode"]?.GetValue<int>() == 0)
                {
                    return (true, "Webhook 連接成功");
                }
                return (false, $"Webhook 連接失敗: {result?["errmsg"]?.GetValue<string>()}");
            }
            else
            {
                // 测试 API
                var token = await GetAccessTokenAsync(ct);
                if (!string.IsNullOrEmpty(token))
                {
                    return (true, "API 連接成功、アクセストークン取得済み");
                }
                return (false, "アクセストークンの取得に失敗しました");
            }
        }
        catch (Exception ex)
        {
            return (false, $"連接エラー: {ex.Message}");
        }
    }
    #endregion
}


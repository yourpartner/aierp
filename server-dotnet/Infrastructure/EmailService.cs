using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Server.Infrastructure;

/// <summary>
/// Configuration for SMTP email sending.
/// </summary>
public sealed class EmailSettings
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "System";
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Service for sending email notifications.
/// </summary>
public sealed class EmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService>? _logger;

    public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService>? logger = null)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Result of an email send operation.
    /// </summary>
    public sealed record EmailResult(bool Success, string? MessageId, string? Error);

    /// <summary>
    /// Sends an email to the specified recipient.
    /// </summary>
    /// <param name="to">Recipient email address.</param>
    /// <param name="subject">Email subject.</param>
    /// <param name="body">Email body (HTML supported).</param>
    /// <param name="isHtml">Whether the body is HTML.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<EmailResult> SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger?.LogDebug("Email sending is disabled. Would have sent to {To}: {Subject}", to, subject);
            return new EmailResult(true, "disabled", null);
        }

        if (string.IsNullOrWhiteSpace(_settings.SmtpHost) || string.IsNullOrWhiteSpace(_settings.FromAddress))
        {
            _logger?.LogWarning("Email settings not configured");
            return new EmailResult(false, null, "Email settings not configured");
        }

        try
        {
            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.EnableSsl,
                Credentials = string.IsNullOrWhiteSpace(_settings.SmtpUser)
                    ? null
                    : new NetworkCredential(_settings.SmtpUser, _settings.SmtpPassword)
            };

            var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress, _settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };
            message.To.Add(to);

            await client.SendMailAsync(message, ct);

            var messageId = Guid.NewGuid().ToString("N");
            _logger?.LogInformation("Email sent to {To}: {Subject} (ID: {MessageId})", to, subject, messageId);
            return new EmailResult(true, messageId, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send email to {To}: {Subject}", to, subject);
            return new EmailResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Sends an email to multiple recipients.
    /// </summary>
    public async Task<EmailResult> SendToManyAsync(IEnumerable<string> recipients, string subject, string body, bool isHtml = false, CancellationToken ct = default)
    {
        var toList = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (toList.Count == 0)
        {
            return new EmailResult(false, null, "No recipients");
        }

        if (!_settings.Enabled)
        {
            _logger?.LogDebug("Email sending is disabled. Would have sent to {Count} recipients: {Subject}", toList.Count, subject);
            return new EmailResult(true, "disabled", null);
        }

        if (string.IsNullOrWhiteSpace(_settings.SmtpHost) || string.IsNullOrWhiteSpace(_settings.FromAddress))
        {
            return new EmailResult(false, null, "Email settings not configured");
        }

        try
        {
            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.EnableSsl,
                Credentials = string.IsNullOrWhiteSpace(_settings.SmtpUser)
                    ? null
                    : new NetworkCredential(_settings.SmtpUser, _settings.SmtpPassword)
            };

            var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress, _settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };

            foreach (var to in toList)
            {
                message.To.Add(to);
            }

            await client.SendMailAsync(message, ct);

            var messageId = Guid.NewGuid().ToString("N");
            _logger?.LogInformation("Email sent to {Count} recipients: {Subject} (ID: {MessageId})", toList.Count, subject, messageId);
            return new EmailResult(true, messageId, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send email to {Count} recipients: {Subject}", toList.Count, subject);
            return new EmailResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Sends a payroll-related notification email.
    /// </summary>
    public async Task<EmailResult> SendPayrollNotificationAsync(
        string to,
        string templateType,
        Dictionary<string, string> variables,
        CancellationToken ct = default)
    {
        var (subject, body) = BuildPayrollEmailContent(templateType, variables);
        return await SendAsync(to, subject, body, true, ct);
    }

    private static (string Subject, string Body) BuildPayrollEmailContent(string templateType, Dictionary<string, string> variables)
    {
        string subject, body;

        switch (templateType.ToLowerInvariant())
        {
            case "payroll_ready":
                subject = $"【給与計算】{GetVar(variables, "month", "今月")}の給与計算が完了しました";
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <h2>給与計算完了のお知らせ</h2>
                    <p>{GetVar(variables, "companyName", "会社")}の{GetVar(variables, "month", "今月")}分の給与計算が完了しました。</p>
                    <ul>
                    <li>対象人数: {GetVar(variables, "employeeCount", "N/A")}名</li>
                    <li>正常: {GetVar(variables, "normalCount", "N/A")}名</li>
                    <li>要確認: {GetVar(variables, "anomalyCount", "0")}名</li>
                    </ul>
                    <p>詳細は管理画面でご確認ください。</p>
                    <p style="margin-top: 30px; color: #666; font-size: 12px;">このメールは自動送信されています。</p>
                    </body></html>
                    """;
                break;

            case "payroll_anomaly":
                subject = $"【要確認】{GetVar(variables, "month", "今月")}の給与計算で異常が検出されました";
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <h2 style="color: #e74c3c;">給与計算異常のお知らせ</h2>
                    <p>{GetVar(variables, "employeeName", "従業員")}さんの給与計算結果に異常が検出されました。</p>
                    <ul>
                    <li>対象月: {GetVar(variables, "month", "N/A")}</li>
                    <li>計算金額: {GetVar(variables, "amount", "N/A")}</li>
                    <li>異常理由: {GetVar(variables, "reason", "前月比で大きな差異")}</li>
                    </ul>
                    <p>至急ご確認をお願いいたします。</p>
                    <p style="margin-top: 30px; color: #666; font-size: 12px;">このメールは自動送信されています。</p>
                    </body></html>
                    """;
                break;

            case "payroll_deadline_warning":
                subject = $"【警告】{GetVar(variables, "month", "今月")}の給与計算期限が近づいています";
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <h2 style="color: #f39c12;">給与計算期限のお知らせ</h2>
                    <p>{GetVar(variables, "month", "今月")}分の給与計算期限が近づいています。</p>
                    <ul>
                    <li>期限: {GetVar(variables, "deadline", "N/A")}</li>
                    <li>残り時間: {GetVar(variables, "remaining", "N/A")}</li>
                    <li>ステータス: {GetVar(variables, "status", "未完了")}</li>
                    </ul>
                    <p>期限内に計算を完了してください。</p>
                    <p style="margin-top: 30px; color: #666; font-size: 12px;">このメールは自動送信されています。</p>
                    </body></html>
                    """;
                break;

            case "payroll_deadline_missed":
                subject = $"【緊急】{GetVar(variables, "month", "今月")}の給与計算期限を過ぎています";
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <h2 style="color: #c0392b;">給与計算期限超過のお知らせ</h2>
                    <p><strong>{GetVar(variables, "month", "今月")}分の給与計算期限を過ぎています。</strong></p>
                    <ul>
                    <li>期限: {GetVar(variables, "deadline", "N/A")}</li>
                    <li>超過時間: {GetVar(variables, "overdue", "N/A")}</li>
                    </ul>
                    <p style="color: #c0392b; font-weight: bold;">至急対応をお願いいたします。</p>
                    <p style="margin-top: 30px; color: #666; font-size: 12px;">このメールは自動送信されています。</p>
                    </body></html>
                    """;
                break;

            case "payroll_calculation_failed":
                subject = $"【エラー】{GetVar(variables, "month", "今月")}の給与計算でエラーが発生しました";
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <h2 style="color: #c0392b;">給与計算エラーのお知らせ</h2>
                    <p>給与計算処理中にエラーが発生しました。</p>
                    <ul>
                    <li>対象月: {GetVar(variables, "month", "N/A")}</li>
                    <li>エラー: {GetVar(variables, "error", "不明なエラー")}</li>
                    <li>発生時刻: {GetVar(variables, "timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm"))}</li>
                    </ul>
                    <p>システム管理者に連絡してください。</p>
                    <p style="margin-top: 30px; color: #666; font-size: 12px;">このメールは自動送信されています。</p>
                    </body></html>
                    """;
                break;

            default:
                subject = GetVar(variables, "subject", "通知");
                body = $"""
                    <html><body style="font-family: sans-serif; padding: 20px;">
                    <p>{GetVar(variables, "message", "通知があります。")}</p>
                    </body></html>
                    """;
                break;
        }

        return (subject, body);
    }

    private static string GetVar(Dictionary<string, string> vars, string key, string defaultValue)
    {
        return vars.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
    }
}


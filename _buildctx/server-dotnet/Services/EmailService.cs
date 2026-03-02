using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Server.Services;

/// <summary>
/// Staffing Email service for IMAP sync and SMTP send (using MailKit)
/// </summary>
public class StaffingEmailService
{
    private readonly Infrastructure.EmailSettings _settings;
    private readonly ILogger<StaffingEmailService> _logger;
    private readonly NpgsqlDataSource _ds;

    public StaffingEmailService(IOptions<Infrastructure.EmailSettings> settings, ILogger<StaffingEmailService> logger, NpgsqlDataSource ds)
    {
        _settings = settings.Value;
        _logger = logger;
        _ds = ds;
    }

    /// <summary>
    /// Sync emails from IMAP server to database
    /// </summary>
    public async Task<(int newCount, int totalCount)> SyncInboxAsync(string companyCode, int maxMessages = 50, CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogWarning("Email service is disabled");
            return (0, 0);
        }

        var newCount = 0;
        var totalCount = 0;

        using var imap = new ImapClient();
        
        try
        {
            _logger.LogInformation("Connecting to IMAP server {Host}:{Port}", _settings.ImapHost, _settings.ImapPort);
            
            await imap.ConnectAsync(_settings.ImapHost, _settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await imap.AuthenticateAsync(_settings.SmtpUser, _settings.SmtpPassword, ct);

            var inbox = imap.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            totalCount = inbox.Count;
            _logger.LogInformation("IMAP inbox has {Count} messages", totalCount);

            // Get recent messages (last N days or max messages)
            var query = SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-30));
            var uids = await inbox.SearchAsync(query, ct);
            
            // Take only the most recent ones
            var recentUids = uids.OrderByDescending(u => u.Id).Take(maxMessages).ToList();

            foreach (var uid in recentUids)
            {
                try
                {
                    var message = await inbox.GetMessageAsync(uid, ct);
                    var messageId = message.MessageId ?? uid.ToString();

                    // Check if already exists
                    if (await EmailExistsAsync(companyCode, messageId, ct))
                    {
                        continue;
                    }

                    // Save to database
                    await SaveEmailAsync(companyCode, message, messageId, ct);
                    newCount++;
                    
                    _logger.LogDebug("Saved email: {Subject}", message.Subject);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process message UID {Uid}", uid);
                }
            }

            await imap.DisconnectAsync(true, ct);
            _logger.LogInformation("IMAP sync completed: {NewCount} new emails", newCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IMAP sync failed");
            throw;
        }

        return (newCount, totalCount);
    }

    /// <summary>
    /// Send an email via SMTP
    /// </summary>
    public async Task SendEmailAsync(string toAddress, string subject, string bodyHtml, string? bodyText = null, CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogWarning("Email service is disabled, not sending email to {To}", toAddress);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;

        var builder = new BodyBuilder();
        if (!string.IsNullOrEmpty(bodyHtml))
        {
            builder.HtmlBody = bodyHtml;
        }
        if (!string.IsNullOrEmpty(bodyText))
        {
            builder.TextBody = bodyText;
        }
        else if (!string.IsNullOrEmpty(bodyHtml))
        {
            // Generate plain text from HTML
            builder.TextBody = StripHtml(bodyHtml);
        }
        message.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        
        try
        {
            _logger.LogInformation("Connecting to SMTP server {Host}:{Port}", _settings.SmtpHost, _settings.SmtpPort);
            
            await smtp.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(_settings.SmtpUser, _settings.SmtpPassword, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
            
            _logger.LogInformation("Email sent to {To}: {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", toAddress);
            throw;
        }
    }

    /// <summary>
    /// Process email queue - send pending emails
    /// </summary>
    public async Task<int> ProcessQueueAsync(string companyCode, CancellationToken ct = default)
    {
        if (!_settings.Enabled)
        {
            return 0;
        }

        var sentCount = 0;
        
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        
        // Get pending emails from queue
        cmd.CommandText = @"
            SELECT id, payload 
            FROM stf_email_queue 
            WHERE company_code = $1 
              AND (payload->>'status' IS NULL OR payload->>'status' = 'pending')
            ORDER BY created_at ASC
            LIMIT 10";
        cmd.Parameters.AddWithValue(companyCode);

        var emails = new List<(Guid id, JsonElement payload)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var payloadJson = reader.GetString(1);
                using var doc = JsonDocument.Parse(payloadJson);
                emails.Add((id, doc.RootElement.Clone()));
            }
        }

        foreach (var (id, payload) in emails)
        {
            try
            {
                var to = payload.GetProperty("to_addresses").GetString()!;
                var subject = payload.GetProperty("subject").GetString()!;
                var bodyHtml = payload.TryGetProperty("body_html", out var bh) ? bh.GetString() : "";

                await SendEmailAsync(to, subject, bodyHtml ?? "", null, ct);

                // Update status to sent
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE stf_email_queue 
                    SET payload = payload || '{""status"": ""sent"", ""sent_at"": """ + DateTime.UtcNow.ToString("o") + @"""}'::jsonb
                    WHERE id = $1";
                updateCmd.Parameters.AddWithValue(id);
                await updateCmd.ExecuteNonQueryAsync(ct);

                sentCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send queued email {Id}", id);
                
                // Update status to failed
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
                    UPDATE stf_email_queue 
                    SET payload = payload || '{""status"": ""failed"", ""error"": """ + ex.Message.Replace("\"", "'") + @"""}'::jsonb
                    WHERE id = $1";
                updateCmd.Parameters.AddWithValue(id);
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        return sentCount;
    }

    private async Task<bool> EmailExistsAsync(string companyCode, string messageId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM stf_email_messages 
            WHERE company_code = $1 AND payload->>'message_id' = $2
            LIMIT 1";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(messageId);
        
        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    private async Task SaveEmailAsync(string companyCode, MimeMessage message, string messageId, CancellationToken ct)
    {
        var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var fromName = message.From.Mailboxes.FirstOrDefault()?.Name ?? "";
        var toAddresses = string.Join(", ", message.To.Mailboxes.Select(m => m.Address));
        var subject = message.Subject ?? "";
        var bodyText = message.TextBody ?? "";
        var bodyHtml = message.HtmlBody ?? "";
        var receivedAt = message.Date.UtcDateTime;

        var payload = JsonSerializer.Serialize(new
        {
            message_id = messageId,
            from_address = fromAddress,
            from_name = fromName,
            to_addresses = toAddresses,
            subject = subject,
            body_text = bodyText,
            body_html = bodyHtml,
            received_at = receivedAt,
            status = "new",
            is_read = false,
            folder = "inbox"
        });

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO stf_email_messages (company_code, payload)
            VALUES ($1, $2::jsonb)
            ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue(companyCode);
        cmd.Parameters.AddWithValue(payload);
        
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        
        // Simple HTML stripping
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }
}


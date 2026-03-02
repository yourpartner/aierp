using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure;

/// <summary>
/// Background worker that periodically executes enabled notification policies for each company.
/// It keeps an internal loop aligned with natural minute boundaries to honor policy schedules.
/// </summary>
public sealed class NotificationSchedulerService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ApnsService _apns;
    private readonly ILogger<NotificationSchedulerService>? _logger;

    /// <summary>
    /// Creates the scheduler with the shared data source, push service, and optional logger.
    /// </summary>
    /// <param name="ds">Data source used to query active policies.</param>
    /// <param name="apns">Push notification service injected into policy executions.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public NotificationSchedulerService(NpgsqlDataSource ds, ApnsService apns, ILogger<NotificationSchedulerService>? logger = null)
    {
        _ds = ds;
        _apns = apns;
        _logger = logger;
    }

    /// <summary>
    /// Main worker loop that keeps firing near minute boundaries until cancellation.
    /// </summary>
    /// <param name="stoppingToken">Signals when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Minute-level cadence: stay close to the natural minute boundary.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                try { _logger?.LogError(ex, "Notification scheduler tick failed"); } catch { }
            }

            var now = DateTimeOffset.UtcNow;
            var msToNextMinute = 60000 - (now.Second * 1000 + now.Millisecond);
            if (msToNextMinute < 200) msToNextMinute = 200;
            try { await Task.Delay(msToNextMinute, stoppingToken); } catch { }
        }
    }

    /// <summary>
    /// Executes one scan across companies and runs their active notification policies.
    /// </summary>
    /// <param name="ct">Cancellation token propagated from the outer loop.</param>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        var companies = new List<string>();
        await using (var conn = await _ds.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT company_code FROM notification_policies WHERE is_active=TRUE";
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                companies.Add(rd.GetString(0));
            }
        }

        foreach (var cc in companies)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // honor schedule (force=false)
                _ = await Server.Modules.NotificationsPoliciesModule.RunPoliciesAsync(_ds, _apns, cc, false);
            }
            catch (Exception ex)
            {
                try { _logger?.LogWarning(ex, "Run policies failed for company {Company}", cc); } catch { }
            }
        }
    }
}



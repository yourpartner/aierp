using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Server.Infrastructure;

public sealed class NotificationSchedulerService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ApnsService _apns;
    private readonly ILogger<NotificationSchedulerService>? _logger;

    public NotificationSchedulerService(NpgsqlDataSource ds, ApnsService apns, ILogger<NotificationSchedulerService>? logger = null)
    {
        _ds = ds;
        _apns = apns;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 分钟级调度：尽量贴近自然分钟边界执行
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



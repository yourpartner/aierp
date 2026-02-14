using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Server.Infrastructure.Skills;

/// <summary>
/// 后台定时任务 — 执行异常检测 + 学习模式凝练
/// 每天执行一次（可配置）
/// </summary>
public class ProactiveAlertBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ProactiveAlertBackgroundService> _logger;

    /// <summary>执行间隔（默认每24小时一次）</summary>
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    /// <summary>启动延迟（等待应用完全启动后再开始）</summary>
    private readonly TimeSpan _startupDelay = TimeSpan.FromMinutes(2);

    public ProactiveAlertBackgroundService(
        IServiceProvider services,
        ILogger<ProactiveAlertBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ProactiveAlert] 后台服务启动，{Delay} 后开始首次执行",
            _startupDelay);

        await Task.Delay(_startupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProactiveAlert] 执行周期异常");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("[ProactiveAlert] 开始执行异常检测 + 学习凝练...");

        using var scope = _services.CreateScope();
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // 获取所有活跃公司
        var companyCodes = new List<string>();
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT code FROM companies WHERE status = 'active'";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                companyCodes.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProactiveAlert] 获取公司列表失败，使用默认");
            companyCodes.Add("JP01");
        }

        foreach (var companyCode in companyCodes)
        {
            try
            {
                // 1. 异常检测 + 推送
                var alertService = scope.ServiceProvider.GetRequiredService<ProactiveAlertService>();
                var alerts = await alertService.RunAllChecksAsync(companyCode, ct);

                if (alerts.Count > 0)
                {
                    _logger.LogInformation("[ProactiveAlert] 检测到 {Count} 个异常: {Company}",
                        alerts.Count, companyCode);

                    // 获取渠道适配器
                    WeComChannelAdapter? wecomAdapter = null;
                    LineChannelAdapter? lineAdapter = null;
                    try { wecomAdapter = scope.ServiceProvider.GetService<WeComChannelAdapter>(); } catch { }
                    try { lineAdapter = scope.ServiceProvider.GetService<LineChannelAdapter>(); } catch { }

                    await alertService.DispatchAlertsAsync(
                        companyCode, alerts, wecomAdapter, lineAdapter, ct);
                }

                // 2. 学习模式凝练
                var learningCollector = scope.ServiceProvider.GetRequiredService<LearningEventCollector>();
                await learningCollector.ConsolidatePatternsAsync(companyCode, ct);

                _logger.LogInformation("[ProactiveAlert] {Company} 处理完成", companyCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ProactiveAlert] {Company} 处理失败", companyCode);
            }
        }

        _logger.LogInformation("[ProactiveAlert] 本次执行完成");
    }
}

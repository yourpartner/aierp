using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Server.Infrastructure;

namespace Server.Modules;

public sealed class MoneytreePostingBackgroundService : BackgroundService
{
    private readonly MoneytreePostingJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneytreePostingBackgroundService> _logger;

    public MoneytreePostingBackgroundService(
        MoneytreePostingJobQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<MoneytreePostingBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.DequeueAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var postingService = scope.ServiceProvider.GetRequiredService<MoneytreePostingService>();
                var user = new Auth.UserCtx(
                    job.RequestedBy ?? "moneytree-bot",
                    new[] { "SYSTEM" },
                    Array.Empty<string>(),
                    null,
                    null,
                    "Moneytree Bot",
                    job.CompanyCode);
                await postingService.ProcessAsync(job.CompanyCode, user, job.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Moneytree posting job failed for company {CompanyCode}", job.CompanyCode);
            }
        }
    }
}


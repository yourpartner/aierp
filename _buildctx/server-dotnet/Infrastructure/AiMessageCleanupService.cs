using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Server.Infrastructure;

public sealed class AiMessageCleanupOptions
{
    public int RetainDays { get; set; } = 30;
    public int RetainPerSession { get; set; } = 500;
    public int IntervalMinutes { get; set; } = 360;
}

public sealed class AiMessageCleanupService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<AiMessageCleanupService> _logger;
    private readonly AiMessageCleanupOptions _options;

    public AiMessageCleanupService(
        NpgsqlDataSource dataSource,
        ILogger<AiMessageCleanupService> logger,
        IOptions<AiMessageCleanupOptions>? options = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _options = options?.Value ?? new AiMessageCleanupOptions();
        if (_options.IntervalMinutes <= 0)
        {
            _options.IntervalMinutes = 360;
        }
        if (_options.RetainPerSession <= 0)
        {
            _options.RetainPerSession = 500;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromMinutes(2);
        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI message cleanup failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        if (_options.RetainDays <= 0 && _options.RetainPerSession <= 0)
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var threshold = _options.RetainDays > 0
                ? DateTime.UtcNow.AddDays(-_options.RetainDays)
                : (DateTime?)null;

            var retainPerSession = _options.RetainPerSession;

            cmd.CommandText = @"
WITH ranked AS (
    SELECT id, session_id, role, content, payload, task_id, created_at,
           row_number() OVER (PARTITION BY session_id ORDER BY created_at DESC) AS rn
    FROM ai_messages
),
target AS (
    SELECT id, session_id, role, content, payload, task_id, created_at
    FROM ranked
    WHERE ($1 IS NULL OR created_at < $1)
       OR rn > $2
),
inserted AS (
    INSERT INTO ai_messages_archive (id, session_id, role, content, payload, task_id, created_at)
    SELECT id, session_id, role, content, payload, task_id, created_at
    FROM target
    ON CONFLICT (id) DO NOTHING
    RETURNING id
)
DELETE FROM ai_messages WHERE id IN (SELECT id FROM target);
";

            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)threshold ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.AddWithValue(retainPerSession);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);

            if (affected > 0)
            {
                _logger.LogInformation("AI message cleanup removed {Count} records", affected);
            }
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}


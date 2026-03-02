using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace Server.Modules;

public sealed class MoneytreePostingJobQueue
{
    private readonly Channel<MoneytreePostingJob> _channel = Channel.CreateUnbounded<MoneytreePostingJob>();

    public ValueTask EnqueueAsync(MoneytreePostingJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<MoneytreePostingJob> DequeueAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public readonly record struct MoneytreePostingJob(string CompanyCode, string? RequestedBy, int BatchSize);
}


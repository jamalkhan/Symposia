using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// Moves every stored secret to the active master key (#245 FR7), online: batch by batch, each a short transaction,
/// pausing between batches. Idempotent and resumable: rows are picked by their key, not a cursor, so a run that stops
/// (a crash, a deploy) simply continues next time. A hosted service, so shutdown cancels it between batches rather
/// than abandoning one (review: Carl #8). One run at a time across replicas (advisory lock).
/// </summary>
public sealed class ResealService(AuthorityStore store, IOptions<AuthorityOptions> options, ILogger<ResealService> logger) : BackgroundService
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private int _running;
    private long _resealed;
    private volatile string? _lastError;

    public bool Running => Volatile.Read(ref _running) == 1;

    /// <summary>Secrets re-sealed by this process since it started (remaining work: <c>KeyCountsAsync</c>).</summary>
    public long Resealed => Interlocked.Read(ref _resealed);

    public string? LastError => _lastError;

    /// <summary>
    /// Asks for a run; false when one is already running or queued here, or re-sealing isn't allowed yet (the
    /// Legacy seal format would re-seal into a format the previous release can't read; Frank #2).
    /// </summary>
    public bool Request() =>
        store.Keys.Format == SealFormat.Bound && !Running && _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            Volatile.Write(ref _running, 1);
            try
            {
                // Acquired inside try/finally: a failure to take the lock must not leave Running stuck on (Lenny #4).
                await using var held = await store.TryLockResealAsync(stoppingToken);
                if (held is null)
                {
                    _lastError = "another replica is re-sealing";
                    continue;
                }

                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Re-seal could not start.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }

    /// <summary>Re-seals until nothing is left (also used directly by tests); returns secrets re-sealed in this run.</summary>
    public async Task<long> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        _lastError = null;
        long run = 0;
        try
        {
            while (await store.ResealBatchAsync(Math.Max(1, o.ResealBatchSize), ct) is var n and > 0)
            {
                run += n;
                Interlocked.Add(ref _resealed, n);
                logger.LogInformation("Re-seal: {Total} secret(s) moved to master key {Key} this run.", run, store.Keys.Active);
                await Task.Delay(Math.Max(0, o.ResealPauseMilliseconds), ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Re-seal stopped after {Total} secret(s); run it again to resume.", run);
        }

        return run;
    }
}

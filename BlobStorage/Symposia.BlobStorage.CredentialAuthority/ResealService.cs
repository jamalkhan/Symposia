using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.CredentialAuthority;

/// <summary>
/// Moves every stored secret to the active master key (#245 FR7), online: batch by batch, each a short transaction,
/// pausing between batches. Idempotent and resumable: rows are picked by their key, not a cursor, so a run that
/// stops (a crash, a deploy) simply continues next time. One run at a time across replicas (advisory lock).
/// </summary>
public sealed class ResealService(AuthorityStore store, IOptions<AuthorityOptions> options, ILogger<ResealService> logger)
{
    private int _running;

    public bool Running => Volatile.Read(ref _running) == 1;

    public long Resealed { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Starts a run in the background; false when one is already running here or on another replica.</summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        var lockConn = await store.TryLockResealAsync(ct);
        if (lockConn is null)
        {
            Volatile.Write(ref _running, 0);
            return false;
        }

        _ = Task.Run(async () =>
        {
            await using var held = lockConn;
            try
            {
                await RunAsync(CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }

    /// <summary>Re-seals until nothing is left (also used directly by tests).</summary>
    public async Task<long> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        LastError = null;
        Resealed = 0;
        try
        {
            while (await store.ResealBatchAsync(Math.Max(1, o.ResealBatchSize), ct) is var n and > 0)
            {
                Resealed += n;
                logger.LogInformation("Re-seal: {Total} secret(s) moved to master key {Key} so far.", Resealed, store.Keys.Active);
                await Task.Delay(Math.Max(0, o.ResealPauseMilliseconds), ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            logger.LogError(ex, "Re-seal stopped after {Total} secret(s); run it again to resume.", Resealed);
        }

        return Resealed;
    }
}

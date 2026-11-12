using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Symposia.Network.Node.Rewards;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Network.Verifier;

/// <summary>One gateway's factors for an epoch (#225 FR4.4), in ScoringWeights order.</summary>
public sealed record GatewayFactors(uint UptimeBps, uint SuccessBps, long P95Ms, int Samples, long ExpectedSamples)
{
    public bool LowCoverage => (long)Samples * 2 < ExpectedSamples;
}

/// <summary>The arithmetic, kept pure so it's tested exactly (#225 FR4.4, huddle: bps round down, nearest-rank p95).</summary>
public static class FactorMath
{
    public static GatewayFactors Compute(IReadOnlyList<ProbeSample> samples, long expectedSamples)
    {
        var n = samples.Count;
        if (n == 0)
        {
            return new GatewayFactors(0, 0, 0, 0, expectedSamples);
        }

        var up = samples.Count(s => ProbeOutcomes.IsUp(s.Outcome));
        var ok = samples.Where(s => s.Outcome == ProbeOutcome.Ok).Select(s => s.LatencyMs).Order().ToList();
        var p95 = ok.Count == 0 ? 0 : ok[(int)((((long)ok.Count * 95) + 99) / 100) - 1];
        return new GatewayFactors((uint)((long)up * 10_000 / n), (uint)((long)ok.Count * 10_000 / n), p95, n, expectedSamples);
    }

    /// <summary>10x the median of the valid figures, never below <paramref name="floor"/>; an even count averages the middle two.</summary>
    public static long Cap(IReadOnlyCollection<long> validFigures, long floor)
    {
        if (validFigures.Count == 0)
        {
            return floor;
        }

        var sorted = validFigures.Order().ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] / 2) + (sorted[mid] / 2);
        return Math.Max(floor, median > long.MaxValue / 10 ? long.MaxValue : median * 10);
    }
}

/// <summary>What the Verifier card shows (#225 FR7): read by the UI, never a database query per page load.</summary>
public sealed record VerifierSnapshot(
    VerifierMode Mode, string Reason, ulong Epoch, long DiscardedMinutes, IReadOnlyList<VerifierSnapshot.Target> Targets, IReadOnlyList<VerifierReport> Reports)
{
    public sealed record Target(string Node, string? Url, string? Problem, ProbeOutcome? LastOutcome, long? LastLatencyMs, long? LastProbeMs);
}

/// <summary>
/// The Verifier role's engine (#225): one probe loop (monitor health, discovery, jittered per-target probes) and one
/// attest loop (measure finished epochs, cap bytes, sign, submit, sweep). Both persist everything they decide, so
/// a restart neither loses nor repeats a report. Blob Gateway is the only measured service for now.
/// </summary>
public sealed class VerifierEngine : IAsyncDisposable
{
    public const int Lookback = 3;

    /// <summary>Wait after an epoch ends before measuring: the last minute's probes and health checks have landed.</summary>
    public static readonly TimeSpan MeasureGrace = TimeSpan.FromSeconds(45);

    /// <summary>How long after the epoch ends gateways' signed figures are waited for before missing ones count as 0.</summary>
    public static readonly TimeSpan FigureWait = TimeSpan.FromSeconds(120);

    private const byte Service = BlobGatewayProbe.Service;
    private const int MaxConcurrentProbes = 32;

    private readonly VerifierOptions _options;
    private readonly VerifierStore _store;
    private readonly IVerifierChain _chain;
    private readonly IReportRelay _relay;
    private readonly BlobGatewayProbe _probe;
    private readonly MonitorHealthGuard _health;
    private readonly TargetDirectory _directory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _vantage;
    private readonly ConcurrentDictionary<string, (ProbeOutcome Outcome, long LatencyMs, long AtMs)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _due = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _jitter;
    private readonly HashSet<(ulong, string)> _warned = [];
    private IReadOnlyList<ProbeTarget> _targets = [];
    private (ulong Epoch, long StartMs, long NextStartMs)? _epoch;
    private long _checkedStartMinute = -1;
    private long _checkedEndMinute = -1;
    private long _lastTickMs;
    private long _targetsRefreshedMs;
    private CancellationTokenSource? _stop;
    private Task? _loops;

    public VerifierEngine(VerifierOptions options, VerifierStore store, IVerifierChain chain, IReportRelay relay, HttpClient http, ILogger logger,
        TimeProvider? time = null, Random? jitter = null)
    {
        _options = options;
        _store = store;
        _chain = chain;
        _relay = relay;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared;
        _vantage = options.NodeAddress.ToLowerInvariant();
        _probe = new BlobGatewayProbe(http);
        _health = new MonitorHealthGuard(http, options);
        _directory = new TargetDirectory(chain, store, http, options, logger, _time);
        Authority = new VerifierAuthority(chain, options, logger, _time);
    }

    public VerifierAuthority Authority { get; }

    public void Start()
    {
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loops = Task.WhenAll(Loop(TimeSpan.FromSeconds(5), ProbeTickAsync, ct), Loop(TimeSpan.FromSeconds(15), AttestTickAsync, ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is null)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            await (_loops ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _stop = null;
    }

    private async Task Loop(TimeSpan every, Func<CancellationToken, Task> tick, CancellationToken ct)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(every, _time);
        do
        {
            try
            {
                await tick(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Verifier: pass failed, retrying: {Reason}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One probe pass: health checks at each minute's start and end, discovery each minute, due probes. Public for tests.</summary>
    public async Task ProbeTickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var minute = now / 60_000 * 60_000;

        // A stalled scheduler (sleep, GC, starvation) makes probes look like timeouts: discard that minute.
        if (_lastTickMs > 0 && now - _lastTickMs > 15_000)
        {
            _store.RecordMinute(minute, false, "stall");
        }

        _lastTickMs = now;
        if (_checkedStartMinute != minute || (now - minute >= 50_000 && _checkedEndMinute != minute))
        {
            var problem = await _health.CheckAsync(ct);
            _store.RecordMinute(minute, problem is null, problem);
            if (_checkedStartMinute != minute)
            {
                _checkedStartMinute = minute;
            }
            else
            {
                _checkedEndMinute = minute;
            }

            if (problem is not null)
            {
                _logger.LogWarning("Verifier: minute discarded for every gateway: {Problem}.", problem);
            }
        }

        if (_epoch is not { } epoch || now >= epoch.NextStartMs || now - _targetsRefreshedMs >= 60_000)
        {
            var current = await _chain.CurrentEpochAsync(ct);
            _epoch = epoch = (current, await _chain.EpochStartAsync(current, ct) * 1000, await _chain.EpochStartAsync(current + 1, ct) * 1000);
            _targets = await _directory.TargetsAsync(Service, current, ct);
            _targetsRefreshedMs = now;
        }

        var due = _targets.Where(t => !_due.TryGetValue(t.Node, out var at) || now >= at).ToList();
        if (due.Count == 0)
        {
            return;
        }

        var key = Authority.Key;
        var samples = new ConcurrentBag<ProbeSample>();
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentProbes, CancellationToken = ct }, async (target, token) =>
        {
            var at = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var (outcome, latency) = await _probe.ProbeAsync(target, key, token);
            samples.Add(new ProbeSample(Service, target.Node, at, at >= epoch.NextStartMs ? epoch.Epoch + 1 : epoch.Epoch, _vantage, outcome, latency));
            _last[target.Node] = (outcome, latency, at);
            if (outcome is ProbeOutcome.ConnectFailed or ProbeOutcome.Timeout)
            {
                _directory.Failed(target.Node);
            }
            else
            {
                _directory.Succeeded(target.Node);
            }
        });
        _store.AddSamples([.. samples]);

        // Once a minute per target, at a random second, so a gateway can't time good behaviour to the probe.
        foreach (var target in due)
        {
            _due[target.Node] = minute + 60_000 + _jitter.NextInt64(0, 60_000);
        }
    }

    /// <summary>One attest pass over the last <see cref="Lookback"/> finished epochs. Public for tests.</summary>
    public async Task AttestTickAsync(CancellationToken ct)
    {
        var (mode, _) = await Authority.CheckAsync(fresh: false, ct);
        var current = await _chain.CurrentEpochAsync(ct);
        var now = _time.GetUtcNow();
        for (var epoch = current > Lookback ? current - Lookback : 0; epoch < current; epoch++)
        {
            var startMs = await _chain.EpochStartAsync(epoch, ct) * 1000;
            var endMs = await _chain.EpochStartAsync(epoch + 1, ct) * 1000;
            if (now < DateTimeOffset.FromUnixTimeMilliseconds(endMs) + MeasureGrace)
            {
                continue;
            }

            var open = await _chain.AcceptsReportsAsync(epoch, ct);
            if (open)
            {
                await MeasureAsync(epoch, startMs, endMs, mode, now, ct);
            }

            await AdvanceAsync(epoch, open, ct);
        }

        if (current > Lookback + 2)
        {
            var oldest = current - (Lookback + 2);
            _store.Sweep(oldest, await _chain.EpochStartAsync(oldest, ct) * 1000);
        }
    }

    /// <summary>Phase 1: every probed target gets a report row, once all signed figures are in (or the wait is over).</summary>
    private async Task MeasureAsync(ulong epoch, long startMs, long endMs, VerifierMode mode, DateTimeOffset now, CancellationToken ct)
    {
        var distributor = _options.RewardDistributorAddress;
        var pending = _store.TargetsIn(Service, epoch).Where(t => _store.GetReport(distributor, Service, t.Target, epoch) is null).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        // The cap needs every gateway's figure, so the whole epoch is fetched together.
        var urls = _targets.ToDictionary(t => t.Node, StringComparer.OrdinalIgnoreCase);
        var all = _store.TargetsIn(Service, epoch);
        var figures = new ConcurrentDictionary<string, (long? Bytes, ReportFlags Flag)>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (t, token) =>
        {
            var target = urls.GetValueOrDefault(t.Target) ?? new ProbeTarget(t.Target, null, "not currently discoverable");
            var figure = await _probe.FigureAsync(target, epoch, token);
            figures[t.Target] = figure is null ? (null, ReportFlags.BytesMissing)
                : EpochReportSignature.IsValid(figure, _options.ChainId, distributor, t.Target, epoch) ? (figure.BytesServed, ReportFlags.None)
                : (null, ReportFlags.BytesBadSignature);
        });

        var waited = now >= DateTimeOffset.FromUnixTimeMilliseconds(endMs) + FigureWait;
        if (!waited && figures.Values.Any(f => f.Flag == ReportFlags.BytesMissing))
        {
            return; // a gateway measures its own epoch 45 s after it ends: give it until FigureWait
        }

        var cap = FactorMath.Cap([.. figures.Values.Where(f => f.Bytes is not null).Select(f => f.Bytes!.Value)], _options.BytesServedFloor);
        foreach (var (target, firstMs) in pending)
        {
            var samples = _store.HealthySamples(Service, target, startMs, endMs, _vantage);
            if (samples.Count == 0)
            {
                continue; // zero samples: no report, which settles "No metric report"
            }

            var expected = _store.HealthyMinutes(Math.Max(startMs, firstMs / 60_000 * 60_000), endMs);
            var factors = FactorMath.Compute(samples, expected);
            var (claimed, flag) = figures.GetValueOrDefault(target, (null, ReportFlags.BytesMissing));
            var bytes = claimed is { } c ? Math.Min(c, cap) : 0;
            var flags = flag | (claimed > cap ? ReportFlags.BytesCapped : ReportFlags.None) | (factors.LowCoverage ? ReportFlags.LowCoverage : ReportFlags.None);
            if (flags != ReportFlags.None)
            {
                _logger.LogWarning("Verifier: {Target} epoch {Epoch} flagged {Flags} (bytes claimed {Claimed}, cap {Cap}, {Samples}/{Expected} samples).",
                    target, epoch, flags, claimed, cap, factors.Samples, factors.ExpectedSamples);
            }

            Save(new VerifierReport(distributor, Service, target, epoch, [factors.UptimeBps, factors.SuccessBps, factors.P95Ms, bytes], factors.UptimeBps,
                claimed, flags, mode == VerifierMode.Attesting ? ReportState.Measured : ReportState.ObservedOnly, null, null, null, null, 0, 0));
        }
    }

    /// <summary>Phase 2: sign what's measured and submit what's signed, checking authority fresh before each.</summary>
    private async Task AdvanceAsync(ulong epoch, bool open, CancellationToken ct)
    {
        var rows = _store.OpenReports(_options.RewardDistributorAddress).Where(r => r.Epoch == epoch).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        if (!open)
        {
            foreach (var r in rows)
            {
                Save(r with { State = ReportState.Terminal, LastError = r.State == ReportState.ObservedOnly ? "NotSigner" : "FinalityWindowElapsed" });
            }

            _logger.LogWarning("Verifier: epoch {Epoch}'s window closed with {Count} report(s) unsent.", epoch, rows.Count);
            return;
        }

        if ((await Authority.CheckAsync(fresh: true, ct)).Mode != VerifierMode.Attesting || Authority.Key is not { } key)
        {
            return; // observed only: kept open, signed if this host becomes the signer while the window is open
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].State != ReportState.Signed || !string.Equals(rows[i].Signer, key.Address, StringComparison.OrdinalIgnoreCase))
            {
                var attestation = await key.AttestAsync(ReportOf(rows[i]), ct);
                rows[i] = Save(rows[i] with { State = ReportState.Signed, Signer = attestation.Signer, Signature = attestation.Signature });
            }
        }

        if ((await Authority.CheckAsync(fresh: true, ct)).Mode != VerifierMode.Attesting)
        {
            return; // authority changed between signing and sending: send nothing
        }

        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (row, token) =>
        {
            var outcome = await _relay.SubmitAsync(Wire.Request.From(ReportOf(row), new MetricAttestation(row.Signature!, row.Signer!)), token);
            if (outcome.Submitted)
            {
                _logger.LogInformation("Verifier: {Target} epoch {Epoch} reported in {Tx}.", row.Target, epoch, outcome.TransactionHash ?? "an earlier transaction");
                Save(row with { State = ReportState.Submitted, TxHash = outcome.TransactionHash ?? row.TxHash, LastError = null, Attempts = row.Attempts + 1 });
            }
            else if (outcome.Terminal)
            {
                _logger.LogWarning("Verifier: {Target} epoch {Epoch} refused: {Error}.", row.Target, epoch, outcome.Error);
                Save(row with { State = ReportState.Terminal, LastError = outcome.Error, Attempts = row.Attempts + 1 });
            }
            else
            {
                lock (_warned)
                {
                    if (_warned.Add((epoch, row.Target)))
                    {
                        _logger.LogWarning("Verifier: {Target} epoch {Epoch} didn't go through, retrying until the window closes: {Error}.", row.Target, epoch, outcome.Error);
                    }
                }

                Save(row with { LastError = outcome.Error, Attempts = row.Attempts + 1 });
            }
        });
    }

    public async Task<VerifierSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var (mode, reason) = Authority.Current;
        var epoch = _epoch;
        var discarded = epoch is { } e ? _store.DiscardedMinutes(e.StartMs, e.NextStartMs) : 0;
        var targets = _targets.Select(t => _last.TryGetValue(t.Node, out var l)
            ? new VerifierSnapshot.Target(t.Node, t.Url, t.Problem, l.Outcome, l.LatencyMs, l.AtMs)
            : new VerifierSnapshot.Target(t.Node, t.Url, t.Problem, null, null, null)).ToList();
        var since = epoch is { Epoch: > Lookback } x ? x.Epoch - Lookback : 0;
        await Task.CompletedTask;
        return new VerifierSnapshot(mode, reason, epoch?.Epoch ?? 0, discarded, targets, _store.LatestReports(_options.RewardDistributorAddress, since));
    }

    private MetricReport ReportOf(VerifierReport r) => new(r.Distributor, r.Target, r.Service, r.Epoch, [.. r.Values.Select(v => new System.Numerics.BigInteger(v))], r.HeartbeatBps);

    private VerifierReport Save(VerifierReport r)
    {
        r = r with { UpdatedMs = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        _store.SaveReport(r);
        return r;
    }
}

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
/// The Verifier role's engine (#225). Four loops, so none can hold up another:
/// <list type="bullet">
/// <item><b>health</b>: checks the references at each minute's start and end; a failure discards that minute for every gateway;</item>
/// <item><b>discovery</b>: the epoch, the Active targets and their node-signed URLs, once a minute;</item>
/// <item><b>probe</b>: due targets, each persisted as it completes; a scheduler stall discards the minutes it spanned;</item>
/// <item><b>attest</b>: measures each finished epoch once (after its samples are all in), caps bytes, signs, submits, sweeps.</item>
/// </list>
/// Everything decided is persisted, so a restart neither loses nor repeats a report.
/// </summary>
public sealed class VerifierEngine : IAsyncDisposable
{
    public const int Lookback = 3;

    /// <summary>Wait after an epoch ends before measuring: the last minute's probes and health checks have landed.</summary>
    public static readonly TimeSpan MeasureGrace = TimeSpan.FromSeconds(45);

    /// <summary>How long after the epoch ends gateways' signed figures are waited for before missing ones count as 0.</summary>
    public static readonly TimeSpan FigureWait = TimeSpan.FromSeconds(120);

    /// <summary>A probe loop that didn't run for this long was stalled (sleep, GC, starvation): its probes can't be trusted.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    private const int MaxConcurrentProbes = 32;

    private readonly VerifierOptions _options;
    private readonly VerifierStore _store;
    private readonly IVerifierChain _chain;
    private readonly IReportRelay _relay;
    private readonly IServiceProbe _probe;
    private readonly MonitorHealthGuard _health;
    private readonly TargetDirectory _directory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _vantage;
    private readonly ConcurrentDictionary<string, (ProbeOutcome Outcome, long LatencyMs, long AtMs)> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _due = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(ulong, string), byte> _warned = new();
    private readonly Random _jitter;
    private View _view = new(null, []);
    private long _checkedStartMinute = -1;
    private long _checkedEndMinute = -1;
    private long _lastProbeEndMs;
    private long _samplesThroughMs;
    private CancellationTokenSource? _stop;
    private Task? _loops;

    /// <summary>What the probe and attest loops share, swapped whole: never read half-updated.</summary>
    private sealed record View((ulong Epoch, long StartMs, long NextStartMs)? Epoch, IReadOnlyList<ProbeTarget> Targets);

    /// <param name="http">References, directories and the relay's client.</param>
    /// <param name="targetHttp">Probes and figures: no redirects, connect-time address checks (<see cref="TargetHttp"/>). Defaults to <paramref name="http"/>.</param>
    public VerifierEngine(VerifierOptions options, VerifierStore store, IVerifierChain chain, IReportRelay relay, HttpClient http, ILogger logger,
        TimeProvider? time = null, Random? jitter = null, HttpClient? targetHttp = null, IServiceProbe? probe = null)
    {
        _options = options;
        _store = store;
        _chain = chain;
        _relay = relay;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared;
        _vantage = options.NodeAddress.ToLowerInvariant();
        _probe = probe ?? new BlobGatewayProbe(targetHttp ?? http);
        _health = new MonitorHealthGuard(http, options);
        _directory = new TargetDirectory(chain, store, targetHttp ?? http, options, logger, _time);
        Authority = new VerifierAuthority(chain, options, logger, _time);
    }

    public VerifierAuthority Authority { get; }

    private byte Service => _probe.Service;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loops = Task.WhenAll(
            Loop(TimeSpan.FromSeconds(5), HealthTickAsync, ct),
            Loop(TimeSpan.FromSeconds(60), DiscoveryTickAsync, ct),
            Loop(TimeSpan.FromSeconds(5), ProbeTickAsync, ct),
            Loop(TimeSpan.FromSeconds(15), AttestTickAsync, ct));
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

    /// <summary>Checks the references at each minute's start and again in its last 10 s. Public for tests.</summary>
    public async Task HealthTickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var minute = now / 60_000 * 60_000;
        var start = _checkedStartMinute != minute;
        if (!start && (now - minute < 50_000 || _checkedEndMinute == minute))
        {
            return;
        }

        var problem = await _health.CheckAsync(ct);
        _store.RecordMinute(minute, problem is null, problem);
        (_checkedStartMinute, _checkedEndMinute) = start ? (minute, _checkedEndMinute) : (_checkedStartMinute, minute);
        if (problem is not null)
        {
            _logger.LogWarning("Verifier: minute discarded for every gateway: {Problem}.", problem);
        }
    }

    /// <summary>The current epoch and its targets. Public for tests.</summary>
    public async Task DiscoveryTickAsync(CancellationToken ct)
    {
        var current = await _chain.CurrentEpochAsync(ct);
        var epoch = (current, await _chain.EpochStartAsync(current, ct) * 1000, await _chain.EpochStartAsync(current + 1, ct) * 1000);
        var targets = await _directory.TargetsAsync(Service, current, ct);
        Volatile.Write(ref _view, new View(epoch, targets));
        foreach (var gone in _due.Keys.Where(k => !targets.Any(t => string.Equals(t.Node, k, StringComparison.OrdinalIgnoreCase))))
        {
            _due.TryRemove(gone, out _);
        }
    }

    /// <summary>Probes due targets, persisting each sample as it completes. Public for tests.</summary>
    public async Task ProbeTickAsync(CancellationToken ct)
    {
        var view = Volatile.Read(ref _view);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();

        // The loop wakes every 5 s; a gap since the last pass ended means the host didn't run us (sleep, GC,
        // starvation), and probes around it look like timeouts. Discard every minute the gap touched.
        if (_lastProbeEndMs > 0 && now - _lastProbeEndMs > StallAfter.TotalMilliseconds)
        {
            for (var m = _lastProbeEndMs / 60_000 * 60_000; m <= now; m += 60_000)
            {
                _store.RecordMinute(m, false, "stall");
            }
        }

        if (view.Epoch is not { } epoch)
        {
            _lastProbeEndMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
            return;
        }

        var minute = now / 60_000 * 60_000;
        var due = view.Targets.Where(t => !_due.TryGetValue(t.Node, out var at) || now >= at).ToList();
        var key = Authority.Key;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentProbes, CancellationToken = ct }, async (target, token) =>
        {
            var at = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var sampleEpoch = at >= epoch.NextStartMs ? epoch.Epoch + 1 : epoch.Epoch;
            var (outcome, latency) = await _probe.ProbeAsync(target, key, token);
            _store.AddSamples([new ProbeSample(Service, target.Node, at, sampleEpoch, _vantage, outcome, latency)]);
            if (target.Url is { } url)
            {
                _store.RememberUrl(Service, target.Node, sampleEpoch, url);
            }

            _last[target.Node] = (outcome, latency, at);
            if (outcome is ProbeOutcome.ConnectFailed or ProbeOutcome.Timeout)
            {
                _directory.Failed(target.Node);
            }
            else
            {
                _directory.Succeeded(target.Node);
            }

            // Once a minute per target, at a random second, so a gateway can't time good behaviour to the probe.
            _due[target.Node] = minute + 60_000 + _jitter.NextInt64(0, 60_000);
        });

        // Every probe started before `now` is persisted: the attest loop measures an epoch only once this passes its end.
        Volatile.Write(ref _samplesThroughMs, now);
        _lastProbeEndMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
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
            if (open && Volatile.Read(ref _samplesThroughMs) >= endMs)
            {
                await MeasureAsync(epoch, startMs, endMs, mode, now, ct);
            }

            await AdvanceAsync(epoch, open, ct);
        }

        foreach (var key in _warned.Keys.Where(k => k.Item1 + Lookback < current))
        {
            _warned.TryRemove(key, out _);
        }

        if (current > Lookback + 2)
        {
            var oldest = current - (Lookback + 2);
            _store.Sweep(oldest, await _chain.EpochStartAsync(oldest, ct) * 1000);
        }
    }

    /// <summary>
    /// Phase 1, once per epoch: every probed target gets a row in one pass, after all signed figures are in (or the
    /// wait is over), so the whole epoch shares one bytes cap. A target with no healthy samples gets a Terminal
    /// "NoSamples" row, never submitted, so it isn't re-examined every tick.
    /// </summary>
    private async Task MeasureAsync(ulong epoch, long startMs, long endMs, VerifierMode mode, DateTimeOffset now, CancellationToken ct)
    {
        var distributor = _options.RewardDistributorAddress;
        var pending = _store.UnreportedTargets(distributor, Service, epoch);
        if (pending.Count == 0)
        {
            return;
        }

        var figures = new ConcurrentDictionary<string, (long? Bytes, ReportFlags Flag)>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(_store.TargetsIn(Service, epoch), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (t, token) =>
        {
            // The URL it was probed at that epoch: a gateway deactivated or moved since still gets its figure fetched.
            var target = new ProbeTarget(t.Target, _store.UrlIn(Service, t.Target, epoch), null);
            var figure = await _probe.FigureAsync(target, epoch, token);
            figures[t.Target] = figure is null ? (null, ReportFlags.BytesMissing)
                : EpochReportSignature.IsValid(figure, _options.ChainId, distributor, t.Target, epoch) ? (figure.BytesServed, ReportFlags.None)
                : (null, ReportFlags.BytesBadSignature);
        });

        if (now < DateTimeOffset.FromUnixTimeMilliseconds(endMs) + FigureWait && figures.Values.Any(f => f.Flag == ReportFlags.BytesMissing))
        {
            return; // a gateway measures its own epoch 45 s after it ends: give it until FigureWait
        }

        var cap = FactorMath.Cap([.. figures.Values.Where(f => f.Bytes is not null).Select(f => f.Bytes!.Value)], _options.BytesServedFloor);
        var (state, why) = mode switch
        {
            VerifierMode.Attesting => (ReportState.Measured, (string?)null),
            VerifierMode.ObserveOnly => (ReportState.ObservedOnly, "NotSigner"),
            _ => (ReportState.ObservedOnly, "AuthorityUnknown"),
        };
        foreach (var (target, firstMs) in pending)
        {
            var samples = _store.HealthySamples(Service, target, startMs, endMs, _vantage);
            if (samples.Count == 0)
            {
                Save(new VerifierReport(distributor, Service, target, epoch, [], 0, null, ReportFlags.None, ReportState.Terminal, null, null, null, "NoSamples", 0, 0));
                continue; // no healthy samples: no report, which settles "No metric report"
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
                claimed, flags, state, null, null, null, why, 0, 0));
        }
    }

    /// <summary>Phase 2: sign what's measured and submit what's signed, checking authority fresh before each.</summary>
    private async Task AdvanceAsync(ulong epoch, bool open, CancellationToken ct)
    {
        var rows = _store.OpenReports(_options.RewardDistributorAddress, epoch).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        if (!open)
        {
            foreach (var r in rows)
            {
                // Observed-only rows keep why they weren't signed (NotSigner / AuthorityUnknown).
                Save(r with { State = ReportState.Terminal, LastError = r.State == ReportState.ObservedOnly ? r.LastError ?? "NotSigner" : "FinalityWindowElapsed" });
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
                rows[i] = Save(rows[i] with { State = ReportState.Signed, Signer = attestation.Signer, Signature = attestation.Signature, LastError = null });
            }
        }

        if ((await Authority.CheckAsync(fresh: true, ct)).Mode != VerifierMode.Attesting)
        {
            return; // authority changed between signing and sending: send nothing
        }

        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (row, token) =>
        {
            // The relay answers 200 without sending when the epoch already has this report on chain (#221), so a
            // resend after a lost response lands as Submitted, never as a refusal.
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
                if (_warned.TryAdd((epoch, row.Target), 0))
                {
                    _logger.LogWarning("Verifier: {Target} epoch {Epoch} didn't go through, retrying until the window closes: {Error}.", row.Target, epoch, outcome.Error);
                }

                Save(row with { LastError = outcome.Error, Attempts = row.Attempts + 1 });
            }
        });
    }

    /// <summary>The card's data: in-memory state plus two primary-key reads.</summary>
    public VerifierSnapshot Snapshot()
    {
        var (mode, reason) = Authority.Current;
        var view = Volatile.Read(ref _view);
        var discarded = view.Epoch is { } e ? _store.DiscardedMinutes(e.StartMs, e.NextStartMs) : 0;
        var targets = view.Targets.Select(t => _last.TryGetValue(t.Node, out var l)
            ? new VerifierSnapshot.Target(t.Node, t.Url, t.Problem, l.Outcome, l.LatencyMs, l.AtMs)
            : new VerifierSnapshot.Target(t.Node, t.Url, t.Problem, null, null, null)).ToList();
        var since = view.Epoch is { Epoch: > Lookback } x ? x.Epoch - Lookback : 0;
        return new VerifierSnapshot(mode, reason, view.Epoch?.Epoch ?? 0, discarded, targets, _store.LatestReports(_options.RewardDistributorAddress, since));
    }

    private MetricReport ReportOf(VerifierReport r) => new(r.Distributor, r.Target, r.Service, r.Epoch, [.. r.Values.Select(v => new System.Numerics.BigInteger(v))], r.HeartbeatBps);

    private VerifierReport Save(VerifierReport r)
    {
        r = r with { UpdatedMs = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        _store.SaveReport(r);
        return r;
    }
}

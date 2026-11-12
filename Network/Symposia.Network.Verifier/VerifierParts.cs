using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Symposia.Network.Node.Discovery;
using Symposia.Network.Node.Rewards;

namespace Symposia.Network.Verifier;

public enum VerifierMode
{
    /// <summary>The key is <c>reward.metric.signer</c>: measure, sign and submit.</summary>
    Attesting,

    /// <summary>Measure and show, never sign: wrong key, no key, or signer unset.</summary>
    ObserveOnly,

    /// <summary>Authority couldn't be confirmed (RPC down): sign nothing.</summary>
    Unknown,
}

/// <summary>
/// Whether this host may sign (#225 FR1.2): authority is on-chain, never a setting. The key's address must be
/// <c>reward.metric.signer</c>; off LocalDev it must also differ from the node key, so a node can't attest itself.
/// </summary>
public sealed class VerifierAuthority(IVerifierChain chain, VerifierOptions options, ILogger logger, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private (VerifierMode Mode, string Reason, DateTimeOffset At)? _last;

    public LocalKeyMetricAttestor? Key { get; private set; }

    public (VerifierMode Mode, string Reason) Current => _last is { } l ? (l.Mode, l.Reason) : (VerifierMode.Unknown, "Not checked yet");

    /// <summary>The mode, re-read when <paramref name="fresh"/> or the cache is older than 5 minutes.</summary>
    public async Task<(VerifierMode Mode, string Reason)> CheckAsync(bool fresh, CancellationToken ct)
    {
        if (!fresh && _last is { } cached && time.GetUtcNow() - cached.At < CacheFor)
        {
            return (cached.Mode, cached.Reason);
        }

        var result = await EvaluateAsync(ct);
        if (_last is not { } previous || previous.Mode != result.Mode || previous.Reason != result.Reason)
        {
            logger.LogInformation("Verifier: {Mode} ({Reason}).", result.Mode, result.Reason);
        }

        _last = (result.Mode, result.Reason, time.GetUtcNow());
        return result;
    }

    private async Task<(VerifierMode Mode, string Reason)> EvaluateAsync(CancellationToken ct)
    {
        try
        {
            Key = LocalKeyMetricAttestor.Load(options.SignerKeyPath);
        }
        catch (MetricSignerKeyException ex)
        {
            Key = null;
            return (VerifierMode.ObserveOnly, ex.Message);
        }

        string signer;
        try
        {
            if (await chain.ChainIdAsync(ct) != options.ChainId)
            {
                return (VerifierMode.ObserveOnly, "The RPC is on a different chain than this network");
            }

            signer = await chain.MetricSignerAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (VerifierMode.Unknown, $"Couldn't read the metric signer: {ex.Message}");
        }

        if (string.IsNullOrEmpty(signer) || signer == "0x0000000000000000000000000000000000000000")
        {
            return (VerifierMode.ObserveOnly, "The network has no metric signer set");
        }

        if (!string.Equals(signer, Key.Address, StringComparison.OrdinalIgnoreCase))
        {
            return (VerifierMode.ObserveOnly, $"This key ({Key.Address}) isn't the network's metric signer ({signer})");
        }

        if (!options.IsLocalDev && string.Equals(Key.Address, options.NodeAddress, StringComparison.OrdinalIgnoreCase))
        {
            return (VerifierMode.ObserveOnly, "The metric signer key is this node's own key; use a separate key");
        }

        return (VerifierMode.Attesting, "This key is the network's metric signer");
    }
}

/// <summary>A target the Verifier can probe: a node with the service Active, at its node-signed URL (or none).</summary>
public sealed record ProbeTarget(string Node, string? Url, string? Problem);

/// <summary>
/// Finds the gateways to probe (#225 FR2): nodes with Blob Gateway Active, at the URL from their node-signed
/// service directory on the on-chain primary endpoint. Off LocalDev it skips this host's own node (no
/// self-scoring) and rejects URLs that resolve to loopback, private or link-local addresses (SSRF).
/// </summary>
public sealed class TargetDirectory(IVerifierChain chain, VerifierStore store, HttpClient http, VerifierOptions options, ILogger logger, TimeProvider time)
{
    private readonly Dictionary<string, (string? Url, string? Problem, ulong Epoch, int Failures)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ProbeTarget>> TargetsAsync(byte service, ulong epoch, CancellationToken ct)
    {
        var active = await chain.ActiveAsync(service, ct);
        var targets = new List<ProbeTarget>();
        foreach (var node in active)
        {
            if (!options.IsLocalDev && string.Equals(node.Node, options.NodeAddress, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_cache.TryGetValue(node.Node, out var cached) || cached.Epoch != epoch || cached.Failures >= 3 || cached.Url is null)
            {
                var (url, problem) = await ResolveAsync(node.Node, node.Endpoint, service, ct);
                cached = (url, problem, epoch, 0);
                _cache[node.Node] = cached;
                if (problem is not null)
                {
                    logger.LogDebug("Verifier: {Node} has no probe URL: {Problem}.", node.Node, problem);
                }
            }

            targets.Add(new ProbeTarget(node.Node, cached.Url, cached.Problem));
        }

        return targets;
    }

    /// <summary>Counts a connect failure: three in a row refetch the directory, so a moved gateway is found mid-epoch.</summary>
    public void Failed(string node)
    {
        if (_cache.TryGetValue(node, out var c))
        {
            _cache[node] = c with { Failures = c.Failures + 1 };
        }
    }

    public void Succeeded(string node)
    {
        if (_cache.TryGetValue(node, out var c) && c.Failures > 0)
        {
            _cache[node] = c with { Failures = 0 };
        }
    }

    private async Task<(string? Url, string? Problem)> ResolveAsync(string node, string primary, byte service, CancellationToken ct)
    {
        ServiceDirectory? directory;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            directory = await http.GetFromJsonAsync<ServiceDirectory>(new Uri(new Uri(primary), ServiceDirectory.Path), timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, $"no service directory at {primary} ({ex.Message})");
        }

        if (directory is null)
        {
            return (null, "empty service directory");
        }

        if (directory.Problem(node, options.ChainId, options.NodeRegistryAddress, time.GetUtcNow(), store.DirectorySeq(node)) is { } problem)
        {
            return (null, problem);
        }

        store.SeeDirectory(node, directory.Seq);
        if (directory.UrlOf(service) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return (null, "the directory lists no Blob Gateway URL");
        }

        if (!options.IsLocalDev && !await options.IsPublicTarget(uri, ct))
        {
            return (null, "the Blob Gateway URL points at a loopback, private or link-local address");
        }

        return (url.TrimEnd('/'), null);
    }

    internal static async Task<bool> IsPublicAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    internal static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
        {
            return false;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0);
    }
}

/// <summary>
/// Probes one Blob Gateway (#225 FR4): a fresh nonce, signed with the metric-signer key when there is one (the
/// reserved lane), answered with <c>keccak(nonce ‖ canary)</c>; a 5 s budget.
/// </summary>
public sealed class BlobGatewayProbe(HttpClient http)
{
    public const byte Service = 8;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Nethereum.Signer.EthECKey _anonymous = Nethereum.Signer.EthECKey.GenerateKey();

    public async Task<(ProbeOutcome Outcome, long LatencyMs)> ProbeAsync(ProbeTarget target, LocalKeyMetricAttestor? key, CancellationToken ct)
    {
        if (target.Url is null)
        {
            return (ProbeOutcome.Undiscoverable, 0);
        }

        var nonce = ProbeChallenge.NewNonce();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var digest = ProbeChallenge.Digest(target.Node, ts, nonce);
        var signature = key?.SignDigest(digest) ?? new Nethereum.Signer.EthereumMessageSigner().Sign(digest, _anonymous);
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url + ProbeChallenge.Path);
        request.Headers.Add(ProbeChallenge.Header, ProbeChallenge.Format(ts, nonce, signature));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            if (status == 429)
            {
                return (ProbeOutcome.RateLimited, watch.ElapsedMilliseconds);
            }

            if (status >= 500)
            {
                return (ProbeOutcome.ServerError, watch.ElapsedMilliseconds);
            }

            if (status is < 200 or >= 300)
            {
                return (ProbeOutcome.OtherStatus, watch.ElapsedMilliseconds);
            }

            var body = await response.Content.ReadFromJsonAsync<ProbeChallenge.Response>(timeout.Token);
            var latency = watch.ElapsedMilliseconds;
            var expected = ProbeChallenge.Answer(nonce, ProbeChallenge.Canary(target.Node));
            return (string.Equals(body?.Answer, expected, StringComparison.OrdinalIgnoreCase) ? ProbeOutcome.Ok : ProbeOutcome.WrongAnswer, latency);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (ProbeOutcome.Timeout, watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return (ProbeOutcome.ConnectFailed, watch.ElapsedMilliseconds);
        }
        catch (System.Text.Json.JsonException)
        {
            return (ProbeOutcome.WrongAnswer, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>The gateway's signed bytes-served figure for <paramref name="epoch"/>; null when it has none yet.</summary>
    public async Task<EpochReportSignature.Report?> FigureAsync(ProbeTarget target, ulong epoch, CancellationToken ct)
    {
        if (target.Url is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync($"{target.Url}{EpochReportSignature.PathPrefix}{epoch}", timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<EpochReportSignature.Report>(timeout.Token) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>
/// Decides whether a minute counts (#225 FR4.3): the Blockchain Gateway must answer, and (off LocalDev) at least
/// one external reference. A monitor outage is discarded for every gateway instead of counting against them.
/// </summary>
public sealed class MonitorHealthGuard(HttpClient http, VerifierOptions options)
{
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Null when healthy; otherwise which reference failed.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        if (!await AnswersAsync(options.BlockchainGatewayUrl.TrimEnd('/') + "/healthz", requireSuccess: true, ct))
        {
            return "Blockchain Gateway unreachable";
        }

        if (options.IsLocalDev || options.ExternalReferences.Count == 0)
        {
            return null;
        }

        var answers = await Task.WhenAll(options.ExternalReferences.Select(r => AnswersAsync(r, requireSuccess: false, ct)));
        return answers.Any(a => a) ? null : "every external reference unreachable";
    }

    private async Task<bool> AnswersAsync(string url, bool requireSuccess, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return !requireSuccess || response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

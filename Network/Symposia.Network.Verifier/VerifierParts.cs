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

    private sealed record Checked(VerifierMode Mode, string Reason, DateTimeOffset At);

    private Checked? _last; // a reference swapped whole, so the UI thread never reads it half-written

    public LocalKeyMetricAttestor? Key { get; private set; }

    public (VerifierMode Mode, string Reason) Current => Volatile.Read(ref _last) is { } l ? (l.Mode, l.Reason) : (VerifierMode.Unknown, "Not checked yet");

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

        Volatile.Write(ref _last, new Checked(result.Mode, result.Reason, time.GetUtcNow()));
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Url, string? Problem, ulong Epoch, int Failures, long RetryAtMs)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A node with no usable directory isn't asked again for this long, so hung endpoints can't stall discovery.</summary>
    public static readonly TimeSpan UnresolvedBackoff = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<ProbeTarget>> TargetsAsync(byte service, ulong epoch, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var active = (await chain.ActiveAsync(service, ct)).DistinctBy(n => n.Node, StringComparer.OrdinalIgnoreCase)
            .Where(n => options.IsLocalDev || !string.Equals(n.Node, options.NodeAddress, StringComparison.OrdinalIgnoreCase)).ToList();

        // Resolved in parallel, each with its own timeout; unresolvable nodes back off instead of being retried every pass.
        var stale = active.Where(n => !_cache.TryGetValue(n.Node, out var c)
            || (c.Url is not null && (c.Epoch != epoch || c.Failures >= 3))
            || (c.Url is null && now >= c.RetryAtMs)).ToList();
        await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (node, token) =>
        {
            var (url, problem) = await ResolveAsync(node.Node, node.Endpoint, service, token);
            _cache[node.Node] = (url, problem, epoch, 0, url is null ? now + (long)UnresolvedBackoff.TotalMilliseconds : 0);
            if (problem is not null)
            {
                logger.LogDebug("Verifier: {Node} has no probe URL: {Problem}.", node.Node, problem);
            }
        });

        return [.. active.Select(n => _cache.TryGetValue(n.Node, out var c) ? new ProbeTarget(n.Node, c.Url, c.Problem) : new ProbeTarget(n.Node, null, "not resolved yet"))];
    }

    /// <summary>Counts a connect failure: three in a row refetch the directory, so a moved gateway is found mid-epoch.</summary>
    public void Failed(string node) => _cache.AddOrUpdate(node, _ => (null, null, 0, 1, 0), (_, c) => c with { Failures = c.Failures + 1 });

    public void Succeeded(string node) => _cache.AddOrUpdate(node, _ => (null, null, 0, 0, 0), (_, c) => c with { Failures = 0 });

    private async Task<(string? Url, string? Problem)> ResolveAsync(string node, string primary, byte service, CancellationToken ct)
    {
        if (!Uri.TryCreate(primary, UriKind.Absolute, out var primaryUri) || (!options.IsLocalDev && !await options.IsPublicTarget(primaryUri, ct)))
        {
            return (null, "the primary endpoint isn't a public URL");
        }

        ServiceDirectory? directory;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            directory = await http.GetFromJsonAsync<ServiceDirectory>(new Uri(new Uri(primary), ServiceDirectory.Path), timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, $"no service directory at {primary} ({ex.GetType().Name})");
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

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast)
        {
            return false;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            var v6 = ip.GetAddressBytes();
            return !(v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b); // 64:ff9b::/96 NAT64 reaches IPv4 space
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0 || (b[0] == 198 && b[1] is 18 or 19) || b[0] >= 224);
    }
}

/// <summary>
/// How the Verifier measures one Node Service (#225 arch §2): a new service adds a probe and nothing else.
/// </summary>
public interface IServiceProbe
{
    byte Service { get; }

    Task<(ProbeOutcome Outcome, long LatencyMs)> ProbeAsync(ProbeTarget target, LocalKeyMetricAttestor? key, CancellationToken ct);

    /// <summary>The service's own signed volume figure for an epoch, when it has one; null otherwise.</summary>
    Task<EpochReportSignature.Report?> FigureAsync(ProbeTarget target, ulong epoch, CancellationToken ct);
}

/// <summary>
/// Probes one Blob Gateway (#225 FR4): a fresh nonce, signed with the metric-signer key when there is one (the
/// reserved lane), answered with <c>keccak(nonce ‖ canary)</c>; a 5 s budget.
/// </summary>
public sealed class BlobGatewayProbe(HttpClient http) : IServiceProbe
{
    byte IServiceProbe.Service => Service;

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
        var signature = key?.SignProbe(target.Node, ts, nonce)
            ?? new Nethereum.Signer.EthereumMessageSigner().Sign(ProbeChallenge.Digest(target.Node, ts, nonce), _anonymous);
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

/// <summary>
/// HTTP for target nodes (#225, SSRF): no redirects, and off LocalDev every connection's resolved address is checked
/// at connect time, so a public name that later resolves (or redirects) into private space is refused.
/// </summary>
public static class TargetHttp
{
    public static HttpClient Create(VerifierOptions options)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!options.IsLocalDev)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(TargetDirectory.IsPublic)
                    ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} doesn't resolve to a public address.");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
}

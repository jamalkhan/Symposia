using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Basemail.Protocol;

namespace NativeSmtpReceiver;

public sealed class BasemailReplicaFanoutService
{
    private const int MinimumReplicaCount = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BasemailNodeOptions _options;
    private readonly BasemailMailboxRegistryService _mailboxRegistryService;
    private readonly MailboxDeliveryService _mailboxDeliveryService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BasemailReplicaFanoutService> _logger;

    public BasemailReplicaFanoutService(
        BasemailNodeOptions options,
        BasemailMailboxRegistryService mailboxRegistryService,
        MailboxDeliveryService mailboxDeliveryService,
        IHttpClientFactory httpClientFactory,
        ILogger<BasemailReplicaFanoutService> logger)
    {
        _options = options;
        _mailboxRegistryService = mailboxRegistryService;
        _mailboxDeliveryService = mailboxDeliveryService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<BasemailIngressAcceptedResponse> IngestAsync(BasemailCanonicalMessagePackage request, CancellationToken cancellationToken)
    {
        var selectedReplicaNodes = new List<string>();
        var rawMessage = BuildRawMessage(request);
        var selectedNodeIds = await _mailboxRegistryService.SelectReplicaNodeIdsAsync(request.MailboxId, cancellationToken);
        var localBindings = ResolveBindings(request.MailboxId, request.EnvelopeRecipients);
        if (selectedNodeIds.Contains(_options.NodeId, StringComparer.Ordinal) && localBindings.Count > 0)
        {
            await StoreLocallyAsync(request, rawMessage, localBindings, cancellationToken);
            selectedReplicaNodes.Add(_options.NodeId);
        }

        foreach (var peer in _options.Peers.Where(peer => selectedNodeIds.Contains(peer.NodeId, StringComparer.Ordinal)))
        {
            if (selectedReplicaNodes.Count >= MinimumReplicaCount)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(peer.BaseUrl))
            {
                continue;
            }

            await ReplicateToPeerAsync(peer, request, rawMessage, cancellationToken);
            selectedReplicaNodes.Add(peer.NodeId);
        }

        if (selectedReplicaNodes.Count < MinimumReplicaCount)
        {
            throw new InvalidOperationException(
                $"Basemail ingress package '{request.MessageId}' did not reach the minimum replica count of {MinimumReplicaCount}.");
        }

        return new BasemailIngressAcceptedResponse(true, request.MessageId, selectedReplicaNodes);
    }

    public IReadOnlyList<MailboxRoute> ResolveBindings(string mailboxId, IReadOnlyList<string> deliveredAddresses)
    {
        return _mailboxRegistryService.ResolveLocalRoutes(mailboxId, deliveredAddresses);
    }

    public async Task StoreLocallyAsync(
        BasemailCanonicalMessagePackage request,
        string rawMessage,
        IReadOnlyList<MailboxRoute> routes,
        CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
        {
            throw new InvalidOperationException($"Mailbox '{request.MailboxId}' is not configured on this node.");
        }

        var dataLines = SplitRawMessageLines(rawMessage);
        var deliveredAddresses = routes
            .Select(static route => route.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var message = new StoredEmailMessage(
            request.MessageId,
            request.EnvelopeFrom,
            request.EnvelopeRecipients.Count == 0 ? deliveredAddresses : request.EnvelopeRecipients,
            dataLines,
            rawMessage,
            request.ReceivedAtUtc);

        var deliveries = routes
            .GroupBy(static route => $"{route.StorageProviderName}\u001f{route.MailboxId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new MailboxStorageDelivery(group.First().MailboxId, group.First().StorageProviderName, group.ToArray(), message))
            .ToArray();

        await _mailboxDeliveryService.StorePreparedDeliveriesAsync(deliveries, cancellationToken);
    }

    public static string BuildRawMessage(BasemailCanonicalMessagePackage request)
    {
        if (!string.IsNullOrWhiteSpace(request.RawMessage))
        {
            return request.RawMessage;
        }

        var lines = new List<string>();
        foreach (var header in request.Headers)
        {
            lines.Add($"{header.Name}: {header.Value}");
        }

        lines.Add(string.Empty);

        if (!string.IsNullOrWhiteSpace(request.PlainTextBody))
        {
            lines.AddRange(SplitRawMessageLines(request.PlainTextBody));
        }
        else if (!string.IsNullOrWhiteSpace(request.HtmlBody))
        {
            lines.AddRange(SplitRawMessageLines(request.HtmlBody));
        }

        return string.Join("\r\n", lines);
    }

    private async Task ReplicateToPeerAsync(
        BasemailPeerRecord peer,
        BasemailCanonicalMessagePackage request,
        string rawMessage,
        CancellationToken cancellationToken)
    {
        var deliveredAddresses = request.EnvelopeRecipients.Count == 0
            ? Array.Empty<string>()
            : request.EnvelopeRecipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var replicaRequest = new BasemailReplicaWriteRequest(
            request.MailboxId,
            request.ContentHash,
            rawMessage,
            new BasemailReplicaMetadata(request.EnvelopeFrom, deliveredAddresses, request.ReceivedAtUtc));

        var client = _httpClientFactory.CreateClient(nameof(BasemailReplicaFanoutService));
        var baseUri = new Uri(peer.BaseUrl!, UriKind.Absolute);
        var relativePath = $"/network/messages/{Uri.EscapeDataString(request.MessageId)}/replicas";
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, relativePath));
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(replicaRequest, SerializerOptions);
        message.Content = new ByteArrayContent(bodyBytes);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApplySignatureHeaders(message, relativePath, bodyBytes);

        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Peer '{peer.NodeId}' rejected replica {request.MessageId} with HTTP {(int)response.StatusCode}: {responseBody}");
        }

        _logger.LogInformation(
            "Replicated message {MessageId} for mailbox {MailboxId} to peer {PeerNodeId}",
            request.MessageId,
            request.MailboxId,
            peer.NodeId);
    }

    private void ApplySignatureHeaders(HttpRequestMessage request, string relativePath, byte[] body)
    {
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderProtocolVersion, BasemailProtocolConstants.ProtocolVersion);

        if (!_options.HasSigningKeyPair)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var canonicalBytes = BasemailCanonicalRequest.GetCanonicalBytes(
            HttpMethod.Post.Method,
            relativePath,
            timestamp,
            nonce,
            body);
        var signature = BasemailSignature.Sign(canonicalBytes, _options.PrivateKeyPem!);

        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderNode, _options.NodeId);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderTimestamp, timestamp);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderNonce, nonce);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderSignature, signature);
        request.Headers.TryAddWithoutValidation(BasemailProtocolConstants.HeaderKeyId, _options.NodeId);
    }

    private static string[] SplitRawMessageLines(string rawMessage)
    {
        return rawMessage
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
    }
}

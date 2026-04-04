using Basemail.Protocol;

namespace NativeSmtpReceiver;

public sealed class BasemailRequestSignatureValidator
{
    private readonly BasemailNodeOptions _options;
    private readonly ILogger<BasemailRequestSignatureValidator> _logger;

    public BasemailRequestSignatureValidator(
        BasemailNodeOptions options,
        ILogger<BasemailRequestSignatureValidator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public BasemailSignatureValidationResult Validate(HttpRequest request, byte[] body)
    {
        if (!_options.RequireSignedRequests)
        {
            return new BasemailSignatureValidationResult(true, null, null);
        }

        if (!request.Headers.TryGetValue(BasemailProtocolConstants.HeaderNode, out var nodeId) ||
            string.IsNullOrWhiteSpace(nodeId))
        {
            return Failed("Missing Basemail node header.");
        }

        if (!request.Headers.TryGetValue(BasemailProtocolConstants.HeaderTimestamp, out var timestamp) ||
            string.IsNullOrWhiteSpace(timestamp))
        {
            return Failed("Missing Basemail timestamp header.");
        }

        if (!request.Headers.TryGetValue(BasemailProtocolConstants.HeaderNonce, out var nonce) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            return Failed("Missing Basemail nonce header.");
        }

        if (!request.Headers.TryGetValue(BasemailProtocolConstants.HeaderSignature, out var signature) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return Failed("Missing Basemail signature header.");
        }

        var peer = _options.Peers.FirstOrDefault(candidate =>
            string.Equals(candidate.NodeId, nodeId.ToString(), StringComparison.Ordinal));
        if (peer is null)
        {
            return Failed($"Unknown Basemail peer '{nodeId}'.");
        }

        var canonicalBytes = BasemailCanonicalRequest.GetCanonicalBytes(
            request.Method,
            $"{request.Path.Value ?? "/"}{request.QueryString.Value}",
            timestamp.ToString(),
            nonce.ToString(),
            body);

        if (!BasemailSignature.Verify(canonicalBytes, peer.PublicKeyPem, signature.ToString()))
        {
            return Failed($"Signature verification failed for peer '{peer.NodeId}'.");
        }

        return new BasemailSignatureValidationResult(true, peer, null);
    }

    private BasemailSignatureValidationResult Failed(string error)
    {
        _logger.LogWarning("Rejected Basemail network request: {Error}", error);
        return new BasemailSignatureValidationResult(false, null, error);
    }
}

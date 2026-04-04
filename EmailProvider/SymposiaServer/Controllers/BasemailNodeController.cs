using Basemail.Protocol;
using Microsoft.AspNetCore.Mvc;

namespace NativeSmtpReceiver.Controllers;

[ApiController]
[Route("network")]
public sealed class BasemailNodeController : ControllerBase
{
    private readonly BasemailNodeOptions _options;
    private readonly HostingDirectory _hostingDirectory;
    private readonly BasemailMailboxRegistryService _mailboxRegistryService;
    private readonly BasemailReplicaFanoutService _replicaFanoutService;
    private readonly MailboxReadService _mailboxReadService;
    private readonly ILogger<BasemailNodeController> _logger;

    public BasemailNodeController(
        BasemailNodeOptions options,
        HostingDirectory hostingDirectory,
        BasemailMailboxRegistryService mailboxRegistryService,
        BasemailReplicaFanoutService replicaFanoutService,
        MailboxReadService mailboxReadService,
        ILogger<BasemailNodeController> logger)
    {
        _options = options;
        _hostingDirectory = hostingDirectory;
        _mailboxRegistryService = mailboxRegistryService;
        _replicaFanoutService = replicaFanoutService;
        _mailboxReadService = mailboxReadService;
        _logger = logger;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(BasemailNodeStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<BasemailNodeStatusResponse> GetStatus()
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        var processStatus = StatusPayloads.Create();

        return Ok(new BasemailNodeStatusResponse(
            _options.NodeId,
            _options.OperatorAddress,
            new BasemailNodeCapabilitiesDto(
                _options.NetworkEnabled && _hostingDirectory.GetDomains().Count > 0,
                true,
                true,
                true,
                _options.AdvertisedStorageGb,
                _options.AdvertisedBandwidthGbPerDay),
            new BasemailNodeHealthDto(
                1.0d,
                EstimateStorageAvailableBytes(),
                new BasemailAppMemoryDto(processStatus.appMemory.workingSetBytes, processStatus.appMemory.privateMemoryBytes),
                new BasemailAppCpuDto(processStatus.appCpu.totalProcessorTimeMs))));
    }

    [HttpPost("messages/ingest")]
    [ProducesResponseType(typeof(BasemailIngressAcceptedResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BasemailIngressAcceptedResponse>> IngestAsync(
        [FromBody] BasemailCanonicalMessagePackage request,
        CancellationToken cancellationToken)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.MailboxId) || string.IsNullOrWhiteSpace(request.MessageId))
        {
            return BadRequest(new { error = "MailboxId and MessageId are required." });
        }

        try
        {
            var response = await _replicaFanoutService.IngestAsync(request, cancellationToken);
            _logger.LogInformation(
                "Accepted Basemail ingress package {MessageId} for mailbox {MailboxId}; achieved replicas: {ReplicaCount}",
                request.MessageId,
                request.MailboxId,
                response.SelectedReplicaNodes.Count);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed Basemail ingress package {MessageId} for mailbox {MailboxId}",
                request.MessageId,
                request.MailboxId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = ex.Message
            });
        }
    }

    [HttpPost("messages/{messageId}/replicas")]
    [ProducesResponseType(typeof(BasemailReplicaStoredResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BasemailReplicaStoredResponse>> StoreReplicaAsync(
        string messageId,
        [FromBody] BasemailReplicaWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        var routes = _replicaFanoutService.ResolveBindings(request.MailboxId, request.Metadata.DeliveredAddresses);
        if (routes.Count == 0)
        {
            return NotFound(new { error = $"Mailbox '{request.MailboxId}' is not configured on this node." });
        }

        var replicaPackage = new BasemailCanonicalMessagePackage(
            request.MailboxId,
            messageId,
            request.ContentHash,
            request.Metadata.EnvelopeFrom,
            request.Metadata.DeliveredAddresses,
            Array.Empty<BasemailParsedHeaderDto>(),
            null,
            null,
            request.RawMessage,
            request.Metadata.ReceivedAtUtc);
        await _replicaFanoutService.StoreLocallyAsync(
            replicaPackage,
            request.RawMessage,
            routes,
            cancellationToken);

        _logger.LogInformation(
            "Stored Basemail replica {MessageId} for mailbox {MailboxId} with {RouteCount} routes",
            messageId,
            request.MailboxId,
            routes.Count);

        return Ok(new BasemailReplicaStoredResponse(true, messageId, request.ContentHash));
    }

    [HttpGet("mailboxes/{mailboxId}/index")]
    [ProducesResponseType(typeof(BasemailMailboxIndexResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BasemailMailboxIndexResponse>> GetMailboxIndexAsync(string mailboxId, CancellationToken cancellationToken)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        var bindings = _mailboxReadService.GetMailboxBindings(mailboxId);
        if (bindings.Count == 0)
        {
            return NotFound(new { error = $"Mailbox '{mailboxId}' is not configured on this node." });
        }

        var messages = await _mailboxReadService.ListMessagesAsync(mailboxId, cancellationToken);
        var indexEntries = messages
            .Select(message => new BasemailMailboxIndexEntry(
                message.MessageId,
                null,
                message.ReceivedAtUtc,
                message.Subject,
                BuildPreview(message.Subject, message.EnvelopeFrom)))
            .ToArray();

        return Ok(new BasemailMailboxIndexResponse(mailboxId, indexEntries.Length, indexEntries));
    }

    [HttpGet("registry/mailboxes")]
    [ProducesResponseType(typeof(IReadOnlyList<BasemailMailboxRouteDefinition>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<BasemailMailboxRouteDefinition>> GetMailboxRegistry()
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        return Ok(_mailboxRegistryService.GetKnownRoutes());
    }

    [HttpGet("registry/mailboxes/{mailboxId}")]
    [ProducesResponseType(typeof(BasemailMailboxRouteDefinition), StatusCodes.Status200OK)]
    public async Task<ActionResult<BasemailMailboxRouteDefinition>> GetMailboxRegistryEntryAsync(string mailboxId, CancellationToken cancellationToken)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        var route = await _mailboxRegistryService.GetRouteAsync(mailboxId, cancellationToken);
        if (route is null)
        {
            return NotFound(new { error = $"Mailbox route '{mailboxId}' is not known to this node." });
        }

        return Ok(route);
    }

    [HttpGet("registry/snapshot")]
    [ProducesResponseType(typeof(BasemailMailboxRegistrySnapshot), StatusCodes.Status200OK)]
    public ActionResult<BasemailMailboxRegistrySnapshot> GetRegistrySnapshot([FromQuery] long? sinceVersion = null)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        return Ok(_mailboxRegistryService.GetSnapshot(sinceVersion));
    }

    [HttpGet("registry/version")]
    [ProducesResponseType(typeof(BasemailMailboxRegistryVersion), StatusCodes.Status200OK)]
    public ActionResult<BasemailMailboxRegistryVersion> GetRegistryVersion()
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        return Ok(_mailboxRegistryService.GetVersion());
    }

    [HttpPost("registry/invalidate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> InvalidateRegistryAsync(
        [FromBody] BasemailMailboxRegistryInvalidation invalidation,
        CancellationToken cancellationToken)
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        await _mailboxRegistryService.HandleInvalidationAsync(invalidation, cancellationToken);
        return Accepted();
    }

    [HttpGet("registry/stats")]
    [ProducesResponseType(typeof(BasemailMailboxRegistryStats), StatusCodes.Status200OK)]
    public ActionResult<BasemailMailboxRegistryStats> GetRegistryStats()
    {
        if (!_options.NetworkEnabled)
        {
            return NotFound();
        }

        return Ok(_mailboxRegistryService.GetStats());
    }

    private static string BuildPreview(string? subject, string envelopeFrom)
    {
        var value = string.IsNullOrWhiteSpace(subject) ? envelopeFrom : $"{subject} from {envelopeFrom}";
        return value.Length <= 140 ? value : value[..140];
    }

    private long EstimateStorageAvailableBytes()
    {
        var roots = _hostingDirectory.StorageProviders.Values
            .Where(static provider => string.Equals(provider.Type, MailStorageProviderTypes.FileSystem, StringComparison.OrdinalIgnoreCase) &&
                                      provider.FileSystem is not null)
            .Select(static provider => provider.FileSystem!.RootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in roots)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root)!);
                return drive.AvailableFreeSpace;
            }
            catch
            {
                // Best effort only.
            }
        }

        return 0L;
    }
}

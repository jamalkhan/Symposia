using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class DashboardSummaryService
{
    private readonly HostingDirectory _hostingDirectory;
    private readonly MailboxReadService _mailboxReadService;
    private readonly ILogger<DashboardSummaryService> _logger;

    public DashboardSummaryService(
        HostingDirectory hostingDirectory,
        MailboxReadService mailboxReadService,
        ILogger<DashboardSummaryService> logger)
    {
        _hostingDirectory = hostingDirectory;
        _mailboxReadService = mailboxReadService;
        _logger = logger;
    }

    public async Task<DashboardSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var domains = _hostingDirectory.GetDomains();
        var domainCounts = domains.ToDictionary(
            static domain => domain.Name,
            static domain => new DashboardDomainSummary(
                domain.Name,
                0,
                domain.Mailboxes
                    .Select(static mailbox => new DashboardMailboxSummary(
                        mailbox.Address,
                        mailbox.MailboxId,
                        mailbox.StorageProviderName,
                        0))
                    .ToDictionary(static mailbox => mailbox.Address, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var mailboxId in _hostingDirectory.GetMailboxIds())
        {
            var summaries = await _mailboxReadService.ListMessagesAsync(mailboxId, cancellationToken);
            foreach (var summary in summaries)
            {
                foreach (var deliveredAddress in summary.DeliveredAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!TryGetDomain(deliveredAddress, out var domainName))
                    {
                        continue;
                    }

                    if (!domainCounts.TryGetValue(domainName, out var domainSummary))
                    {
                        continue;
                    }

                    domainSummary.MessageCount++;

                    if (domainSummary.Mailboxes.TryGetValue(deliveredAddress, out var mailboxSummary))
                    {
                        mailboxSummary.MessageCount++;
                    }
                }
            }
        }

        var response = new DashboardSummaryResponse(
            DateTimeOffset.UtcNow,
            domainCounts.Values
                .OrderBy(static domain => domain.DomainName, StringComparer.OrdinalIgnoreCase)
                .Select(static domain => domain.ToResponse())
                .ToArray());

        _logger.LogInformation(
            "Computed dashboard summary for {DomainCount} domains",
            response.Domains.Count);

        return response;
    }

    private static bool TryGetDomain(string address, out string domain)
    {
        var atIndex = address.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == address.Length - 1)
        {
            domain = string.Empty;
            return false;
        }

        domain = address[(atIndex + 1)..];
        return true;
    }
}

public sealed record DashboardSummaryResponse(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DashboardDomainResponse> Domains);

public sealed record DashboardDomainResponse(
    string DomainName,
    int MessageCount,
    IReadOnlyList<DashboardMailboxResponse> Mailboxes);

public sealed record DashboardMailboxResponse(
    string Address,
    string MailboxId,
    string StorageProviderName,
    int MessageCount);

internal sealed class DashboardDomainSummary
{
    public DashboardDomainSummary(string domainName, int messageCount, Dictionary<string, DashboardMailboxSummary> mailboxes)
    {
        DomainName = domainName;
        MessageCount = messageCount;
        Mailboxes = mailboxes;
    }

    public string DomainName { get; }
    public int MessageCount { get; set; }
    public Dictionary<string, DashboardMailboxSummary> Mailboxes { get; }

    public DashboardDomainResponse ToResponse()
    {
        return new DashboardDomainResponse(
            DomainName,
            MessageCount,
            Mailboxes.Values
                .OrderBy(static mailbox => mailbox.Address, StringComparer.OrdinalIgnoreCase)
                .Select(static mailbox => mailbox.ToResponse())
                .ToArray());
    }
}

internal sealed class DashboardMailboxSummary
{
    public DashboardMailboxSummary(string address, string mailboxId, string storageProviderName, int messageCount)
    {
        Address = address;
        MailboxId = mailboxId;
        StorageProviderName = storageProviderName;
        MessageCount = messageCount;
    }

    public string Address { get; }
    public string MailboxId { get; }
    public string StorageProviderName { get; }
    public int MessageCount { get; set; }

    public DashboardMailboxResponse ToResponse()
    {
        return new DashboardMailboxResponse(Address, MailboxId, StorageProviderName, MessageCount);
    }
}

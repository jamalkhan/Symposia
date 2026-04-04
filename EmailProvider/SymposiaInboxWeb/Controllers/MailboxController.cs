using InboxWeb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SymposiaInboxWeb.Controllers;

[ApiController]
[Authorize]
[Route("api/mailbox")]
public sealed class MailboxController : ControllerBase
{
    private readonly InboxAccountService _accountService;
    private readonly HostedMailboxRepository _mailboxRepository;
    private readonly MailboxContentStore _mailboxContentStore;
    private readonly RequestSecurityService _requestSecurityService;

    public MailboxController(
        InboxAccountService accountService,
        HostedMailboxRepository mailboxRepository,
        MailboxContentStore mailboxContentStore,
        RequestSecurityService requestSecurityService)
    {
        _accountService = accountService;
        _mailboxRepository = mailboxRepository;
        _mailboxContentStore = mailboxContentStore;
        _requestSecurityService = requestSecurityService;
    }

    [HttpGet("bootstrap")]
    public async Task<ActionResult<MailboxBootstrapResponse>> GetBootstrapAsync(CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        return Ok(new MailboxBootstrapResponse(
            account,
            await _mailboxRepository.ListHostedDomainsAsync(cancellationToken),
            await _accountService.ListContactsAsync(account.AccountId, null, cancellationToken),
            await _mailboxContentStore.GetFolderCountsAsync(account.MailboxId, cancellationToken),
            await _mailboxContentStore.GetMessagePageAsync(account.MailboxId, new MailboxMessageQuery("inbox", null, null, 1, 25), cancellationToken)));
    }

    [HttpGet("messages")]
    public async Task<ActionResult<IReadOnlyList<MailboxMessageListItem>>> ListMessagesCompatAsync(
        [FromQuery] string? folder,
        [FromQuery(Name = "q")] string? query,
        CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        return Ok(await _mailboxContentStore.ListMessagesAsync(account.MailboxId, folder ?? "inbox", query, cancellationToken));
    }

    [HttpGet("messages/page")]
    public async Task<ActionResult<MailboxMessagePage>> GetMessagePageAsync(
        [FromQuery] string? folder,
        [FromQuery(Name = "q")] string? query,
        [FromQuery] string? label,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        return Ok(await _mailboxContentStore.GetMessagePageAsync(
            account.MailboxId,
            new MailboxMessageQuery(folder ?? "inbox", query, label, page, pageSize),
            cancellationToken));
    }

    [HttpGet("threads")]
    public async Task<ActionResult<MailboxThreadPage>> GetThreadPageAsync(
        [FromQuery] string? folder,
        [FromQuery(Name = "q")] string? query,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        return Ok(await _mailboxContentStore.GetThreadPageAsync(account.MailboxId, folder ?? "inbox", query, page, pageSize, cancellationToken));
    }

    [HttpGet("threads/{threadId}")]
    public async Task<ActionResult<MailboxThreadDetail>> GetThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var thread = await _mailboxContentStore.GetThreadAsync(account.MailboxId, threadId, cancellationToken);
        return thread is null ? NotFound() : Ok(thread);
    }

    [HttpGet("messages/{messageId}")]
    public async Task<ActionResult<MailboxMessageDetail>> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var message = await _mailboxContentStore.GetMessageAsync(account.MailboxId, messageId, cancellationToken);
        return message is null ? NotFound() : Ok(message);
    }

    [HttpPost("messages/{messageId}/read")]
    public Task<IActionResult> MarkReadAsync(string messageId, CancellationToken cancellationToken) => UpdateReadStateAsync(messageId, true, cancellationToken);

    [HttpPost("messages/{messageId}/unread")]
    public Task<IActionResult> MarkUnreadAsync(string messageId, CancellationToken cancellationToken) => UpdateReadStateAsync(messageId, false, cancellationToken);

    [HttpPost("messages/{messageId}/delete")]
    public Task<IActionResult> DeleteAsync(string messageId, CancellationToken cancellationToken) => MoveMessageAsync(messageId, "trash", cancellationToken);

    [HttpPost("messages/{messageId}/restore")]
    public Task<IActionResult> RestoreAsync(string messageId, CancellationToken cancellationToken) => MoveMessageAsync(messageId, "inbox", cancellationToken);

    [HttpPost("messages/{messageId}/labels")]
    public async Task<IActionResult> UpdateLabelsAsync(string messageId, [FromBody] LabelUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!EnsureCsrf())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var updated = await _mailboxContentStore.SetLabelsAsync(account.MailboxId, messageId, request.Labels, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("messages/{messageId}/star")]
    public async Task<IActionResult> UpdateStarAsync(string messageId, [FromBody] StarUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!EnsureCsrf())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var updated = await _mailboxContentStore.SetStarredAsync(account.MailboxId, messageId, request.IsStarred, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("compose")]
    public async Task<ActionResult<ComposeMessageResult>> ComposeAsync([FromBody] ComposeMessageRequest request, CancellationToken cancellationToken)
    {
        if (!EnsureCsrf())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _mailboxContentStore.ComposeAsync(account, request, cancellationToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IActionResult> UpdateReadStateAsync(string messageId, bool isRead, CancellationToken cancellationToken)
    {
        if (!EnsureCsrf())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var updated = await _mailboxContentStore.MarkReadAsync(account.MailboxId, messageId, isRead, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    private async Task<IActionResult> MoveMessageAsync(string messageId, string folder, CancellationToken cancellationToken)
    {
        if (!EnsureCsrf())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        var account = await GetAccountAsync(cancellationToken);
        if (account is null)
        {
            return Unauthorized();
        }

        var updated = await _mailboxContentStore.MoveToFolderAsync(account.MailboxId, messageId, folder, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    private Task<InboxAccountSession?> GetAccountAsync(CancellationToken cancellationToken)
    {
        var accountId = User.GetAccountId();
        return string.IsNullOrWhiteSpace(accountId)
            ? Task.FromResult<InboxAccountSession?>(null)
            : GetAccountWithSessionTokenAsync(accountId, cancellationToken);
    }

    private bool EnsureCsrf() => _requestSecurityService.IsValid(Request);

    private async Task<InboxAccountSession?> GetAccountWithSessionTokenAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = await _accountService.GetAccountAsync(accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var csrfToken = User.GetCsrfToken() ?? account.CsrfToken;
        return account with { CsrfToken = csrfToken };
    }
}

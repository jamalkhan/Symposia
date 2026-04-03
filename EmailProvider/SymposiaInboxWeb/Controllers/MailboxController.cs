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

    public MailboxController(
        InboxAccountService accountService,
        HostedMailboxRepository mailboxRepository,
        MailboxContentStore mailboxContentStore)
    {
        _accountService = accountService;
        _mailboxRepository = mailboxRepository;
        _mailboxContentStore = mailboxContentStore;
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
            await _mailboxContentStore.ListMessagesAsync(account.MailboxId, "inbox", null, cancellationToken)));
    }

    [HttpGet("messages")]
    public async Task<ActionResult<IReadOnlyList<MailboxMessageListItem>>> ListMessagesAsync(
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
    public Task<IActionResult> MarkReadAsync(string messageId, CancellationToken cancellationToken)
    {
        return UpdateReadStateAsync(messageId, true, cancellationToken);
    }

    [HttpPost("messages/{messageId}/unread")]
    public Task<IActionResult> MarkUnreadAsync(string messageId, CancellationToken cancellationToken)
    {
        return UpdateReadStateAsync(messageId, false, cancellationToken);
    }

    [HttpPost("messages/{messageId}/delete")]
    public Task<IActionResult> DeleteAsync(string messageId, CancellationToken cancellationToken)
    {
        return MoveMessageAsync(messageId, "trash", cancellationToken);
    }

    [HttpPost("messages/{messageId}/restore")]
    public Task<IActionResult> RestoreAsync(string messageId, CancellationToken cancellationToken)
    {
        return MoveMessageAsync(messageId, "inbox", cancellationToken);
    }

    [HttpPost("compose")]
    public async Task<ActionResult<ComposeMessageResult>> ComposeAsync(
        [FromBody] ComposeMessageRequest request,
        CancellationToken cancellationToken)
    {
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
            : _accountService.GetAccountAsync(accountId, cancellationToken);
    }
}

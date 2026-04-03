using InboxWeb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SymposiaInboxWeb.Controllers;

[ApiController]
[Authorize]
[Route("api/contacts")]
public sealed class ContactsController : ControllerBase
{
    private readonly InboxAccountService _accountService;

    public ContactsController(InboxAccountService accountService)
    {
        _accountService = accountService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AddressBookContactRecord>>> ListAsync(
        [FromQuery(Name = "q")] string? query,
        CancellationToken cancellationToken)
    {
        var accountId = User.GetAccountId();
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Unauthorized();
        }

        return Ok(await _accountService.ListContactsAsync(accountId, query, cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<AddressBookContactRecord>> UpsertAsync(
        [FromBody] ContactUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = User.GetAccountId();
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _accountService.UpsertContactAsync(accountId, request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{contactId}")]
    public async Task<IActionResult> DeleteAsync(string contactId, CancellationToken cancellationToken)
    {
        var accountId = User.GetAccountId();
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Unauthorized();
        }

        await _accountService.DeleteContactAsync(accountId, contactId, cancellationToken);
        return NoContent();
    }
}

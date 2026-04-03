using System.Security.Claims;
using InboxWeb;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace SymposiaInboxWeb.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly HostedMailboxRepository _mailboxRepository;
    private readonly InboxAccountService _accountService;

    public AuthController(
        HostedMailboxRepository mailboxRepository,
        InboxAccountService accountService)
    {
        _mailboxRepository = mailboxRepository;
        _accountService = accountService;
    }

    [HttpGet("domains")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetDomainsAsync(CancellationToken cancellationToken)
    {
        return Ok(await _mailboxRepository.ListHostedDomainsAsync(cancellationToken));
    }

    [HttpPost("register")]
    public async Task<ActionResult<InboxAccountSession>> RegisterAsync(
        [FromBody] RegisterAccountRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await _accountService.RegisterAsync(request, cancellationToken);
            await SignInAsync(account);
            return Ok(account);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<InboxAccountSession>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var account = await _accountService.AuthenticateAsync(request.EmailAddress, request.Password, cancellationToken);
        if (account is null)
        {
            return Unauthorized(new { error = "Email address or password is incorrect." });
        }

        await SignInAsync(account);
        return Ok(account);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<ActionResult<InboxAccountSession>> GetCurrentAccountAsync(CancellationToken cancellationToken)
    {
        var accountId = User.GetAccountId();
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Unauthorized();
        }

        var account = await _accountService.GetAccountAsync(accountId, cancellationToken);
        return account is null ? Unauthorized() : Ok(account);
    }

    private Task SignInAsync(InboxAccountSession account)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.AccountId),
            new Claim(ClaimTypes.Name, account.Address),
            new Claim("mailboxId", account.MailboxId),
            new Claim("displayName", account.DisplayName)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }
}

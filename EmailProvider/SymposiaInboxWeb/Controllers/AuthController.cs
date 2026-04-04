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
    private readonly RequestSecurityService _requestSecurityService;

    public AuthController(
        HostedMailboxRepository mailboxRepository,
        InboxAccountService accountService,
        RequestSecurityService requestSecurityService)
    {
        _mailboxRepository = mailboxRepository;
        _accountService = accountService;
        _requestSecurityService = requestSecurityService;
    }

    [HttpGet("domains")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetDomainsAsync(CancellationToken cancellationToken)
    {
        return Ok(await _mailboxRepository.ListHostedDomainsAsync(cancellationToken));
    }

    [HttpPost("register")]
    public async Task<ActionResult<InboxAccountSession>> RegisterAsync([FromBody] RegisterAccountRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var account = await _accountService.RegisterAsync(request, cancellationToken);
            await SignInAsync(account);
            _requestSecurityService.SetCsrfCookie(Response, account.CsrfToken);
            return Ok(account);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<ActionResult<InboxAccountSession>> LoginAsync([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _accountService.AuthenticateAsync(request.EmailAddress, request.Password, cancellationToken);
        if (!result.Succeeded || result.Session is null)
        {
            return result.IsLockedOut
                ? StatusCode(StatusCodes.Status423Locked, new { error = result.ErrorMessage })
                : Unauthorized(new { error = result.ErrorMessage ?? "Email address or password is incorrect." });
        }

        await SignInAsync(result.Session);
        _requestSecurityService.SetCsrfCookie(Response, result.Session.CsrfToken);
        return Ok(result.Session);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        if (User.Identity?.IsAuthenticated == true && !_requestSecurityService.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "CSRF token validation failed." });
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        _requestSecurityService.ClearCsrfCookie(Response);
        return NoContent();
    }

    [HttpPost("password-reset/request")]
    public async Task<ActionResult<PasswordResetResponse>> RequestPasswordResetAsync(
        [FromBody] PasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _accountService.RequestPasswordResetAsync(request.EmailAddress, cancellationToken));
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordResetAsync(
        [FromBody] PasswordResetConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _accountService.ResetPasswordAsync(request.Token, request.NewPassword, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
        if (account is null)
        {
            return Unauthorized();
        }

        var csrfToken = User.GetCsrfToken() ?? account.CsrfToken;
        _requestSecurityService.SetCsrfCookie(Response, csrfToken);
        return Ok(account with { CsrfToken = csrfToken });
    }

    private Task SignInAsync(InboxAccountSession account)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.AccountId),
            new Claim(ClaimTypes.Name, account.Address),
            new Claim("mailboxId", account.MailboxId),
            new Claim("displayName", account.DisplayName),
            new Claim("csrf", account.CsrfToken)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }
}

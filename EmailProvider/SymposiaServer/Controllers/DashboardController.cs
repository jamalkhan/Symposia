using Microsoft.AspNetCore.Mvc;

namespace NativeSmtpReceiver.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly DashboardSummaryService _summaryService;

    public DashboardController(DashboardSummaryService summaryService)
    {
        _summaryService = summaryService;
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(DashboardSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummaryResponse>> GetSummary(CancellationToken cancellationToken)
    {
        var response = await _summaryService.GetSummaryAsync(cancellationToken);
        return Ok(response);
    }
}

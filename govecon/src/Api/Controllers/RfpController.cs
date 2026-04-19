using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/{tenantId}/rfp")]
public class RfpController : ControllerBase
{
    [HttpPost("evaluate")]
    public IActionResult Evaluate(string tenantId)
    {
        return Ok(new { Score = 0.72, Decision = "BID" });
    }
}
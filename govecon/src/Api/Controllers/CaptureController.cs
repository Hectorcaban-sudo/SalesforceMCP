using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/{tenantId}/capture")]
public class CaptureController : ControllerBase
{
    [HttpPost("run")]
    public IActionResult Run(string tenantId)
    {
        return Ok(new { Tenant = tenantId, Status = "Capture pipeline started" });
    }
}
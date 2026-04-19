using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/{tenantId}/audit")]
public class AuditController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult Get(string tenantId, string id)
    {
        return Ok(new { Tenant = tenantId, AuditId = id });
    }
}
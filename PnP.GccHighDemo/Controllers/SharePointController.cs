using Microsoft.AspNetCore.Mvc;
using PnP.GccHighDemo.Services;

namespace PnP.GccHighDemo.Controllers;

/// <summary>
/// Controller for SharePoint operations via PnP Core SDK
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SharePointController : ControllerBase
{
    private readonly ISharePointService _sharePointService;
    private readonly ILogger<SharePointController> _logger;

    public SharePointController(
        ISharePointService sharePointService,
        ILogger<SharePointController> logger)
    {
        _sharePointService = sharePointService ?? throw new ArgumentNullException(nameof(sharePointService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the title of the configured SharePoint site
    /// </summary>
    [HttpGet("site-title")]
    public async Task<ActionResult<string>> GetSiteTitle()
    {
        try
        {
            var title = await _sharePointService.GetSiteTitleAsync();
            return Ok(new { Title = title });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving site title");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Gets all lists from the configured SharePoint site
    /// </summary>
    [HttpGet("lists")]
    public async Task<ActionResult<IEnumerable<SharePointListInfo>>> GetLists()
    {
        try
        {
            var lists = await _sharePointService.GetListsAsync();
            return Ok(lists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving lists");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
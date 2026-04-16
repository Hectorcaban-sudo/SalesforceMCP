using Microsoft.Extensions.Logging;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;

namespace PnP.GccHighDemo.Services;

/// <summary>
/// SharePoint service implementation using PnP Core SDK
/// Registered as singleton - uses IPnPContextFactory which is also singleton
/// </summary>
public class SharePointService : ISharePointService
{
    private readonly IPnPContextFactory _pnpContextFactory;
    private readonly ILogger<SharePointService> _logger;
    private readonly IConfiguration _configuration;

    public SharePointService(
        IPnPContextFactory pnpContextFactory,
        ILogger<SharePointService> logger,
        IConfiguration configuration)
    {
        _pnpContextFactory = pnpContextFactory ?? throw new ArgumentNullException(nameof(pnpContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Creates a new PnPContext for the configured target site
    /// The IPnPContextFactory is registered as singleton by PnP Core
    /// </summary>
    public async Task<PnPContext> GetContextAsync()
    {
        _logger.LogInformation("Creating PnPContext for target site");
        
        // Create context using the named site configuration
        return await _pnpContextFactory.CreateAsync("TargetSite");
    }

    /// <summary>
    /// Gets the title of the configured SharePoint site
    /// </summary>
    public async Task<string> GetSiteTitleAsync()
    {
        _logger.LogInformation("Getting site title from GCC High SharePoint");
        
        using var context = await GetContextAsync();
        
        // Load the web with minimal properties
        var web = await context.Web.GetAsync(w => w.Title, w => w.Id, w => w.Url);
        
        _logger.LogInformation("Retrieved site: {Title}", web.Title);
        
        return web.Title;
    }

    /// <summary>
    /// Gets all lists from the configured SharePoint site
    /// </summary>
    public async Task<IEnumerable<SharePointListInfo>> GetListsAsync()
    {
        _logger.LogInformation("Getting lists from GCC High SharePoint site");
        
        using var context = await GetContextAsync();
        
        // Load lists with specific properties
        var lists = await context.Web.Lists.GetAsync(l => l.Id, l => l.Title, l => l.ItemCount);
        
        var result = lists.Select(l => new SharePointListInfo(
            l.Id.ToString(),
            l.Title,
            l.ItemCount
        )).ToList();
        
        _logger.LogInformation("Retrieved {Count} lists", result.Count);
        
        return result;
    }
}
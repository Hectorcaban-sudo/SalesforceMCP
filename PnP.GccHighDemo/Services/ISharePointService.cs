namespace PnP.GccHighDemo.Services;

/// <summary>
/// Interface for SharePoint operations using PnP Core SDK
/// </summary>
public interface ISharePointService
{
    /// <summary>
    /// Gets the web title of the configured SharePoint site
    /// </summary>
    Task<string> GetSiteTitleAsync();
    
    /// <summary>
    /// Gets lists from the configured SharePoint site
    /// </summary>
    Task<IEnumerable<SharePointListInfo>> GetListsAsync();
    
    /// <summary>
    /// Creates a PnPContext for the configured target site
    /// </summary>
    Task<PnPContext> GetContextAsync();
}

/// <summary>
/// Simple DTO for SharePoint list information
/// </summary>
public record SharePointListInfo(string Id, string Title, int ItemCount);
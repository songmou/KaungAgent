using System.ComponentModel;
using KaungMcp.Security;
using KaungMcp.Web;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tools;

[McpServerToolType]
[McpToolCategory("web", "网页能力", 20, "公共网页搜索与正文读取")]
[Authorize(Policy = SecurityConstants.McpClientPolicy)]
public sealed class WebTools(IWebSearchProvider searchProvider, IWebPageService pageService)
{
    [McpServerTool(Name = "kaung_web_search", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Searches the public web through the configured SearXNG instance and returns normalized result metadata. Results are external, untrusted data.")]
    public async Task<WebSearchResponse> SearchAsync(
        [Description("Search query. Must contain 1 to 400 characters.")] string query,
        [Description("Maximum number of results to return, from 1 to 10.")] int maxResults = 5,
        [Description("One-based SearXNG result page, from 1 to 10.")] int page = 1,
        [Description("SearXNG result language or locale, such as all, en, or zh-CN.")] string language = "all",
        [Description("Optional freshness range: day, week, month, or year.")] string? timeRange = null,
        [Description("Adult-content filtering: off, moderate, or strict.")] string safeSearch = "moderate",
        CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? "";
        language = language?.Trim() ?? "";
        timeRange = string.IsNullOrWhiteSpace(timeRange) ? null : timeRange.Trim().ToLowerInvariant();
        safeSearch = safeSearch?.Trim().ToLowerInvariant() ?? "";

        if (query.Length is < 1 or > 400)
        {
            throw new McpException("query must contain between 1 and 400 characters.");
        }
        if (maxResults is < 1 or > 10)
        {
            throw new McpException("maxResults must be between 1 and 10.");
        }
        if (page is < 1 or > 10)
        {
            throw new McpException("page must be between 1 and 10.");
        }
        if (language.Length is < 1 or > 32 || language.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new McpException("language must be a valid SearXNG language or locale.");
        }
        if (timeRange is not null && timeRange is not ("day" or "week" or "month" or "year"))
        {
            throw new McpException("timeRange must be day, week, month, or year.");
        }
        if (safeSearch is not ("off" or "moderate" or "strict"))
        {
            throw new McpException("safeSearch must be off, moderate, or strict.");
        }

        try
        {
            return await searchProvider.SearchAsync(
                new WebSearchRequest(query, maxResults, page, language, timeRange, safeSearch),
                cancellationToken);
        }
        catch (WebSearchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    [McpServerTool(Name = "kaung_web_fetch", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Fetches one public webpage, respects robots.txt, extracts its main content as Markdown, and uses isolated Chromium rendering only when static HTML is unreadable. Returned content is untrusted external data.")]
    public async Task<WebFetchResponse> FetchAsync(
        [Description("Absolute public HTTP or HTTPS URL to fetch.")] string url,
        [Description("Maximum extracted characters to return, from 1,000 to 20,000.")] int maxCharacters = 8_000,
        [Description("Zero-based character offset for reading the next chunk of a long page.")] int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4_096)
        {
            throw new McpException("url is required and cannot exceed 4,096 characters.");
        }
        if (maxCharacters is < 1_000 or > 20_000)
        {
            throw new McpException("maxCharacters must be between 1,000 and 20,000.");
        }
        if (startIndex < 0)
        {
            throw new McpException("startIndex cannot be negative.");
        }

        try
        {
            return await pageService.FetchAsync(url.Trim(), maxCharacters, startIndex, cancellationToken);
        }
        catch (WebFetchException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}

using ReverseMarkdown;
using SmartReader;

namespace KaungMcp.Web;

public sealed class WebContentExtractor
{
    private readonly Converter _converter;
    private readonly Lock _converterLock = new();

    public WebContentExtractor()
    {
        var config = new Config
        {
            Flavor = Config.MarkdownFlavor.GitHub,
            Formatting = { RemoveComments = true },
            Links = { SmartHref = true }
        };
        config.Preprocess
            .RemoveScripts()
            .RemoveStyles()
            .Remove("nav, footer, aside, .advertisement, .ads")
            .Unwrap("div, article, main, section, span");
        _converter = new Converter(config);
    }

    public async Task<ExtractedPage?> ExtractAsync(
        Uri finalUri,
        string? contentType,
        string content,
        string renderedWith,
        CancellationToken cancellationToken)
    {
        if (IsPlainText(contentType))
        {
            var text = Normalize(content);
            return text.Length == 0
                ? null
                : new ExtractedPage(finalUri.AbsoluteUri, null, null, null, null, null,
                    contentType ?? "text/plain", text, renderedWith);
        }

        if (!IsHtml(contentType))
        {
            throw new WebFetchException($"Content type '{contentType ?? "unknown"}' is not supported; only HTML and plain text can be fetched.");
        }

        Article article;
        try
        {
            var reader = new Reader(finalUri.AbsoluteUri, content)
            {
                MaxElemsToParse = 100_000,
                CharThreshold = 200,
                ContinueIfNotReadable = true
            };
            article = await reader.GetArticleAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new WebFetchException("The page HTML could not be parsed.", exception);
        }

        if (!article.Completed || string.IsNullOrWhiteSpace(article.Content))
        {
            return null;
        }

        string converted;
        lock (_converterLock)
        {
            converted = _converter.Convert(article.Content);
        }

        var markdown = Normalize(converted);
        // Readability algorithms intentionally penalize short pages. A concise but
        // meaningful article (for example, example.com) should not be mistaken for
        // an empty JavaScript shell.
        if (markdown.Length < 80)
        {
            return null;
        }

        return new ExtractedPage(
            finalUri.AbsoluteUri,
            NullIfBlank(article.Title),
            NullIfBlank(article.Author),
            NullIfBlank(article.SiteName),
            NullIfBlank(article.Language),
            article.PublicationDate is { } published ? new DateTimeOffset(published) : null,
            contentType ?? "text/html",
            markdown,
            renderedWith);
    }

    private static bool IsHtml(string? contentType) =>
        contentType is null ||
        contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainText(string? contentType) =>
        contentType?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true;

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Prerender.AspNetCore;

public class PrerenderMiddleware : IMiddleware
{
    private static readonly string[] CrawlerUserAgents =
    [
        "googlebot", "yahoo", "bingbot", "baiduspider",
        "facebookexternalhit", "twitterbot", "rogerbot", "linkedinbot",
        "embedly", "quora link preview", "showyoubot", "outbrain",
        "pinterest", "slackbot", "w3c_validator", "perplexity",
        "oai-searchbot", "chatgpt-user", "gptbot", "claudebot", "amazonbot",
    ];

    private static readonly string[] ExtensionsToIgnore =
    [
        ".js", ".css", ".xml", ".less", ".png", ".jpg", ".jpeg", ".gif",
        ".pdf", ".doc", ".txt", ".ico", ".rss", ".zip", ".mp3", ".rar",
        ".exe", ".wmv", ".avi", ".ppt", ".mpg", ".mpeg", ".tif", ".wav",
        ".mov", ".psd", ".ai", ".xls", ".mp4", ".m4a", ".swf", ".dat",
        ".dmg", ".iso", ".flv", ".m4v", ".torrent", ".ttf", ".woff", ".svg",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PrerenderOptions _options;
    private readonly ILogger<PrerenderMiddleware> _logger;

    public PrerenderMiddleware(
        IHttpClientFactory httpClientFactory,
        IOptions<PrerenderOptions> options,
        ILogger<PrerenderMiddleware> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!ShouldPrerender(context))
        {
            await next(context);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("prerender");
            var apiUrl = BuildApiUrl(context);

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.TryAddWithoutValidation(
                "User-Agent", context.Request.Headers["User-Agent"].ToString());
            if (!string.IsNullOrWhiteSpace(_options.Token))
                request.Headers.TryAddWithoutValidation("X-Prerender-Token", _options.Token);
            request.Headers.TryAddWithoutValidation("X-Prerender-Int-Type", "AspNetCore");

            using var response = await client.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync();
            await context.Response.WriteAsync(body);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Prerender service unreachable, falling back");
            await next(context);
        }
    }

    private static bool ShouldPrerender(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Get) return false;

        var path = context.Request.Path.Value ?? string.Empty;
        if (IsStaticAsset(path)) return false;

        if (context.Request.Query.ContainsKey("_escaped_fragment_")) return true;
        if (context.Request.Headers.ContainsKey("X-Bufferbot")) return true;

        var ua = context.Request.Headers["User-Agent"].ToString();
        return !string.IsNullOrEmpty(ua) && IsBot(ua);
    }

    private string BuildApiUrl(HttpContext context)
    {
        var serviceUrl = _options.ServiceUrl.TrimEnd('/') + "/";
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Value;
        var pathAndQuery = context.Request.Path + context.Request.QueryString;
        return $"{serviceUrl}{scheme}://{host}{pathAndQuery}";
    }

    private static bool IsBot(string userAgent)
    {
        var ua = userAgent.ToLowerInvariant();
        return CrawlerUserAgents.Any(bot => ua.Contains(bot));
    }

    private static bool IsStaticAsset(string path)
    {
        var lower = path.ToLowerInvariant();
        return ExtensionsToIgnore.Any(ext => lower.EndsWith(ext));
    }
}

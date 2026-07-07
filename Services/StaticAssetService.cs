using MyNotes.Config;
using System.Net.Http.Headers;

namespace MyNotes.Services;

public sealed class StaticAssetService(
    ILogger<StaticAssetService> logger,
    IHttpClientFactory httpClientFactory)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    private readonly HttpClient _officialAssets = httpClientFactory.CreateClient("official-assets");

    public Task ProxyAndroidAssetAsync(HttpContext context, string assetPath, bool headOnly)
    {
        var normalizedPath = NormalizeAssetPath(assetPath);
        if (normalizedPath == null)
            return WriteBadRequestAsync(context);

        return ProxyOfficialAssetAsync(context, $"Android/{normalizedPath}", headOnly);
    }

    private async Task ProxyOfficialAssetAsync(HttpContext context, string assetPath, bool headOnly)
    {
        var upstreamUrl = BuildOfficialAssetUrl(assetPath);
        if (upstreamUrl == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("official asset root is not configured", context.RequestAborted);
            return;
        }

        logger.LogInformation("Proxying asset {AssetPath} from {UpstreamUrl}", assetPath, upstreamUrl);

        using var upstreamRequest = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, upstreamUrl);
        if (ServerConfig.HasBasicAuth)
            upstreamRequest.Headers.TryAddWithoutValidation("AUTHORIZATION", ServerConfig.ExpectedBasicAuth);
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", "MyNotes/1.0");

        if (context.Request.Headers.TryGetValue("Range", out var range))
            upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToString());

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await _officialAssets.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Official asset proxy timed out for {UpstreamUrl}", upstreamUrl);
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsync("official asset upstream timed out", context.RequestAborted);
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Official asset proxy failed for {UpstreamUrl}", upstreamUrl);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("official asset upstream failed", context.RequestAborted);
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Response.Headers["X-Accel-Buffering"] = "no";
            CopyHeaders(context, upstreamResponse.Headers);
            CopyHeaders(context, upstreamResponse.Content.Headers);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Official asset proxy returned {StatusCode} for {UpstreamUrl}",
                    (int)upstreamResponse.StatusCode,
                    upstreamUrl);
            }

            if (!headOnly)
                await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static Task WriteBadRequestAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsync("invalid asset path", context.RequestAborted);
    }

    private static string? NormalizeAssetPath(string assetPath)
    {
        var normalizedPath = assetPath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathFullyQualified(normalizedPath) ||
            segments.Length == 0 ||
            segments.Any(static s => s is "." or ".."))
            return null;

        return string.Join('/', segments);
    }

    private static string? BuildOfficialAssetUrl(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(ServerConfig.OfficialAssetRootUrl))
            return null;

        var root = ServerConfig.OfficialAssetRootUrl.TrimEnd('/');
        return $"{root}/asset/{assetPath.TrimStart('/')}";
    }

    private static void CopyHeaders(HttpContext context, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }
}

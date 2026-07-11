using Microsoft.AspNetCore.StaticFiles;
using MyNotes.Config;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security;

namespace MyNotes.Services;

public sealed class StaticAssetService
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

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly char[] InvalidCacheFileNameChars = ['<', '>', ':', '"', '|', '?', '*', '\0'];
    private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly ILogger<StaticAssetService> _logger;
    private readonly HttpClient _officialAssets;
    private readonly string? _cacheRoot;
    private readonly ConcurrentDictionary<string, CacheLockEntry> _cacheLocks = new(StringComparer.OrdinalIgnoreCase);

    public StaticAssetService(
        ILogger<StaticAssetService> logger,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment)
    {
        _logger = logger;
        _officialAssets = httpClientFactory.CreateClient("official-assets");
        _cacheRoot = ServerConfig.StaticAssetCacheEnabled
            ? ResolveCacheRoot(environment.ContentRootPath)
            : null;

        if (_cacheRoot != null)
            _logger.LogInformation("Static asset cache enabled at {CacheRoot}", _cacheRoot);
    }

    public async Task ProxyAndroidAssetAsync(HttpContext context, string assetPath, bool headOnly)
    {
        var normalizedPath = NormalizeAssetPath(assetPath);
        if (normalizedPath == null)
        {
            await WriteBadRequestAsync(context);
            return;
        }

        var officialAssetPath = $"Android/{normalizedPath}";
        var cachePath = GetCachePath(officialAssetPath);
        if (cachePath == null)
        {
            await ProxyOfficialAssetAsync(context, officialAssetPath, headOnly, null);
            return;
        }

        if (File.Exists(cachePath))
        {
            await ServeCachedAssetAsync(context, cachePath);
            return;
        }

        if (headOnly)
        {
            await ProxyOfficialAssetAsync(context, officialAssetPath, true, null);
            return;
        }

        var cacheLock = await AcquireCacheLockAsync(cachePath, context.RequestAborted);
        try
        {
            if (!File.Exists(cachePath))
            {
                await ProxyOfficialAssetAsync(context, officialAssetPath, false, cachePath);
                return;
            }
        }
        finally
        {
            ReleaseCacheLock(cachePath, cacheLock);
        }

        await ServeCachedAssetAsync(context, cachePath);
    }

    private async Task ProxyOfficialAssetAsync(
        HttpContext context,
        string assetPath,
        bool headOnly,
        string? cachePath)
    {
        var upstreamUrl = BuildOfficialAssetUrl(assetPath);
        if (upstreamUrl == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("official asset root is not configured", context.RequestAborted);
            return;
        }

        _logger.LogInformation("Proxying asset {AssetPath} from {UpstreamUrl}", assetPath, upstreamUrl);

        var populateCacheForRange = cachePath != null && context.Request.Headers.ContainsKey("Range");

        using var upstreamRequest = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, upstreamUrl);
        if (ServerConfig.HasBasicAuth)
            upstreamRequest.Headers.TryAddWithoutValidation("AUTHORIZATION", ServerConfig.ExpectedBasicAuth);
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", "MyNotes/1.0");
        upstreamRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (!populateCacheForRange && context.Request.Headers.TryGetValue("Range", out var range))
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
            _logger.LogWarning("Official asset proxy timed out for {UpstreamUrl}", upstreamUrl);
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsync("official asset upstream timed out", context.RequestAborted);
            return;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Official asset proxy failed for {UpstreamUrl}", upstreamUrl);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("official asset upstream failed", context.RequestAborted);
            return;
        }

        using (upstreamResponse)
        {
            if (populateCacheForRange && IsCacheable(upstreamResponse))
            {
                var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
                if (await CopyAndCacheAsync(
                        upstreamBody,
                        null,
                        cachePath!,
                        upstreamResponse.Content.Headers.ContentLength,
                        context.RequestAborted))
                {
                    await ServeCachedAssetAsync(context, cachePath!);
                    return;
                }

                _logger.LogWarning(
                    "Could not populate cache before range response for {AssetPath}; falling back to direct proxy",
                    assetPath);
                await ProxyOfficialAssetAsync(context, assetPath, false, null);
                return;
            }

            if (populateCacheForRange && upstreamResponse.IsSuccessStatusCode)
            {
                upstreamResponse.Dispose();
                await ProxyOfficialAssetAsync(context, assetPath, false, null);
                return;
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Response.Headers["X-Accel-Buffering"] = "no";
            CopyHeaders(context, upstreamResponse.Headers);
            CopyHeaders(context, upstreamResponse.Content.Headers);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Official asset proxy returned {StatusCode} for {UpstreamUrl}",
                    (int)upstreamResponse.StatusCode,
                    upstreamUrl);
            }

            if (!headOnly)
            {
                var upstreamBody = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
                if (cachePath != null && IsCacheable(upstreamResponse))
                {
                    if (await CopyAndCacheAsync(
                            upstreamBody,
                            context.Response.Body,
                            cachePath,
                            upstreamResponse.Content.Headers.ContentLength,
                            context.RequestAborted))
                    {
                        _logger.LogInformation("Cached static asset at {CachePath}", cachePath);
                    }
                }
                else
                {
                    await upstreamBody.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }
        }
    }

    private async Task ServeCachedAssetAsync(HttpContext context, string cachePath)
    {
        if (!ContentTypes.TryGetContentType(cachePath, out var contentType))
            contentType = "application/octet-stream";

        _logger.LogInformation("Serving static asset from cache {CachePath}", cachePath);
        await Results.File(
                cachePath,
                contentType,
                enableRangeProcessing: true)
            .ExecuteAsync(context);
    }

    private async Task<bool> CopyAndCacheAsync(
        Stream source,
        Stream? responseBody,
        string cachePath,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        FileStream? cacheStream = null;

        try
        {
            try
            {
                var directory = Path.GetDirectoryName(cachePath)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
                cacheStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (IsCacheStorageException(ex))
            {
                _logger.LogWarning(ex, "Could not create static asset cache file {CachePath}", cachePath);
                if (responseBody != null)
                    await source.CopyToAsync(responseBody, cancellationToken);
                return false;
            }

            var buffer = new byte[81920];
            var cacheWritable = true;
            long bytesCopied = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                bytesCopied += bytesRead;

                if (responseBody != null)
                    await responseBody.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

                if (!cacheWritable)
                    continue;

                try
                {
                    await cacheStream!.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
                catch (Exception ex) when (IsCacheStorageException(ex))
                {
                    _logger.LogWarning(ex, "Could not write static asset cache file {CachePath}", cachePath);
                    cacheWritable = false;
                    await DisposeCacheStreamAsync(cacheStream!);
                    cacheStream = null;
                    DeleteTemporaryFile(temporaryPath!);
                    temporaryPath = null;

                    if (responseBody == null)
                        return false;
                }
            }

            if (!cacheWritable)
                return false;

            if (expectedLength.HasValue && bytesCopied != expectedLength.Value)
            {
                _logger.LogWarning(
                    "Static asset body length mismatch for {CachePath}: expected {ExpectedLength}, received {ActualLength}",
                    cachePath,
                    expectedLength.Value,
                    bytesCopied);
                return false;
            }

            try
            {
                var completedStream = cacheStream!;
                await completedStream.FlushAsync(cancellationToken);
                await completedStream.DisposeAsync();
                cacheStream = null;

                File.Move(temporaryPath!, cachePath, true);
                temporaryPath = null;
                return true;
            }
            catch (Exception ex) when (IsCacheStorageException(ex))
            {
                _logger.LogWarning(ex, "Could not finalize static asset cache file {CachePath}", cachePath);
                return false;
            }
        }
        finally
        {
            if (cacheStream != null)
                await DisposeCacheStreamAsync(cacheStream);
            if (temporaryPath != null)
                DeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<CacheLockEntry> AcquireCacheLockAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _cacheLocks.GetOrAdd(cachePath, static _ => new CacheLockEntry());
            if (!entry.TryAddReference())
            {
                RemoveCacheLock(cachePath, entry);
                continue;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return entry;
            }
            catch
            {
                ReleaseCacheLockReference(cachePath, entry);
                throw;
            }
        }
    }

    private void ReleaseCacheLock(string cachePath, CacheLockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseCacheLockReference(cachePath, entry);
    }

    private void ReleaseCacheLockReference(string cachePath, CacheLockEntry entry)
    {
        if (!entry.ReleaseReference())
            return;

        RemoveCacheLock(cachePath, entry);
        entry.Semaphore.Dispose();
    }

    private void RemoveCacheLock(string cachePath, CacheLockEntry entry) =>
        ((ICollection<KeyValuePair<string, CacheLockEntry>>)_cacheLocks)
            .Remove(new KeyValuePair<string, CacheLockEntry>(cachePath, entry));

    private string? GetCachePath(string assetPath)
    {
        if (_cacheRoot == null)
            return null;

        var segments = assetPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => !IsCacheableFileName(segment)))
        {
            _logger.LogWarning("Static asset path cannot be represented safely in the cache: {AssetPath}", assetPath);
            return null;
        }

        try
        {
            var relativePath = Path.Combine(["asset", .. segments]);
            var cachePath = Path.GetFullPath(Path.Combine(_cacheRoot, relativePath));
            var relativeToRoot = Path.GetRelativePath(_cacheRoot, cachePath);
            if (Path.IsPathFullyQualified(relativeToRoot) ||
                relativeToRoot == ".." ||
                relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                _logger.LogWarning("Static asset cache path escaped its configured root: {AssetPath}", assetPath);
                return null;
            }

            return cachePath;
        }
        catch (Exception ex) when (IsCacheStorageException(ex) || ex is ArgumentException)
        {
            _logger.LogWarning(ex, "Could not resolve static asset cache path for {AssetPath}", assetPath);
            return null;
        }
    }

    private static string ResolveCacheRoot(string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(ServerConfig.StaticAssetCacheDirectory))
            throw new InvalidOperationException("Static:CacheDirectory must be set when static asset caching is enabled");

        return Path.GetFullPath(ServerConfig.StaticAssetCacheDirectory, contentRootPath);
    }

    private static bool IsCacheable(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK &&
        response.Headers.CacheControl?.NoStore != true &&
        response.Content.Headers.ContentRange == null &&
        response.Content.Headers.ContentEncoding.Count == 0;

    private static bool IsCacheableFileName(string segment)
    {
        if (segment.Length == 0 ||
            segment.EndsWith('.') ||
            segment.EndsWith(' ') ||
            segment.IndexOfAny(InvalidCacheFileNameChars) >= 0 ||
            segment.Any(char.IsControl))
            return false;

        var baseName = segment.Split('.')[0];
        return !ReservedWindowsFileNames.Contains(baseName);
    }

    private static bool IsCacheStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException;

    private static async Task DisposeCacheStreamAsync(FileStream stream)
    {
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception ex) when (IsCacheStorageException(ex))
        {
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsCacheStorageException(ex))
        {
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

    private static Uri? BuildOfficialAssetUrl(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(ServerConfig.OfficialAssetRootUrl))
            return null;

        var root = ServerConfig.OfficialAssetRootUrl.TrimEnd('/') + "/";
        if (!Uri.TryCreate(root, UriKind.Absolute, out var rootUri) ||
            rootUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(rootUri.Query) ||
            !string.IsNullOrEmpty(rootUri.Fragment))
            return null;

        var escapedAssetPath = string.Join(
            '/',
            assetPath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(rootUri, $"asset/{escapedAssetPath}");
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

    private sealed class CacheLockEntry
    {
        private readonly object _sync = new();
        private int _referenceCount;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                    return false;

                _referenceCount++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _referenceCount--;
                if (_referenceCount != 0)
                    return false;

                _retired = true;
                return true;
            }
        }
    }
}

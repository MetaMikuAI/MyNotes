namespace MyNotes.Middleware;

public sealed class RequestLoggingScopeMiddleware(RequestDelegate next, ILogger<RequestLoggingScopeMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("authorization", out var authHeader))
            logger.LogDebug("authorization header present: {Prefix}", authHeader.ToString().Split(' ')[0]);

        if (context.Request.Headers.TryGetValue("user-agent", out var userAgent))
            logger.LogDebug("user-agent: {UserAgent}", userAgent.ToString());

        await next(context);
    }
}

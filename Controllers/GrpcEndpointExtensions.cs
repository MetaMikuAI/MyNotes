using MyNotes.Config;
using MyNotes.Services;

namespace MyNotes.Middleware;

public static class GrpcEndpointExtensions
{
    public static IEndpointRouteBuilder MapGrpcUnary(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, ReadOnlyMemory<byte>, Task<byte[]>> handler)
    {
        endpoints.MapPost(pattern, async context =>
        {
            if (!IsAuthorized(context))
            {
                await GrpcFraming.WriteErrorAsync(context, "16", "unauthenticated");
                return;
            }

            var frame = await GrpcFraming.ReadRequestAsync(context.Request, context.RequestAborted);
            if (frame.Compressed)
            {
                await GrpcFraming.WriteErrorAsync(context, "12", "compressed grpc messages are not supported");
                return;
            }

            var response = await handler(context, frame.Payload);
            await GrpcFraming.WriteResponseAsync(context, response);
        });

        return endpoints;
    }

    private static bool IsAuthorized(HttpContext context)
    {
        var request = context.Request;
        if (!request.Headers.TryGetValue("authorization", out var value))
            return true;

        var authorization = value.ToString();
        if (ServerConfig.IsExpectedBasicAuth(authorization))
            return true;

        if (ServerConfig.RequireBasicAuth)
            return false;

        var players = context.RequestServices.GetRequiredService<PlayerManager>();
        return players.IsKnownCredential(request);
    }
}

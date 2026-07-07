using System.Buffers.Binary;
using Microsoft.AspNetCore.Http.Features;

namespace MyNotes.Services;

public readonly record struct GrpcFrame(bool Compressed, ReadOnlyMemory<byte> Payload);

public static class GrpcFraming
{
    public const string ContentType = "application/grpc";

    public static bool IsGrpcRequest(HttpRequest request) =>
        request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task<GrpcFrame> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken);
        var data = memory.ToArray();

        if (data.Length == 0)
            return new GrpcFrame(false, ReadOnlyMemory<byte>.Empty);

        if (data.Length < 5)
            throw new InvalidDataException("Invalid gRPC frame: shorter than 5 bytes.");

        var compressed = data[0] != 0;
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1, 4));
        if (length > data.Length - 5)
            throw new InvalidDataException("Invalid gRPC frame: length exceeds body size.");

        return new GrpcFrame(compressed, data.AsMemory(5, (int)length));
    }

    public static async Task WriteResponseAsync(HttpContext context, byte[] protobuf)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.Append("grpc-encoding", "identity");
        if (response.SupportsTrailers())
            response.DeclareTrailer("grpc-status");
        else
            response.Headers.Append("grpc-status", "0");
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var prefix = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), (uint)protobuf.Length);
        await response.Body.WriteAsync(prefix, context.RequestAborted);
        await response.Body.WriteAsync(protobuf, context.RequestAborted);

        if (response.SupportsTrailers())
            response.AppendTrailer("grpc-status", "0");
    }

    public static async Task WriteErrorAsync(HttpContext context, string status, string message)
    {
        context.Response.ContentType = ContentType;
        if (context.Response.SupportsTrailers())
        {
            context.Response.DeclareTrailer("grpc-status");
            context.Response.DeclareTrailer("grpc-message");
            context.Response.AppendTrailer("grpc-status", status);
            context.Response.AppendTrailer("grpc-message", Uri.EscapeDataString(message));
        }
        else
        {
            context.Response.Headers.Append("grpc-status", status);
            context.Response.Headers.Append("grpc-message", Uri.EscapeDataString(message));
        }

        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}

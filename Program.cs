using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyNotes.Controllers;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

ServerConfig.Load(builder.Configuration);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(ServerConfig.GrpcPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
    });

    options.ListenAnyIP(ServerConfig.HttpPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1;
    });
});

builder.Services.AddHttpClient("official-assets", client =>
{
    client.Timeout = TimeSpan.FromSeconds(ServerConfig.OfficialAssetRequestTimeoutSeconds);
});

builder.Services.AddSingleton<PlayerManager>();
builder.Services.AddSingleton<MasterDataService>();
builder.Services.AddSingleton<HomeSnapshotService>();
builder.Services.AddSingleton<StaticAssetService>();

var app = builder.Build();
var appLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MyNotes");

app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();

    appLogger.LogInformation("{Method} {Path} -> {StatusCode} ({Elapsed}ms)",
        ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    if (feature != null)
        appLogger.LogError(feature.Error, "Unhandled request error");

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    if (GrpcFraming.IsGrpcRequest(ctx.Request))
    {
        ctx.Response.ContentType = GrpcFraming.ContentType;
        ctx.Response.Headers.Append("grpc-status", "13");
        ctx.Response.Headers.Append("grpc-message", "internal server error");
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        return;
    }

    await ctx.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
}));

app.UseRouting();
app.UseMiddleware<RequestLoggingScopeMiddleware>();

app.MapGrpcUnary("/app.masterdata.MasterdataService/Version",
    async (ctx, _) =>
    {
        var master = ctx.RequestServices.GetRequiredService<MasterDataService>();
        return MyNotesProtobuf.VersionResponse(master.Version);
    });

app.MapGrpcUnary("/app.player.PlayerService/Register",
    async (ctx, payload) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var request = MyNotesProtobuf.ParseRegisterRequest(payload);
        var player = players.Register(request.InitialDataGroup);
        return MyNotesProtobuf.RegisterResponse(player);
    });

app.MapGrpcUnary("/app.player.PlayerService/GetPlayerData",
    async (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var master = ctx.RequestServices.GetRequiredService<MasterDataService>();
        var player = players.GetFromRequest(ctx.Request);
        return MyNotesProtobuf.GetPlayerDataResponse(player, master);
    });

app.MapGrpcUnary("/app.player.PlayerService/EditProfile",
    async (ctx, payload) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var displayName = MyNotesProtobuf.ParseEditProfileName(payload);
        players.UpdateDisplayName(player, displayName);
        return MyNotesProtobuf.EmptyResponse();
    });

app.MapGrpcUnary("/app.home.HomeService/Get",
    async (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var home = ctx.RequestServices.GetRequiredService<HomeSnapshotService>();
        var player = players.GetFromRequest(ctx.Request);
        return MyNotesProtobuf.GetHomeResponse(home.GetFor(player));
    });

app.MapGrpcUnary("/app.live.LiveService/SaveSetting",
    async (ctx, payload) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var settingAll = MyNotesProtobuf.ParseSaveSettingAll(payload);
        players.UpdateLiveSetting(player, settingAll);
        return MyNotesProtobuf.EmptyResponse();
    });

app.MapGrpcUnary("/app.external_payments.ExternalPaymentsService/Nop",
    async (_, _) => MyNotesProtobuf.EmptyResponse());

app.MapStaticAssetEndpoints();

app.MapGet("/", () => Results.Json(new
{
    name = "MyNotes",
    status = "ok"
}));

Console.WriteLine($"MyNotes gRPC h2c is running on port {ServerConfig.GrpcPort}");
Console.WriteLine($"MyNotes HTTP static/admin is running on port {ServerConfig.HttpPort}");
app.Run();

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Google.Protobuf.WellKnownTypes;
using MyNotes.Controllers;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using HomeGetRequest = App.Protobuf.Home.GetHomeRequest;
using LiveSaveSettingRequest = App.Protobuf.Live.SaveSettingRequest;
using MasterdataVersionRequest = App.Protobuf.Masterdata.VersionRequest;
using PlayerEditProfileRequest = App.Protobuf.Player.EditProfileRequest;
using PlayerEditProfileResponse = App.Protobuf.Player.EditProfileResponse;
using PlayerGetDataRequest = App.Protobuf.Player.GetPlayerDataRequest;
using PlayerRegisterRequest = App.Protobuf.Player.RegisterRequest;

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
builder.Services.AddSingleton<MasterdataProtocolBuilder>();
builder.Services.AddSingleton<PlayerProtocolBuilder>();
builder.Services.AddSingleton<HomeProtocolBuilder>();
builder.Services.AddSingleton<LiveProtocolBuilder>();

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

app.MapGrpcUnary(
    "/app.masterdata.MasterdataService/Version",
    MasterdataVersionRequest.Parser,
    (ctx, _) =>
    {
        var protocol = ctx.RequestServices.GetRequiredService<MasterdataProtocolBuilder>();
        return Task.FromResult(protocol.Version());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/Register",
    PlayerRegisterRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.Register(request.InitialDataGroup);
        return Task.FromResult(protocol.Register(player));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/GetPlayerData",
    PlayerGetDataRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.GetPlayerData(player));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/EditProfile",
    PlayerEditProfileRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateDisplayName(player, request.Name);
        return Task.FromResult(new PlayerEditProfileResponse());
    });

app.MapGrpcUnary(
    "/app.home.HomeService/Get",
    HomeGetRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var home = ctx.RequestServices.GetRequiredService<HomeSnapshotService>();
        var protocol = ctx.RequestServices.GetRequiredService<HomeProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.Get(home.GetFor(player)));
    });

app.MapGrpcUnary(
    "/app.live.LiveService/SaveSetting",
    LiveSaveSettingRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<LiveProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateLiveSetting(player, request.SettingAll.ToByteArray());
        return Task.FromResult(protocol.SaveSetting());
    });

app.MapGrpcUnary(
    "/app.external_payments.ExternalPaymentsService/Nop",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Empty()));

app.MapStaticAssetEndpoints();

app.MapGet("/", () => Results.Json(new
{
    name = "MyNotes",
    status = "ok"
}));

Console.WriteLine($"MyNotes gRPC h2c is running on port {ServerConfig.GrpcPort}");
Console.WriteLine($"MyNotes HTTP static/admin is running on port {ServerConfig.HttpPort}");
app.Run();

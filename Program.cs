using Microsoft.AspNetCore.Server.Kestrel.Core;
using Google.Protobuf.WellKnownTypes;
using MyNotes.Controllers;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using AnnouncementGetListRequest = App.Protobuf.Announcement.GetListRequest;
using AnnouncementGetListResponse = App.Protobuf.Announcement.GetListResponse;
using CarouselHelpShownRequest = App.Protobuf.CarouselHelp.ShownRequest;
using CarouselHelpShownResponse = App.Protobuf.CarouselHelp.ShownResponse;
using ContentUnlockShownRequest = App.Protobuf.ContentUnlock.ShownRequest;
using ContentUnlockShownResponse = App.Protobuf.ContentUnlock.ShownResponse;
using DeckSaveDecksRequest = App.Protobuf.Deck.SaveDecksRequest;
using DeckSaveDecksResponse = App.Protobuf.Deck.SaveDecksResponse;
using HomeGetRequest = App.Protobuf.Home.GetHomeRequest;
using LiveFinishFreeRequest = App.Protobuf.Live.FinishFreeRequest;
using LiveFinishFreeResponse = App.Protobuf.Live.FinishFreeResponse;
using LiveMusicGetRankingRequest = App.Protobuf.LiveMusic.GetRankingRequest;
using LiveMusicGetRankingResponse = App.Protobuf.LiveMusic.GetRankingResponse;
using LiveSaveSettingRequest = App.Protobuf.Live.SaveSettingRequest;
using LiveSkipFreeRequest = App.Protobuf.Live.SkipFreeRequest;
using LiveSkipFreeResponse = App.Protobuf.Live.SkipFreeResponse;
using LiveStartFreeRequest = App.Protobuf.Live.StartFreeRequest;
using LiveStartFreeResponse = App.Protobuf.Live.StartFreeResponse;
using MasterdataVersionRequest = App.Protobuf.Masterdata.VersionRequest;
using MissionFetchRequest = App.Protobuf.Present.FetchMissionRequest;
using MissionFetchResponse = App.Protobuf.Present.FetchMissionResponse;
using PlayerCheckNgWordRequest = App.Protobuf.Player.CheckNgWordRequest;
using PlayerCheckNgWordResponse = App.Protobuf.Player.CheckNgWordResponse;
using PlayerChangeFavoriteMemberRequest = App.Protobuf.Player.ChangeFavoriteMemberRequest;
using PlayerChangeFavoriteMemberResponse = App.Protobuf.Player.ChangeFavoriteMemberResponse;
using PlayerEditProfileRequest = App.Protobuf.Player.EditProfileRequest;
using PlayerEditProfileResponse = App.Protobuf.Player.EditProfileResponse;
using PlayerGetDataRequest = App.Protobuf.Player.GetPlayerDataRequest;
using PlayerRegisterRequest = App.Protobuf.Player.RegisterRequest;
using PresentFetchRequest = App.Protobuf.Present.FetchRequest;
using PresentFetchResponse = App.Protobuf.Present.FetchResponse;
using PresentHistoryRequest = App.Protobuf.Present.HistoryRequest;
using PresentHistoryResponse = App.Protobuf.Present.HistoryResponse;
using StoryCheckMaintenanceRequest = App.Protobuf.Story.CheckMaintenanceStoryRequest;
using StoryCheckMaintenanceResponse = App.Protobuf.Story.CheckMaintenanceStoryResponse;

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
    "/app.player.PlayerService/CheckNgWord",
    PlayerCheckNgWordRequest.Parser,
    static (_, _) => Task.FromResult(new PlayerCheckNgWordResponse()));

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
    "/app.player.PlayerService/ChangeFavoriteMember",
    PlayerChangeFavoriteMemberRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateFavoriteMember(player, request.MemberId);
        return Task.FromResult(new PlayerChangeFavoriteMemberResponse());
    });

app.MapGrpcUnary(
    "/app.deck.DeckService/SaveDecks",
    DeckSaveDecksRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveDecks(player, request.Decks, request.MainDeck);
        return Task.FromResult(new DeckSaveDecksResponse());
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
    "/app.present.PresentService/Fetch",
    PresentFetchRequest.Parser,
    static (_, _) => Task.FromResult(new PresentFetchResponse()));

app.MapGrpcUnary(
    "/app.present.PresentService/History",
    PresentHistoryRequest.Parser,
    static (_, _) => Task.FromResult(new PresentHistoryResponse()));

app.MapGrpcUnary(
    "/app.mission.MissionService/Fetch",
    MissionFetchRequest.Parser,
    static (_, _) => Task.FromResult(new MissionFetchResponse()));

app.MapGrpcUnary(
    "/app.announcement.AnnouncementService/GetList",
    AnnouncementGetListRequest.Parser,
    static (_, _) => Task.FromResult(new AnnouncementGetListResponse()));

app.MapGrpcUnary(
    "/app.carousel_help.CarouselHelpService/Shown",
    CarouselHelpShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownCarouselHelps(player, request.MasterIds);
        return Task.FromResult(new CarouselHelpShownResponse());
    });

app.MapGrpcUnary(
    "/app.content_unlock.ContentUnlockService/Shown",
    ContentUnlockShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownContentUnlocks(player, request.MasterIds);
        return Task.FromResult(new ContentUnlockShownResponse());
    });

app.MapGrpcUnary(
    "/app.live.LiveService/StartFree",
    LiveStartFreeRequest.Parser,
    static (_, _) => Task.FromResult(new LiveStartFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/FinishFree",
    LiveFinishFreeRequest.Parser,
    static (_, _) => Task.FromResult(new LiveFinishFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/SkipFree",
    LiveSkipFreeRequest.Parser,
    static (_, _) => Task.FromResult(new LiveSkipFreeResponse()));

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
    "/app.livemusic.LiveMusicService/GetRanking",
    LiveMusicGetRankingRequest.Parser,
    static (_, _) => Task.FromResult(new LiveMusicGetRankingResponse()));

app.MapGrpcUnary(
    "/app.story.StoryService/CheckMaintenanceStory",
    StoryCheckMaintenanceRequest.Parser,
    static (_, _) => Task.FromResult(new StoryCheckMaintenanceResponse()));

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

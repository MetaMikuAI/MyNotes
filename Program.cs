using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyNotes.Controllers;
using MyNotes.Config;
using MyNotes.Middleware;
using MyNotes.Services;
using Protocol = App.Protobuf;

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
    Protocol.Masterdata.VersionRequest.Parser,
    (ctx, _) =>
    {
        var protocol = ctx.RequestServices.GetRequiredService<MasterdataProtocolBuilder>();
        return Task.FromResult(protocol.Version());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/Register",
    Protocol.Player.RegisterRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.Register(request.InitialDataGroup);
        return Task.FromResult(protocol.Register(player));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/GetPlayerData",
    Protocol.Player.GetPlayerDataRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.GetPlayerData(player, players));
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/CheckNgWord",
    Protocol.Player.CheckNgWordRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Player.CheckNgWordResponse()));

app.MapGrpcUnary(
    "/app.player.PlayerService/EditProfile",
    Protocol.Player.EditProfileRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateDisplayName(player, request.Name);
        return Task.FromResult(new Protocol.Player.EditProfileResponse());
    });

app.MapGrpcUnary(
    "/app.player.PlayerService/ChangeFavoriteMember",
    Protocol.Player.ChangeFavoriteMemberRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.UpdateFavoriteMember(player, request.MemberId);
        return Task.FromResult(new Protocol.Player.ChangeFavoriteMemberResponse());
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/FindByProfileID",
    Protocol.Friend.FindByProfileIDRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var protocol = ctx.RequestServices.GetRequiredService<PlayerProtocolBuilder>();
        var response = new Protocol.Friend.FindByProfileIDResponse();
        if (players.TryGetByProfileId(request.PlayerProfileId, out var player))
            response.PlayerProfile = protocol.BuildSimpleProfile(player);

        return Task.FromResult(response);
    });

app.MapGrpcUnary(
    "/app.friend.FriendService/PlayerReport",
    Protocol.Friend.PlayerReportRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var reporter = players.GetFromRequest(ctx.Request);
        players.LogPlayerReport(
            reporter,
            request.TargetPlayerId,
            request.ReportType,
            request.ReportIds);
        return Task.FromResult(new Protocol.Friend.PlayerReportResponse());
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/GenerateInvitationCode",
    Protocol.Invitation.GenerateInvitationCodeRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.Invitation.GenerateInvitationCodeResponse
        {
            InvitationCode = PlayerManager.GetInvitationCode(player)
        });
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/InputInvitationCode",
    Protocol.Invitation.InputInvitationCodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(new Protocol.Invitation.InputInvitationCodeResponse
        {
            IsEstablishment = players.InputInvitationCode(player, request.InvitationCode)
        });
    });

app.MapGrpcUnary(
    "/app.invitation.InvitationService/UpdateInvitationView",
    Protocol.Invitation.UpdateInvitationViewRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        players.MarkInvitationsViewed(players.GetFromRequest(ctx.Request), request.TargetPlayerIds);
        return Task.FromResult(new Protocol.Invitation.UpdateInvitationViewResponse());
    });

app.MapGrpcUnary(
    "/app.circle.CircleService/GetRecommendedCircleList",
    Protocol.Circle.GetRecommendedCircleListRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Circle.GetRecommendedCircleListResponse()));

app.MapGrpcUnary(
    "/app.circle.CircleService/Search",
    Protocol.Circle.SearchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Circle.SearchResponse()));

app.MapGrpcUnary(
    "/app.circle.CircleService/GetCircleDetail",
    Protocol.Circle.GetCircleRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Circle.GetCircleResponse()));

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitationList",
    Protocol.CircleInvitation.GetInvitationListRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleInvitation.GetInvitationListResponse()));

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitingPlayer",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleInvitation.GetInvitingPlayerResponse()));

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetCircleInvitationRecommendList",
    Empty.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleInvitation.GetCircleInvitationRecommendListResponse()));

app.MapGrpcUnary(
    "/app.circleinvitation.CircleInvitationService/GetInvitablePlayer",
    Protocol.CircleInvitation.GetInvitablePlayerRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.CircleInvitation.GetInvitablePlayerResponse()));

app.MapGrpcUnary(
    "/app.deck.DeckService/SaveDecks",
    Protocol.Deck.SaveDecksRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveDecks(player, request.Decks, request.MainDeck);
        return Task.FromResult(new Protocol.Deck.SaveDecksResponse());
    });

app.MapGrpcUnary(
    "/app.home.HomeService/Get",
    Protocol.Home.GetHomeRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var home = ctx.RequestServices.GetRequiredService<HomeSnapshotService>();
        var protocol = ctx.RequestServices.GetRequiredService<HomeProtocolBuilder>();
        var player = players.GetFromRequest(ctx.Request);
        return Task.FromResult(protocol.Get(home.GetFor(player), player));
    });

app.MapGrpcUnary(
    "/app.present.PresentService/Fetch",
    Protocol.Present.FetchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.FetchResponse()));

app.MapGrpcUnary(
    "/app.present.PresentService/History",
    Protocol.Present.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.HistoryResponse()));

app.MapGrpcUnary(
    "/app.mission.MissionService/Fetch",
    Protocol.Present.FetchMissionRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.FetchMissionResponse()));

app.MapGrpcUnary(
    "/app.loginbonus.LoginBonusService/Update",
    Protocol.Present.UpdateLoginbonusRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Present.UpdateLoginbonusResponse()));

app.MapGrpcUnary(
    "/app.announcement.AnnouncementService/GetList",
    Protocol.Announcement.GetListRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Announcement.GetListResponse()));

app.MapGrpcUnary(
    "/app.announcement.AnnouncementService/Get",
    Protocol.Announcement.GetRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Announcement.GetResponse()));

app.MapGrpcUnary(
    "/app.carousel_help.CarouselHelpService/Shown",
    Protocol.CarouselHelp.ShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownCarouselHelps(player, request.MasterIds);
        return Task.FromResult(new Protocol.CarouselHelp.ShownResponse());
    });

app.MapGrpcUnary(
    "/app.content_unlock.ContentUnlockService/Shown",
    Protocol.ContentUnlock.ShownRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveShownContentUnlocks(player, request.MasterIds);
        return Task.FromResult(new Protocol.ContentUnlock.ShownResponse());
    });

app.MapGrpcUnary(
    "/app.stamp.StampService/UpdateStampFavorites",
    Protocol.Stamp.UpdateStampFavoritesRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveStampFavorites(player, request.Favorites);
        return Task.FromResult(new Protocol.Stamp.UpdateStampFavoritesResponse());
    });

app.MapGrpcUnary(
    "/app.stamp.StampService/UpdateStampFavoriteName",
    Protocol.Stamp.UpdateStampFavoriteNameRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveStampFavoriteName(player, request.FavoriteId, request.Name);
        return Task.FromResult(new Protocol.Stamp.UpdateStampFavoriteNameResponse());
    });

app.MapGrpcUnary(
    "/app.gacha.GachaService/History",
    Protocol.Gacha.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Gacha.HistoryResponse()));

app.MapGrpcUnary(
    "/app.gacha.GachaService/CheckMaintenanceGacha",
    Protocol.Gacha.CheckMaintenanceGachaRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Gacha.CheckMaintenanceGachaResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/StartFree",
    Protocol.Live.StartFreeRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.StartFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/FinishFree",
    Protocol.Live.FinishFreeRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.FinishFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/SkipFree",
    Protocol.Live.SkipFreeRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.SkipFreeResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/CheckMaintenanceCasualMatch",
    Protocol.Live.CheckMaintenanceCasualMatchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.CheckMaintenanceCasualMatchResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/CheckMaintenanceRankMatch",
    Protocol.Live.CheckMaintenanceRankMatchRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.CheckMaintenanceRankMatchResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/GetBattleLiveSearchId",
    Protocol.Live.GetBattleLiveSearchIdRequest.Parser,
    (ctx, _) =>
    {
        var protocol = ctx.RequestServices.GetRequiredService<LiveProtocolBuilder>();
        return Task.FromResult(protocol.GetBattleLiveSearchId());
    });

app.MapGrpcUnary(
    "/app.live.LiveService/StartBattleLive",
    Protocol.Live.StartBattleLiveRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Live.StartBattleLiveResponse()));

app.MapGrpcUnary(
    "/app.live.LiveService/SaveSetting",
    Protocol.Live.SaveSettingRequest.Parser,
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
    Protocol.LiveMusic.GetRankingRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.LiveMusic.GetRankingResponse()));

app.MapGrpcUnary(
    "/app.shop.ShopService/CheckMaintenanceShop",
    Protocol.Shop.CheckMaintenanceShopRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Shop.CheckMaintenanceShopResponse()));

app.MapGrpcUnary(
    "/app.shop.ShopService/Fetch",
    Protocol.Shop.FetchRequest.Parser,
    (ctx, _) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        var birth = players.GetShopBirth(player);
        return Task.FromResult(new Protocol.Shop.FetchResponse
        {
            Year = birth.Year,
            Month = birth.Month
        });
    });

app.MapGrpcUnary(
    "/app.shop.ShopService/SaveBirth",
    Protocol.Shop.SaveBirthRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.SaveBirth(player, request.Year, request.Month);
        return Task.FromResult(new Protocol.Shop.SaveBirthResponse());
    });

app.MapGrpcUnary(
    "/app.shop.ShopService/History",
    Protocol.Shop.HistoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Shop.HistoryResponse()));

app.MapGrpcUnary(
    "/app.story.StoryService/CheckMaintenanceStory",
    Protocol.Story.CheckMaintenanceStoryRequest.Parser,
    static (_, _) => Task.FromResult(new Protocol.Story.CheckMaintenanceStoryResponse()));

app.MapGrpcUnary(
    "/app.story.StoryService/ReadStoryEpisode",
    Protocol.Story.ReadStoryEpisodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.ReadStoryEpisode(player, request.EpisodeId, request.IsSkipped);
        return Task.FromResult(new Protocol.Story.ReadStoryEpisodeResponse());
    });

app.MapGrpcUnary(
    "/app.story.StoryService/ReadFriendshipEpisode",
    Protocol.Story.ReadFriendshipEpisodeRequest.Parser,
    (ctx, request) =>
    {
        var players = ctx.RequestServices.GetRequiredService<PlayerManager>();
        var player = players.GetFromRequest(ctx.Request);
        players.ReadFriendshipEpisode(player, request.EpisodeId, request.IsSkipped);
        return Task.FromResult(new Protocol.Story.ReadFriendshipEpisodeResponse());
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

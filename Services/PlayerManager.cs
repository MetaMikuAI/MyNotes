using System.Collections.Concurrent;
using System.Security.Cryptography;
using App.Protobuf.Entity;
using MyNotes.Config;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerManager(ILogger<PlayerManager> logger)
{
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersById = new();
    private readonly ConcurrentDictionary<long, PlayerRecord> _playersByProfileId = new();
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersByAuthorization = new(StringComparer.Ordinal);
    private long _nextProfileId = 100000000;

    public PlayerRecord Register(string initialDataGroup)
    {
        var profileId = Interlocked.Increment(ref _nextProfileId);
        var now = DateTimeOffset.UtcNow;
        var player = new PlayerRecord
        {
            ProfileId = profileId,
            PlayerId = profileId.ToString(),
            DisplayName = $"Player{profileId % 10000:D4}",
            AuthorizationKey = NewToken(32),
            DeviceId = NewToken(16),
            InitialDataGroup = string.IsNullOrWhiteSpace(initialDataGroup) ? ServerConfig.InitialDataGroup : initialDataGroup,
            LiveSettingAll = LiveSettingCodec.CreateDefaultLiveSettingAll(),
            CreatedAt = now,
            ProfileUpdatedAtUnixSeconds = now.ToUnixTimeSeconds()
        };

        Add(player);
        logger.LogInformation("Registered player {PlayerId} profile {ProfileId}", player.PlayerId, player.ProfileId);
        return player;
    }

    public PlayerRecord GetFromRequest(HttpRequest request)
    {
        if (TryGetFromRequest(request, out var player))
            return player;

        return _playersById.Values.OrderBy(p => p.ProfileId).FirstOrDefault() ?? Register(ServerConfig.InitialDataGroup);
    }

    public bool TryGetByProfileId(long profileId, out PlayerRecord player) =>
        _playersByProfileId.TryGetValue(profileId, out player!);

    public void UpdateDisplayName(PlayerRecord player, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        player.DisplayName = displayName.Trim();
        player.TouchProfile();
        logger.LogInformation("Updated player {PlayerId} display name to {DisplayName}", player.PlayerId, player.DisplayName);
    }

    public void UpdateLiveSetting(PlayerRecord player, byte[] settingAll)
    {
        player.LiveSettingAll = LiveSettingCodec.NormalizeLiveSettingAll(settingAll);
        logger.LogInformation("Updated player {PlayerId} live setting ({ByteCount} bytes)", player.PlayerId, settingAll.Length);
    }

    public void UpdateFavoriteMember(PlayerRecord player, long memberCardId)
    {
        player.FavoriteMemberCardId = memberCardId;
        player.TouchProfile();
        logger.LogInformation(
            "Updated player {PlayerId} favorite member card to {MemberCardId}",
            player.PlayerId,
            memberCardId);
    }

    public void SaveDecks(PlayerRecord player, IEnumerable<Deck> decks, int mainDeck)
    {
        var deckPatches = decks.Select(CloneDeckState).ToArray();

        lock (player.DeckStateLock)
        {
            foreach (var deck in deckPatches)
                player.DeckOverrides[deck.Id] = deck;

            if (mainDeck != 0)
                player.MainDeckOverride = mainDeck;

            if (player.FavoriteMemberCardId == 0)
                player.TouchProfile();
        }

        logger.LogInformation(
            "Updated player {PlayerId} decks ({DeckCount} patches, main deck {MainDeck})",
            player.PlayerId,
            deckPatches.Length,
            mainDeck);
    }

    public void ReadStoryEpisode(PlayerRecord player, long episodeId, bool isSkipped)
    {
        var episode = new StoryEpisode
        {
            EpisodeId = episodeId,
            IsSkipped = isSkipped,
            LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        lock (player.StoryStateLock)
            player.SeenStoryEpisodes[episodeId] = episode;

        logger.LogInformation(
            "Updated player {PlayerId} story episode {EpisodeId} (skipped: {IsSkipped})",
            player.PlayerId,
            episodeId,
            isSkipped);
    }

    public void ReadFriendshipEpisode(PlayerRecord player, long episodeId, bool isSkipped)
    {
        var episode = new StoryEpisode
        {
            EpisodeId = episodeId,
            IsSkipped = isSkipped,
            LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        lock (player.StoryStateLock)
            player.SeenStoryFriendshipEpisodes[episodeId] = episode;

        logger.LogInformation(
            "Updated player {PlayerId} friendship story episode {EpisodeId} (skipped: {IsSkipped})",
            player.PlayerId,
            episodeId,
            isSkipped);
    }

    public void SaveBirth(PlayerRecord player, int year, int month)
    {
        var now = DateTimeOffset.UtcNow;
        var isFutureMonth = year > now.Year || (year == now.Year && month > now.Month);
        if (year < 1900 || month is < 1 or > 12 || isFutureMonth)
        {
            logger.LogWarning(
                "Ignored invalid birth month {Year}-{Month} for player {PlayerId}",
                year,
                month,
                player.PlayerId);
            return;
        }

        lock (player.ShopStateLock)
        {
            player.ShopBirthYear = year;
            player.ShopBirthMonth = month;
        }

        logger.LogInformation(
            "Updated player {PlayerId} birth month to {Year}-{Month}",
            player.PlayerId,
            year,
            month);
    }

    public (int Year, int Month) GetShopBirth(PlayerRecord player)
    {
        lock (player.ShopStateLock)
            return (player.ShopBirthYear, player.ShopBirthMonth);
    }

    public void SaveShownCarouselHelps(PlayerRecord player, IEnumerable<long> masterIds)
    {
        foreach (var masterId in masterIds)
            player.ShownCarouselHelpIds.TryAdd(masterId, 0);

        logger.LogInformation(
            "Updated player {PlayerId} shown carousel helps ({Count} ids)",
            player.PlayerId,
            player.ShownCarouselHelpIds.Count);
    }

    public void SaveShownContentUnlocks(PlayerRecord player, IEnumerable<long> masterIds)
    {
        foreach (var masterId in masterIds)
            player.ShownContentUnlockIds.TryAdd(masterId, 0);

        logger.LogInformation(
            "Updated player {PlayerId} shown content unlocks ({Count} ids)",
            player.PlayerId,
            player.ShownContentUnlockIds.Count);
    }

    public bool IsKnownCredential(HttpRequest request)
    {
        if (TryGetFromRequest(request, out _))
            return true;

        if (!request.Headers.TryGetValue("authorization", out var value))
            return true;

        var authorization = value.ToString();
        return string.IsNullOrWhiteSpace(authorization) || ServerConfig.IsExpectedBasicAuth(authorization);
    }

    private bool TryGetFromRequest(HttpRequest request, out PlayerRecord player)
    {
        var authorization = ReadHeader(request, "authorization");
        if (authorization != null && _playersByAuthorization.TryGetValue(StripBearerPrefix(authorization), out player!))
            return true;

        var playerId = ReadHeader(request, "x-player-id")
            ?? ReadHeader(request, "player-id")
            ?? ReadHeader(request, "playerid")
            ?? ReadHeader(request, "x-playerid");

        if (playerId != null && _playersById.TryGetValue(playerId, out var matched))
        {
            player = matched;
            return true;
        }

        player = null!;
        return false;
    }

    private void Add(PlayerRecord player)
    {
        _playersById[player.PlayerId] = player;
        _playersByProfileId[player.ProfileId] = player;
        _playersByAuthorization[player.AuthorizationKey] = player;
    }

    private static string? ReadHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;

    private static string StripBearerPrefix(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static Deck CloneDeckState(Deck source)
    {
        var result = new Deck
        {
            Id = source.Id,
            Name = source.Name
        };

        foreach (var card in source.Cards)
            result.Cards.Add(card.Clone());

        return result;
    }

    private static string NewToken(int bytes)
    {
        Span<byte> data = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(data);
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}

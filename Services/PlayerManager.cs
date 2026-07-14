using System.Collections.Concurrent;
using System.Security.Cryptography;
using App.Protobuf.Entity;
using MyNotes.Config;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerManager(ILogger<PlayerManager> logger)
{
    private const int FavoriteStampGroupCount = 3;
    private const int FavoriteStampNameMaxLength = 6;
    private const int FavoriteStampSlotCount = 20;
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersById = new();
    private readonly ConcurrentDictionary<long, PlayerRecord> _playersByProfileId = new();
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersByAuthorization = new(StringComparer.Ordinal);
    private readonly object _invitationStateLock = new();
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

        foreach (var stampId in ServerConfig.InitialStampIds)
            player.OwnedStampIds.Add(stampId);

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

    public static string GetInvitationCode(PlayerRecord player) => player.ProfileId.ToString("D10");

    public bool InputInvitationCode(PlayerRecord invitee, string invitationCode)
    {
        if (invitationCode.Length != 10 ||
            invitationCode.Any(character => character is < '0' or > '9') ||
            !long.TryParse(invitationCode, out var inviterProfileId))
            return false;

        lock (_invitationStateLock)
        {
            if (invitee.IsInvitationCodeInputAlready ||
                !_playersByProfileId.TryGetValue(inviterProfileId, out var inviter) ||
                ReferenceEquals(inviter, invitee))
                return false;

            invitee.IsInvitationCodeInputAlready = true;
            var establishmentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            inviter.InvitationEstablishments[invitee.PlayerId] = establishmentAt;
            inviter.NewInvitationPlayerIds.Add(invitee.PlayerId);
            return true;
        }
    }

    public void MarkInvitationsViewed(PlayerRecord player, IEnumerable<string> playerIds)
    {
        lock (_invitationStateLock)
        {
            foreach (var playerId in playerIds)
                player.NewInvitationPlayerIds.Remove(playerId);
        }
    }

    public (bool InputAlready, string[] NewPlayerIds) GetInvitationState(PlayerRecord player)
    {
        lock (_invitationStateLock)
            return (player.IsInvitationCodeInputAlready, player.NewInvitationPlayerIds.ToArray());
    }

    public (PlayerRecord Player, long EstablishmentAt)[] GetInvitationProfiles(PlayerRecord player)
    {
        lock (_invitationStateLock)
        {
            return player.InvitationEstablishments
                .Select(pair => (_playersById[pair.Key], pair.Value))
                .ToArray();
        }
    }

    public void LogPlayerReport(
        PlayerRecord reporter,
        string targetPlayerId,
        int reportType,
        IEnumerable<long> reportIds)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var reportIdSnapshot = reportIds.ToArray();
        logger.LogInformation(
            "Player report received at {ReceivedAt} from {ReporterPlayerId} for {TargetPlayerId} " +
            "(type {ReportType}, report ids [{ReportIds}])",
            receivedAt,
            reporter.PlayerId,
            targetPlayerId,
            reportType,
            string.Join(',', reportIdSnapshot));
    }

    public void UpdateDisplayName(PlayerRecord player, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        player.DisplayName = displayName.Trim();
        player.TouchProfile();
        logger.LogInformation("Updated player {PlayerId} display name to {DisplayName}", player.PlayerId, player.DisplayName);
    }

    public void RegisterConnection(PlayerRecord player, string password)
    {
        lock (player.ConnectionStateLock)
            player.ConnectionPassword = password;
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

    public void SaveStampFavorites(PlayerRecord player, IEnumerable<UpdateStampFavorite> favorites)
    {
        var patches = new Dictionary<int, long[]>();
        var oversizedGroupCount = 0;
        var negativeStampIdCount = 0;
        var invalidFavoriteIdCount = 0;
        var duplicateGroupCount = 0;

        foreach (var favorite in favorites)
        {
            if (favorite.FavoriteId is < 0 or >= FavoriteStampGroupCount)
            {
                invalidFavoriteIdCount++;
                continue;
            }

            var stampIds = new long[FavoriteStampSlotCount];
            var count = Math.Min(favorite.StampIds.Count, FavoriteStampSlotCount);
            for (var index = 0; index < count; index++)
            {
                var stampId = favorite.StampIds[index];
                if (stampId > 0)
                    stampIds[index] = stampId;
                else if (stampId < 0)
                    negativeStampIdCount++;
            }

            if (favorite.StampIds.Count > FavoriteStampSlotCount)
                oversizedGroupCount++;

            if (!patches.TryAdd(favorite.FavoriteId, stampIds))
            {
                patches[favorite.FavoriteId] = stampIds;
                duplicateGroupCount++;
            }
        }

        var unknownStampIdCount = 0;
        lock (player.StampStateLock)
        {
            foreach (var (favoriteId, stampIds) in patches)
            {
                for (var index = 0; index < stampIds.Length; index++)
                {
                    if (stampIds[index] > 0 && !player.OwnedStampIds.Contains(stampIds[index]))
                    {
                        stampIds[index] = 0;
                        unknownStampIdCount++;
                    }
                }

                player.StampFavoriteGroups[favoriteId] = stampIds;
            }
        }

        logger.LogInformation(
            "Updated player {PlayerId} stamp favorites ({GroupCount} groups)",
            player.PlayerId,
            patches.Count);

        var wasNormalized = oversizedGroupCount > 0 ||
            negativeStampIdCount > 0 ||
            invalidFavoriteIdCount > 0 ||
            duplicateGroupCount > 0 ||
            unknownStampIdCount > 0;
        if (wasNormalized)
        {
            logger.LogWarning(
                "Normalized stamp favorites for player {PlayerId} " +
                "({OversizedGroupCount} oversized groups, {NegativeStampIdCount} negative stamp ids, " +
                "{InvalidFavoriteIdCount} invalid favorite ids, {DuplicateGroupCount} duplicate groups, " +
                "{UnknownStampIdCount} unknown stamp ids)",
                player.PlayerId,
                oversizedGroupCount,
                negativeStampIdCount,
                invalidFavoriteIdCount,
                duplicateGroupCount,
                unknownStampIdCount);
        }
    }

    public void SaveStampFavoriteName(PlayerRecord player, int favoriteId, string name)
    {
        if (favoriteId is < 0 or >= FavoriteStampGroupCount || name.Length > FavoriteStampNameMaxLength)
        {
            logger.LogWarning(
                "Ignored invalid stamp favorite name for player {PlayerId} (favorite {FavoriteId}, length {NameLength})",
                player.PlayerId,
                favoriteId,
                name.Length);
            return;
        }

        lock (player.StampStateLock)
        {
            if (name.Length == 0)
                player.StampFavoriteNames.Remove(favoriteId);
            else
                player.StampFavoriteNames[favoriteId] = name;
        }

        logger.LogInformation(
            "Updated player {PlayerId} stamp favorite {FavoriteId} name",
            player.PlayerId,
            favoriteId);
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

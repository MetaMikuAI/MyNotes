using System.Collections.Concurrent;
using System.Security.Cryptography;
using MyNotes.Config;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerManager(ILogger<PlayerManager> logger)
{
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersById = new();
    private readonly ConcurrentDictionary<string, PlayerRecord> _playersByAuthorization = new(StringComparer.Ordinal);
    private long _nextProfileId = 100000000;

    public PlayerRecord Register(string initialDataGroup)
    {
        var profileId = Interlocked.Increment(ref _nextProfileId);
        var player = new PlayerRecord
        {
            ProfileId = profileId,
            PlayerId = profileId.ToString(),
            DisplayName = $"Player{profileId % 10000:D4}",
            AuthorizationKey = NewToken(32),
            DeviceId = NewToken(16),
            InitialDataGroup = string.IsNullOrWhiteSpace(initialDataGroup) ? ServerConfig.InitialDataGroup : initialDataGroup,
            LiveSettingAll = LiveSettingCodec.CreateDefaultLiveSettingAll()
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

    public void UpdateDisplayName(PlayerRecord player, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        player.DisplayName = displayName.Trim();
        logger.LogInformation("Updated player {PlayerId} display name to {DisplayName}", player.PlayerId, player.DisplayName);
    }

    public void UpdateLiveSetting(PlayerRecord player, byte[] settingAll)
    {
        player.LiveSettingAll = LiveSettingCodec.NormalizeLiveSettingAll(settingAll);
        logger.LogInformation("Updated player {PlayerId} live setting ({ByteCount} bytes)", player.PlayerId, settingAll.Length);
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
        _playersByAuthorization[player.AuthorizationKey] = player;
    }

    private static string? ReadHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) && value.Count > 0 ? value.ToString() : null;

    private static string StripBearerPrefix(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static string NewToken(int bytes)
    {
        Span<byte> data = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(data);
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}

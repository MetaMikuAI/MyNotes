using System.Collections.Concurrent;
using App.Protobuf.Entity;

namespace MyNotes.Models;

public sealed class PlayerRecord
{
    private long _profileUpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public long ProfileId { get; init; }
    public string PlayerId { get; init; } = "";
    public string DisplayName { get; set; } = "MyNotes";
    public string AuthorizationKey { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string InitialDataGroup { get; init; } = "";
    public long FavoriteMemberCardId { get; set; }
    public byte[] LiveSettingAll { get; set; } = [];
    internal object DeckStateLock { get; } = new();
    internal Dictionary<int, Deck> DeckOverrides { get; } = [];
    internal int MainDeckOverride { get; set; }
    internal object StoryStateLock { get; } = new();
    internal Dictionary<long, StoryEpisode> SeenStoryEpisodes { get; } = [];
    internal Dictionary<long, StoryEpisode> SeenStoryFriendshipEpisodes { get; } = [];
    internal object ShopStateLock { get; } = new();
    internal int ShopBirthYear { get; set; }
    internal int ShopBirthMonth { get; set; }
    internal object StampStateLock { get; } = new();
    internal HashSet<long> OwnedStampIds { get; } = [];
    internal Dictionary<int, long[]> StampFavoriteGroups { get; } = [];
    internal Dictionary<int, string> StampFavoriteNames { get; } = [];
    internal bool IsInvitationCodeInputAlready { get; set; }
    internal Dictionary<string, long> InvitationEstablishments { get; } = [];
    internal HashSet<string> NewInvitationPlayerIds { get; } = [];
    public ConcurrentDictionary<long, byte> ShownCarouselHelpIds { get; } = new();
    public ConcurrentDictionary<long, byte> ShownContentUnlockIds { get; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    internal long ProfileUpdatedAtUnixSeconds
    {
        get => Interlocked.Read(ref _profileUpdatedAtUnixSeconds);
        init => Interlocked.Exchange(ref _profileUpdatedAtUnixSeconds, value);
    }

    internal void TouchProfile()
    {
        var candidate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (true)
        {
            var current = Interlocked.Read(ref _profileUpdatedAtUnixSeconds);
            if (candidate <= current)
                return;

            if (Interlocked.CompareExchange(ref _profileUpdatedAtUnixSeconds, candidate, current) == current)
                return;
        }
    }
}

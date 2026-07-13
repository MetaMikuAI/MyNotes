using System.Collections.Concurrent;
using App.Protobuf.Entity;

namespace MyNotes.Models;

public sealed class PlayerRecord
{
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
    public ConcurrentDictionary<long, byte> ShownCarouselHelpIds { get; } = new();
    public ConcurrentDictionary<long, byte> ShownContentUnlockIds { get; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

using App.Protobuf.Live;

namespace MyNotes.Services;

public sealed class LiveProtocolBuilder
{
    private const int FirstBattleLiveSearchId = 1_000_000;
    private const int LastBattleLiveSearchId = 9_999_999;
    private readonly object _battleLiveSearchIdLock = new();
    private int _nextBattleLiveSearchId = FirstBattleLiveSearchId;

    public SaveSettingResponse SaveSetting() => new();

    public GetBattleLiveSearchIdResponse GetBattleLiveSearchId()
    {
        int searchId;
        lock (_battleLiveSearchIdLock)
        {
            searchId = _nextBattleLiveSearchId;
            _nextBattleLiveSearchId = searchId == LastBattleLiveSearchId
                ? FirstBattleLiveSearchId
                : searchId + 1;
        }

        return new GetBattleLiveSearchIdResponse
        {
            SearchId = searchId
        };
    }
}

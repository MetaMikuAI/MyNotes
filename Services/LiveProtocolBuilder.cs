using App.Protobuf.Live;

namespace MyNotes.Services;

public sealed class LiveProtocolBuilder
{
    public SaveSettingResponse SaveSetting() => new();
}

using MessagePack;
using MyNotes.Models;

namespace MyNotes.Services;

public static class LiveSettingCodec
{
    private static readonly byte[] DefaultLiveSettingAllBytes = CreateDefaultLiveSettingAllCore();

    public static byte[] CreateDefaultLiveSettingAll() => DefaultLiveSettingAllBytes.ToArray();

    public static byte[] NormalizeLiveSettingAll(byte[] settingAll)
    {
        if (settingAll.Length == 0)
            return CreateDefaultLiveSettingAll();

        try
        {
            var optionData = MessagePackSerializer.Deserialize<MyNotesOptionData>(settingAll);
            optionData.EnsureDefaults();
            return MessagePackSerializer.Serialize(optionData);
        }
        catch (MessagePackSerializationException)
        {
            return CreateDefaultLiveSettingAll();
        }
    }

    private static byte[] CreateDefaultLiveSettingAllCore()
    {
        var optionData = MyNotesOptionData.CreateDefault();
        return MessagePackSerializer.Serialize(optionData);
    }
}

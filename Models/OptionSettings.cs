using MessagePack;

namespace MyNotes.Models;

[MessagePackObject]
public sealed class MyNotesOptionData
{
    [Key(0)] public int CurrentPresetIndex { get; set; }
    [Key(1)] public MyNotesOptionPresetData? Preset1 { get; set; }
    [Key(2)] public MyNotesOptionPresetData? Preset2 { get; set; }
    [Key(3)] public MyNotesOptionPresetData? Preset3 { get; set; }
    [Key(4)] public int LatestVersion { get; set; }

    public static MyNotesOptionData CreateDefault() => new()
    {
        CurrentPresetIndex = 0,
        Preset1 = MyNotesOptionPresetData.CreateDefault("Default"),
        Preset2 = MyNotesOptionPresetData.CreateDefault("Preset 2"),
        Preset3 = MyNotesOptionPresetData.CreateDefault("Preset 3"),
        LatestVersion = 0
    };

    public void EnsureDefaults()
    {
        Preset1 ??= MyNotesOptionPresetData.CreateDefault("Default");
        Preset2 ??= MyNotesOptionPresetData.CreateDefault("Preset 2");
        Preset3 ??= MyNotesOptionPresetData.CreateDefault("Preset 3");

        Preset1.EnsureDefaults("Default");
        Preset2.EnsureDefaults("Preset 2");
        Preset3.EnsureDefaults("Preset 3");
    }
}

[MessagePackObject]
public sealed class MyNotesOptionPresetData
{
    [Key(0)] public string? Name { get; set; }
    [Key(1)] public MyNotesBasicSettings? Basic { get; set; }
    [Key(2)] public MyNotesDetailSettings? Detail { get; set; }
    [Key(3)] public MyNotesDisplay1Settings? Display1 { get; set; }
    [Key(4)] public MyNotesDisplay2Settings? Display2 { get; set; }
    [Key(5)] public MyNotesSoundSettings? Sound { get; set; }
    [Key(6)] public MyNotesIndividualNoteSeSettings? IndividualNoteSe { get; set; }
    [Key(7)] public MyNotesSystemSettings? System { get; set; }
    [Key(8)] public MyNotesBluetoothSettings? Bluetooth { get; set; }

    public static MyNotesOptionPresetData CreateDefault(string name) => new()
    {
        Name = name,
        Basic = new MyNotesBasicSettings(),
        Detail = new MyNotesDetailSettings(),
        Display1 = new MyNotesDisplay1Settings(),
        Display2 = new MyNotesDisplay2Settings(),
        Sound = new MyNotesSoundSettings(),
        IndividualNoteSe = new MyNotesIndividualNoteSeSettings(),
        System = new MyNotesSystemSettings(),
        Bluetooth = new MyNotesBluetoothSettings()
    };

    public void EnsureDefaults(string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(Name))
            Name = fallbackName;

        Basic ??= new MyNotesBasicSettings();
        Detail ??= new MyNotesDetailSettings();
        Display1 ??= new MyNotesDisplay1Settings();
        Display2 ??= new MyNotesDisplay2Settings();
        Sound ??= new MyNotesSoundSettings();
        IndividualNoteSe ??= new MyNotesIndividualNoteSeSettings();
        System ??= new MyNotesSystemSettings();
        Bluetooth ??= new MyNotesBluetoothSettings();
    }
}

[MessagePackObject]
public sealed class MyNotesBasicSettings
{
    [Key(0)] public float NoteSpeed { get; set; } = 9.0f;
    [Key(1)] public float NoteTimingOffset { get; set; }
    [Key(2)] public float ChartPositionOffset { get; set; }
    [Key(3)] public bool Value3 { get; set; }
    [Key(4)] public bool Value4 { get; set; }
    [Key(5)] public int LiveQuality { get; set; } = 1;
    [Key(6)] public bool Value6 { get; set; }
}

[MessagePackObject]
public sealed class MyNotesDetailSettings
{
    [Key(0)] public bool Value0 { get; set; }
    [Key(1)] public bool Value1 { get; set; }
    [Key(2)] public bool Value2 { get; set; }
    [Key(3)] public int Value3 { get; set; }
    [Key(4)] public int Value4 { get; set; }
    [Key(5)] public int Value5 { get; set; }
    [Key(6)] public int Value6 { get; set; }
    [Key(7)] public bool Value7 { get; set; }
    [Key(8)] public bool Value8 { get; set; }
    [Key(9)] public int Value9 { get; set; }
    [Key(10)] public bool Value10 { get; set; }
    [Key(11)] public bool Value11 { get; set; }
    [Key(12)] public bool Value12 { get; set; }
    [Key(13)] public bool Value13 { get; set; }
    [Key(14)] public bool Value14 { get; set; }
    [Key(15)] public int Value15 { get; set; }
    [Key(16)] public bool Value16 { get; set; }
    [Key(17)] public int Value17 { get; set; }
    [Key(18)] public bool Value18 { get; set; }
}

[MessagePackObject]
public sealed class MyNotesDisplay1Settings
{
    [Key(0)] public int LiveScreenMode { get; set; } = 3;
    [Key(1)] public int BackgroundBrightness { get; set; }
    [Key(2)] public int MvModeBrightness { get; set; }
    [Key(3)] public int Value3 { get; set; }
    [Key(4)] public bool Value4 { get; set; }
    [Key(5)] public bool Value5 { get; set; }
    [Key(6)] public bool Value6 { get; set; }
    [Key(7)] public bool Value7 { get; set; }
    [Key(8)] public bool Value8 { get; set; }
}

[MessagePackObject]
public sealed class MyNotesDisplay2Settings
{
    [Key(0)] public int Value0 { get; set; }
    [Key(1)] public int Value1 { get; set; }
    [Key(2)] public int Value2 { get; set; }
    [Key(3)] public int Value3 { get; set; }
    [Key(4)] public bool Value4 { get; set; }
    [Key(5)] public int Value5 { get; set; }
    [Key(6)] public int Value6 { get; set; }
    [Key(7)] public int Value7 { get; set; }
    [Key(8)] public int Value8 { get; set; }
    [Key(9)] public bool Value9 { get; set; }
}

[MessagePackObject]
public sealed class MyNotesSoundSettings
{
}

[MessagePackObject]
public sealed class MyNotesIndividualNoteSeSettings
{
    [Key(0)] public int Value0 { get; set; }
    [Key(1)] public int Value1 { get; set; }
    [Key(2)] public int Value2 { get; set; }
    [Key(3)] public int Value3 { get; set; }
    [Key(4)] public int Value4 { get; set; }
    [Key(5)] public int Value5 { get; set; }
    [Key(6)] public int Value6 { get; set; }
    [Key(7)] public int Value7 { get; set; }
    [Key(8)] public int Value8 { get; set; }
    [Key(9)] public int Value9 { get; set; }
    [Key(10)] public int Value10 { get; set; }
    [Key(11)] public int Value11 { get; set; }
    [Key(12)] public int Value12 { get; set; }
    [Key(13)] public int Value13 { get; set; }
    [Key(14)] public int Value14 { get; set; }
    [Key(15)] public int Value15 { get; set; }
    [Key(16)] public int Value16 { get; set; }
    [Key(17)] public int Value17 { get; set; }
    [Key(18)] public int Value18 { get; set; }
    [Key(19)] public int Value19 { get; set; }
    [Key(20)] public int Value20 { get; set; }
    [Key(21)] public int Value21 { get; set; }
    [Key(22)] public bool Value22 { get; set; }
    [Key(23)] public bool Value23 { get; set; }
    [Key(24)] public bool Value24 { get; set; }
    [Key(25)] public bool Value25 { get; set; }
    [Key(26)] public bool Value26 { get; set; }
    [Key(27)] public bool Value27 { get; set; }
    [Key(28)] public bool Value28 { get; set; }
    [Key(29)] public bool Value29 { get; set; }
    [Key(30)] public bool Value30 { get; set; }
    [Key(31)] public bool Value31 { get; set; }
    [Key(32)] public bool Value32 { get; set; }
}

[MessagePackObject]
public sealed class MyNotesSystemSettings
{
}

[MessagePackObject]
public sealed class MyNotesBluetoothSettings
{
}

using System.Text;
using MessagePack;
using MyNotes.Models;

namespace MyNotes.Services;

public static class MyNotesProtobuf
{
    private static readonly long[] InitialCharacterIds = [1, 2, 3, 4, 5];
    private static readonly byte[] DefaultLiveSettingAllBytes = CreateDefaultLiveSettingAllCore();

    public static byte[] EmptyResponse() => [];

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

    public static string ParseEditProfileName(ReadOnlyMemory<byte> payload)
    {
        var name = "";
        var reader = new ProtoReader(payload.Span);
        while (reader.TryReadField(out var field, out var wireType, out var value))
        {
            if (field == 1 && wireType == 2)
                name = Encoding.UTF8.GetString(value);
        }

        return name;
    }

    public static byte[] ParseSaveSettingAll(ReadOnlyMemory<byte> payload)
    {
        var reader = new ProtoReader(payload.Span);
        while (reader.TryReadField(out var field, out var wireType, out var value))
        {
            if (field == 1 && wireType == 2)
                return value.ToArray();
        }

        return [];
    }

    public static RegisterRequest ParseRegisterRequest(ReadOnlyMemory<byte> payload)
    {
        var initialDataGroup = "";
        var reader = new ProtoReader(payload.Span);
        while (reader.TryReadField(out var field, out var wireType, out var value))
        {
            if (field == 1 && wireType == 2)
                initialDataGroup = Encoding.UTF8.GetString(value);
        }

        return new RegisterRequest(initialDataGroup);
    }

    public static byte[] VersionResponse(string version)
    {
        var writer = new ProtoWriter();
        writer.WriteString(1, version);
        return writer.ToArray();
    }

    public static byte[] RegisterResponse(PlayerRecord player)
    {
        var writer = new ProtoWriter();
        writer.WriteMessage(1, PlayerCredential(player));
        writer.WriteInt64(2, player.ProfileId);
        return writer.ToArray();
    }

    public static byte[] GetPlayerDataResponse(PlayerRecord player, MasterDataService master)
    {
        var writer = new ProtoWriter();
        writer.WriteMessage(1, PlayerData(player, master));
        writer.WriteEmptyMessage(2);
        writer.WriteEmptyMessage(3);
        writer.WriteEmptyMessage(4);
        writer.WriteEmptyMessage(5);
        return writer.ToArray();
    }

    public static byte[] GetHomeResponse(HomeSnapshot snapshot)
    {
        var writer = new ProtoWriter();
        if (snapshot.HasNotification)
            writer.WriteEmptyMessage(3);
        if (snapshot.HasFriends)
            writer.WriteEmptyMessage(4);
        for (var i = 0; i < snapshot.InvitationProfiles; i++)
            writer.WriteEmptyMessage(5);
        return writer.ToArray();
    }

    private static byte[] PlayerCredential(PlayerRecord player)
    {
        var writer = new ProtoWriter();
        writer.WriteString(1, player.PlayerId);
        writer.WriteString(2, player.AuthorizationKey);
        writer.WriteString(3, player.DeviceId);
        return writer.ToArray();
    }

    private static byte[] PlayerData(PlayerRecord player, MasterDataService master)
    {
        var writer = new ProtoWriter();
        var initialData = master.GetInitialPlayerData(player.InitialDataGroup);

        for (var i = 0; i < initialData.MemberCards.Count; i++)
            writer.WriteMessage(2, MemberCard(i + 1, initialData.MemberCards[i], player.CreatedAt));

        for (var i = 0; i < initialData.SupportCards.Count; i++)
            writer.WriteMessage(3, SupportCard(i + 1, initialData.SupportCards[i], player.CreatedAt));

        foreach (var deck in initialData.Decks)
            writer.WriteMessage(4, Deck(deck));

        writer.WriteInt32(5, initialData.Decks.FirstOrDefault()?.Id ?? 1);
        writer.WriteMessage(6, Gem());
        writer.WriteMessage(7, LiveBoost(player.CreatedAt));
        writer.WriteInt32(9, 1); // playerRankExp

        foreach (var score in master.LiveScoreSeeds)
            writer.WriteMessage(10, LiveScore(score.MusicId, score.Difficulty));

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            writer.WriteMessage(11, LiveMusicReward(musicId));
        }

        foreach (var characterId in InitialCharacterIds)
            writer.WriteMessage(12, CharacterRank(characterId, 1));

        // Do not emit PlayerData.liveMusic here. The client builds the delivered music list from master data,
        // then treats this field only as unlock timestamp overrides for already-known music entries.
        writer.WriteEmptyMessage(18); // liveSkip
        writer.WriteEmptyMessage(24); // playerMissionData
        var liveSettingAll = player.LiveSettingAll is { Length: > 0 }
            ? NormalizeLiveSettingAll(player.LiveSettingAll)
            : DefaultLiveSettingAllBytes;
        writer.WriteMessage(28, LiveSetting(liveSettingAll));
        writer.WriteMessage(34, PlayerSimpleProfile(player));
        writer.WriteEmptyMessage(35); // liveStampReward

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            foreach (var difficulty in master.LiveComboRewardDifficulties)
                writer.WriteMessage(36, LiveMusicComboReward(musicId, difficulty));
        }

        writer.WriteEmptyMessage(37); // comeback

        return writer.ToArray();
    }

    private static byte[] MemberCard(int id, InitialMemberCardSeed seed, DateTimeOffset gainAt)
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, id);
        writer.WriteInt64(2, seed.MasterId);
        writer.WriteInt32(3, seed.Exp);
        writer.WriteInt32(4, seed.AwakeCount);
        writer.WriteInt32(5, seed.CardRank);
        writer.WriteInt32(6, seed.LeaderSkillLevel);
        writer.WriteInt32(7, seed.LiveSkillLevel);
        writer.WriteInt32(8, seed.PerformanceSkillLevel);
        writer.WriteInt64(9, UnixSeconds(gainAt));
        writer.WriteInt32(10, seed.LinkSkillLevel);
        writer.WriteInt32(11, seed.GekisouSkillLevel);
        return writer.ToArray();
    }

    private static byte[] SupportCard(int id, InitialSupportCardSeed seed, DateTimeOffset gainAt)
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, id);
        writer.WriteInt64(2, seed.MasterId);
        writer.WriteInt32(3, seed.Exp);
        writer.WriteInt32(5, seed.CardRank);
        writer.WriteInt64(6, UnixSeconds(gainAt));
        writer.WriteInt32(7, seed.DuplicateCount);
        return writer.ToArray();
    }

    private static byte[] Deck(InitialDeckSeed deck)
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, deck.Id);
        writer.WriteString(2, deck.Name);

        var cardCount = Math.Min(deck.MemberCardIds.Count, deck.SupportCardIds.Count);
        for (var i = 0; i < cardCount; i++)
            writer.WriteMessage(3, DeckCard(i, i, deck.MemberCardIds[i], deck.SupportCardIds[i]));

        if (cardCount > 0)
            writer.WriteInt32(4, 1000);

        return writer.ToArray();
    }

    private static byte[] DeckCard(int slotIndex, int performanceOrderIndex, long memberCardId, long supportCardId)
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, slotIndex);
        writer.WriteInt32(2, performanceOrderIndex);
        writer.WriteInt64(3, memberCardId);
        writer.WriteInt64(4, supportCardId);
        return writer.ToArray();
    }

    private static byte[] Gem()
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, 10000); // free
        writer.WriteInt32(2, 0);     // paid
        return writer.ToArray();
    }

    private static byte[] LiveBoost(DateTimeOffset createdAt)
    {
        var writer = new ProtoWriter();
        writer.WriteInt32(1, 10); // amount
        writer.WriteInt64(2, UnixSeconds(createdAt)); // previousRecoveryAt
        writer.WriteInt64(5, UnixSeconds(createdAt)); // lastDailyResetAt
        return writer.ToArray();
    }

    private static byte[] LiveMusic(long musicId, DateTimeOffset gotAt)
    {
        var writer = new ProtoWriter();
        writer.WriteInt64(1, musicId);
        writer.WriteInt64(2, UnixSeconds(gotAt));
        return writer.ToArray();
    }

    private static byte[] LiveScore(long musicId, int difficulty)
    {
        var writer = new ProtoWriter();
        writer.WriteInt64(3, musicId);
        writer.WriteInt32(4, difficulty);
        return writer.ToArray();
    }

    private static byte[] LiveMusicReward(long musicId)
    {
        var writer = new ProtoWriter();
        writer.WriteInt64(1, musicId);
        return writer.ToArray();
    }

    private static byte[] LiveMusicComboReward(long musicId, int difficulty)
    {
        var writer = new ProtoWriter();
        writer.WriteInt64(1, musicId);
        writer.WriteInt32(2, difficulty);
        return writer.ToArray();
    }

    private static byte[] CharacterRank(long characterId, int exp)
    {
        var writer = new ProtoWriter();
        writer.WriteInt64(1, characterId);
        writer.WriteInt32(2, exp);
        return writer.ToArray();
    }

    private static byte[] LiveSetting(byte[] settingAll)
    {
        var writer = new ProtoWriter();
        writer.WriteBytes(1, settingAll);
        return writer.ToArray();
    }

    private static byte[] PlayerSimpleProfile(PlayerRecord player)
    {
        var writer = new ProtoWriter();
        writer.WriteString(1, player.PlayerId);
        writer.WriteString(2, player.DisplayName);
        writer.WriteInt64(4, UnixSeconds(player.CreatedAt));
        writer.WriteInt64(6, player.ProfileId);
        return writer.ToArray();
    }

    private static long UnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static byte[] CreateDefaultLiveSettingAllCore()
    {
        var optionData = MyNotesOptionData.CreateDefault();
        return MessagePackSerializer.Serialize(optionData);
    }
}

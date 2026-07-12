using App.Protobuf.Entity;
using App.Protobuf.Player;
using Google.Protobuf;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class PlayerProtocolBuilder(MasterDataService master)
{
    private static readonly long[] InitialCharacterIds = [1, 2, 3, 4, 5];

    public RegisterResponse Register(PlayerRecord player) => new()
    {
        Credential = new PlayerCredential
        {
            Id = player.PlayerId,
            Credential = player.AuthorizationKey,
            DeviceId = player.DeviceId
        },
        ProfileId = player.ProfileId
    };

    public GetPlayerDataResponse GetPlayerData(PlayerRecord player) => new()
    {
        PlayerData = BuildPlayerData(player),
        Notification = new Notification(),
        Limitation = new Limitation(),
        Friends = new Friends(),
        Invitation = new Invitation()
    };

    private PlayerData BuildPlayerData(PlayerRecord player)
    {
        var initialData = master.GetInitialPlayerData(player.InitialDataGroup);
        var mainDeck = initialData.Decks.FirstOrDefault();
        var data = new PlayerData
        {
            MainDeck = mainDeck?.Id ?? 1,
            Gem = new Gem
            {
                Free = 10000,
                Paid = 0
            },
            LiveBoost = new LiveBoost
            {
                Amount = 10,
                PreviousRecoveryAt = UnixSeconds(player.CreatedAt),
                LastDailyResetAt = UnixSeconds(player.CreatedAt)
            },
            PlayerRankExp = 1,
            LiveSkip = new LiveSkip(),
            PlayerMissionData = new PlayerMissionData(),
            LiveSetting = new LiveSetting
            {
                SettingAll = ByteString.CopyFrom(
                    player.LiveSettingAll is { Length: > 0 }
                        ? LiveSettingCodec.NormalizeLiveSettingAll(player.LiveSettingAll)
                        : LiveSettingCodec.CreateDefaultLiveSettingAll())
            },
            MyProfile = BuildSimpleProfile(player, initialData, mainDeck),
            LiveStampReward = new LiveStampReward(),
            Comeback = new Comeback()
        };

        for (var i = 0; i < initialData.MemberCards.Count; i++)
            data.MemberCards.Add(BuildMemberCard(i + 1, initialData.MemberCards[i], player.CreatedAt));

        for (var i = 0; i < initialData.SupportCards.Count; i++)
            data.SupportCards.Add(BuildSupportCard(i + 1, initialData.SupportCards[i], player.CreatedAt));

        foreach (var deck in initialData.Decks)
            data.Decks.Add(BuildDeck(deck));

        foreach (var score in master.LiveScoreSeeds)
        {
            data.LiveScore.Add(new LiveScore
            {
                MusicId = score.MusicId,
                Difficulty = score.Difficulty
            });
        }

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            data.LiveMusicReward.Add(new LiveMusicReward
            {
                MusicId = musicId
            });
        }

        foreach (var characterId in InitialCharacterIds)
        {
            data.CharacterRank.Add(new CharacterRank
            {
                CharacterId = characterId,
                Exp = 1
            });
        }

        foreach (var musicId in master.DefaultUnlockedLiveMusicIds)
        {
            foreach (var difficulty in master.LiveComboRewardDifficulties)
            {
                data.LiveMusicComboReward.Add(new LiveMusicComboReward
                {
                    MusicId = musicId,
                    Difficulty = difficulty
                });
            }
        }

        foreach (var masterId in player.ShownCarouselHelpIds.Keys.Order())
            data.ShownCarouselHelps.Add(new CarouselHelp { MasterId = masterId });

        foreach (var masterId in player.ShownContentUnlockIds.Keys.Order())
            data.ShownContentUnlocks.Add(new ContentUnlock { MasterId = masterId });

        return data;
    }

    private static PlayerSimpleProfile BuildSimpleProfile(
        PlayerRecord player,
        InitialPlayerData initialData,
        InitialDeckSeed? mainDeck)
    {
        var profile = new PlayerSimpleProfile
        {
            Id = player.PlayerId,
            Name = player.DisplayName,
            LastUpdatedAt = UnixSeconds(player.CreatedAt),
            ProfileId = player.ProfileId
        };

        var requestedMemberCardId = player.FavoriteMemberCardId;
        var displayMemberCardId = requestedMemberCardId > 0
            ? requestedMemberCardId
            : mainDeck?.MemberCardIds.FirstOrDefault() ?? 0;
        if (displayMemberCardId < 1 || displayMemberCardId > initialData.MemberCards.Count)
            displayMemberCardId = initialData.MemberCards.Count > 0 ? 1 : 0;

        if (displayMemberCardId > 0)
        {
            var seed = initialData.MemberCards[(int)displayMemberCardId - 1];
            profile.FavoriteMemberCard = BuildDeckMemberCardDetail(seed);
            if (requestedMemberCardId > 0 && requestedMemberCardId <= initialData.MemberCards.Count)
                profile.FavoriteMemberCardMasterId = requestedMemberCardId;
        }

        return profile;
    }

    private static MemberCard BuildMemberCard(int id, InitialMemberCardSeed seed, DateTimeOffset gainAt) => new()
    {
        Id = id,
        MasterId = seed.MasterId,
        Exp = seed.Exp,
        AwakeCount = seed.AwakeCount,
        CardRank = seed.CardRank,
        LeaderSkillLevel = seed.LeaderSkillLevel,
        LiveSkillLevel = seed.LiveSkillLevel,
        PerformanceSkillLevel = seed.PerformanceSkillLevel,
        GainAt = UnixSeconds(gainAt),
        LinkSkillLevel = seed.LinkSkillLevel,
        GekisouSkillLevel = seed.GekisouSkillLevel
    };

    private static DeckMemberCardDetail BuildDeckMemberCardDetail(InitialMemberCardSeed seed) => new()
    {
        CardId = seed.MasterId,
        Exp = seed.Exp,
        AwakeCount = seed.AwakeCount,
        CardRank = seed.CardRank,
        LiveSkillLevel = seed.LiveSkillLevel,
        PerformanceSkillLevel = seed.PerformanceSkillLevel
    };

    private static SupportCard BuildSupportCard(int id, InitialSupportCardSeed seed, DateTimeOffset gainAt) => new()
    {
        Id = id,
        MasterId = seed.MasterId,
        Exp = seed.Exp,
        CardRank = seed.CardRank,
        GainAt = UnixSeconds(gainAt),
        DuplicateCount = seed.DuplicateCount
    };

    private static Deck BuildDeck(InitialDeckSeed deck)
    {
        var result = new Deck
        {
            Id = deck.Id,
            Name = deck.Name
        };

        var cardCount = Math.Min(deck.MemberCardIds.Count, deck.SupportCardIds.Count);
        for (var i = 0; i < cardCount; i++)
        {
            result.Cards.Add(new DeckCard
            {
                SlotIndex = i,
                PerformanceOrderIndex = i,
                MemberCardId = deck.MemberCardIds[i],
                SupportCardId = deck.SupportCardIds[i]
            });
        }

        if (cardCount > 0)
            result.TotalPower = 1000;

        return result;
    }

    private static long UnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();
}

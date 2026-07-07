using MyNotes.Models;

namespace MyNotes.Services;

public sealed class HomeSnapshotService
{
    public HomeSnapshot GetFor(PlayerRecord _) => new()
    {
        HasNotification = true,
        HasFriends = true,
        HasInvitation = true,
        InvitationProfiles = 0
    };
}

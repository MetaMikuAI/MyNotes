namespace MyNotes.Models;

public sealed class HomeSnapshot
{
    public bool HasNotification { get; init; }
    public bool HasFriends { get; init; }
    public bool HasInvitation { get; init; }
    public int InvitationProfiles { get; init; }
}

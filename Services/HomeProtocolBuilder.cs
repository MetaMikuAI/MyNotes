using App.Protobuf.Entity;
using App.Protobuf.Home;
using MyNotes.Models;

namespace MyNotes.Services;

public sealed class HomeProtocolBuilder
{
    public GetHomeResponse Get(HomeSnapshot snapshot)
    {
        var response = new GetHomeResponse
        {
            Notification = snapshot.HasNotification ? new Notification() : null,
            Friends = snapshot.HasFriends ? new Friends() : null
        };

        for (var i = 0; i < snapshot.InvitationProfiles; i++)
            response.Invitations.Add(new InvitationProfile());

        return response;
    }
}

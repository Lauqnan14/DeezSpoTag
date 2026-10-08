using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Hubs;

/// <summary>
///     Realtime channel for the Soulseek integration.
/// </summary>
/// <remarks>
///     An empty marker hub, matching <see cref="ActivitiesHub"/> and the other hubs in this app. Everything is
///     published server-side through <c>ISoulseekRealtimePublisher</c>; there are no client-to-server methods.
/// </remarks>
[Authorize]
public sealed class SoulseekHub : Hub
{
}

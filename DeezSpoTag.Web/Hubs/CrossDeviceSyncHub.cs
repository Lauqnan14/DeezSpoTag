using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DeezSpoTag.Web.Hubs;

[Authorize]
public sealed class CrossDeviceSyncHub : Hub
{
}

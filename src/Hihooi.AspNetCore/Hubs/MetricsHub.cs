using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Hihooi.AspNetCore.Hubs;

[Authorize(AuthenticationSchemes = DashboardAuthConstants.Scheme)]
public sealed class MetricsHub : Hub
{
}

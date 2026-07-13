using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Dashboard;

/// <summary>Browser-only SignalR hub that streams operations activity.</summary>
public sealed class DashboardHub : Hub;

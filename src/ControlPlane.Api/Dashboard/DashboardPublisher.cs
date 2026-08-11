using Microsoft.AspNetCore.SignalR;
using ControlPlane.Api.Chat;

namespace ControlPlane.Api.Dashboard;

/// <summary>Persists each event in memory before broadcasting it to dashboards.</summary>
public sealed class DashboardPublisher(ActivityStore store, IHubContext<DashboardHub> hub)
{
    /// <summary>Adds and broadcasts an event using its type as the SignalR method name.</summary>
    public async Task PublishAsync(DashboardActivity activity, CancellationToken cancellationToken = default)
    {
        store.Add(activity);
        await hub.Clients.All.SendAsync(activity.Type, activity, cancellationToken);
    }

    public Task PublishHelpdeskAsync(HelpdeskChange change,
        CancellationToken cancellationToken = default) =>
        hub.Clients.All.SendAsync("helpdesk.changed", change, cancellationToken);
}

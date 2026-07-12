using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Dashboard;

public sealed class DashboardPublisher(ActivityStore store, IHubContext<DashboardHub> hub)
{
    public async Task PublishAsync(DashboardActivity activity, CancellationToken cancellationToken = default)
    {
        store.Add(activity);
        await hub.Clients.All.SendAsync(activity.Type, activity, cancellationToken);
    }
}

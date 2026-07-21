using ControlPlane.Api.Dashboard;
using EndpointAgents.Shared.Support;
using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Support;

public sealed class SupportRequestPublisher(
    SupportRequestStore store,
    IHubContext<DashboardHub> dashboard)
{
    public async Task<SupportRequestReceipt> PublishAsync(SupportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!store.TryAdd(request, out var rejection))
            return new SupportRequestReceipt(string.Empty, request.ReceivedAtUtc, false, rejection!);

        await dashboard.Clients.All.SendAsync("support.requested", request, cancellationToken);
        return new SupportRequestReceipt(request.RequestId, request.ReceivedAtUtc, true,
            "Your helpdesk request was received.");
    }
}

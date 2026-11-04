using ControlPlane.Api.Agents;
using ControlPlane.Api.Problems.Forwarding;
using ControlPlane.Api.Security;
using ControlPlane.Api.Updates;
using EndpointAgents.Shared.Problems;
using EndpointAgents.Shared.Rpc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Problems;

public sealed record MarkFixedRequest(string? Version);

public sealed record LogServerSettingsRequest(
    bool Enabled, string? Type, string? Host, int Port, string? Protocol, string? Token, string? Index,
    string? CertificateThumbprint);

/// <summary>The Problems page, the log server settings, and the two narrow routes PCs may reach.</summary>
public static partial class ProblemEndpoints
{
    private const int MaxProblems = 500;
    private const int MaxInstallBodyBytes = 4 * 1024;
    private static readonly TimeSpan BundleUploadWindow = TimeSpan.FromMinutes(15);

    public static void MapProblemEndpoints(this WebApplication app)
    {
        var installLimits = new InstallReportLimits(app.Services.GetRequiredService<TimeProvider>());

        app.MapGet("/api/problems", async (string? status, string? kind, string? version, string? pc, string? os,
            string? model, ProblemStore store, ProblemRecorder recorder, LogForwarder forwarder,
            CancellationToken cancellationToken) =>
        {
            var problems = await store.ListProblemsAsync(MaxProblems, cancellationToken);
            var occurrences = await store.ListOccurrencesAsync(null, cancellationToken);
            var fleet = await store.ListDevicesAsync(cancellationToken);
            return Results.Ok(new
            {
                problems = ProblemViews.Build(problems, occurrences, fleet,
                    new ProblemFilter(status, kind, version, pc, os, model)),
                filters = new
                {
                    kinds = problems.Select(item => item.Kind).Distinct().Order().ToArray(),
                    versions = occurrences.Select(item => item.AgentVersion).OfType<string>().Distinct()
                        .OrderByDescending(item => item, Comparer<string>.Create(ProblemText.CompareVersions)).ToArray(),
                    windows = occurrences.Select(item => ProblemViews.WindowsLabel(item.OsName, item.OsVersion))
                        .Where(item => item.Length > 0).Distinct().Order().ToArray(),
                    models = occurrences.Select(item => ProblemViews.ModelLabel(item.Manufacturer, item.Model))
                        .Where(item => item.Length > 0).Distinct().Order().ToArray()
                },
                storage = new
                {
                    databaseBytes = store.DatabaseBytes(),
                    diskLow = recorder.DiskLow,
                    droppedByLimit = recorder.DroppedByLimit,
                    droppedDiskFull = recorder.DroppedDiskFull
                },
                logServer = forwarder.GetStatus()
            });
        }).RequireAuthorization(OperatorPolicies.Reviewer);

        app.MapGet("/api/problems/{fingerprint}", async (string fingerprint, ProblemStore store,
            CancellationToken cancellationToken) =>
        {
            var problem = await store.GetProblemAsync(fingerprint, cancellationToken);
            if (problem is null) return Results.NotFound(new { message = "That problem is no longer kept." });
            var occurrences = await store.ListOccurrencesAsync(fingerprint, cancellationToken);
            return Results.Ok(new
            {
                problem,
                occurrences,
                developerText = ProblemViews.DeveloperText(problem, occurrences)
            });
        }).RequireAuthorization(OperatorPolicies.Reviewer);

        app.MapPost("/api/problems/{fingerprint}/fixed", async (string fingerprint, MarkFixedRequest request,
            HttpContext context, ProblemStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var version = request.Version?.Trim() ?? "";
            if (!VersionText().IsMatch(version))
                return Results.BadRequest(new { message = "Enter the version that has the fix, such as 0.4.0." });
            return await store.MarkFixedAsync(fingerprint, version, Operator(context), clock.GetUtcNow(), cancellationToken)
                ? Results.NoContent() : Results.NotFound(new { message = "That problem is no longer kept." });
        }).RequireAuthorization(OperatorPolicies.Reviewer);

        app.MapPost("/api/problems/{fingerprint}/reopen", async (string fingerprint, ProblemStore store,
            TimeProvider clock, CancellationToken cancellationToken) =>
            await store.ReopenAsync(fingerprint, clock.GetUtcNow(), cancellationToken)
                ? Results.NoContent() : Results.NotFound(new { message = "That problem is no longer kept." }))
            .RequireAuthorization(OperatorPolicies.Reviewer);

        app.MapGet("/api/problems/pcs", async (ProblemStore store, AgentUpdateStore updates, AgentRegistry registry,
            Microsoft.Extensions.Options.IOptions<ProblemOptions> options, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var online = registry.GetOnlineAgents().ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
            var settings = await updates.GetSettingsAsync(cancellationToken);
            var facts = (await store.ListDevicesAsync(cancellationToken))
                .ToDictionary(device => device.AgentId, StringComparer.OrdinalIgnoreCase);
            var rows = (await updates.GetDevicesAsync(cancellationToken)).Select(device =>
            {
                facts.TryGetValue(device.AgentId, out var known);
                online.TryGetValue(device.AgentId, out var live);
                return new FleetPc(device.AgentId,
                    live?.MachineName ?? (device.MachineName.Length > 0 ? device.MachineName : known?.Facts.MachineName ?? ""),
                    live is not null, live?.Version ?? device.CurrentVersion ?? known?.Facts.AgentVersion,
                    Latest(device.LastCheckUtc, known?.LastContactUtc), known?.Facts);
            }).ToArray();
            var bundles = await store.ListBundlesAsync(cancellationToken);
            return Results.Ok(new
            {
                quiet = FleetViews.Quiet(rows, clock.GetUtcNow(), TimeSpan.FromHours(options.Value.QuietAfterHours)),
                oldVersion = FleetViews.OnOldVersion(rows, settings),
                all = rows,
                bundles
            });
        }).RequireAuthorization(OperatorPolicies.Reviewer);

        // "Get logs": a fixed, catalogued collection on one PC. It takes no script and no path.
        app.MapPost("/api/problems/pcs/{agentId}/logs", async (string agentId, HttpContext context, ProblemStore store,
            AgentRegistry registry, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var agent = registry.GetOnlineAgents().FirstOrDefault(item =>
                item.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null) return Results.Conflict(new { message = "That PC is offline. Try again when it is online." });
            var bundleId = Guid.NewGuid().ToString("N");
            await store.CreateBundleAsync(new LogBundle(bundleId, agent.AgentId, agent.MachineName, Operator(context),
                clock.GetUtcNow(), LogBundleStatuses.Requested, 0, null, null), cancellationToken);
            var request = RpcRequest.Create(bundleId, AgentLogRoutes.CollectMethod,
                JsonSerializer.SerializeToElement(new { requestId = bundleId }));
            var response = await registry.SendCommandAsync(agent.AgentId, request, cancellationToken);
            if (response.Error is not null)
            {
                await store.CompleteBundleAsync(bundleId, LogBundleStatuses.Failed, 0,
                    ProblemText.Limit(ProblemText.Scrub(response.Error.Message), 300), clock.GetUtcNow(), cancellationToken);
                return Results.UnprocessableEntity(new { message = "The PC could not collect its logs: " + response.Error.Message });
            }
            return Results.Ok(await store.GetBundleAsync(bundleId, cancellationToken));
        }).RequireAuthorization(OperatorPolicies.Admin);

        app.MapGet("/api/problems/logs/{bundleId}/download", async (string bundleId, ProblemStore store,
            CancellationToken cancellationToken) =>
        {
            if (!BundleId().IsMatch(bundleId)) return Results.NotFound();
            var bundle = await store.GetBundleAsync(bundleId, cancellationToken);
            var path = store.BundlePath(bundleId);
            if (bundle is not { Status: LogBundleStatuses.Ready } || !File.Exists(path))
                return Results.NotFound(new { message = "Those logs are no longer kept." });
            var name = SafeFileName().Replace(bundle.MachineName, "_");
            return Results.File(path, "application/zip", $"logs-{name}-{bundle.RequestedAtUtc:yyyyMMdd-HHmm}.zip");
        }).RequireAuthorization(OperatorPolicies.Admin);

        // A PC uploads the logs an admin asked for, with its device token, and only for that request.
        app.MapPut(AgentLogRoutes.Root + "/{bundleId}", async (string bundleId, HttpContext context, ProblemStore store,
            ProblemRecorder recorder, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var agentId = context.User.FindFirst(AgentAuthentication.AgentIdClaim)?.Value;
            if (agentId is null || !BundleId().IsMatch(bundleId)) return Results.NotFound();
            var bundle = await store.GetBundleAsync(bundleId, cancellationToken);
            var now = clock.GetUtcNow();
            if (bundle is not { Status: LogBundleStatuses.Requested }
                || !bundle.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                || now - bundle.RequestedAtUtc > BundleUploadWindow)
                return Results.NotFound();
            if (recorder.DiskLow) return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
            if (context.Request.ContentLength > AgentLogRoutes.MaxBundleBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var path = store.BundlePath(bundleId);
            var size = await SaveCappedAsync(context.Request.Body, path, cancellationToken);
            if (size is null || !await IsZipAsync(path, cancellationToken))
            {
                File.Delete(path);
                await store.CompleteBundleAsync(bundleId, LogBundleStatuses.Failed, 0,
                    size is null ? "The logs were over the size limit." : "The upload was not a zip file.", now, cancellationToken);
                return Results.BadRequest(new { message = "The upload was refused." });
            }
            await store.CompleteBundleAsync(bundleId, LogBundleStatuses.Ready, size.Value, null, now, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(OperatorPolicies.AgentConnection);

        // An installer that failed has no agent and no token yet, so this one route is open. It takes
        // one small fixed-format report, limited per address and in total, and can only add a row.
        app.MapPost("/api/install-reports", async (HttpContext context, ProblemRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            if (context.Request.ContentLength is null or > MaxInstallBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!installLimits.Allow(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            InstallFailureReport? report;
            try { report = await context.Request.ReadFromJsonAsync<InstallFailureReport>(cancellationToken); }
            catch (JsonException) { return Results.BadRequest(); }
            if (report is null || !InstallStep().IsMatch(report.Step ?? "")
                || !MachineText().IsMatch(report.MachineName ?? ""))
                return Results.BadRequest();
            await recorder.RecordInstallFailureAsync(report);
            return Results.Accepted();
        }).AllowAnonymous();

        app.MapGet("/api/settings/log-server", async (LogServerSettingsStore settings, LogForwarder forwarder,
            CancellationToken cancellationToken) => Results.Ok(new
            {
                settings = await settings.GetAsync(cancellationToken),
                hasToken = await settings.HasTokenAsync(cancellationToken),
                status = forwarder.GetStatus()
            })).RequireAuthorization(OperatorPolicies.Admin);

        app.MapPut("/api/settings/log-server", async (LogServerSettingsRequest request, HttpContext context,
            LogServerSettingsStore settings, LogForwarder forwarder, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var next = ToSettings(request) with { UpdatedBy = Operator(context), UpdatedAtUtc = clock.GetUtcNow() };
            // An empty box keeps the saved token; it was never sent to the page.
            var newToken = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
            var hasToken = newToken is not null || await settings.HasTokenAsync(cancellationToken);
            var problem = TokenProblem(newToken) ?? LogServerValidation.Problem(next, hasToken);
            // Off with nothing filled in is the normal starting state.
            if (problem is not null && (next.Enabled || next.Host.Length > 0))
                return Results.BadRequest(new { message = problem });
            await settings.SaveAsync(next, newToken, cancellationToken);
            await forwarder.ReloadAsync(cancellationToken);
            return Results.Ok(new
            {
                settings = next,
                hasToken,
                status = forwarder.GetStatus()
            });
        }).RequireAuthorization(OperatorPolicies.Admin);

        app.MapPost("/api/settings/log-server/test", async (LogServerSettingsRequest request,
            LogServerSettingsStore settings, LogForwarder forwarder, CancellationToken cancellationToken) =>
        {
            var draft = ToSettings(request);
            var token = string.IsNullOrWhiteSpace(request.Token)
                ? await settings.GetTokenAsync(cancellationToken) : request.Token.Trim();
            var problem = TokenProblem(request.Token?.Trim()) ?? LogServerValidation.Problem(draft, token is not null);
            if (problem is not null) return Results.BadRequest(new { message = problem });
            var failure = await forwarder.TestAsync(draft, token, cancellationToken);
            return Results.Ok(new { ok = failure is null, message = failure ?? "The log server accepted the test message." });
        }).RequireAuthorization(OperatorPolicies.Admin);
    }

    private static string? TokenProblem(string? token) =>
        token is { Length: > LogServerValidation.MaxTokenCharacters } || token?.Any(char.IsControl) == true
            ? "The token does not look right." : null;

    private static LogServerSettings ToSettings(LogServerSettingsRequest request) => new(
        request.Enabled, request.Type?.Trim() ?? "", request.Host?.Trim() ?? "", request.Port,
        request.Protocol?.Trim().ToLowerInvariant() ?? "",
        string.IsNullOrWhiteSpace(request.Index) ? null : request.Index.Trim(),
        LogServerValidation.NormalizeThumbprint(request.CertificateThumbprint) ?? NullIfEmpty(request.CertificateThumbprint));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<long?> SaveCappedAsync(Stream body, string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[81_920];
        await using (var file = File.Create(path))
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > AgentLogRoutes.MaxBundleBytes) return null;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        return total;
    }

    private static async Task<bool> IsZipAsync(string path, CancellationToken cancellationToken)
    {
        var head = new byte[2];
        await using var file = File.OpenRead(path);
        return await file.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, cancellationToken) == 2
            && head[0] == (byte)'P' && head[1] == (byte)'K';
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private static string Operator(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.Identity.Name is { Length: > 0 } name
            ? name : "operator";

    [GeneratedRegex(@"^[0-9]{1,6}(\.[0-9]{1,6}){0,3}$")]
    private static partial Regex VersionText();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex BundleId();

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex SafeFileName();

    [GeneratedRegex(@"^[A-Za-z0-9 ._\-]{1,60}$")]
    private static partial Regex InstallStep();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,63}$")]
    private static partial Regex MachineText();
}

public sealed record FleetPc(string AgentId, string MachineName, bool Online, string? Version,
    DateTimeOffset? LastContactUtc, ProblemDeviceFacts? Facts);

public static class FleetViews
{
    /// <summary>Offline, with no contact for longer than <paramref name="after"/>, or never heard from.</summary>
    public static IReadOnlyList<FleetPc> Quiet(IEnumerable<FleetPc> pcs, DateTimeOffset now, TimeSpan after) =>
        pcs.Where(pc => !pc.Online && (pc.LastContactUtc is not { } seen || now - seen > after))
            .OrderBy(pc => pc.LastContactUtc ?? DateTimeOffset.MinValue).ToArray();

    /// <summary>
    /// Behind the version set on the Agent updates page (pilot PCs: the pilot version). With no
    /// version set, behind the newest version seen in the fleet.
    /// </summary>
    public static IReadOnlyList<FleetPc> OnOldVersion(IReadOnlyCollection<FleetPc> pcs, AgentUpdateSettings settings)
    {
        var newest = pcs.Select(pc => pc.Version).OfType<string>()
            .OrderByDescending(version => version, Comparer<string>.Create(ProblemText.CompareVersions)).FirstOrDefault();
        return pcs.Where(pc =>
        {
            if (pc.Version is null) return false;
            var pilot = settings.PilotVersion is not null && settings.PilotAgentIds.Contains(pc.AgentId, StringComparer.OrdinalIgnoreCase);
            var wanted = pilot ? settings.PilotVersion : settings.TargetVersion ?? newest;
            return wanted is not null && ProblemText.CompareVersions(pc.Version, wanted) < 0;
        }).OrderBy(pc => pc.MachineName).ToArray();
    }
}

/// <summary>Limits the one open route: per address, and in total so many addresses cannot fill it.</summary>
internal sealed class InstallReportLimits(TimeProvider clock)
{
    private readonly HourlyLimiter _perAddress = new(10, clock);
    private readonly HourlyLimiter _total = new(300, clock);

    public bool Allow(string address) => _perAddress.Allow(address) && _total.Allow("all");
}

using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Chat;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ControlPlane.Api.Hermes;

public sealed class HermesRunWorker(
    ChatStore store,
    IHttpClientFactory httpClientFactory,
    HermesSkillInstructions skillInstructions,
    IOptions<HermesOptions> options,
    ILogger<HermesRunWorker> logger) : BackgroundService
{
    private readonly HermesOptions _options = options.Value;
    private readonly HashSet<Task> _activeRuns = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Hermes run processing is disabled.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        logger.LogInformation("Hermes run processing enabled with a limit of {RunLimit}.",
            _options.MaxConcurrentRuns);
        while (!stoppingToken.IsCancellationRequested)
        {
            _activeRuns.RemoveWhere(task => task.IsCompleted);
            while (_activeRuns.Count < _options.MaxConcurrentRuns)
            {
                var lease = await store.LeaseNextRunAsync(_options.MaxHistoryMessages,
                    TimeSpan.FromSeconds(_options.RunTimeoutSeconds + 60), stoppingToken);
                if (lease is null) break;
                _activeRuns.Add(ProcessRunAsync(lease, stoppingToken));
            }

            if (_activeRuns.Count == 0)
            {
                await Task.Delay(500, stoppingToken);
                continue;
            }

            var delay = Task.Delay(500, stoppingToken);
            await Task.WhenAny(_activeRuns.Append(delay));
        }

        await Task.WhenAll(_activeRuns);
    }

    private async Task ProcessRunAsync(ChatRunLease lease, CancellationToken stoppingToken)
    {
        string? frameworkRunId = null;
        var hermesSessionId = "endpoint-run-" + lease.RunId;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RunTimeoutSeconds));
            var client = httpClientFactory.CreateClient("Hermes");
            frameworkRunId = await StartRunAsync(
                client, lease, hermesSessionId, timeout.Token);
            await store.SetFrameworkRunIdAsync(lease.RunId, frameworkRunId, timeout.Token);

            while (true)
            {
                var run = await GetRunStatusAsync(client, frameworkRunId, timeout.Token);
                var status = run.Status;
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(run.Output))
                        throw new InvalidDataException("Hermes completed without a response.");
                    var safeOutput = PromptRedactor.Redact(run.Output, lease.Capability,
                        lease.AgentId, lease.UserSid, lease.UserName, lease.MachineName);
                    await store.CompleteRunAsync(lease.RunId, safeOutput, stoppingToken);
                    return;
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(run.Error
                        ?? $"Hermes run ended as {status}.");

                await Task.Delay(_options.PollIntervalMilliseconds, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await StopFrameworkRunBestEffortAsync(frameworkRunId);
            await store.FailRunAsync(lease.RunId, "Hermes run timed out.", CancellationToken.None);
            logger.LogWarning("Hermes run {RunId} timed out.", lease.RunId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await StopFrameworkRunBestEffortAsync(frameworkRunId);
            await store.RequeueRunAsync(lease.RunId, CancellationToken.None);
            logger.LogInformation(
                "Hermes run {RunId} was requeued during ControlPlane shutdown.",
                lease.RunId);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            await StopFrameworkRunBestEffortAsync(frameworkRunId);
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (await store.TryRequeueTransientRunAsync(
                    lease.RunId, maxAttempts: 3, CancellationToken.None))
            {
                logger.LogWarning(ex,
                    "Transient Hermes failure requeued ControlPlane run {RunId}.",
                    lease.RunId);
            }
            else
            {
                await store.FailRunAsync(lease.RunId,
                    PromptRedactor.Redact(ex.Message, lease.Capability, lease.AgentId,
                        lease.UserSid, lease.UserName, lease.MachineName),
                    CancellationToken.None);
                logger.LogError(ex,
                    "Hermes run {RunId} exhausted transient retries.", lease.RunId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await store.FailRunAsync(lease.RunId,
                PromptRedactor.Redact(ex.Message, lease.Capability, lease.AgentId,
                    lease.UserSid, lease.UserName, lease.MachineName),
                CancellationToken.None);
            logger.LogError(ex, "Hermes run {RunId} failed.", lease.RunId);
        }
        finally
        {
            await DeleteHermesSessionBestEffortAsync(hermesSessionId);
        }
    }

    private async Task<string> StartRunAsync(HttpClient client, ChatRunLease lease,
        string hermesSessionId, CancellationToken cancellationToken)
    {
        var history = lease.History.Select(message => new
        {
            role = message.Role switch
            {
                ChatMessageRole.Assistant => "assistant",
                ChatMessageRole.System => "system",
                _ => "user"
            },
            content = PromptRedactor.Redact(message.Body, lease.AgentId,
                lease.UserSid, lease.UserName, lease.MachineName)
        }).ToArray();
        var lastInput = history.LastOrDefault(item => item.role == "user")?.content
            ?? throw new InvalidDataException("The queued run has no user message.");
        var requestBody = new
        {
            input = lastInput,
            session_id = hermesSessionId,
            model = string.IsNullOrWhiteSpace(_options.Model) ? null : _options.Model,
            conversation_history = history.Take(Math.Max(0, history.Length - 1)),
            instructions =
                $"""
                You are the isolated EndpointAgents support agent. The reviewed skills below are
                authoritative for this run.
                Use only the ControlPlane MCP tools. Never use terminal, filesystem, browser,
                memory, delegation, cron, learning, or skill-management tools.
                Never ask for passwords, tokens, or endpoint identifiers. Never claim a diagnosis
                or action succeeded without tool evidence. Read-only diagnostics may run
                automatically. Any change must use propose_endpoint_action and wait for user consent.
                Reply in the user's language. For every MCP call pass this single-run capability:
                {lease.Capability}

                {skillInstructions.GetInstructions()}
                """
        };

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/runs")
                {
                    Content = JsonContent.Create(requestBody)
                };
                request.Headers.Add("Idempotency-Key", lease.RunId);
                request.Headers.Add("X-Hermes-Session-Id", hermesSessionId);
                request.Headers.Add("X-Hermes-Session-Key", "endpoint-run-" + lease.RunId);
                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("run_id", out var runId)
                    && !string.IsNullOrWhiteSpace(runId.GetString()))
                    return runId.GetString()!;
                throw new InvalidDataException("Hermes did not return a run ID.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
        throw lastError ?? new InvalidOperationException("Hermes run submission failed.");
    }

    private async Task<HermesRunStatus> GetRunStatusAsync(HttpClient client,
        string frameworkRunId, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(
                    $"/v1/runs/{Uri.EscapeDataString(frameworkRunId)}",
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStreamAsync(cancellationToken));
                var root = document.RootElement;
                return new HermesRunStatus(
                    root.TryGetProperty("status", out var status)
                        ? status.GetString()
                        : null,
                    root.TryGetProperty("output", out var output)
                        ? output.GetString()
                        : null,
                    root.TryGetProperty("error", out var error)
                        ? error.ToString()
                        : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("Hermes run polling failed.");
    }

    private async Task StopFrameworkRunBestEffortAsync(string? frameworkRunId)
    {
        if (string.IsNullOrWhiteSpace(frameworkRunId)) return;
        try
        {
            var client = httpClientFactory.CreateClient("Hermes");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.PostAsync(
                $"/v1/runs/{Uri.EscapeDataString(frameworkRunId)}/stop",
                content: null, timeout.Token);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning(
                    "Hermes rejected the stop request for framework run {FrameworkRunId} with {StatusCode}.",
                    frameworkRunId, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not stop Hermes framework run {FrameworkRunId}.",
                frameworkRunId);
        }
    }

    private async Task DeleteHermesSessionBestEffortAsync(string sessionId)
    {
        try
        {
            var client = httpClientFactory.CreateClient("Hermes");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.DeleteAsync(
                $"/api/sessions/{Uri.EscapeDataString(sessionId)}", timeout.Token);
            if (!response.IsSuccessStatusCode
                && response.StatusCode != global::System.Net.HttpStatusCode.NotFound)
                logger.LogWarning(
                    "Hermes rejected cleanup for isolated session {SessionId} with {StatusCode}.",
                    sessionId, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not remove isolated Hermes session {SessionId}.", sessionId);
        }
    }

    private sealed record HermesRunStatus(string? Status, string? Output, string? Error);
}

public static class HermesHttpClientRegistration
{
    public static IServiceCollection AddHermesHttpClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Hermes").Get<HermesOptions>()
            ?? new HermesOptions();
        services.AddHttpClient("Hermes", client =>
        {
            client.BaseAddress = new Uri(settings.BaseUrl, UriKind.Absolute);
            client.Timeout = Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        });
        return services;
    }
}

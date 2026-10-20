using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Files;
using ControlPlane.Api.Hermes;
using ControlPlane.Api.Tools;
using EndpointAgents.Shared.Actions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Actions;

public sealed record ApprovalOutcome(bool Accepted, string? Error, ApprovalRequest? Approval);

/// <summary>
/// Runs or declines the steps the agent sent for approval (the ItApproval tier, actions an
/// administrator switched to "needs approval", and removal packages). An approved action goes
/// through <see cref="ActionRunner"/> with every other check still applied; the user sees the
/// outcome as a helpdesk message in the same conversation.
/// </summary>
public sealed class ApprovalService(ActionStore store, ActionRunner runner, ChatStore chat,
    AgentRegistry registry, CommandCatalog catalog, FileArtifactRepository files,
    ILogger<ApprovalService> logger)
{
    public async Task<ApprovalOutcome> ApproveAsync(string approvalId, string decidedBy,
        string? note, CancellationToken cancellationToken)
    {
        var approval = await store.GetApprovalAsync(approvalId, cancellationToken);
        if (approval is null) return new(false, "Request not found.", null);
        if (LooksSecret(note))
            return new(false, NoSecretsMessage, approval);
        if (!await store.DecideApprovalAsync(approvalId, ApprovalStatus.Running, decidedBy, note,
                cancellationToken))
            return new(false, "Someone already decided this request.", approval);

        bool ok;
        string summary;
        if (approval.Kind == ApprovalKinds.Person)
        {
            // A person did the work outside the system; only their note reaches the user.
            var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await store.CompleteApprovalAsync(approvalId, ApprovalStatus.Done,
                cleanNote ?? "Done by the helpdesk.", cancellationToken);
            await NotifyAsync(approval,
                $"کارشناس پشتیبانی درخواست «{UserTitle(approval)}» را انجام داد."
                + (cleanNote is null ? string.Empty : $" توضیح: {cleanNote}"),
                cancellationToken);
            logger.LogInformation("Person request {ApprovalId} ({Item}) marked done by {Operator}",
                approvalId, approval.ItemId, decidedBy);
            return new(true, null, await store.GetApprovalAsync(approvalId, cancellationToken));
        }
        if (approval.Kind == ApprovalKinds.Package)
        {
            (ok, summary) = await EndpointSupportTools.RunApprovedPackageAsync(registry, catalog,
                files, approval.AgentId, approval.UserSid, approval.ItemId, cancellationToken);
        }
        else
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, string?>>(
                approval.ArgumentsJson) ?? [];
            var outcome = await runner.RunAsync(new ActionRunContext(approval.AgentId,
                    approval.UserSid, approval.ConversationId, ActionRunSources.Approval, decidedBy),
                approval.ItemId, arguments, userConfirmed: true, cancellationToken);
            ok = outcome.Accepted && outcome.Status is ActionStatus.Done or ActionStatus.NotNeeded
                or ActionStatus.Ok;
            summary = outcome.Error ?? outcome.Summary ?? outcome.Status?.ToString() ?? "No answer.";
        }

        await store.CompleteApprovalAsync(approvalId, ok ? ApprovalStatus.Done : ApprovalStatus.Failed,
            summary, cancellationToken);
        await NotifyAsync(approval, ok
            ? $"کارشناس پشتیبانی درخواست «{UserTitle(approval)}» را تأیید کرد و انجام شد."
            : $"کارشناس پشتیبانی درخواست «{UserTitle(approval)}» را تأیید کرد، اما انجام نشد. همکاران پشتیبانی پیگیری می‌کنند.",
            cancellationToken);
        logger.LogInformation("Approval {ApprovalId} ({Item}) approved by {Operator}: {Result}",
            approvalId, approval.ItemId, decidedBy, ok ? "done" : "failed");
        return new(true, null, await store.GetApprovalAsync(approvalId, cancellationToken));
    }

    public async Task<ApprovalOutcome> DeclineAsync(string approvalId, string decidedBy,
        string? note, CancellationToken cancellationToken)
    {
        var approval = await store.GetApprovalAsync(approvalId, cancellationToken);
        if (approval is null) return new(false, "Request not found.", null);
        if (LooksSecret(note))
            return new(false, NoSecretsMessage, approval);
        if (!await store.DecideApprovalAsync(approvalId, ApprovalStatus.Declined, decidedBy, note,
                cancellationToken))
            return new(false, "Someone already decided this request.", approval);
        await NotifyAsync(approval,
            $"کارشناس پشتیبانی درخواست «{UserTitle(approval)}» را انجام نداد."
            + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" توضیح: {note.Trim()}"),
            cancellationToken);
        return new(true, null, await store.GetApprovalAsync(approvalId, cancellationToken));
    }

    private const string NoSecretsMessage =
        "The note is shown in the user's chat, which the AI model also reads. Give keys and passwords to the user in person or by phone, never in the note.";

    /// <summary>Notes reach the chat (and so the model): refuse recovery keys and passwords.</summary>
    internal static bool LooksSecret(string? note) =>
        !string.IsNullOrWhiteSpace(note)
        && (Regex.IsMatch(note, @"\d{6}(?:[-\s]\d{6}){7}")
            || PromptRedactor.RedactSecrets(note) != note
            || Regex.IsMatch(note,
                @"(password|passwd|رمز|کلمه\s*عبور)\s*[:=]", RegexOptions.IgnoreCase));

    /// <summary>The Persian action name, plus the target label the helpdesk listed.</summary>
    private static string UserTitle(ApprovalRequest approval)
    {
        if (approval.Kind == ApprovalKinds.Person)
            return PersonRequestKinds.Find(approval.ItemId)?.TitleFa ?? approval.Title;
        if (approval.Kind != ApprovalKinds.Action
            || EndpointActionCatalog.Find(approval.ItemId) is not { } definition)
            return approval.Title;
        var colon = approval.Title.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? definition.NameFa : definition.NameFa + approval.Title[colon..];
    }

    private async Task NotifyAsync(ApprovalRequest approval, string message,
        CancellationToken cancellationToken)
    {
        if (approval.ConversationId is null) return;
        await chat.AppendHelpdeskNoticeAsync(approval.ConversationId, message, cancellationToken);
        await store.AppendConversationTraceAsync(approval.ConversationId, "approval",
            new { approval.ApprovalId, approval.Kind, approval.ItemId, message },
            cancellationToken);
    }
}

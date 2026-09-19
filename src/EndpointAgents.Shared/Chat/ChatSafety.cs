using System.Text;
using System.Text.Json;

namespace EndpointAgents.Shared.Chat;

public static class ChatLimits
{
    public const int MessageCharacters = 200;
}

public static class ChatActionReply
{
    private static readonly HashSet<string> Positive = new(StringComparer.Ordinal)
    {
        "yes", "yes please", "approve", "approved", "do it", "continue", "ok", "okay",
        "بله", "بله لطفا", "آره", "اره", "تایید", "تأیید", "انجام بده", "ادامه بده"
    };

    private static readonly HashSet<string> Negative = new(StringComparer.Ordinal)
    {
        "no", "reject", "cancel", "stop", "نه", "خیر", "رد", "لغو", "انجام نده"
    };

    public static bool TryParse(string? response, out bool approved)
    {
        var normalized = Normalize(response);
        if (Positive.Contains(normalized))
        {
            approved = true;
            return true;
        }
        if (Negative.Contains(normalized))
        {
            approved = false;
            return true;
        }
        approved = false;
        return false;
    }

    private static string Normalize(string? value)
    {
        var builder = new StringBuilder((value ?? string.Empty).Trim().ToLowerInvariant())
            .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک')
            .Replace('\u200c', ' ');
        var text = string.Join(' ', builder.ToString().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.TrimEnd('.', '!', '?', '؟', '،', ',', ';', '؛', ':');
    }
}

public static class ChatActionApproval
{
    public const string ParameterName = "_chatApprovalId";
    public const string ArtifactFileNameParameterName = "_approvedArtifactFileName";
    public const string ArtifactSha256ParameterName = "_approvedArtifactSha256";
    public const string PlaybookSha256ParameterName = "_approvedPlaybookSha256";

    public static bool TryRead(JsonElement? parameters, out string approvalId)
    {
        approvalId = string.Empty;
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(ParameterName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        var candidate = property.GetString();
        if (candidate is null || candidate.Length != 32
            || !Guid.TryParseExact(candidate, "N", out _))
            return false;
        approvalId = candidate;
        return true;
    }

    public static bool HasMarker(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(ParameterName, out _);

    public static bool MatchesRequest(JsonElement? parameters, string requestId) =>
        TryRead(parameters, out var approvalId)
        && string.Equals(approvalId, requestId, StringComparison.Ordinal);

    public static bool MatchesArtifact(JsonElement? parameters, string fileName,
        string sha256)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(ArtifactFileNameParameterName,
                out var fileNameProperty)
            || fileNameProperty.ValueKind != JsonValueKind.String
            || !value.TryGetProperty(ArtifactSha256ParameterName,
                out var sha256Property)
            || sha256Property.ValueKind != JsonValueKind.String)
            return false;
        return string.Equals(fileNameProperty.GetString(), fileName,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(sha256Property.GetString(), sha256,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesArtifactAndPlaybook(JsonElement? parameters,
        string fileName, string sha256, string playbookSha256)
    {
        if (!MatchesArtifact(parameters, fileName, sha256)
            || parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(PlaybookSha256ParameterName,
                out var playbookSha256Property)
            || playbookSha256Property.ValueKind != JsonValueKind.String)
            return false;
        return string.Equals(playbookSha256Property.GetString(), playbookSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsArtifactPin(string propertyName) =>
        string.Equals(propertyName, ArtifactFileNameParameterName,
            StringComparison.Ordinal)
        || string.Equals(propertyName, ArtifactSha256ParameterName,
            StringComparison.Ordinal)
        || string.Equals(propertyName, PlaybookSha256ParameterName,
            StringComparison.Ordinal);

    public static bool IsReserved(string propertyName) =>
        string.Equals(propertyName, ParameterName, StringComparison.Ordinal)
        || IsArtifactPin(propertyName);
}

public static class ControlPlaneActionAuthorization
{
    public const string ParameterName = "_controlPlaneAuthorizationId";

    public static bool HasMarker(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(ParameterName, out _);

    public static bool MatchesRequest(JsonElement? parameters, string requestId)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(ParameterName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        var candidate = property.GetString();
        return candidate is { Length: 32 }
            && Guid.TryParseExact(candidate, "N", out _)
            && string.Equals(candidate, requestId, StringComparison.Ordinal);
    }

    public static bool IsReserved(string propertyName) =>
        string.Equals(propertyName, ParameterName, StringComparison.Ordinal);
}

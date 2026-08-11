using System.Text.Json;
using EndpointAgents.Shared.Chat;

namespace ControlPlane.Api.Commands;

/// <summary>
/// Prevents secrets from entering command parameters and audit/activity data.
/// </summary>
public static class SensitiveCommandParameterGuard
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "accessToken",
        "refreshToken",
        "apiKey",
        "authorization",
        "cookie",
        "privateKey",
        "licenseKey",
        "productKey",
        "credential",
        "credentials"
    };

    public static bool ContainsSensitiveData(JsonElement? parameters) =>
        parameters is { } value && ContainsSensitiveData(value);

    public static bool ContainsReservedActionAuthorization(JsonElement? parameters) =>
        parameters is { } value && ContainsReservedActionAuthorization(value);

    private static bool ContainsSensitiveData(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (SensitiveNames.Contains(property.Name) || ContainsSensitiveData(property.Value))
                    return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (ContainsSensitiveData(item))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsReservedActionAuthorization(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                if (ChatActionApproval.IsReserved(property.Name)
                    || ControlPlaneActionAuthorization.IsReserved(property.Name)
                    || ContainsReservedActionAuthorization(property.Value))
                    return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (ContainsReservedActionAuthorization(item)) return true;
        }
        return false;
    }
}

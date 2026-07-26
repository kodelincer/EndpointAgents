using System.Text.Json;

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
}

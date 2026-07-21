using System.Text.Json;

namespace ControlPlane.Api.Commands;

/// <summary>
/// Prevents secrets from entering the unauthenticated command channel and its
/// in-memory dashboard activity history.
/// </summary>
public static class SensitiveCommandParameterGuard
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
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

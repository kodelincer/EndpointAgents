namespace EndpointAgents.Platform.Windows.Windows;

internal static class WindowsVersionInfo
{
    internal static string? NormalizeProductName(string? productName, string? build)
    {
        if (productName?.StartsWith("Windows 10 ", StringComparison.OrdinalIgnoreCase) == true
            && int.TryParse(build, out var buildNumber)
            && buildNumber >= 22000)
            return $"Windows 11{productName[10..]}";

        return productName;
    }
}

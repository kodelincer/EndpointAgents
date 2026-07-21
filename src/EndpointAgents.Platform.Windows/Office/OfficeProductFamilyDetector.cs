using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public static class OfficeProductFamilyDetector
{
    public static IReadOnlyList<string> Detect(OfficeInventory inventory)
    {
        var text = string.Join(' ', inventory.ClickToRun.Select(item => item.ProductReleaseIds)
            .Concat(inventory.Products.Select(item => item.DisplayName))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var families = new List<string>();

        if (text.Contains("O365", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase)
            || text.Contains("M365", StringComparison.OrdinalIgnoreCase))
            families.Add(OfficeProductFamilies.Microsoft365Apps);

        if (text.Contains("Volume", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LTSC", StringComparison.OrdinalIgnoreCase))
            families.Add(OfficeProductFamilies.VolumeLicensed);

        if (families.Count == 0)
            families.Add(OfficeProductFamilies.Unknown);
        return families;
    }
}

using EndpointAgents.Shared.Actions;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Health;

/// <summary>One answer an IT member can pick for a check, such as "10 GB" or "Print Spooler".</summary>
public sealed record HealthOption(string Value, string Label);

/// <summary>What one run of a check found.</summary>
public sealed record HealthFinding(bool Passed, string Text);

/// <summary>
/// One fixed check a health rule can use: which read-only (Look) action reads the PC, how its
/// answer is judged, the fixes allowed for it (catalogued SafeFix actions only), and the
/// fixed Tray notice for "tell the user". IT members only pick from these; nobody writes code
/// or scripts, and no AI is involved.
/// </summary>
public sealed record HealthCheckDefinition(
    string Id,
    string Name,
    string NameFa,
    string Description,
    string LookActionId,
    IReadOnlyList<HealthOption> Options,
    string? DefaultOption,
    string OptionLabel,
    IReadOnlyList<string> Fixes,
    string? NoticeId,
    Func<string?, IReadOnlyDictionary<string, string?>> LookArguments,
    Func<JsonNode?, string?, HealthFinding> Evaluate)
{
    public IReadOnlyDictionary<string, string?> FixArguments(string? option) =>
        LookActionId == "services.status" ? LookArguments(option) : new Dictionary<string, string?>();
}

public static class HealthCheckCatalog
{
    private static readonly IReadOnlyDictionary<string, string?> NoArguments = new Dictionary<string, string?>();

    public static IReadOnlyList<HealthCheckDefinition> All { get; } =
    [
        new("disk-space", "Low disk space on C:", "فضای کم درایو C",
            "Free space on the system drive is below the amount you pick.",
            "disk.usage", [new("5", "5 GB"), new("10", "10 GB"), new("20", "20 GB")], "10",
            "Fail when free space is below", ["disk.cleanup_safe"], UserNotices.DiskFull,
            _ => NoArguments, (data, option) =>
            {
                var limit = Number(option, 10);
                var drive = Array(data, "drives").FirstOrDefault(item =>
                    string.Equals(Text(item, "letter")?.TrimEnd(':', '\\'), "C", StringComparison.OrdinalIgnoreCase));
                var free = Number(drive, "freeGb");
                return free is null ? new(false, "Drive C: was not reported.")
                    : new(free >= limit, $"{free.Value.ToString("0.#", CultureInfo.InvariantCulture)} GB free on C:");
            }),
        new("restart-overdue", "Not restarted for days", "روزها بدون راه‌اندازی دوباره",
            "The PC has been running longer than the days you pick, or a restart for updates is waiting that long.",
            "restart.pending", [new("3", "3 days"), new("7", "7 days"), new("14", "14 days")], "7",
            "Fail when running longer than", [], UserNotices.RestartNeeded,
            _ => NoArguments, (data, option) =>
            {
                var days = Number(option, 7);
                var hours = Number(data, "uptimeHours") ?? 0;
                var pending = Bool(data, "pending") == true;
                var text = $"Running for {Math.Floor(hours / 24)} days{(pending ? ", restart pending" : "")}";
                return new(hours < days * 24, text);
            }),
        new("clock-sync", "Clock out of sync", "ساعت ناهماهنگ",
            "The PC clock differs from the domain controller (sign-in and Kerberos fail when it is off by minutes).",
            "time.status", [], null, "", ["time.resync"], null,
            _ => NoArguments, (data, _) => Bool(data, "clockOk") == false
                ? new(false, $"Clock off by {Number(data, "clockOffsetSeconds") ?? 0} s")
                : new(true, "Clock in sync")),
        new("time-zone", "Wrong time zone", "منطقهٔ زمانی نادرست",
            "The time zone differs from the standard on the Environment page.",
            "time.status", [], null, "", ["timezone.set_standard"], null,
            _ => NoArguments, (data, _) => Bool(data, "matchesStandardZone") == false
                ? new(false, $"Time zone {Text(data, "timeZone")}")
                : new(true, "Standard time zone")),
        new("defender-signatures", "Old antivirus definitions", "تعریف‌های قدیمی آنتی‌ویروس",
            "Microsoft Defender is off, or its definitions are older than the days you pick.",
            "security.status", [new("3", "3 days"), new("7", "7 days"), new("14", "14 days")], "7",
            "Fail when older than", ["defender.update_signatures"], null,
            _ => NoArguments, (data, option) =>
            {
                var defender = Child(data, "defender");
                if (defender is null) return new(false, "Defender did not answer");
                if (Bool(defender, "enabled") == false) return new(false, "Defender is off");
                var age = Number(defender, "signatureAgeDays");
                return age is null ? new(false, "Definition age unknown")
                    : new(age <= Number(option, 7), $"Definitions {age} days old");
            }),
        new("service-running", "Service stopped", "سرویس متوقف",
            "A Windows service from the fixed list is not running.",
            "services.status", [.. EndpointActionCatalog.RestartableServiceChoices.Select(choice =>
                new HealthOption(choice.Value, choice.Label))], "Spooler",
            "Service", ["service.restart"], null,
            option => new Dictionary<string, string?> { ["service"] = option ?? "Spooler" },
            (data, option) =>
            {
                var service = Array(data, "services").FirstOrDefault();
                var state = Text(service, "state") ?? "unknown";
                return new(string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase),
                    $"{option} is {state}");
            }),
        new("disk-failing", "Disk may fail", "احتمال خرابی دیسک",
            "A physical disk reports a health warning or predicts its own failure (SMART). Back up the user's files.",
            "disk.health", [], null, "", [], UserNotices.DiskFailing,
            _ => NoArguments, (data, _) => Bool(data, "predictsFailure") == true || Bool(data, "anyWarning") == true
                ? new(false, "A disk reports a warning") : new(true, "Disks healthy"))
    ];

    public static HealthCheckDefinition? Find(string? id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    private static JsonNode? Child(JsonNode? node, string name) =>
        node is JsonObject obj ? obj.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static IEnumerable<JsonNode?> Array(JsonNode? node, string name) =>
        Child(node, name) is JsonArray array ? array : [];

    private static string? Text(JsonNode? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? Bool(JsonNode? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static double? Number(JsonNode? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static double Number(string? option, double fallback) =>
        double.TryParse(option, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

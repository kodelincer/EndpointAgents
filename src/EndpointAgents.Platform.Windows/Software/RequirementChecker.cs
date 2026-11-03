using EndpointAgents.Shared.Packages;
using System.Globalization;

namespace EndpointAgents.Platform.Windows.Software;

/// <summary>Read-only facts about this PC that install requirements are checked against.</summary>
public interface IMachineFacts
{
    int WindowsBuild { get; }
    string EditionId { get; }
    /// <summary>x64, x86 or arm64.</summary>
    string Architecture { get; }
    long MemoryMb { get; }
    long FreeSystemDiskMb { get; }
    int CpuCores { get; }
    /// <summary>The largest attached screen, or null when Windows does not report one.</summary>
    (int Width, int Height)? Screen { get; }
    bool DomainJoined { get; }
    bool HasRuntime(string runtimeId);
    bool IsDetected(DetectionRule rule);
}

/// <summary>
/// Compares a package's signed requirements with this PC. Pure: every fact comes from
/// <see cref="IMachineFacts"/>, so the same rules are tested without a real PC.
/// </summary>
public static class RequirementChecker
{
    public static bool IsInstalled(SoftwareSpec spec, IMachineFacts facts) =>
        spec.Detection is { Count: > 0 } rules && rules.Any(facts.IsDetected);

    /// <param name="catalog">The published catalog, to find packages that provide a missing runtime.</param>
    public static RequirementReport Check(PackageDescriptor package, IMachineFacts facts,
        IReadOnlyList<PackageDescriptor> catalog)
    {
        var spec = package.Software ?? new SoftwareSpec();
        var results = new List<RequirementResult>();
        var missingRuntimes = new List<string>();
        var installFirst = new List<string>();
        var r = spec.Requirements;

        // The package's own Windows range, as before, then the typed rules.
        if (package.MinWindowsVersion is { } min && Version.TryParse(min, out var minVersion)
            && minVersion.Build > facts.WindowsBuild)
            results.Add(Fail("windows-build", $"build {minVersion.Build}", $"build {facts.WindowsBuild}",
                $"This PC runs Windows build {facts.WindowsBuild}; the software needs build {minVersion.Build} or newer."));
        if (package.MaxWindowsVersionExclusive is { } max && Version.TryParse(max, out var maxVersion)
            && facts.WindowsBuild >= maxVersion.Build)
            results.Add(Fail("windows-build", $"below build {maxVersion.Build}", $"build {facts.WindowsBuild}",
                $"This PC runs Windows build {facts.WindowsBuild}; the software only supports builds below {maxVersion.Build}."));

        if (r is not null)
        {
            if (r.MinWindowsBuild is { } build)
                results.Add(facts.WindowsBuild >= build
                    ? Pass("windows-build", $"build {build}", $"build {facts.WindowsBuild}")
                    : Fail("windows-build", $"build {build}", $"build {facts.WindowsBuild}",
                        $"This PC runs Windows build {facts.WindowsBuild}; the software needs build {build} or newer."));
            if (r.Editions is { Count: > 0 } editions)
                results.Add(editions.Contains(facts.EditionId, StringComparer.OrdinalIgnoreCase)
                    ? Pass("edition", string.Join(" or ", editions), facts.EditionId)
                    : Fail("edition", string.Join(" or ", editions), facts.EditionId,
                        $"This PC has Windows {facts.EditionId}; the software needs {string.Join(" or ", editions)}."));
            if (r.Architectures is { Count: > 0 } architectures)
                results.Add(architectures.Contains(facts.Architecture, StringComparer.OrdinalIgnoreCase)
                    ? Pass("architecture", string.Join(" or ", architectures), facts.Architecture)
                    : Fail("architecture", string.Join(" or ", architectures), facts.Architecture,
                        $"This PC is {facts.Architecture}; the software needs {string.Join(" or ", architectures)}."));
            if (r.MinMemoryMb is { } memory)
                results.Add(facts.MemoryMb >= memory * 95 / 100 // Windows reports a little less than the installed memory
                    ? Pass("memory", Size(memory), Size(facts.MemoryMb))
                    : Fail("memory", Size(memory), Size(facts.MemoryMb),
                        $"This PC has {Size(facts.MemoryMb)} of memory; the software needs {Size(memory)}."));
            if (r.MinFreeDiskMb is { } disk)
                results.Add(facts.FreeSystemDiskMb >= disk
                    ? Pass("disk", Size(disk), Size(facts.FreeSystemDiskMb))
                    : Fail("disk", Size(disk), Size(facts.FreeSystemDiskMb),
                        $"Drive C: has {Size(facts.FreeSystemDiskMb)} free; the software needs {Size(disk)}."));
            if (r.MinCpuCores is { } cores)
                results.Add(facts.CpuCores >= cores
                    ? Pass("cpu", $"{cores} cores", $"{facts.CpuCores} cores")
                    : Fail("cpu", $"{cores} cores", $"{facts.CpuCores} cores",
                        $"This PC has {facts.CpuCores} processor cores; the software needs {cores}."));
            if (r.MinScreenWidth is not null || r.MinScreenHeight is not null)
            {
                var needed = $"{r.MinScreenWidth ?? 0} x {r.MinScreenHeight ?? 0}";
                // A PC without a reported screen (remote session, no monitor) is not refused for it.
                if (facts.Screen is not { } screen)
                    results.Add(Pass("screen", needed, "not reported"));
                else
                    results.Add(screen.Width >= (r.MinScreenWidth ?? 0) && screen.Height >= (r.MinScreenHeight ?? 0)
                        ? Pass("screen", needed, $"{screen.Width} x {screen.Height}")
                        : Fail("screen", needed, $"{screen.Width} x {screen.Height}",
                            $"The screen is {screen.Width} x {screen.Height}; the software needs at least {needed}."));
            }
            if (r.DomainJoined is { } domain)
                results.Add(facts.DomainJoined == domain
                    ? Pass("domain", domain ? "domain joined" : "not domain joined", facts.DomainJoined ? "domain joined" : "not domain joined")
                    : Fail("domain", domain ? "domain joined" : "not domain joined", facts.DomainJoined ? "domain joined" : "not domain joined",
                        domain ? "This PC is not joined to the company domain; the software needs it."
                            : "This PC is joined to the domain; the software is only for PCs outside it."));
            foreach (var runtime in r.Runtimes ?? [])
            {
                if (facts.HasRuntime(runtime))
                {
                    results.Add(Pass("runtime", KnownRuntimes.Name(runtime), "installed"));
                    continue;
                }
                missingRuntimes.Add(runtime);
                var provider = catalog.FirstOrDefault(item => item.Software?.Provides == runtime
                    && item.PackageId != package.PackageId && item.Software.Install is { Kind: not InstallerKinds.Manual });
                if (provider is not null)
                {
                    if (!installFirst.Contains(provider.PackageId)) installFirst.Add(provider.PackageId);
                    results.Add(Pass("runtime", KnownRuntimes.Name(runtime),
                        $"missing; {provider.DisplayName} is installed first"));
                }
                else
                    results.Add(Fail("runtime", KnownRuntimes.Name(runtime), "missing",
                        $"{KnownRuntimes.Name(runtime)} is not installed and the catalog has no package for it."));
            }
            foreach (var conflict in r.MustNotBeInstalled ?? [])
                if (facts.IsDetected(conflict))
                    results.Add(Fail("conflict", "not installed", "installed",
                        $"{Describe(conflict)} is installed and must be removed first."));
        }

        return new RequirementReport(package.PackageId, results.All(result => result.Passed),
            IsInstalled(spec, facts), results, missingRuntimes, installFirst);
    }

    internal static string Size(long megabytes) => megabytes >= 1024
        ? (megabytes / 1024d).ToString(megabytes % 1024 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GB"
        : megabytes.ToString(CultureInfo.InvariantCulture) + " MB";

    internal static string Describe(DetectionRule rule) => rule.Kind switch
    {
        DetectionKinds.UninstallEntry => rule.DisplayName!,
        DetectionKinds.MsiProduct => $"The program {rule.ProductCode}",
        DetectionKinds.File => System.IO.Path.GetFileName(rule.Path ?? "a file"),
        _ => rule.FamilyName ?? "Another program"
    };

    private static RequirementResult Pass(string rule, string needed, string found) =>
        new(rule, true, needed, found, "OK");

    private static RequirementResult Fail(string rule, string needed, string found, string message) =>
        new(rule, false, needed, found, message);
}

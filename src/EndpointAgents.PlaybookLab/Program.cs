using ControlPlane.Api.Files;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Administrator authoring aid for BAT packages. It hashes, lints and validates a BAT
// and its playbook with the control plane's own rules, and with --execute runs it
// through the real endpoint runner as the current user (the BAT really runs).

var options = ParseArguments(args);
if (options is null)
{
    Console.Error.WriteLine("""
        Usage: PlaybookLab --package <packages\id folder> [--validate] [--fix]
                           [--execute] [--discover] [--env NAME=VALUE]...
               PlaybookLab --bat <file.bat> --playbook <playbook.json>
                           [--artifact-id <id>] [--execute] [--discover] [--env NAME=VALUE]...
          --validate  check the package exactly as the control plane does (default)
          --fix       write the current BAT, supporting-file and playbook hashes
                      into package.json (only after you reviewed the changes)
          --execute   run the BAT through the endpoint runner (it really runs!)
          --discover  with --execute, show the screen that follows the last step
        """);
    return 2;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
return options.PackageFolder is { } folder
    ? await RunPackageAsync(Path.GetFullPath(folder), options, json)
    : await RunLooseAsync(options, json);

static async Task<int> RunPackageAsync(string folder, LabOptions options,
    JsonSerializerOptions json)
{
    var manifestPath = Path.Combine(folder, FileArtifactRepository.PackageManifestFileName);
    var packagesRoot = Path.GetDirectoryName(folder)!;
    if (!File.Exists(manifestPath)
        || !Path.GetFileName(packagesRoot).Equals(
            FileArtifactRepository.PackagesDirectoryName, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Expected <FileRepository>\\packages\\<id>\\package.json, got {folder}.");
        return 1;
    }

    if (options.Fix) await FixHashesAsync(folder, manifestPath, json);

    var repository = new FileArtifactRepository(
        Options.Create(new FileRepositoryOptions
        {
            RootPath = Path.GetDirectoryName(packagesRoot)!, RequireProtectedAcl = false
        }),
        new HostingEnvironment { ContentRootPath = packagesRoot });
    var status = (await repository.GetPackageStatusesAsync(CancellationToken.None))
        .Single(item => item.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));

    Console.WriteLine($"Package:          {status.Id} ({status.Kind ?? "unknown kind"})");
    Console.WriteLine($"Status:           {status.Status}");
    foreach (var reason in status.Reasons) Console.WriteLine($"  FIX   {reason}");
    foreach (var warning in status.Warnings) Console.WriteLine($"  WARN  {warning}");
    if (status.ActualBatSha256 is not null)
        Console.WriteLine($"BAT SHA-256:      {status.ActualBatSha256}");
    if (status.ComputedPlaybookSha256 is not null)
        Console.WriteLine($"Playbook SHA-256: {status.ComputedPlaybookSha256}");
    var ok = BatPackageStatuses.IsPublished(status.Status);

    if (options.Execute)
    {
        var manifest = JsonSerializer.Deserialize<BatPackageManifest>(
            await File.ReadAllTextAsync(manifestPath), json)!;
        if (!BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out var playbook,
                out var error))
        {
            Console.WriteLine($"Cannot run: the playbook is invalid ({error}).");
            return 1;
        }
        var result = await ExecuteAsync(Path.Combine(folder, manifest.Bat!.FileName),
            playbook!, options);
        ok &= options.Discover || result;
    }
    return ok || options.Discover ? 0 : 1;
}

static async Task FixHashesAsync(string folder, string manifestPath,
    JsonSerializerOptions json)
{
    var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
    var manifest = node.Deserialize<BatPackageManifest>(json)!;
    if (manifest.Bat?.FileName is { Length: > 0 } batName
        && File.Exists(Path.Combine(folder, batName)))
        node["bat"]!["sha256"] = HashFile(Path.Combine(folder, batName));
    if (node["supportFiles"] is JsonArray files)
        foreach (var file in files.OfType<JsonObject>())
            if (file["fileName"]?.GetValue<string>() is { } name
                && File.Exists(Path.Combine(folder,
                    FileArtifactRepository.PackageSupportDirectoryName, name)))
                file["sha256"] = HashFile(Path.Combine(folder,
                    FileArtifactRepository.PackageSupportDirectoryName, name));
    if (BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out var playbook, out _))
        node["playbookSha256"] = BatchPlaybookPolicy.ComputeSha256(playbook!);
    await File.WriteAllTextAsync(manifestPath, node.ToJsonString(json) + Environment.NewLine);
    Console.WriteLine("Updated the hashes in package.json.");
}

static async Task<int> RunLooseAsync(LabOptions options, JsonSerializerOptions json)
{
    var ok = true;
    var batSha256 = HashFile(options.BatPath!);
    Console.WriteLine($"BAT SHA-256:      {batSha256}");
    var lint = BatScriptLint.Inspect(await File.ReadAllTextAsync(options.BatPath!));
    foreach (var error in lint.Errors) Console.WriteLine($"LINT REJECTS  {error}");
    foreach (var warning in lint.Warnings) Console.WriteLine($"lint warning  {warning}");
    ok &= lint.Errors.Count == 0;

    var playbook = JsonSerializer.Deserialize<BatchPlaybook>(
        await File.ReadAllTextAsync(options.PlaybookPath!), json);
    if (!BatchPlaybookPolicy.TryNormalize(playbook, out var normalized, out var policyError))
    {
        Console.WriteLine($"PLAYBOOK INVALID  {policyError}");
        return 1;
    }
    Console.WriteLine($"Playbook SHA-256: {BatchPlaybookPolicy.ComputeSha256(normalized!)}");
    if (options.Execute)
        ok &= await ExecuteAsync(options.BatPath!, normalized!, options) || options.Discover;
    return ok ? 0 : 1;
}

static async Task<bool> ExecuteAsync(string batPath, BatchPlaybook playbook,
    LabOptions options)
{
    if (options.Discover)
    {
        // A final step that never matches makes the runner stop and report the
        // next screen in its output tail.
        BatchPlaybookPolicy.TryNormalize(playbook with
        {
            Version = 2,
            Steps = [.. playbook.Steps, new BatchPlaybookStep(
                "__playbook_lab_discover__", "x", "key", 5)]
        }, out var discover, out _);
        playbook = discover!;
    }
    var startInfo = OfficeBugfixRunner.CreateStartInfo(Path.GetFullPath(batPath));
    foreach (var (name, value) in options.Environment)
        startInfo.Environment[name] = value;
    var result = await OfficeBugfixRunner.RunProcessAsync(startInfo, playbook,
        CancellationToken.None);
    Console.WriteLine($"Run state:        {result.State}");
    Console.WriteLine($"Exit code:        {result.ExitCode?.ToString() ?? "-"}");
    Console.WriteLine($"Responses sent:   {result.CompletedStepCount}");
    if (result.FailedStepIndex is { } failed)
        Console.WriteLine($"Waiting at step:  {failed}");
    if (result.OutputTail is { Length: > 0 } tail)
        Console.WriteLine($"Output tail:      {tail}");
    return result.State == OfficeBugfixRunStates.Completed;
}

static string HashFile(string path) =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static LabOptions? ParseArguments(string[] args)
{
    string? bat = null, playbook = null, package = null, artifactId = null;
    bool execute = false, discover = false, fix = false;
    var environment = new List<(string, string)>();
    for (var index = 0; index < args.Length; index++)
    {
        string? Next() => index + 1 < args.Length ? args[++index] : null;
        switch (args[index])
        {
            case "--bat": bat = Next(); break;
            case "--playbook": playbook = Next(); break;
            case "--package": package = Next(); break;
            case "--artifact-id": artifactId = Next(); break;
            case "--execute": execute = true; break;
            case "--discover": discover = true; break;
            case "--validate": break;
            case "--fix": fix = true; break;
            case "--env":
                var pair = Next()?.Split('=', 2);
                if (pair is not { Length: 2 }) return null;
                environment.Add((pair[0], pair[1]));
                break;
            default: return null;
        }
    }
    var valid = package is not null ? bat is null && playbook is null
        : bat is not null && playbook is not null && !fix;
    return !valid || discover && !execute ? null
        : new LabOptions(bat, playbook, package, artifactId, execute, discover, fix,
            environment);
}

sealed record LabOptions(string? BatPath, string? PlaybookPath, string? PackageFolder,
    string? ArtifactId, bool Execute, bool Discover, bool Fix,
    IReadOnlyList<(string Name, string Value)> Environment);

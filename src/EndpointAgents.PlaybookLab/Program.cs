using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using System.Security.Cryptography;
using System.Text.Json;

// Administrator authoring aid: hashes, lints and validates a BAT and its playbook,
// and with --execute runs it through the real endpoint runner as the current user.
// Execution really runs the BAT, so use a disposable VM for unknown scripts.

var options = ParseArguments(args);
if (options is null)
{
    Console.Error.WriteLine("""
        Usage: PlaybookLab --bat <file.bat> --playbook <playbook.json>
                           [--artifact-id <id>] [--execute] [--discover]
                           [--env NAME=VALUE]...
          --execute   run the BAT through the endpoint runner (it really runs!)
          --discover  with --execute, show the screen that follows the last step
        """);
    return 2;
}

var ok = true;
var batBytes = await File.ReadAllBytesAsync(options.BatPath);
var batSha256 = Convert.ToHexString(SHA256.HashData(batBytes));
Console.WriteLine($"BAT SHA-256:      {batSha256}");

var lint = BatScriptLint.Inspect(await File.ReadAllTextAsync(options.BatPath));
foreach (var error in lint.Errors) Console.WriteLine($"LINT REJECTS  {error}");
foreach (var warning in lint.Warnings) Console.WriteLine($"lint warning  {warning}");
ok &= lint.Errors.Count == 0;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var playbook = JsonSerializer.Deserialize<BatchPlaybook>(
    await File.ReadAllTextAsync(options.PlaybookPath), json);
if (!BatchPlaybookPolicy.TryNormalize(playbook, out var normalized, out var policyError))
{
    Console.WriteLine($"PLAYBOOK INVALID  {policyError}");
    return 1;
}
Console.WriteLine($"Playbook SHA-256: {BatchPlaybookPolicy.ComputeSha256(normalized!)}");

if (options.Execute)
{
    var run = normalized!;
    if (options.Discover)
    {
        // A final step that never matches makes the runner stop and report the
        // next screen in its output tail.
        run = run with
        {
            Version = 2,
            Steps = [.. run.Steps, new BatchPlaybookStep(
                "__playbook_lab_discover__", "x", "key", 5)]
        };
        BatchPlaybookPolicy.TryNormalize(run, out run, out _);
    }
    var startInfo = OfficeBugfixRunner.CreateStartInfo(Path.GetFullPath(options.BatPath));
    foreach (var (name, value) in options.Environment)
        startInfo.Environment[name] = value;
    var result = await OfficeBugfixRunner.RunProcessAsync(startInfo, run!,
        CancellationToken.None);
    Console.WriteLine($"Run state:        {result.State}");
    Console.WriteLine($"Exit code:        {result.ExitCode?.ToString() ?? "-"}");
    Console.WriteLine($"Responses sent:   {result.CompletedStepCount}");
    if (result.FailedStepIndex is { } failed)
        Console.WriteLine($"Waiting at step:  {failed}");
    if (result.OutputTail is { Length: > 0 } tail)
        Console.WriteLine($"Output tail:      {tail}");
    ok &= options.Discover || result.State == OfficeBugfixRunStates.Completed;
}

var artifactId = options.ArtifactId
    ?? Path.GetFileNameWithoutExtension(options.BatPath).ToLowerInvariant();
Console.WriteLine();
Console.WriteLine("Manifest entry draft (add compatibility fields before publishing):");
Console.WriteLine(JsonSerializer.Serialize(new
{
    artifactId,
    displayName = artifactId,
    description = "Administrator-reviewed repair.",
    fileName = $"{artifactId}.bat",
    sha256 = batSha256,
    playbook = normalized
}, json));
return ok ? 0 : 1;

static LabOptions? ParseArguments(string[] args)
{
    string? bat = null, playbook = null, artifactId = null;
    bool execute = false, discover = false;
    var environment = new List<(string, string)>();
    for (var index = 0; index < args.Length; index++)
    {
        string? Next() => index + 1 < args.Length ? args[++index] : null;
        switch (args[index])
        {
            case "--bat": bat = Next(); break;
            case "--playbook": playbook = Next(); break;
            case "--artifact-id": artifactId = Next(); break;
            case "--execute": execute = true; break;
            case "--discover": discover = true; break;
            case "--env":
                var pair = Next()?.Split('=', 2);
                if (pair is not { Length: 2 }) return null;
                environment.Add((pair[0], pair[1]));
                break;
            default: return null;
        }
    }
    return bat is null || playbook is null ? null
        : new LabOptions(bat, playbook, artifactId, execute, discover, environment);
}

sealed record LabOptions(string BatPath, string PlaybookPath, string? ArtifactId,
    bool Execute, bool Discover, IReadOnlyList<(string Name, string Value)> Environment);

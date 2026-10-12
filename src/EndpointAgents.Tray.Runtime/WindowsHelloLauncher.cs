using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Windows;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EndpointAgents.Tray;

public sealed class WindowsHelloLauncher(IUserConsentPrompt? consent = null)
{
    private readonly IUserConsentPrompt _consent = consent ?? new MessageBoxConsentPrompt();

    public async Task<TrayIpcResponse> LaunchAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var hello = request.WindowsHello;
        if (request.Method != TrayIpcProtocol.WindowsHelloLaunchMethod
            || hello is null
            || hello.ArtifactId != WindowsHelloSamplePaths.ArtifactId)
            return new TrayIpcResponse(request.Id, false, "InvalidWindowsHelloRequest");
        var expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            WindowsHelloSamplePaths.FileName));
        string path;
        try { path = Path.GetFullPath(hello.FilePath); }
        catch { return new TrayIpcResponse(request.Id, false, "InvalidWindowsHelloPath"); }
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !await MatchesHashAsync(path, hello.ExpectedSha256, cancellationToken))
            return new TrayIpcResponse(request.Id, false, "WindowsHelloVerificationFailed");

        if (!hello.ChatApproved
            && !await _consent.ConfirmAsync(UserConsentPrompts.WindowsHello(), cancellationToken))
            return new TrayIpcResponse(request.Id, false, "UserDeclined");

        var process = Process.Start(CreateStartInfo(path));
        return process is null
            ? new TrayIpcResponse(request.Id, false, "LaunchFailed")
            : new TrayIpcResponse(request.Id, true, "Launched", process.Id);
    }

    internal static ProcessStartInfo CreateStartInfo(string batchPath)
    {
        var command = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "cmd.exe");
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = Path.GetDirectoryName(batchPath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(batchPath);
        return startInfo;
    }

    private static async Task<bool> MatchesHashAsync(string path, string expected,
        CancellationToken cancellationToken)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream,
            cancellationToken));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}

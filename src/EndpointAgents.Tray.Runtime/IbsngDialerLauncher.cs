using EndpointAgents.Shared.LocalIpc;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using System.Windows.Forms;

namespace EndpointAgents.Tray;

public sealed class IbsngDialerLauncher
{
    private readonly IbsngDialerUiInspector _uiInspector;

    public IbsngDialerLauncher(IbsngDialerUiInspector? uiInspector = null)
    {
        _uiInspector = uiInspector ?? new IbsngDialerUiInspector();
    }

    /// <summary>Executes an approved dialer operation in the interactive user session.</summary>
    public async Task<TrayIpcResponse> LaunchAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Method == TrayIpcProtocol.ConfirmIbsngInstallMethod)
            return ConfirmInstall(request);
        if (request.Method == TrayIpcProtocol.ReconcileIbsngMethod)
            return await ReconcileResponseAsync(request, cancellationToken);
        if (request.Method == TrayIpcProtocol.InspectIbsngMethod)
            return await InspectResponseAsync(request, cancellationToken);
        if (request.Method == TrayIpcProtocol.ReadIbsngUiMethod)
            return await ReadUiResponseAsync(request, cancellationToken);
        if (request.Method != TrayIpcProtocol.LaunchIbsngMethod)
            return new TrayIpcResponse(request.Id, false, "UnknownMethod");

        if (!File.Exists(request.DialerPath))
            return new TrayIpcResponse(request.Id, false, "DialerMissing");

        if (!await IsApprovedAsync(request.DialerPath, request.ExpectedSha256, cancellationToken))
            return new TrayIpcResponse(request.Id, false, "DialerUntrusted");

        var reconciliation = await ReconcileProcessesAsync(request, cancellationToken);
        if (!reconciliation.Success)
            return reconciliation.Response;

        if (reconciliation.KeeperProcessId is int existingProcessId)
        {
            try
            {
                using var existing = Process.GetProcessById(existingProcessId);
                Focus(existing);
                return new TrayIpcResponse(request.Id, true, "AlreadyRunning", existing.Id,
                    StoppedProcessIds: reconciliation.StoppedProcessIds);
            }
            catch (ArgumentException)
            {
                // The process exited after reconciliation, so start the canonical file below.
            }
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = request.DialerPath,
            WorkingDirectory = Path.GetDirectoryName(request.DialerPath)!,
            UseShellExecute = true
        });

        return process is null
            ? new TrayIpcResponse(request.Id, false, "LaunchFailed")
            : new TrayIpcResponse(request.Id, true, "Launched", process.Id,
                "Enter credentials directly in the IBSng dialer.",
                reconciliation.StoppedProcessIds);
    }

    private static TrayIpcResponse ConfirmInstall(TrayIpcRequest request)
    {
        var consent = MessageBox.Show(
            $"دستیار پشتیبانی نیاز دارد نسخه تأییدشده IBSng را نصب یا تعمیر کند.\r\n\r\n" +
            $"مسیر مقصد:\r\n{request.DialerPath}\r\n\r\nآیا ادامه می‌دهید؟",
            "تأیید نصب یا تعمیر IBSng",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        return consent == DialogResult.Yes
            ? new TrayIpcResponse(request.Id, true, "UserApproved")
            : new TrayIpcResponse(request.Id, false, "UserDeclined",
                Message: "The endpoint user declined IBSng installation or repair.");
    }

    private static async Task<TrayIpcResponse> ReconcileResponseAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var reconciliation = await ReconcileProcessesAsync(request, cancellationToken);
        return reconciliation.Response;
    }

    private static async Task<TrayIpcResponse> InspectResponseAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = await FindCandidatesAsync(request, cancellationToken);
        var keeper = SelectKeeper(candidates);
        return new TrayIpcResponse(request.Id, true, "ProcessesInspected", keeper?.ProcessId,
            CandidateProcessIds: candidates.Select(candidate => candidate.ProcessId).ToArray());
    }

    private async Task<TrayIpcResponse> ReadUiResponseAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = await FindCandidatesAsync(request, cancellationToken);
        var keeper = SelectKeeper(candidates);
        if (keeper is null)
        {
            var unavailable = new EndpointAgents.Shared.Ibsng.IbsngDialerUiSnapshot(
                EndpointAgents.Shared.Ibsng.IbsngUiReadState.ProcessNotRunning, null, false,
                EndpointAgents.Shared.Ibsng.IbsngConnectionState.Disconnected,
                Array.Empty<string>(), "The approved IBSng dialer is not running.");
            return new TrayIpcResponse(request.Id, true, "UiInspected",
                CandidateProcessIds: candidates.Select(candidate => candidate.ProcessId).ToArray(),
                DialerUi: unavailable);
        }

        var snapshot = await _uiInspector.ReadConnectionLogAsync(keeper.ProcessId,
            cancellationToken);
        return new TrayIpcResponse(request.Id, true, "UiInspected", keeper.ProcessId,
            CandidateProcessIds: candidates.Select(candidate => candidate.ProcessId).ToArray(),
            DialerUi: snapshot);
    }

    private static async Task<bool> IsApprovedAsync(string path, string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = new byte[2];
        if (await stream.ReadAsync(signature, cancellationToken) != 2
            || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
            return false;

        stream.Position = 0;
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessReconciliation> ReconcileProcessesAsync(
        TrayIpcRequest request, CancellationToken cancellationToken)
    {
        var candidates = await FindCandidatesAsync(request, cancellationToken);
        var keeper = SelectKeeper(candidates);
        var stopped = new List<int>();
        foreach (var candidate in candidates)
        {
            if (keeper is not null && candidate.ProcessId == keeper.ProcessId)
                continue;

            if (!await StopProcessAsync(candidate.ProcessId, cancellationToken))
            {
                return new ProcessReconciliation(false, keeper?.ProcessId, stopped,
                    new TrayIpcResponse(request.Id, false, "DuplicateProcessStopFailed",
                        keeper?.ProcessId, $"Could not stop process {candidate.ProcessId}.", stopped,
                        candidates.Select(item => item.ProcessId).ToArray()));
            }
            stopped.Add(candidate.ProcessId);
        }

        return new ProcessReconciliation(true, keeper?.ProcessId, stopped,
            new TrayIpcResponse(request.Id, true, "ProcessesReconciled", keeper?.ProcessId,
                StoppedProcessIds: stopped,
                CandidateProcessIds: candidates.Select(candidate => candidate.ProcessId).ToArray()));
    }

    private static async Task<IReadOnlyList<ProcessCandidate>> FindCandidatesAsync(
        TrayIpcRequest request, CancellationToken cancellationToken)
    {
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var canonicalPath = Path.GetFullPath(request.DialerPath);
        var desktopPath = Path.GetDirectoryName(canonicalPath)!;
        var candidates = new List<ProcessCandidate>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != currentSessionId)
                    continue;

                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath))
                    continue;

                var fullPath = Path.GetFullPath(processPath);
                var isCanonical = fullPath.Equals(canonicalPath,
                    StringComparison.OrdinalIgnoreCase);
                var onDesktop = Path.GetDirectoryName(fullPath)?.Equals(desktopPath,
                    StringComparison.OrdinalIgnoreCase) == true;
                var nameMatches = onDesktop && FileSystemName.MatchesSimpleExpression(
                    request.DialerSearchPattern, Path.GetFileName(fullPath), ignoreCase: true);
                var isApproved = false;
                if (isCanonical || onDesktop)
                {
                    try
                    {
                        isApproved = await IsApprovedAsync(fullPath, request.ExpectedSha256,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // A matching filename is still a candidate when its file is unreadable.
                    }
                }

                if (isCanonical || nameMatches || isApproved)
                    candidates.Add(new ProcessCandidate(process.Id, fullPath,
                        isCanonical && isApproved, GetStartTime(process)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Other users' protected processes may not expose MainModule.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates;
    }

    private static ProcessCandidate? SelectKeeper(IEnumerable<ProcessCandidate> candidates) =>
        candidates.Where(candidate => candidate.IsApprovedCanonical)
            .OrderBy(candidate => candidate.StartedAtUtc)
            .FirstOrDefault();

    private static DateTime GetStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static async Task<bool> StopProcessAsync(int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return true;

            if (process.CloseMainWindow())
            {
                var gracefulExit = process.WaitForExitAsync(cancellationToken);
                if (await Task.WhenAny(gracefulExit, Task.Delay(3_000, cancellationToken))
                    == gracefulExit)
                    return true;
            }

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void Focus(Process process)
    {
        try
        {
            if (process.MainWindowHandle == IntPtr.Zero)
                process.WaitForInputIdle(3_000);
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero)
                return;
            NativeMethods.ShowWindowAsync(window, 9);
            NativeMethods.SetForegroundWindow(window);
        }
        catch
        {
            // Focusing an existing process is best effort.
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr window, int command);
    }

    private sealed record ProcessCandidate(int ProcessId, string Path,
        bool IsApprovedCanonical, DateTime StartedAtUtc);

    private sealed record ProcessReconciliation(bool Success, int? KeeperProcessId,
        IReadOnlyList<int> StoppedProcessIds, TrayIpcResponse Response);
}

using EndpointAgents.Shared.Ibsng;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace EndpointAgents.Tray;

/// <summary>Reads non-secret connection evidence from an IBSng window in the current session.</summary>
public sealed class IbsngDialerUiInspector
{
    /// <summary>
    /// Reads the connection log without interacting with fields or changing dialer state.
    /// A typed unavailable result is returned when the custom skin exposes no accessible text.
    /// </summary>
    public Task<IbsngDialerUiSnapshot> ReadConnectionLogAsync(int processId,
        CancellationToken cancellationToken) => Task.Run(() => Read(processId, cancellationToken),
            cancellationToken);

    private static IbsngDialerUiSnapshot Read(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var window = FindTopLevelWindow(process);
            if (window == IntPtr.Zero)
                return Unavailable(IbsngUiReadState.WindowNotFound, processId,
                    "The IBSng process has no accessible top-level window.");

            cancellationToken.ThrowIfCancellationRequested();
            var text = ReadWithUiAutomation(window, cancellationToken)
                ?? ReadWithWin32(window, cancellationToken);
            if (text is null)
                return Unavailable(IbsngUiReadState.LogControlNotAccessible, processId,
                    "This IBSng version does not expose its Connection Log through Windows accessibility APIs.",
                    windowAvailable: true);

            var lines = IbsngConnectionLogParser.Sanitize(text);
            return new IbsngDialerUiSnapshot(IbsngUiReadState.Success, processId, true,
                IbsngConnectionLogParser.ParseState(lines), lines);
        }
        catch (ArgumentException)
        {
            return Unavailable(IbsngUiReadState.ProcessNotRunning, processId,
                "The IBSng process is not running.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(IbsngUiReadState.Error, processId,
                $"The IBSng window could not be inspected: {ex.GetType().Name}.");
        }
    }

    private static string? ReadWithUiAutomation(IntPtr window,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var candidates = new List<(double Score, string Text)>();
            System.Windows.Rect? logLabel = null;

            foreach (AutomationElement element in elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = element.Current;
                // Only static labels are queried by name during discovery. Edit
                // controls may expose their entered value as Name on older apps.
                if (current.ControlType != ControlType.Text) continue;
                var labelName = current.Name;
                if (!IsCredentialControl(labelName)
                    && labelName.Contains("Connection Log", StringComparison.OrdinalIgnoreCase))
                    logLabel = current.BoundingRectangle;
            }

            // Without the visible label we cannot safely distinguish the log from
            // Username, Connect To, or other Edit controls, so use the Win32 fallback.
            if (logLabel is null) return null;

            foreach (AutomationElement element in elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = element.Current;
                if (current.IsPassword) continue;
                if (current.ControlType != ControlType.Edit
                    && current.ControlType != ControlType.Document
                    && current.ControlType != ControlType.Text)
                    continue;

                var bounds = current.BoundingRectangle;
                var isBelowLabel = bounds.Top >= logLabel.Value.Top - 4;
                if (!isBelowLabel) continue;
                if (IsCredentialControl(current.Name)) continue;
                var text = ReadAutomationText(element);
                if (text is null) continue;
                var hasSignal = IbsngConnectionLogParser.ContainsDiagnosticSignal(text);
                var score = 100_000d + bounds.Width * bounds.Height
                    + (hasSignal ? 50_000d : 0d);
                candidates.Add((score, text));
            }

            return candidates.OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Text).FirstOrDefault();
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadAutomationText(AutomationElement element)
    {
        // Read only an already-selected log candidate; never query arbitrary Edit values.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            return ((TextPattern)textPattern).DocumentRange.GetText(8 * 1024);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            return ((ValuePattern)valuePattern).Current.Value;
        return element.Current.Name;
    }

    private static string? ReadWithWin32(IntPtr window, CancellationToken cancellationToken)
    {
        NativeMethods.GetWindowRect(window, out var parentBounds);
        var candidates = new List<(long Score, string Text)>();
        NativeMethods.EnumChildWindows(window, (child, _) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var className = GetClassName(child);
            if (!className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                && !className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
                return true;

            var style = NativeMethods.GetWindowLong(child, NativeMethods.GwlStyle);
            if ((style & NativeMethods.EsPassword) != 0) return true;
            NativeMethods.GetWindowRect(child, out var bounds);
            var lowerHalf = bounds.Top >= parentBounds.Top + (parentBounds.Height / 2);
            // Reading is intentionally delayed until spatial filtering has removed
            // the Username and Password area in the upper half of the dialer.
            if (!lowerHalf) return true;
            var text = ReadWindowText(child);
            if (text is null) return true;
            var hasSignal = IbsngConnectionLogParser.ContainsDiagnosticSignal(text);
            var score = 100_000L + (long)bounds.Width * bounds.Height
                + (hasSignal ? 50_000L : 0L);
            candidates.Add((score, text));
            return true;
        }, IntPtr.Zero);
        return candidates.OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Text).FirstOrDefault();
    }

    private static IntPtr FindTopLevelWindow(Process process)
    {
        process.Refresh();
        if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;

        var found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId != process.Id || !NativeMethods.IsWindowVisible(window)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCredentialControl(string? name) =>
        name?.Contains("password", StringComparison.OrdinalIgnoreCase) == true
        || name?.Contains("username", StringComparison.OrdinalIgnoreCase) == true
        || name?.Contains("credential", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetClassName(IntPtr window)
    {
        var value = new StringBuilder(128);
        _ = NativeMethods.GetClassName(window, value, value.Capacity);
        return value.ToString();
    }

    private static string? ReadWindowText(IntPtr window)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLength(window), 8 * 1024);
        if (length < 0) return null;
        var value = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, value, value.Capacity) >= 0
            ? value.ToString() : null;
    }

    private static IbsngDialerUiSnapshot Unavailable(IbsngUiReadState state, int processId,
        string message, bool windowAvailable = false) =>
        new(state, processId, windowAvailable, IbsngConnectionState.Unknown,
            Array.Empty<string>(), message);

    private static class NativeMethods
    {
        internal const int GwlStyle = -16;
        internal const int EsPassword = 0x20;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className,
            int maximumCharacters);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text,
            int maximumCharacters);

        [DllImport("user32.dll")]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}

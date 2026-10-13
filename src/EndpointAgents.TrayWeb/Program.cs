using EndpointAgents.Tray;
using System.Diagnostics;
using System.IO;

namespace EndpointAgents.TrayWeb;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var actionIndex = Array.IndexOf(args, "--service-action");
        if (actionIndex >= 0 && actionIndex + 1 < args.Length)
            return ElevatedServiceActionRunner.RunAsync(args[actionIndex + 1])
                .GetAwaiter().GetResult();

        // Regenerates the exe icon: EndpointAgents.TrayWeb.exe --export-icon <path.ico>
        var exportIndex = Array.IndexOf(args, "--export-icon");
        if (exportIndex >= 0 && exportIndex + 1 < args.Length)
        {
            TrayIconFactory.WriteIco(args[exportIndex + 1], TrayFace.Happy);
            return 0;
        }

        // Review aid: EndpointAgents.TrayWeb.exe --export-faces <folder> writes one PNG per face and size.
        var facesIndex = Array.IndexOf(args, "--export-faces");
        if (facesIndex >= 0 && facesIndex + 1 < args.Length)
        {
            Directory.CreateDirectory(args[facesIndex + 1]);
            foreach (var face in Enum.GetValues<TrayFace>())
                foreach (var size in new[] { 16, 32, 128 })
                {
                    using var bitmap = TrayIconFactory.Draw(face, size);
                    bitmap.Save(Path.Combine(args[facesIndex + 1], $"{face}-{size}.png"));
                }
            return 0;
        }

        var options = TrayOptions.Parse(args);
        var sessionId = Process.GetCurrentProcess().SessionId;
        // The same names as the WinUI Tray, so only one Tray of either kind runs per session.
        var mutexName = options.Preview
            ? $@"Local\EndpointAgents.Tray.Preview.{Environment.ProcessId}"
            : $@"Local\EndpointAgents.Tray.{sessionId}";
        using var instance = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            TrayHost.SignalShowWindow(sessionId);
            return 0;
        }

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };
        TrayHost? host = null;
        app.Startup += (_, _) => host = new TrayHost(app, options);
        app.DispatcherUnhandledException += (_, e) =>
        {
            TrayEventLogger.Error("خطای پیش‌بینی‌نشده رابط کاربری: " + e.Exception.Message);
            e.Handled = true;
        };
        app.Exit += (_, _) => host?.Dispose();
        return app.Run();
    }
}

/// <summary>Command-line switches. Everything except the defaults is for development and review.</summary>
public sealed record TrayOptions(
    bool Preview,
    bool Smoke,
    bool Dark,
    bool Light,
    string? Section,
    string? SnapshotPath,
    int Width,
    int Height)
{
    public static TrayOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var width = 1240;
        var height = 760;
        if (Value("--size") is { } size && size.Split('x') is [var w, var h]
            && int.TryParse(w, out var parsedWidth) && int.TryParse(h, out var parsedHeight))
            (width, height) = (parsedWidth, parsedHeight);
        return new TrayOptions(
            Preview: args.Contains("--ui-preview"),
            Smoke: args.Contains("--tray-smoke"),
            Dark: args.Contains("--dark"),
            Light: args.Contains("--light"),
            Section: Value("--section"),
            SnapshotPath: Value("--snapshot"),
            Width: width,
            Height: height);
    }
}

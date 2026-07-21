namespace EndpointAgents.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var actionIndex = Array.IndexOf(args, "--service-action");
        if (actionIndex >= 0 && actionIndex + 1 < args.Length)
            return ElevatedServiceActionRunner.RunAsync(args[actionIndex + 1])
                .GetAwaiter().GetResult();

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        using var instance = new Mutex(true, $@"Local\EndpointAgents.Tray.{sessionId}",
            out var createdNew);
        if (!createdNew)
            return 0;

        Application.Run(new TrayApplicationContext());
        return 0;
    }
}

namespace EndpointAgents.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(
            System.Windows.Forms.HighDpiMode.PerMonitorV2);
        var actionIndex = Array.IndexOf(args, "--service-action");
        if (actionIndex >= 0 && actionIndex + 1 < args.Length)
            return ElevatedServiceActionRunner.RunAsync(args[actionIndex + 1])
                .GetAwaiter().GetResult();

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var smoke = args.Any(argument => argument.EndsWith("-smoke",
            StringComparison.OrdinalIgnoreCase));
        var mutexName = smoke
            ? $@"Local\EndpointAgents.Tray.Smoke.{Environment.ProcessId}"
            : $@"Local\EndpointAgents.Tray.{sessionId}";
        using var instance = new Mutex(true, mutexName,
            out var createdNew);
        if (!createdNew)
            return 0;

        Microsoft.UI.Xaml.Application.Start(_initialization =>
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
            new App();
        });
        return 0;
    }
}

using EndpointAgents.Shared.Support;

namespace EndpointAgents.Tray;

public enum TrayHealthState
{
    Checking,
    Ready,
    Paused,
    Stopped,
    Warning,
    Error
}

public sealed record TrayStatus(
    TrayHealthState Health,
    EndpointServiceState ServiceState,
    AgentRuntimeSnapshot? Runtime,
    string Label,
    string Detail);

public static class TrayStatusResolver
{
    public static TrayStatus Resolve(EndpointServiceState service,
        AgentRuntimeSnapshot? runtime, int ipcFailures, DateTimeOffset now)
    {
        if (service is EndpointServiceState.NotInstalled)
            return new(TrayHealthState.Error, service, runtime, "سرویس نصب نشده است",
                "سرویس عامل نقطه پایانی در این رایانه پیدا نشد.");
        if (service is EndpointServiceState.Stopped)
            return new(TrayHealthState.Stopped, service, runtime, "سرویس متوقف است",
                "برای اتصال دوباره این رایانه، سرویس پس‌زمینه را راه‌اندازی کنید.");
        if (service is EndpointServiceState.StartPending or EndpointServiceState.StopPending)
            return new(TrayHealthState.Checking, service, runtime, "وضعیت سرویس در حال تغییر است",
                "در انتظار پاسخ مدیر سرویس‌های ویندوز.");
        if (!EndpointServiceStateResolver.IsOperational(service))
            return new(TrayHealthState.Checking, service, runtime, "در حال بررسی سرویس",
                "وضعیت سرویس پس‌زمینه در حال دریافت است.");
        if (runtime is null)
            return ipcFailures >= 2
                ? new(TrayHealthState.Error, service, null, "سرویس نیاز به بررسی دارد",
                    "سرویس اجرا می‌شود، اما کانال وضعیت محلی آن در دسترس نیست.")
                : new(TrayHealthState.Checking, service, null, "در حال بررسی عامل",
                    "در انتظار کانال وضعیت سرویس.");

        if (runtime.ConnectionStatus is AgentConnectionStatus.Faulted)
            return new(TrayHealthState.Error, service, runtime, "اتصال ناموفق است",
                runtime.LastProblemMessage ?? "اتصال به سرور کنترل برقرار نشد.");
        if (runtime.ConnectionStatus is AgentConnectionStatus.Disconnected)
        {
            var old = runtime.LastProblemAtUtc is { } problemAt
                && now - problemAt > TimeSpan.FromSeconds(60);
            return new(old ? TrayHealthState.Error : TrayHealthState.Warning, service, runtime,
                old ? "سرور در دسترس نیست" : "اتصال قطع شده است",
                runtime.LastProblemMessage ?? "عامل در انتظار اتصال مجدد است.");
        }
        if (runtime.ConnectionStatus is AgentConnectionStatus.Connecting
            or AgentConnectionStatus.Reconnecting or AgentConnectionStatus.Starting)
            return new(TrayHealthState.Warning, service, runtime, "در حال اتصال به سرور",
                runtime.LastProblemMessage ?? "عامل در حال برقراری اتصال امن و زنده است.");
        if (runtime.FunctionMode == AgentFunctionMode.Paused)
            return new(TrayHealthState.Paused, service, runtime, "عملکردها متوقف شده‌اند",
                "سرویس متصل است، اما اجرای عملکردهای جدید موقتاً مسدود شده است.");
        if (runtime.LastCommandSucceeded == false && runtime.LastCommandAtUtc is { } commandAt
            && now - commandAt < TimeSpan.FromMinutes(15))
            return new(TrayHealthState.Warning, service, runtime, "آخرین عملکرد با مشکل روبه‌رو شد",
                runtime.LastProblemMessage ?? "برای جزئیات، گزارش‌ها را باز کنید.");

        return new(TrayHealthState.Ready, service, runtime, "متصل و آماده",
            "سرویس پس‌زمینه آماده دریافت عملکردهای تأییدشده پشتیبانی است.");
    }
}

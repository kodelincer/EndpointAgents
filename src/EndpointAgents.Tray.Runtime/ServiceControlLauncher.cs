using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace EndpointAgents.Tray;

public sealed record ServiceActionResult(bool Success, string Message, bool Cancelled = false);

public static class ServiceControlLauncher
{
    public static async Task<ServiceActionResult> LaunchAsync(string action,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("مسیر اجرای برنامه ناحیه اعلان در دسترس نیست."),
                Arguments = $"--service-action {action}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                return new(false, "ویندوز عملیات مدیریتی را اجرا نکرد.");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new(true, action switch
                {
                    "start" => "سرویس دستیار پشتیبانی راه‌اندازی شد.",
                    "stop" => "سرویس دستیار پشتیبانی متوقف شد.",
                    "pause" => "عملکردهای جدید دستیار پشتیبانی موقتاً متوقف شدند.",
                    "resume" => "عملکردهای دستیار پشتیبانی دوباره فعال شدند.",
                    _ => "عملیات مدیریتی انجام شد."
                })
                : new(false, "عملیات مدیریتی کامل نشد. برای جزئیات، گزارش‌ها را بررسی کنید.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "تأیید مدیر سیستم لغو شد.", true);
        }
        catch (Exception ex)
        {
            TrayEventLogger.Error($"عملیات سرویس '{action}' ناموفق بود: {ex.GetType().Name}: {ex.Message}");
            return new(false, ex.Message);
        }
    }
}

public static class ElevatedServiceActionRunner
{
    public static async Task<int> RunAsync(string action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            switch (action)
            {
                case "start":
                    await new WindowsServiceManager().StartAsync(timeout.Token);
                    break;
                case "stop":
                    await new WindowsServiceManager().StopAsync(timeout.Token);
                    break;
                case "pause":
                case "resume":
                    var response = await new ServiceIpcClient()
                        .SetPausedAsync(action == "pause", timeout.Token);
                    if (!response.Success)
                        throw new InvalidOperationException(response.Message ?? response.State);
                    break;
                default:
                    throw new InvalidOperationException("عملیات درخواستی سرویس شناخته‌شده نیست.");
            }
            TrayEventLogger.Information($"عملیات مدیریتی سرویس '{action}' انجام شد.");
            return 0;
        }
        catch (Exception ex)
        {
            TrayEventLogger.Error($"عملیات مدیریتی سرویس '{action}' ناموفق بود: " +
                $"{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "دستیار پشتیبانی", MessageBoxButtons.OK,
                MessageBoxIcon.Error, MessageBoxDefaultButton.Button1,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            return 1;
        }
    }
}

using System.Windows.Forms;

namespace EndpointAgents.Tray;

/// <summary>A yes/no question shown to the signed-in user before a catalogued task runs.</summary>
public sealed record UserConsentPrompt(
    string Kind,
    string Title,
    string Body,
    string ApproveText,
    string DeclineText = "انصراف",
    string? Detail = null,
    bool Caution = false);

/// <summary>Asks the person at the keyboard. Every implementation defaults to "no" on doubt.</summary>
public interface IUserConsentPrompt
{
    Task<bool> ConfirmAsync(UserConsentPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>Plain Windows message box, used when no richer Tray window is available.</summary>
public sealed class MessageBoxConsentPrompt : IUserConsentPrompt
{
    public Task<bool> ConfirmAsync(UserConsentPrompt prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = prompt.Detail is null ? prompt.Body : $"{prompt.Body}\r\n\r\n{prompt.Detail}";
        var answer = MessageBox.Show(text, prompt.Title, MessageBoxButtons.YesNo,
            prompt.Caution ? MessageBoxIcon.Warning : MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        return Task.FromResult(answer == DialogResult.Yes);
    }
}

/// <summary>Persian wording for every consent the service can ask for.</summary>
public static class UserConsentPrompts
{
    public static UserConsentPrompt IbsngInstall(string targetPath) => new(
        "ibsng.install",
        "نصب یا تعمیر IBSng",
        "دستیار پشتیبانی می‌خواهد نسخه تأییدشده برنامه اتصال IBSng را نصب یا تعمیر کند.",
        "نصب کن",
        Detail: "مسیر نصب: " + targetPath);

    public static UserConsentPrompt OfficeBugfix(string displayName, string description) => new(
        "office.bugfix",
        "تعمیر Office",
        $"دستیار پشتیبانی این تعمیر تأییدشده را پیشنهاد می‌کند: {displayName}",
        "اجرا کن",
        Detail: description + "\r\nاین تعمیر با دسترسی سیستم اجرا می‌شود و نام کاربری یا رمز عبور از شما نمی‌خواهد.",
        Caution: true);

    public static UserConsentPrompt OfficeTool(string displayName, string description,
        string trust) => new(
        "office.tool",
        "اجرای ابزار Office",
        $"دستیار پشتیبانی می‌خواهد این ابزار را اجرا کند: {displayName}",
        "اجرا کن",
        Detail: $"{description}\r\n{trust}\r\nهیچ رمز یا گزینه اضافه‌ای به ابزار داده نمی‌شود.");

    public static UserConsentPrompt WindowsBugfix(string displayName, string description) => new(
        "windows.bugfix",
        "تعمیر فعال‌سازی ویندوز",
        $"دستیار پشتیبانی این تعمیر را پیشنهاد می‌کند: {displayName}",
        "اجرا کن",
        Detail: description + "\r\nاین تعمیر بی‌صدا و با دسترسی سیستم اجرا می‌شود.",
        Caution: true);

    public static UserConsentPrompt WindowsHello() => new(
        "windows.hello",
        "اجرای نمونه آزمایشی",
        "برنامه نمونه بی‌خطر Windows Hello از روی دسکتاپ شما اجرا شود؟",
        "اجرا کن");
}

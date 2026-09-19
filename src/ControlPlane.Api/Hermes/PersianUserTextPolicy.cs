namespace ControlPlane.Api.Hermes;

public static class PersianUserTextPolicy
{
    private const int AssistantMessageCharacters = 2_000;
    private const int ActionReasonCharacters = 800;

    public const string InvalidOutputFallback =
        "نتوانستم نتیجه را به‌صورت قابل نمایش آماده کنم. لطفاً دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.";

    public const string ActionReasonFallback =
        "برای ادامه بررسی و رفع مشکل آفیس، تأیید شما لازم است.";

    public static string AssistantMessage(string? value) =>
        Normalize(value, AssistantMessageCharacters, InvalidOutputFallback);

    public static string ActionReason(string? value) =>
        Normalize(value, ActionReasonCharacters, ActionReasonFallback);

    private static string Normalize(string? value, int maximumCharacters,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();
        return text.Length <= maximumCharacters
            ? text
            : text[..(maximumCharacters - 1)] + '…';
    }
}

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
        IsSafe(value, AssistantMessageCharacters)
            ? value!.Trim().Replace("`", string.Empty)
            : InvalidOutputFallback;

    public static string ActionReason(string? value) =>
        IsSafe(value, ActionReasonCharacters) ? value!.Trim() : ActionReasonFallback;

    public static bool IsSafe(string? value) => IsSafe(value, AssistantMessageCharacters);

    private static bool IsSafe(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
            return false;
        if (value.Contains("```", StringComparison.Ordinal)
            || value.Contains('{') || value.Contains('}')
            || value.Contains('[') || value.Contains(']')
            || value.Contains('"')
            || value.Contains('<') || value.Contains('>')
            || value.Contains('\\') || value.Contains('/')
            || value.Contains('_'))
            return false;
        return value.Any(char.IsLetter);
    }
}

namespace ControlPlane.Api.Hermes;

public static class PersianUserTextPolicy
{
    public const string InvalidOutputFallback =
        "نتوانستم نتیجه را به‌صورت قابل نمایش آماده کنم. لطفاً دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.";

    public const string ActionReasonFallback =
        "برای ادامه بررسی و رفع مشکل آفیس، تأیید شما لازم است.";

    public static string AssistantMessage(string? value) =>
        IsSafe(value) ? ToPersianDigits(value!.Trim()) : InvalidOutputFallback;

    public static string ActionReason(string? value) =>
        IsSafe(value) ? ToPersianDigits(value!.Trim()) : ActionReasonFallback;

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 800)
            return false;
        if (value.Any(character => character is >= 'A' and <= 'Z'
                                  or >= 'a' and <= 'z'))
            return false;
        if (value.Contains('`')
            || value.Contains('{') || value.Contains('}')
            || value.Contains('[') || value.Contains(']')
            || value.Contains('"')
            || value.Contains('<') || value.Contains('>')
            || value.Contains('\\') || value.Contains('/'))
            return false;
        if (value.Count(character => character is '.' or '!' or '?' or '؟') > 3)
            return false;
        return value.Any(character =>
            char.IsLetter(character) && character is >= '\u0600' and <= '\u06ff');
    }

    private static string ToPersianDigits(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character is >= '0' and <= '9')
                characters[index] = (char)('۰' + character - '0');
        }
        return new string(characters);
    }
}

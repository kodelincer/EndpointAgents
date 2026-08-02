using EndpointAgents.Shared.Chat;

namespace ControlPlane.Api.Hermes;

public enum OfficeRequestDecision
{
    Office,
    Clarify,
    Reject
}

public static class OfficeRequestGate
{
    public const string RejectionMessage =
        "من فقط می‌توانم درباره مشکلات برنامه‌های آفیس کمک کنم.";

    public const string ClarificationMessage =
        "مشکل شما مربوط به کدام برنامه آفیس است و هنگام استفاده از آن چه اتفاقی می‌افتد؟";

    private static readonly string[] Products =
    [
        "آفیس", "افیس", "ورد", "اکسل", "پاورپوینت", "پاور پوینت", "اوت لوک",
        "اوت‌لوک", "اوتلوک", "وان نوت", "وان‌نوت", "اکسس", "پابلیشر", "ویزیو",
        "پروجکت", "مایکروسافت 365", "مایکروسافت ۳۶۵", "office", "word", "excel",
        "powerpoint", "outlook", "onenote", "access", "publisher", "visio",
        "microsoft 365"
    ];

    private static readonly string[] SupportTerms =
    [
        "نصب", "فعال", "مجوز", "لایسنس", "نسخه", "ورژن", "به روز", "به‌روز",
        "بروزرسان", "آپدیت", "اجرا", "باز نمی", "بازنمی", "خطا", "ارور", "خراب",
        "کرش", "تعمیر", "رفع مشکل", "کار نمی", "کارنمی", "شناسایی", "پاک شده",
        "حذف شده", "install", "activation", "activate", "license", "version",
        "update", "launch", "won't open", "will not open", "not working", "crash",
        "error", "repair", "missing"
    ];

    private static readonly string[] GenericProblemTerms =
    [
        "مشکل", "خراب", "کار نمی", "کارنمی", "باز نمی", "بازنمی", "اجرا نمی",
        "اجرانمی", "خطا", "ارور", "نصب نمی", "نصب نمی", "فعال نمی", "کمک",
        "problem", "not working", "doesn't work", "does not work", "won't open",
        "error", "help"
    ];

    private static readonly string[] UnsupportedTerms =
    [
        "فرمول", "ماکرو", "وی بی ای", "vba", "formula", "macro", "نامه بنویس",
        "متن بنویس", "سند بساز", "فایل بساز", "رزومه", "اسلاید بساز",
        "write a document", "create a document", "make a spreadsheet",
        "create slides", "weather", "آب و هوا", "هواشناسی", "برنامه نویسی",
        "برنامه‌نویسی", "coding", "code for", "شبکه", "اینترنت", "پروکسی",
        "ibsng", "آی بی اس ان جی", "آی‌بی‌اس‌ان‌جی"
    ];

    private static readonly string[] InjectionTerms =
    [
        "ignore previous", "ignore all", "disregard", "system prompt",
        "developer message", "reveal prompt", "show prompt", "change your role",
        "act as", "call tool", "mcp_", "respond in english", "answer in english",
        "دستور قبلی را نادیده", "دستورها را نادیده", "قوانین را نادیده",
        "دستورهای قبلی را نادیده", "نادیده بگیر",
        "پرامپت سیستم", "پرامپت را نشان", "دستور داخلی", "دستورهای داخلی",
        "نقش خود را تغییر", "به انگلیسی پاسخ", "ابزار را اجرا", "فراخوانی ابزار"
    ];

    private static readonly string[] Continuations =
    [
        "بله", "نه", "خیر", "باشه", "باشد", "ادامه بده", "دوباره امتحان کن",
        "انجام بده", "تایید می کنم", "تأیید می کنم", "تأیید می‌کنم", "yes", "no", "continue",
        "retry", "do it"
    ];

    public static OfficeRequestDecision Classify(string input,
        IReadOnlyList<ChatMessage>? priorHistory = null)
    {
        var text = Normalize(input);
        if (string.IsNullOrWhiteSpace(text)
            || ContainsAny(text, InjectionTerms)
            || ContainsAny(text, UnsupportedTerms))
            return OfficeRequestDecision.Reject;

        var hasProduct = ContainsAny(text, Products);
        var hasSupportTerm = ContainsAny(text, SupportTerms);
        if (hasProduct && hasSupportTerm)
            return OfficeRequestDecision.Office;
        if (hasProduct)
            return OfficeRequestDecision.Clarify;

        if (IsContinuation(text) && HasRecentOfficeContext(priorHistory))
            return OfficeRequestDecision.Office;
        if (ContainsAny(text, GenericProblemTerms))
            return HasRecentOfficeContext(priorHistory)
                ? OfficeRequestDecision.Office
                : OfficeRequestDecision.Clarify;
        return OfficeRequestDecision.Reject;
    }

    private static bool HasRecentOfficeContext(IReadOnlyList<ChatMessage>? history)
    {
        if (history is null || history.Count == 0) return false;
        return history.TakeLast(4).Any(message =>
        {
            var text = Normalize(message.Body);
            return ContainsAny(text, Products);
        });
    }

    private static bool IsContinuation(string text) =>
        Continuations.Any(value => string.Equals(text, value,
            StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAny(string text, IEnumerable<string> values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant()
            .Replace('ي', 'ی')
            .Replace('ك', 'ک')
            .Replace("\u200c", " ");
}

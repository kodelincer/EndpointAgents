using ControlPlane.Api.Hermes;

namespace ControlPlane.Api.Tests;

public sealed class PersianUserTextPolicyTests
{
    [Fact]
    public void KeepsSimplePersianWithoutChangingDigits()
    {
        var result = PersianUserTextPolicy.AssistantMessage(
            "نسخه 16 آفیس شناسایی شد. مشکل هنوز برطرف نشده است.");

        Assert.Equal("نسخه 16 آفیس شناسایی شد. مشکل هنوز برطرف نشده است.", result);
    }

    [Theory]
    [InlineData("Office activation failed.")]
    [InlineData("نسخه Office LTSC Professional Plus 2021 نصب است و خطای 0XC004F014 دارد.")]
    public void KeepsEnglishAndMixedLanguageText(string output)
    {
        Assert.Equal(output, PersianUserTextPolicy.AssistantMessage(output));
    }

    [Fact]
    public void KeepsDiagnosticReplyWithInlineErrorCodeAndExtraSentence()
    {
        const string output =
            "آفیس نصب است ولی وضعیت فعال‌سازی آن «مجوز نداره» با خطای `0XC004F014` نشون میده. " +
            "چند لایسنس حجمی MAK از کار افتادن اما یک لایسنس KMS فعاله. " +
            "پیشنهاد می‌کنم اقدام تعمیر فعال‌سازی رو ثبت کنم. لطفاً تأیید کنید تا ادامه بدم.";

        Assert.Equal(output.Replace("`", string.Empty),
            PersianUserTextPolicy.AssistantMessage(output));
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""["نتیجه"]""")]
    [InlineData("مسیر C:\\Program Files\\Office است.")]
    [InlineData("جزئیات در https://example.test قرار دارد.")]
    [InlineData("ابزار mcp_controlplane_office_inventory اجرا شد.")]
    [InlineData("```دستور```")]
    public void ReplacesTechnicalOrNonPersianOutput(string output)
    {
        Assert.Equal(PersianUserTextPolicy.InvalidOutputFallback,
            PersianUserTextPolicy.AssistantMessage(output));
    }

    [Fact]
    public void ReplacesUnsafeActionReason()
    {
        Assert.Equal("Run approved Office repair",
            PersianUserTextPolicy.ActionReason("Run approved Office repair"));
    }

    [Fact]
    public void AllowsAssistantOutputUpToTwoThousandCharacters()
    {
        Assert.False(PersianUserTextPolicy.IsSafe(" "));
        Assert.True(PersianUserTextPolicy.IsSafe(new string('a', 2_000)));
        Assert.False(PersianUserTextPolicy.IsSafe(new string('a', 2_001)));
    }

    [Fact]
    public void KeepsActionReasonLimitAtEightHundredCharacters()
    {
        Assert.Equal(new string('a', 800),
            PersianUserTextPolicy.ActionReason(new string('a', 800)));
        Assert.Equal(PersianUserTextPolicy.ActionReasonFallback,
            PersianUserTextPolicy.ActionReason(new string('a', 801)));
    }
}

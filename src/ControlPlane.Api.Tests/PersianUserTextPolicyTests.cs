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

        Assert.Equal(output, PersianUserTextPolicy.AssistantMessage(output));
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""["نتیجه"]""")]
    [InlineData("مسیر C:\\Program Files\\Office است.")]
    [InlineData("جزئیات در https://example.test قرار دارد.")]
    [InlineData("ابزار mcp_controlplane_office_inventory اجرا شد.")]
    [InlineData("```دستور```")]
    [InlineData("برای ادامه به Settings > Activation بروید.")]
    public void KeepsDisplayableTechnicalText(string output)
    {
        Assert.Equal(output, PersianUserTextPolicy.AssistantMessage(output));
    }

    [Fact]
    public void KeepsActionReason()
    {
        Assert.Equal("Run approved Office repair",
            PersianUserTextPolicy.ActionReason("Run approved Office repair"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackOnlyForMissingAssistantText(string? output)
    {
        Assert.Equal(PersianUserTextPolicy.InvalidOutputFallback,
            PersianUserTextPolicy.AssistantMessage(output));
    }

    [Fact]
    public void TruncatesLongAssistantOutputInsteadOfHidingIt()
    {
        var result = PersianUserTextPolicy.AssistantMessage(new string('a', 2_001));

        Assert.Equal(2_000, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void TruncatesLongActionReasonAndFallsBackOnlyWhenMissing()
    {
        var result = PersianUserTextPolicy.ActionReason(new string('a', 801));

        Assert.Equal(800, result.Length);
        Assert.EndsWith("…", result);
        Assert.Equal(PersianUserTextPolicy.ActionReasonFallback,
            PersianUserTextPolicy.ActionReason(" "));
    }
}

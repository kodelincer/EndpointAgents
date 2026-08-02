using ControlPlane.Api.Hermes;

namespace ControlPlane.Api.Tests;

public sealed class PersianUserTextPolicyTests
{
    [Fact]
    public void KeepsSimplePersianAndConvertsDigits()
    {
        var result = PersianUserTextPolicy.AssistantMessage(
            "نسخه 16 آفیس شناسایی شد. مشکل هنوز برطرف نشده است.");

        Assert.Equal("نسخه ۱۶ آفیس شناسایی شد. مشکل هنوز برطرف نشده است.", result);
    }

    [Theory]
    [InlineData("Office activation failed.")]
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
        Assert.Equal(PersianUserTextPolicy.ActionReasonFallback,
            PersianUserTextPolicy.ActionReason("Run artifactId office-fix-1"));
    }
}

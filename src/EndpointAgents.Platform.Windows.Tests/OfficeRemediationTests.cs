using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using EndpointAgents.Tray;
using System.Security.Cryptography;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class OfficeRemediationTests
{
    [Theory]
    [InlineData("m365-repair", true)]
    [InlineData("office.fix_2024", true)]
    [InlineData("../tool", false)]
    [InlineData("Tool With Spaces", false)]
    [InlineData("", false)]
    public void CommandAcceptsOnlyStableArtifactIds(string artifactId, bool expected)
    {
        var parameters = JsonSerializer.SerializeToElement(new { artifactId });

        var valid = OfficeRemediationCommandHandler.TryParseArtifactId(parameters, out var parsed);

        Assert.Equal(expected, valid);
        if (expected) Assert.Equal(artifactId, parsed);
    }

    [Fact]
    public async Task TrayHashCheckRequiresPeHeaderAndExactSha256()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-office-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "repair.exe");
            var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            Assert.True(await OfficeRemediationLauncher.MatchesExecutableHashAsync(path, hash,
                TestContext.Current.CancellationToken));
            Assert.False(await OfficeRemediationLauncher.MatchesExecutableHashAsync(path,
                new string('A', 64), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrayRejectsRemediationOutsideAgentStagingRoot()
    {
        var launch = new OfficeRemediationLaunchRequest("m365-repair", "Microsoft repair",
            "Approved repair", @"C:\Windows\Temp\repair.exe", new string('A', 64));
        var request = new TrayIpcRequest("test", TrayIpcProtocol.OfficeRemediationLaunchMethod,
            string.Empty, string.Empty, null, OfficeRemediation: launch);

        var response = await new OfficeRemediationLauncher().LaunchAsync(request,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("RemediationFileUnavailable", response.State);
    }
}

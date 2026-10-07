using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.SystemInfo;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class SystemInfoTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(20, "255.255.240.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(32, "255.255.255.255")]
    public void ConvertsPrefixLengthToSubnetMask(int prefix, string mask) =>
        Assert.Equal(mask, NetworkAddressFacts.SubnetMask(prefix));

    [Fact]
    public void ComputesNetworkFacts()
    {
        var address = IPAddress.Parse("192.168.10.37");
        Assert.Equal("192.168.10.0", NetworkAddressFacts.NetworkAddress(address, 24));
        Assert.True(NetworkAddressFacts.InSameSubnet(address, IPAddress.Parse("192.168.10.1"), 24));
        Assert.False(NetworkAddressFacts.InSameSubnet(address, IPAddress.Parse("192.168.11.1"), 24));
        Assert.True(NetworkAddressFacts.IsApipa(IPAddress.Parse("169.254.3.4")));
        Assert.False(NetworkAddressFacts.IsApipa(address));
        Assert.True(NetworkAddressFacts.IsPrivate(IPAddress.Parse("172.20.0.1")));
        Assert.False(NetworkAddressFacts.IsPrivate(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void NormalizesRequestedSections()
    {
        Assert.True(SystemInfoContracts.TryNormalizeSections(null, out var all));
        Assert.Equal(SystemInfoContracts.Sections, all);

        Assert.True(SystemInfoContracts.TryNormalizeSections(
            ["Domain", " network ", "domain"], out var subset));
        Assert.Equal(["network", "domain"], subset);

        Assert.False(SystemInfoContracts.TryNormalizeSections(["network", "shell"], out _));
    }

    [Fact]
    public async Task CollectsOnlyRequestedSectionsAndSurvivesAFailingSection()
    {
        var handler = new SystemInfoCommandHandler(
            [new FakeSection("network", () => new { Ok = true }),
             new FakeSection("domain", () => throw new InvalidOperationException("boom")),
             new FakeSection("storage", () => new { Ok = true })],
            NullLogger<SystemInfoCommandHandler>.Instance);

        var response = await handler.HandleAsync(Request(new { sections = new[] { "network", "domain" } }),
            Context(), TestCancellation);

        Assert.Null(response.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Result));
        var result = document.RootElement.GetProperty("Result");
        Assert.True(result.GetProperty("network").GetProperty("Ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("domain").ValueKind);
        Assert.False(result.TryGetProperty("storage", out _));
        Assert.Equal("domain", document.RootElement.GetProperty("Warnings")[0]
            .GetProperty("Source").GetString());
    }

    [Fact]
    public async Task RejectsUnknownSections()
    {
        var handler = new SystemInfoCommandHandler([],
            NullLogger<SystemInfoCommandHandler>.Instance);

        var response = await handler.HandleAsync(Request(new { sections = new[] { "registry" } }),
            Context(), TestCancellation);

        Assert.Equal(400, response.Error?.Code);
    }

    [Fact]
    public void FlagsLowDiskSpace()
    {
        const long gb = 1024L * 1024 * 1024;
        var low = JsonSerializer.SerializeToElement(
            StorageInfoSection.Describe("C:", "NTFS", 256 * gb, 3 * gb, true));
        var fine = JsonSerializer.SerializeToElement(
            StorageInfoSection.Describe("D:", "NTFS", 500 * gb, 200 * gb, false));

        Assert.True(low.GetProperty("LowSpace").GetBoolean());
        Assert.False(fine.GetProperty("LowSpace").GetBoolean());
        Assert.Equal(40, fine.GetProperty("FreePercent").GetDouble());
    }

    [Fact]
    public async Task RealSectionsCollectOnThisPc()
    {
        var handler = new SystemInfoCommandHandler(
            [new NetworkInfoSection(), new WindowsInfoSection(), new DomainInfoSection(),
             new HardwareInfoSection(), new StorageInfoSection()],
            NullLogger<SystemInfoCommandHandler>.Instance);

        var response = await handler.HandleAsync(Request(null), Context(), TestCancellation);

        Assert.Null(response.Error);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Result));
        var result = document.RootElement.GetProperty("Result");
        foreach (var section in SystemInfoContracts.Sections)
            Assert.NotEqual(JsonValueKind.Null, result.GetProperty(section).ValueKind);
        Assert.Contains(result.GetProperty("domain").GetProperty("JoinType").GetString(),
            new[] { "Domain", "Workgroup", "Unjoined", "Unknown" });
        Assert.False(string.IsNullOrWhiteSpace(
            result.GetProperty("windows").GetProperty("Build").GetString()));
    }

    private static RpcRequest Request(object? parameters) =>
        RpcRequest.Create("test", SystemInfoContracts.Method,
            parameters is null ? null : JsonSerializer.SerializeToElement(parameters));

    private static CommandExecutionContext Context() =>
        new("agent", null, null, DateTimeOffset.UtcNow);

    private sealed class FakeSection(string name, Func<object> collect) : ISystemInfoSection
    {
        public string Name => name;

        public Task<object?> CollectAsync(SystemInfoWarnings warnings,
            CancellationToken cancellationToken) => Task.FromResult<object?>(collect());
    }
}

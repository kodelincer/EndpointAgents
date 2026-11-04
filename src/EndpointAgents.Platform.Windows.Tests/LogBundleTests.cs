using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class LogBundleTests
{
    private static string[] Lines(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd().Split('\n');
    }

    [Fact]
    public void Every_line_is_cleaned()
    {
        var bundle = LogBundleBuilder.Build([("a.log",
            "open C:\\Users\\reza\\x.txt\nmail reza@example.com\npassword=hunter2\nDOMAIN\\reza logged on")]);
        using var zip = new ZipArchive(new MemoryStream(bundle));
        var text = string.Join('\n', Lines(zip.GetEntry("a.log")!));
        Assert.DoesNotContain("reza", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void A_long_part_keeps_its_newest_lines()
    {
        var text = string.Join('\n', Enumerable.Range(0, 1000).Select(i => "line " + i));
        Assert.EndsWith("line 999", LogBundleBuilder.Clean(text, 100));
        Assert.True(Encoding.UTF8.GetByteCount(LogBundleBuilder.Clean(text, 100)) <= 100);
    }

    [Fact]
    public void The_zip_never_goes_over_the_limit()
    {
        // Text that does not compress: random hex lines.
        var random = new Random(7);
        string Noise() => string.Join('\n', Enumerable.Range(0, 2000).Select(_ =>
        {
            var bytes = new byte[32];
            random.NextBytes(bytes);
            // Short enough not to be masked as a token, and cut with spaces.
            return string.Join(' ', Convert.ToHexString(bytes).Chunk(8).Select(part => new string(part)));
        }));
        const long limit = 300 * 1024;
        var bundle = LogBundleBuilder.Build(Enumerable.Range(0, 10).Select(i => ($"part{i}.log", Noise())), limit);
        Assert.True(bundle.Length <= limit, $"bundle is {bundle.Length} bytes");
        using var zip = new ZipArchive(new MemoryStream(bundle));
        Assert.NotEmpty(zip.Entries);
    }

    [Theory]
    [InlineData("""{"requestId":"../../evil"}""")]
    [InlineData("""{"requestId":"ABC"}""")]
    [InlineData("""{"path":"C:\\Windows"}""")]
    public async Task The_request_can_only_be_a_plain_id(string parameters)
    {
        var transport = new EndpointArtifactTransport(new Uri("http://127.0.0.1:1/agent-hub"), new NoToken());
        var handler = new CollectLogsCommandHandler(transport, NullLogger<CollectLogsCommandHandler>.Instance);
        var response = await handler.HandleAsync(
            RpcRequest.Create("1", handler.Method, JsonDocument.Parse(parameters).RootElement),
            new CommandExecutionContext("a", null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(400, response.Error?.Code);
    }

    [Fact]
    public void Device_facts_come_from_this_pc()
    {
        var facts = WindowsDeviceFacts.Read("0.3.0", "PC");
        Assert.Equal("0.3.0", facts.AgentVersion);
        Assert.StartsWith("Windows", facts.OsName);
        Assert.NotNull(facts.OsBuild);
    }

    [Theory]
    [InlineData("19045", "Windows 10")]
    [InlineData("22000", "Windows 11")]
    [InlineData("26100", "Windows 11")]
    [InlineData(null, null)]
    public void Windows_11_is_told_apart_by_its_build(string? build, string? expected) =>
        Assert.Equal(expected, WindowsDeviceFacts.OsName(build));

    [Theory]
    [InlineData("System Product Name", null)]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("OptiPlex 7080", "OptiPlex 7080")]
    public void Board_blanks_are_not_a_model(string value, string? expected) =>
        Assert.Equal(expected, WindowsDeviceFacts.RealValue(value));

    private sealed class NoToken : IEndpointArtifactAuthorizationProvider
    {
        public Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}

using ControlPlane.Api.Chat;
using ControlPlane.Api.Problems;
using ControlPlane.Api.Problems.Forwarding;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Problems;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ProblemTextTests
{
    [Theory]
    [InlineData(@"cannot open C:\Users\reza\AppData\x.log", @"cannot open C:\Users\<user>\AppData\x.log")]
    [InlineData(@"share \\fs01\finance\a.xlsx failed", @"share \\<server>\<share> failed")]
    [InlineData("sent to reza@example.com today", "sent to <email> today")]
    [InlineData(@"logon by CORP\reza failed", "logon by <account> failed")]
    [InlineData("password=hunter2 rejected", "password=[redacted] rejected")]
    public void Scrub_removes_personal_data_and_secrets(string text, string expected) =>
        Assert.Equal(expected, ProblemText.Scrub(text));

    [Fact]
    public void ScrubStack_keeps_method_names_and_cuts_source_paths()
    {
        var stack = """
            System.IO.IOException: bad C:\Users\reza\file.txt
               at EndpointAgents.Service.AgentWorker.Run(String name) in D:\build\src\AgentWorker.cs:line 42
               at System.Threading.Tasks.Task.Run()
            """;
        var clean = ProblemText.ScrubStack(stack);
        Assert.Contains("at EndpointAgents.Service.AgentWorker.Run(String name)", clean);
        Assert.DoesNotContain(@"D:\build", clean);
        Assert.DoesNotContain("line 42", clean);
        Assert.DoesNotContain("reza", clean);
    }

    [Fact]
    public void ScrubStack_limits_lines_and_size()
    {
        var stack = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"   at A.B.Method{i}()"));
        var clean = ProblemText.ScrubStack(stack);
        Assert.True(clean.Split('\n').Length <= ProblemLimits.MaxStackLines);
        Assert.True(clean.Length <= ProblemLimits.MaxStackCharacters);
    }

    [Fact]
    public void FirstOwnFrame_skips_framework_frames()
    {
        var stack = "x\n   at System.Foo.Bar()\n   at EndpointAgents.Core.Thing.Do(Int32 a) in C:\\a.cs:line 1\n   at ControlPlane.Other.X()";
        Assert.Equal("EndpointAgents.Core.Thing.Do", ProblemText.FirstOwnFrame(stack));
        Assert.Null(ProblemText.FirstOwnFrame("   at System.Foo.Bar()"));
    }

    [Fact]
    public void Fingerprint_ignores_changing_numbers_and_separates_what_differs()
    {
        var one = ProblemText.Fingerprint("LoggedError", "Worker", null, null, "failed after 31 s on 7f9a6b1c-0d2e-4f3a-8b5c-112233445566");
        var two = ProblemText.Fingerprint("LoggedError", "Worker", null, null, "failed after 32 s on 00000000-0d2e-4f3a-8b5c-112233445566");
        Assert.Equal(one, two);
        Assert.NotEqual(one, ProblemText.Fingerprint("Crash", "Worker", null, null, "failed after 31 s"));
        Assert.NotEqual(ProblemText.Fingerprint("Crash", "S", "T", "A.B", "x"), ProblemText.Fingerprint("Crash", "S", "T", "A.C", "x"));
        // With a frame the message does not matter: the same bug can say different things.
        Assert.Equal(ProblemText.Fingerprint("Crash", "S", "T", "A.B", "one"), ProblemText.Fingerprint("Crash", "S", "T", "A.B", "two"));
    }

    [Fact]
    public void FromException_is_clean_and_stable()
    {
        static Exception Thrown(string name)
        {
            try { throw new InvalidOperationException($@"no file C:\Users\{name}\a.txt"); }
            catch (Exception ex) { return ex; }
        }
        var now = DateTimeOffset.UtcNow;
        var one = ProblemText.FromException(ProblemKinds.Crash, "Service", Thrown("reza"), "0.3.0", now);
        var two = ProblemText.FromException(ProblemKinds.Crash, "Service", Thrown("sara"), "0.3.0", now);
        Assert.Equal(one.Fingerprint, two.Fingerprint);
        Assert.DoesNotContain("reza", one.Title);
        Assert.DoesNotContain("reza", one.Stack);
        Assert.Equal(typeof(InvalidOperationException).FullName, one.ExceptionType);
    }

    [Theory]
    [InlineData("0.3.0", "0.3.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("0.3", "0.3.1", -1)]
    public void CompareVersions_is_numeric(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(ProblemText.CompareVersions(left, right)));
}

public sealed class ProblemOutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "problem-outbox-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private string PathName => Path.Combine(_root, "outbox.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ProblemReport Report(string fingerprint, string title = "x") =>
        new(fingerprint, ProblemKinds.LoggedError, "S", title, null, null, "0.3.0", _now, _now, 1);

    private ProblemOutbox Create(int maxBytes = ProblemLimits.MaxOutboxBytes, int perHour = ProblemLimits.MaxReportsPerHour) =>
        new(PathName, maxBytes, perHour, () => _now);

    [Fact]
    public void Same_problem_is_counted_not_added()
    {
        var outbox = Create();
        for (var i = 0; i < 37; i++) outbox.Add(Report("a"));
        var report = Assert.Single(outbox.NextBatch());
        Assert.Equal(37, report.Count);
    }

    [Fact]
    public void Survives_a_restart()
    {
        Create().Add(Report("a"), saveNow: true);
        Assert.Equal("a", Assert.Single(Create().NextBatch()).Fingerprint);
    }

    [Fact]
    public void A_damaged_file_is_not_a_crash()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathName, "{ not json");
        Assert.Equal(0, Create().Count);
    }

    [Fact]
    public void Full_file_drops_the_oldest_first()
    {
        var outbox = Create(maxBytes: 1000);
        for (var i = 0; i < 20; i++)
        {
            _now = _now.AddMinutes(1);
            outbox.Add(Report("fp" + i, new string('x', 100)));
        }
        outbox.Flush();
        Assert.True(new FileInfo(PathName).Length <= 1000);
        var kept = outbox.NextBatch().Select(item => item.Fingerprint).ToArray();
        Assert.Contains("fp19", kept);
        Assert.DoesNotContain("fp0", kept);
    }

    [Fact]
    public void Hourly_limit_holds_reports_back_until_the_hour_passes()
    {
        var outbox = Create(perHour: 3);
        for (var i = 0; i < 5; i++) outbox.Add(Report("fp" + i));
        var first = outbox.NextBatch();
        Assert.Equal(3, first.Count);
        outbox.MarkSent(first);
        Assert.Empty(outbox.NextBatch());
        Assert.Equal(2, outbox.Count);
        _now = _now.AddHours(1).AddSeconds(1);
        Assert.Equal(2, outbox.NextBatch().Count);
    }

    [Fact]
    public void Batch_stays_under_the_hub_message_size()
    {
        var outbox = Create();
        for (var i = 0; i < 40; i++) outbox.Add(Report("fp" + i, new string('x', 2000)));
        var batch = outbox.NextBatch();
        Assert.InRange(JsonSerializer.Serialize(batch).Length, 1, ProblemLimits.MaxBatchCharacters + 2 + batch.Count);
        Assert.True(batch.Count < 40);
    }

    [Fact]
    public void What_happened_while_sending_stays_waiting()
    {
        var outbox = Create();
        outbox.Add(Report("a"));
        var batch = outbox.NextBatch();
        outbox.Add(Report("a"));
        outbox.Add(Report("a"));
        outbox.MarkSent(batch);
        Assert.Equal(2, Assert.Single(outbox.NextBatch()).Count);
    }
}

public sealed class ProblemStoreTests : IDisposable
{
    private static readonly ProblemDeviceFacts Facts = new("0.3.0", "PC-1", "Windows 10", "Enterprise", "22H2",
        "19045.1", "X64", "Dell Inc.", "OptiPlex 7080");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "problem-store-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<ProblemStore> CreateAsync(ProblemOptions? options = null)
    {
        var store = new ProblemStore(Options.Create(options ?? new ProblemOptions()),
            Options.Create(new ChatOptions { DatabasePath = Path.Combine(_root, "control-plane.db") }));
        await store.InitializeAsync();
        return store;
    }

    private ProblemReport Report(string fingerprint = "fp", string version = "0.3.0", DateTimeOffset? seen = null, int count = 1, string? stack = null) =>
        new(fingerprint, ProblemKinds.Crash, "Service", "boom", "E", stack, version, seen ?? _now, seen ?? _now, count);

    [Fact]
    public async Task Reports_live_in_their_own_file()
    {
        var store = await CreateAsync();
        Assert.Equal(Path.Combine(_root, "problems.db"), store.DatabasePath);
        Assert.False(File.Exists(Path.Combine(_root, "control-plane.db")));
    }

    [Fact]
    public async Task Counts_up_one_row_per_problem_pc_and_version()
    {
        var store = await CreateAsync();
        await store.AddAsync("a", Facts, [Report(count: 3)], null, _now, default);
        await store.AddAsync("a", Facts, [Report(count: 4)], null, _now, default);
        await store.AddAsync("b", Facts, [Report()], null, _now, default);
        var occurrences = await store.ListOccurrencesAsync("fp", default);
        Assert.Equal(2, occurrences.Count);
        Assert.Equal(7, occurrences.Single(item => item.AgentId == "a").Count);
        Assert.Equal(8, (await store.GetProblemAsync("fp", default))!.TotalCount);
    }

    [Fact]
    public async Task Fixed_problem_reopens_on_the_fixed_version_or_later_only()
    {
        var store = await CreateAsync();
        await store.AddAsync("a", Facts, [Report()], null, _now, default);
        await store.MarkFixedAsync("fp", "0.4.0", "reza", _now, default);

        await store.AddAsync("a", Facts, [Report(version: "0.3.5")], null, _now, default);
        Assert.Equal(ProblemStatuses.Fixed, (await store.GetProblemAsync("fp", default))!.Status);

        await store.AddAsync("a", Facts, [Report(version: "0.4.0")], null, _now, default);
        var reopened = (await store.GetProblemAsync("fp", default))!;
        Assert.Equal(ProblemStatuses.Open, reopened.Status);
        Assert.NotNull(reopened.ReopenedAtUtc);
    }

    [Fact]
    public async Task Retention_deletes_old_reports_and_empty_problems()
    {
        var store = await CreateAsync(new ProblemOptions { RetentionDays = 90 });
        await store.AddAsync("a", Facts, [Report("old", seen: _now.AddDays(-91))], null, _now, default);
        await store.AddAsync("a", Facts, [Report("new", seen: _now.AddDays(-89))], null, _now, default);
        await store.EnforceLimitsAsync(_now, default);
        Assert.Null(await store.GetProblemAsync("old", default));
        Assert.NotNull(await store.GetProblemAsync("new", default));
    }

    [Fact]
    public async Task Size_limit_deletes_oldest_first_and_shrinks_the_file()
    {
        var store = await CreateAsync(new ProblemOptions { MaxDatabaseMegabytes = 1 });
        var big = new string('x', 3000);
        for (var day = 0; day < 40; day++)
        {
            var reports = Enumerable.Range(0, 20)
                .Select(i => Report($"fp{day}-{i}", seen: _now.AddDays(day - 60), stack: big)).ToArray();
            await store.AddAsync("a", Facts, reports, null, _now, default);
        }
        await store.EnforceLimitsAsync(_now, default);
        Assert.True(store.DatabaseBytes() <= 1024 * 1024 + 64 * 1024, $"file is {store.DatabaseBytes()} bytes");
        Assert.Null(await store.GetProblemAsync("fp0-0", default));
        Assert.NotNull(await store.GetProblemAsync("fp39-0", default));
    }

    [Fact]
    public async Task Old_log_bundles_are_deleted_with_their_files()
    {
        var store = await CreateAsync(new ProblemOptions { LogBundleRetentionDays = 30 });
        var oldId = new string('a', 32);
        var newId = new string('b', 32);
        foreach (var (id, at) in new[] { (oldId, _now.AddDays(-31)), (newId, _now.AddDays(-29)) })
        {
            await store.CreateBundleAsync(new LogBundle(id, "a", "PC", "reza", at, LogBundleStatuses.Requested, 0, null, null), default);
            await File.WriteAllBytesAsync(store.BundlePath(id), [80, 75]);
            await store.CompleteBundleAsync(id, LogBundleStatuses.Ready, 2, null, at, default);
        }
        Assert.Equal(1, await store.DeleteOldBundlesAsync(_now, default));
        Assert.False(File.Exists(store.BundlePath(oldId)));
        Assert.True(File.Exists(store.BundlePath(newId)));
    }

    [Fact]
    public async Task Recorder_applies_the_pc_limit_and_cleans_again()
    {
        var store = await CreateAsync();
        var recorder = Recorder(store, new ProblemOptions { MaxReportsPerPcPerHour = 2 });
        var dirty = Report("fp1") with { Title = @"cannot open C:\Users\reza\a.txt", Kind = ProblemKinds.Crash };
        var kept = await recorder.AddFromAgentAsync("a", Facts,
            [dirty, Report("fp2"), Report("fp3")], default);
        Assert.Equal(2, kept);
        Assert.Equal(1, recorder.DroppedByLimit);
        Assert.Null(await store.GetProblemAsync("fp3", default));
        Assert.DoesNotContain("reza", (await store.GetProblemAsync("fp1", default))!.Title);
    }

    [Fact]
    public async Task Recorder_refuses_kinds_a_pc_may_not_send()
    {
        var store = await CreateAsync();
        var kept = await Recorder(store, new ProblemOptions()).AddFromAgentAsync("a", Facts,
            [Report("fp1") with { Kind = ProblemKinds.UserReported }], default);
        Assert.Equal(0, kept);
    }

    [Fact]
    public async Task Recorder_stops_saving_when_the_disk_is_nearly_full()
    {
        var store = await CreateAsync();
        var recorder = Recorder(store, new ProblemOptions { MinFreeDiskMegabytes = 1024 });
        recorder.FreeDiskBytes = () => 10;
        await recorder.AddFromAgentAsync("a", Facts, [Report()], default);
        Assert.True(recorder.DiskLow);
        Assert.Equal(1, recorder.DroppedDiskFull);
        Assert.Null(await store.GetProblemAsync("fp", default));
    }

    [Fact]
    public async Task Recorder_never_lets_a_pc_put_reports_in_the_future()
    {
        var store = await CreateAsync();
        var future = DateTimeOffset.UtcNow.AddYears(1);
        await Recorder(store, new ProblemOptions()).AddFromAgentAsync("a", Facts, [Report(seen: future)], default);
        Assert.True((await store.GetProblemAsync("fp", default))!.LastSeenUtc <= DateTimeOffset.UtcNow);
    }

    private ProblemRecorder Recorder(ProblemStore store, ProblemOptions options)
    {
        var settings = new LogServerSettingsStore(
            Options.Create(new ChatOptions { DatabasePath = Path.Combine(_root, "control-plane.db") }),
            new EphemeralDataProtectionProvider());
        var forwarder = new LogForwarder(settings, TimeProvider.System, NullLogger<LogForwarder>.Instance);
        return new ProblemRecorder(store, forwarder, Options.Create(options), TimeProvider.System,
            NullLogger<ProblemRecorder>.Instance);
    }
}

public sealed class RepairOutcomeTests
{
    private static EndpointAgents.Shared.Rpc.RpcResponse Answer(string state) =>
        EndpointAgents.Shared.Rpc.RpcResponse.Success("1", new { State = state });

    [Fact]
    public void A_repair_that_changed_nothing_counts_as_a_problem()
    {
        Assert.True(ProblemRecorder.RanButDidNotFix("system.windows_activation_bugfix_run", Answer("CompletedUnresolved")));
        Assert.False(ProblemRecorder.RanButDidNotFix("system.windows_activation_bugfix_run", Answer("Resolved")));
        Assert.False(ProblemRecorder.RanButDidNotFix("endpoint.get_info", Answer("CompletedUnresolved")));
    }
}

public sealed class ProblemViewTests
{
    private static ProblemOccurrence Occurrence(string agent, string os, string version, string model) =>
        new("fp", agent, agent, "0.3.0", "Windows " + os, null, version, null, null, "Dell", model, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static StoredProblemDevice Device(string agent, string os, string version, string model) =>
        new(agent, new ProblemDeviceFacts("0.3.0", agent, "Windows " + os, null, version, null, null, "Dell", model), DateTimeOffset.UtcNow);

    [Fact]
    public void Says_only_when_the_rest_of_the_fleet_differs()
    {
        var occurrences = new[] { Occurrence("a", "10", "22H2", "OptiPlex 7080"), Occurrence("b", "10", "22H2", "Latitude 5420") };
        var fleet = new[]
        {
            Device("a", "10", "22H2", "OptiPlex 7080"), Device("b", "10", "22H2", "Latitude 5420"),
            Device("c", "11", "24H2", "OptiPlex 7080")
        };
        Assert.Equal(["only Windows 10 22H2"], ProblemViews.OnlyOn(occurrences, fleet));
    }

    [Fact]
    public void Names_one_model_and_says_nothing_for_a_uniform_fleet()
    {
        var occurrences = new[] { Occurrence("a", "11", "24H2", "OptiPlex 7080") };
        Assert.Contains("only Dell OptiPlex 7080", ProblemViews.OnlyOn(occurrences,
            [Device("a", "11", "24H2", "OptiPlex 7080"), Device("b", "11", "24H2", "Latitude 5420")]));
        Assert.Empty(ProblemViews.OnlyOn(occurrences, [Device("a", "11", "24H2", "OptiPlex 7080")]));
    }

    [Theory]
    [InlineData("Dell Inc.", "OptiPlex 7080", "Dell Inc. OptiPlex 7080")]
    [InlineData("HP", "HP ProBook 450", "HP ProBook 450")]
    [InlineData(null, "ThinkPad", "ThinkPad")]
    public void Model_label_does_not_repeat_the_maker(string? maker, string model, string expected) =>
        Assert.Equal(expected, ProblemViews.ModelLabel(maker, model));

    [Fact]
    public void Quiet_means_offline_past_the_limit_or_never_heard_from()
    {
        var now = DateTimeOffset.UtcNow;
        FleetPc Pc(string name, bool online, DateTimeOffset? seen) => new(name, name, online, "0.3.0", seen, null);
        var quiet = FleetViews.Quiet([
            Pc("online", true, now.AddDays(-9)), Pc("recent", false, now.AddHours(-23)),
            Pc("gone", false, now.AddHours(-25)), Pc("never", false, null)], now, TimeSpan.FromHours(24));
        Assert.Equal(["never", "gone"], quiet.Select(pc => pc.AgentId));
    }

    [Fact]
    public void Old_version_follows_the_target_and_the_pilot_list()
    {
        FleetPc Pc(string name, string version) => new(name, name, true, version, null, null);
        var settings = new ControlPlane.Api.Updates.AgentUpdateSettings("0.3.0", "0.4.0", ["pilot"], false, null, null);
        var old = FleetViews.OnOldVersion([Pc("behind", "0.2.0"), Pc("current", "0.3.0"), Pc("pilot", "0.3.0")], settings);
        Assert.Equal(["behind", "pilot"], old.Select(pc => pc.AgentId));
    }

    [Fact]
    public void Without_a_target_old_means_behind_the_newest_seen()
    {
        FleetPc Pc(string name, string version) => new(name, name, true, version, null, null);
        var old = FleetViews.OnOldVersion([Pc("a", "0.10.0"), Pc("b", "0.9.0")],
            ControlPlane.Api.Updates.AgentUpdateSettings.Default);
        Assert.Equal("b", Assert.Single(old).AgentId);
    }
}

public sealed class LogServerFormatTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static LogForwardEvent Event(string title = "boom", string kind = ProblemKinds.Crash, string? stack = "at A.B()") =>
        new("fp1", kind, "Service", title, "System.Exception", stack, "agent-1", "PC 1", "0.3.0", "Windows 11", "Enterprise",
            "24H2", "26100.1", "X64", "Dell Inc.", "OptiPlex 7080", 37, Seen, Seen);

    [Fact]
    public void Splunk_uses_concatenated_event_objects_with_the_hec_fields()
    {
        var body = SplunkFormat.Body([Event(), Event("second")], "helpdesk");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body), new JsonReaderOptions { AllowMultipleValues = true });
        var entries = new List<JsonElement>();
        while (reader.Read())
        {
            entries.Add(JsonElement.ParseValue(ref reader));
        }
        Assert.Equal(2, entries.Count);
        var first = entries[0];
        Assert.Equal(Seen.ToUnixTimeSeconds(), first.GetProperty("time").GetDouble());
        Assert.Equal("PC 1", first.GetProperty("host").GetString());
        Assert.Equal("endpointagents:problem", first.GetProperty("sourcetype").GetString());
        Assert.Equal("helpdesk", first.GetProperty("index").GetString());
        var fields = first.GetProperty("event");
        Assert.Equal("boom", fields.GetProperty("title").GetString());
        Assert.Equal("OptiPlex 7080", fields.GetProperty("model").GetString());
        Assert.Equal(37, fields.GetProperty("count").GetInt32());
        Assert.Equal("Splunk abc", SplunkFormat.AuthorizationValue("abc"));
        Assert.Equal("/services/collector/event", SplunkFormat.Path);
    }

    [Fact]
    public void Splunk_leaves_out_the_index_when_none_is_set() =>
        Assert.DoesNotContain("\"index\"", SplunkFormat.Body([Event()], null));

    [Fact]
    public void Gelf_has_the_required_fields_and_underscored_extras()
    {
        var message = JsonDocument.Parse(GelfFormat.Body(Event())).RootElement;
        Assert.Equal("1.1", message.GetProperty("version").GetString());
        Assert.Equal("PC 1", message.GetProperty("host").GetString());
        Assert.Equal("boom", message.GetProperty("short_message").GetString());
        Assert.Equal("at A.B()", message.GetProperty("full_message").GetString());
        Assert.Equal(2, message.GetProperty("level").GetInt32());
        Assert.Equal(Seen.ToUnixTimeSeconds(), message.GetProperty("timestamp").GetDouble());
        Assert.Equal("0.3.0", message.GetProperty("_agent_version").GetString());
        Assert.Equal("Dell Inc.", message.GetProperty("_manufacturer").GetString());
        // GELF forbids an extra field named _id, and null fields are left out.
        Assert.False(message.TryGetProperty("_id", out _));
        Assert.False(JsonDocument.Parse(GelfFormat.Body(Event() with { Model = null })).RootElement.TryGetProperty("_model", out _));
    }

    [Fact]
    public void Syslog_follows_rfc_5424()
    {
        var message = SyslogFormat.Message(Event());
        // local0 (16) * 8 + critical (2) = 130; a space in the host becomes a dash.
        Assert.StartsWith("<130>1 2026-11-04T08:00:00.000Z PC-1 endpointagents - Crash - {", message);
        var json = JsonDocument.Parse(message[message.IndexOf('{')..]).RootElement;
        Assert.Equal("fp1", json.GetProperty("fingerprint").GetString());
        Assert.Equal("boom", json.GetProperty("title").GetString());
    }

    [Fact]
    public void Syslog_tcp_frame_starts_with_the_byte_length()
    {
        var frame = SyslogFormat.TcpFrame(Event("آزمایش"));
        var text = Encoding.UTF8.GetString(frame);
        var space = text.IndexOf(' ');
        Assert.Equal(frame.Length - space - 1, int.Parse(text[..space]));
    }

    [Fact]
    public void Syslog_udp_packet_is_cut_to_a_safe_size_and_stays_json()
    {
        var packet = SyslogFormat.UdpPacket(Event(new string('x', 5000)));
        Assert.True(packet.Length <= SyslogFormat.MaxUdpBytes);
        var text = Encoding.UTF8.GetString(packet);
        Assert.Equal("fp1", JsonDocument.Parse(text[text.IndexOf('{')..]).RootElement.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task Syslog_really_arrives_over_tcp_and_udp()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var accepted = tcp.AcceptTcpClientAsync();
        await LogServerClient.SendAsync(new LogServerSettings(true, LogServerTypes.Syslog, "127.0.0.1", tcpPort,
            LogServerProtocols.Tcp, null, null), null, [Event()], default);
        using (var client = await accepted)
        using (var reader = new StreamReader(client.GetStream()))
            Assert.Contains("endpointagents - Crash", await reader.ReadToEndAsync());

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await LogServerClient.SendAsync(new LogServerSettings(true, LogServerTypes.Syslog, "127.0.0.1", udpPort,
            LogServerProtocols.Udp, null, null), null, [Event()], default);
        var packet = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("endpointagents - Crash", Encoding.UTF8.GetString(packet.Buffer));
    }

    [Theory]
    [InlineData(LogServerTypes.Splunk, "splunk.corp", 8088, LogServerProtocols.Https, true, null)]
    [InlineData(LogServerTypes.Splunk, "splunk.corp", 8088, LogServerProtocols.Https, false, "token")]
    [InlineData(LogServerTypes.Splunk, "splunk.corp", 8088, LogServerProtocols.Http, true, "HTTPS")]
    [InlineData(LogServerTypes.Splunk, "127.0.0.1", 8088, LogServerProtocols.Http, true, null)]
    [InlineData(LogServerTypes.Graylog, "graylog", 12201, LogServerProtocols.Http, false, null)]
    [InlineData(LogServerTypes.Syslog, "10.0.0.5", 514, LogServerProtocols.Udp, false, null)]
    [InlineData(LogServerTypes.Syslog, "10.0.0.5", 514, LogServerProtocols.Https, false, "protocol")]
    [InlineData(LogServerTypes.Syslog, "http://x/y", 514, LogServerProtocols.Tcp, false, "name")]
    [InlineData(LogServerTypes.Graylog, "graylog", 70000, LogServerProtocols.Http, false, "port")]
    [InlineData("Other", "graylog", 1, LogServerProtocols.Http, false, "type")]
    public void Settings_are_checked(string type, string host, int port, string protocol, bool hasToken, string? problem)
    {
        var result = LogServerValidation.Problem(new LogServerSettings(true, type, host, port, protocol, null, null), hasToken);
        if (problem is null) Assert.Null(result);
        else Assert.Contains(problem, result, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LogForwarderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "log-forwarder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<(LogServerSettingsStore Store, LogForwarder Forwarder)> CreateAsync()
    {
        var store = new LogServerSettingsStore(
            Options.Create(new ChatOptions { DatabasePath = Path.Combine(_root, "cp.db") }),
            new EphemeralDataProtectionProvider());
        await store.InitializeAsync();
        return (store, new LogForwarder(store, TimeProvider.System, NullLogger<LogForwarder>.Instance));
    }

    private static LogForwardEvent Event(string fingerprint = "fp") => new(fingerprint, ProblemKinds.Crash, "S", "t", null, null, "a",
        "PC", "0.3.0", null, null, null, null, null, null, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static readonly LogServerSettings On =
        new(true, LogServerTypes.Graylog, "127.0.0.1", 12201, LogServerProtocols.Http, null, null);

    [Fact]
    public async Task Token_is_protected_at_rest_and_kept_when_the_box_stays_empty()
    {
        var (store, _) = await CreateAsync();
        await store.SaveAsync(On, "secret-token-value", default);
        await store.SaveAsync(On with { Port = 1 }, null, default);
        Assert.Equal("secret-token-value", await store.GetTokenAsync(default));
        Assert.DoesNotContain("secret-token-value", JsonSerializer.Serialize(await store.GetAsync(default)));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "cp.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT setting_json || COALESCE(protected_token, '') FROM log_server_settings;";
            Assert.DoesNotContain("secret-token-value", (string)(await command.ExecuteScalarAsync())!);
        }
        await store.SaveAsync(On, "", default);
        Assert.Null(await store.GetTokenAsync(default));
    }

    [Fact]
    public async Task Off_by_default_and_queues_nothing()
    {
        var (store, forwarder) = await CreateAsync();
        Assert.False((await store.GetAsync(default)).Enabled);
        await forwarder.ReloadAsync(default);
        forwarder.Enqueue(Event());
        Assert.Equal(0, forwarder.GetStatus().Waiting);
    }

    [Fact]
    public async Task A_server_that_is_down_drops_the_oldest_and_never_blocks()
    {
        var (store, forwarder) = await CreateAsync();
        await store.SaveAsync(On, null, default);
        await forwarder.ReloadAsync(default);
        var started = DateTime.UtcNow;
        for (var i = 0; i < LogForwarder.QueueCapacity + 50; i++) forwarder.Enqueue(Event("fp" + i));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        var status = forwarder.GetStatus();
        Assert.Equal(LogForwarder.QueueCapacity, status.Waiting);
        Assert.Equal(50, status.Dropped);
    }

    [Fact]
    public async Task Shows_down_with_the_problem_then_recovers()
    {
        var (store, forwarder) = await CreateAsync();
        await store.SaveAsync(On, "the-secret-token", default);
        var healthy = false;
        var sent = new List<string>();
        forwarder.Send = (_, token, events, _) =>
        {
            Assert.Equal("the-secret-token", token);
            if (!healthy) throw new HttpRequestException("The log server answered HTTP 503.");
            lock (sent) sent.AddRange(events.Select(item => item.Fingerprint));
            return Task.CompletedTask;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await forwarder.StartAsync(stop.Token);
        await forwarder.ReloadAsync(stop.Token);
        forwarder.Enqueue(Event("first"));
        while (!forwarder.GetStatus().Down) await Task.Delay(20, stop.Token);
        Assert.Contains("503", forwarder.GetStatus().LastError);
        Assert.DoesNotContain("the-secret-token", forwarder.GetStatus().LastError);

        healthy = true;
        await forwarder.ReloadAsync(stop.Token);
        while (true) { lock (sent) if (sent.Contains("first")) break; await Task.Delay(20, stop.Token); }
        Assert.False(forwarder.GetStatus().Down);
        await forwarder.StopAsync(CancellationToken.None);
    }
}

public sealed class ProblemApiTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ControlPlaneApiFactory _factory;

    public ProblemApiTests(ControlPlaneApiFactory factory) => _factory = factory;

    private static HttpContent Json(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json);
        return content;
    }

    [Fact]
    public async Task Install_report_needs_no_sign_in_and_shows_on_the_page()
    {
        var anonymous = _factory.CreateClient();
        var machine = "PC-" + Guid.NewGuid().ToString("N")[..8];
        var body = $$"""{"machineName":"{{machine}}","step":"Publish service","message":"failed in C:\\Users\\reza\\x","agentVersion":"0.3.0"}""";
        var response = await anonymous.PostAsync("/api/install-reports", Json(body));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var admin = await _factory.CreateOperatorClientAsync();
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/problems?pc={machine}");
        var row = Assert.Single(page.GetProperty("problems").EnumerateArray());
        Assert.Equal(ProblemKinds.InstallFailed, row.GetProperty("kind").GetString());
        Assert.Equal("Publish service", row.GetProperty("source").GetString());
        Assert.DoesNotContain("reza", row.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("""{"machineName":"PC","step":"<script>"}""")]
    [InlineData("""{"machineName":"a b/c","step":"Publish"}""")]
    [InlineData("""{"step":"Publish"}""")]
    [InlineData("not json")]
    public async Task Install_report_refuses_anything_else(string body)
    {
        var response = await _factory.CreateClient().PostAsync("/api/install-reports", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Install_report_refuses_a_large_body()
    {
        var body = $$"""{"machineName":"PC","step":"S","message":"{{new string('x', 5000)}}"}""";
        var response = await _factory.CreateClient().PostAsync("/api/install-reports", Json(body));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Pages_need_the_right_role()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/problems")).StatusCode);
        var member = await _factory.CreateOperatorClientAsync(OperatorRoles.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/problems")).StatusCode);
        var reviewer = await _factory.CreateOperatorClientAsync(OperatorRoles.Reviewer);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync("/api/problems")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync("/api/problems/pcs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync("/api/settings/log-server")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.PostAsync($"/api/problems/pcs/x/logs", null)).StatusCode);
    }

    [Fact]
    public async Task Log_server_token_is_never_returned()
    {
        var admin = await _factory.CreateOperatorClientAsync();
        var save = await admin.PutAsJsonAsync("/api/settings/log-server", new
        {
            enabled = false, type = "Splunk", host = "splunk.corp", port = 8088, protocol = "https",
            token = "very-secret-hec-token"
        });
        save.EnsureSuccessStatusCode();
        var saved = await save.Content.ReadAsStringAsync();
        var read = await admin.GetStringAsync("/api/settings/log-server");
        Assert.DoesNotContain("very-secret-hec-token", saved);
        Assert.DoesNotContain("very-secret-hec-token", read);
        Assert.True(JsonDocument.Parse(read).RootElement.GetProperty("hasToken").GetBoolean());
    }

    [Fact]
    public async Task Log_server_test_reports_a_failure_in_words()
    {
        var admin = await _factory.CreateOperatorClientAsync();
        using var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var response = await admin.PostAsJsonAsync("/api/settings/log-server/test", new
        {
            enabled = true, type = "Syslog", host = "127.0.0.1", port, protocol = "tcp"
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Log_server_test_sends_the_real_splunk_request()
    {
        await using var listener = await FakeHttpServer.StartAsync();
        var admin = await _factory.CreateOperatorClientAsync();
        var response = await admin.PostAsJsonAsync("/api/settings/log-server/test", new
        {
            enabled = true, type = "Splunk", host = "127.0.0.1", port = listener.Port, protocol = "http",
            token = "hec-token-1", index = "helpdesk"
        });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("message").GetString());
        var request = await listener.Request;
        Assert.StartsWith("POST /services/collector/event ", request);
        Assert.Contains("Authorization: Splunk hec-token-1", request);
        Assert.Contains("\"sourcetype\":\"endpointagents:problem\"", request);
    }

    [Fact]
    public async Task Log_server_test_sends_the_real_gelf_request()
    {
        await using var listener = await FakeHttpServer.StartAsync();
        var admin = await _factory.CreateOperatorClientAsync();
        var response = await admin.PostAsJsonAsync("/api/settings/log-server/test", new
        {
            enabled = true, type = "Graylog", host = "127.0.0.1", port = listener.Port, protocol = "http"
        });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("message").GetString());
        var request = await listener.Request;
        Assert.StartsWith("POST /gelf ", request);
        Assert.Contains("\"short_message\":", request);
    }

    [Fact]
    public async Task Upload_route_is_closed_without_a_device_token()
    {
        var response = await _factory.CreateClient().PutAsync(
            $"{AgentLogRoutes.Root}/{new string('a', 32)}", new ByteArrayContent([80, 75]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_pc_that_was_asked_can_upload_and_only_once()
    {
        var chat = _factory.Services.GetRequiredService<ChatStore>();
        async Task<EnrollmentRedemption> Enroll()
        {
            var issue = await chat.CreateEnrollmentAsync(TimeSpan.FromMinutes(10), default);
            return (await chat.RedeemEnrollmentAsync(new EnrollmentRequest(issue!.EnrollmentCode,
                Guid.NewGuid().ToString("D"), "test-machine"), default))!;
        }
        var asked = await Enroll();
        var other = await Enroll();
        var store = _factory.Services.GetRequiredService<ProblemStore>();
        var bundleId = Guid.NewGuid().ToString("N");
        await store.CreateBundleAsync(new LogBundle(bundleId, asked.AgentId, "PC", "reza", DateTimeOffset.UtcNow,
            LogBundleStatuses.Requested, 0, null, null), default);

        Task<HttpResponseMessage> Upload(string token, byte[] bytes)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"{AgentLogRoutes.Root}/{bundleId}")
            {
                Content = new ByteArrayContent(bytes)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return _factory.CreateClient().SendAsync(request);
        }

        byte[] zip = [(byte)'P', (byte)'K', 3, 4];
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(other.DeviceToken, zip)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Upload(asked.DeviceToken, zip)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(asked.DeviceToken, zip)).StatusCode);

        var bundle = await store.GetBundleAsync(bundleId, default);
        Assert.Equal(LogBundleStatuses.Ready, bundle!.Status);
        var admin = await _factory.CreateOperatorClientAsync();
        var download = await admin.GetAsync($"/api/problems/logs/{bundleId}/download");
        download.EnsureSuccessStatusCode();
        Assert.Equal(zip, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_upload_that_is_not_a_zip_is_refused()
    {
        var chat = _factory.Services.GetRequiredService<ChatStore>();
        var issue = await chat.CreateEnrollmentAsync(TimeSpan.FromMinutes(10), default);
        var device = (await chat.RedeemEnrollmentAsync(new EnrollmentRequest(issue!.EnrollmentCode,
            Guid.NewGuid().ToString("D"), "test-machine"), default))!;
        var store = _factory.Services.GetRequiredService<ProblemStore>();
        var bundleId = Guid.NewGuid().ToString("N");
        await store.CreateBundleAsync(new LogBundle(bundleId, device.AgentId, "PC", "reza", DateTimeOffset.UtcNow,
            LogBundleStatuses.Requested, 0, null, null), default);
        var request = new HttpRequestMessage(HttpMethod.Put, $"{AgentLogRoutes.Root}/{bundleId}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>nope</html>"))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.DeviceToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request)).StatusCode);
        Assert.Equal(LogBundleStatuses.Failed, (await store.GetBundleAsync(bundleId, default))!.Status);
        Assert.False(File.Exists(store.BundlePath(bundleId)));
    }

    [Fact]
    public void Remote_pcs_reach_only_the_two_narrow_routes()
    {
        Assert.True(ControlPlane.Api.System.OperatorAccessPolicy.AllowsRemoteRequest("/api/install-reports"));
        Assert.True(ControlPlane.Api.System.OperatorAccessPolicy.AllowsRemoteRequest(AgentLogRoutes.Root + "/x"));
        Assert.False(ControlPlane.Api.System.OperatorAccessPolicy.AllowsRemoteRequest("/api/problems"));
        Assert.False(ControlPlane.Api.System.OperatorAccessPolicy.AllowsRemoteRequest("/api/settings/log-server"));
    }

    [Fact]
    public async Task Fixed_and_copy_for_developer_work_through_the_api()
    {
        var machine = "PC-" + Guid.NewGuid().ToString("N")[..8];
        var recorder = _factory.Services.GetRequiredService<ProblemRecorder>();
        await recorder.RecordAsync("agent-" + machine, machine, "0.3.0", ProblemKinds.OperationFailed, "net.test",
            "net.test failed with code 408: timeout", "code-408-" + machine);
        var admin = await _factory.CreateOperatorClientAsync();
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/problems?pc={machine}");
        var fingerprint = Assert.Single(page.GetProperty("problems").EnumerateArray()).GetProperty("fingerprint").GetString();

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/problems/{fingerprint}");
        Assert.Contains("net.test failed", detail.GetProperty("developerText").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/problems/{fingerprint}/fixed", new { version = "soon" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/problems/{fingerprint}/fixed", new { version = "0.4.0" })).StatusCode);
        var open = await admin.GetFromJsonAsync<JsonElement>($"/api/problems?pc={machine}&status=Open");
        Assert.Empty(open.GetProperty("problems").EnumerateArray());
    }

    /// <summary>Answers one HTTP request with 200 and keeps what it received.</summary>
    private sealed class FakeHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private FakeHttpServer(TcpListener listener)
        {
            _listener = listener;
            Request = AnswerAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Task<string> Request { get; }

        public static Task<FakeHttpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeHttpServer(listener));
        }

        private async Task<string> AnswerAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var text = new StringBuilder();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var all = text.ToString();
                var end = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var length = all.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var expected = length is null ? 0 : int.Parse(length.Split(':')[1].Trim());
                if (Encoding.UTF8.GetByteCount(all[(end + 4)..]) >= expected) break;
            }
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return text.ToString();
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}

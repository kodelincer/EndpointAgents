using EndpointAgents.Shared.Support;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class EndpointActivityJournalTests
{
    [Fact]
    public void PersistsAndReadsActivityEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "EndpointAgents-journal-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "events.jsonl");
        try
        {
            var first = new EndpointActivityEntry(DateTimeOffset.UtcNow,
                "Information", "AgentWorker", 10, "Connected.");
            var second = new EndpointActivityEntry(DateTimeOffset.UtcNow,
                "Warning", "CommandDispatcher", 20, "Command failed.");

            Assert.True(EndpointActivityJournal.TryAppend(path, first));
            Assert.True(EndpointActivityJournal.TryAppend(path, second));

            Assert.Equal([first, second], EndpointActivityJournal.Read(path,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

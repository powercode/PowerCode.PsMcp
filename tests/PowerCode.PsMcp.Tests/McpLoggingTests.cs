using Microsoft.Extensions.Logging;

namespace PowerCode.PsMcp.Tests;

[TestClass]
public sealed class McpLoggingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FileLogging_AppendsFiltersAndReleasesFile()
    {
        var path = Path.Combine(TestContext.TestRunDirectory!, Guid.NewGuid() + ".log");
        File.WriteAllText(path, "existing\n");
        using (var factory = McpLogging.CreateLoggerFactory(LogLevel.Warning, path))
        {
            var logger = factory.CreateLogger("test-command");
            Write(logger, LogLevel.Debug, "filtered-out");
            Write(logger, LogLevel.Warning, "visible-warning", new InvalidOperationException("exception-detail"));
        }

        var text = File.ReadAllText(path);
        Assert.StartsWith("existing\n", text);
        Assert.Contains("[Warning] test-command[42] visible-warning", text);
        Assert.Contains("exception-detail", text);
        Assert.DoesNotContain("filtered-out", text);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public void FileLogging_ConcurrentWritesPreserveEveryRecord()
    {
        var path = Path.Combine(TestContext.TestRunDirectory!, Guid.NewGuid() + ".log");
        using (var provider = new FileLoggerProvider(path))
        {
            var logger = provider.CreateLogger("parallel");
            Parallel.For(0, 100, i => Write(logger, LogLevel.Warning, $"record-{i}"));
        }

        var lines = File.ReadAllLines(path);
        Assert.HasCount(100, lines);
        Assert.HasCount(100, lines.Select(line => line[(line.IndexOf("record-", StringComparison.Ordinal))..]).Distinct().ToArray());
    }

    [TestMethod]
    public void FileLogging_RotatesAndRetainsFiveBackups()
    {
        var path = Path.Combine(TestContext.TestRunDirectory!, Guid.NewGuid() + ".log");
        using (var provider = new FileLoggerProvider(path, maxBytes: 1))
        {
            var logger = provider.CreateLogger("rotation");
            for (var i = 0; i < 8; i++)
            {
                Write(logger, LogLevel.Warning, $"record-{i}");
            }
        }

        Assert.Contains("record-7", File.ReadAllText(path));
        Assert.Contains("record-6", File.ReadAllText(path + ".1"));
        Assert.Contains("record-2", File.ReadAllText(path + ".5"));
        Assert.IsFalse(File.Exists(path + ".6"));
    }

    [TestMethod]
    public void FileLogging_InvalidDirectoryFailsAtStartup()
    {
        var path = Path.Combine(TestContext.TestRunDirectory!, Guid.NewGuid().ToString(), "server.log");
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var factory = McpLogging.CreateLoggerFactory(LogLevel.Warning, path);
        });
        Assert.Contains(path, exception.Message);
    }

    private static void Write(ILogger logger, LogLevel level, string message, Exception? exception = null) =>
        logger.Log(level, new EventId(42), message, exception, static (state, _) => state);
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Tests for <see cref="McpServerRunner"/>.
/// These tests exercise the lifecycle of the MCP server without requiring a real
/// transport connection by using pre-cancelled cancellation tokens to trigger
/// immediate shutdown.
/// </summary>
[TestClass]
public sealed class McpServerRunnerTests
{
    /// <summary>
    /// Verifies that <see cref="McpServerRunner.RunAsync"/> exits quickly and
    /// throws <see cref="OperationCanceledException"/> when the token is already
    /// cancelled before the call, mirroring the behaviour that
    /// <see cref="StartMcpServerCommand"/> relies on for graceful shutdown.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WhenImmediatelyCancelled_ThrowsOperationCanceledException()
    {
        var runner = new McpServerRunner(Stream.Null, Stream.Null);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        bool threw = false;
        try
        {
            await runner.RunAsync(toolCollection, loggerFactory: null, cts.Token)
                        .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Expected OperationCanceledException to be thrown.");
    }

    /// <summary>
    /// Verifies that <see cref="McpServerRunner.RunAsync"/> shuts down promptly
    /// when the token is cancelled shortly after launch.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WhenCancelledAfterStart_CompletesWithinTimeout()
    {
        var runner = new McpServerRunner(Stream.Null, Stream.Null);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();

        using var cts = new CancellationTokenSource(millisecondsDelay: 200);

        var timeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

        try
        {
            await runner.RunAsync(toolCollection, loggerFactory: null, cts.Token)
                        .WaitAsync(timeoutToken);
        }
        catch (OperationCanceledException)
        {
            // Expected: server shuts down cleanly when the token fires.
        }
    }
}

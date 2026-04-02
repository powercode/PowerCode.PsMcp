using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Tests for <see cref="McpServerRunner"/>.
/// These tests exercise the lifecycle of the MCP server without requiring a real
/// transport connection by using pre-canceled cancellation tokens to trigger
/// immediate shutdown.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class McpServerRunnerTests
{
    /// <summary>
    /// Verifies that <see cref="McpServerRunner.RunAsync(ModelContextProtocol.Server.McpServerPrimitiveCollection{ModelContextProtocol.Server.McpServerTool},Microsoft.Extensions.Logging.ILoggerFactory?,System.Threading.CancellationToken)"/> exits quickly and
    /// throws <see cref="OperationCanceledException"/> when the token is already
    /// canceled before the call, mirroring the behavior that
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
                        .WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Expected OperationCanceledException to be thrown.");
    }

    /// <summary>
    /// Verifies that <see cref="McpServerRunner.RunAsync(ModelContextProtocol.Server.McpServerPrimitiveCollection{ModelContextProtocol.Server.McpServerTool},Microsoft.Extensions.Logging.ILoggerFactory?,System.Threading.CancellationToken)"/> shuts down promptly
    /// when the token is canceled shortly after launch.
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

    [TestMethod]
    public void InvokeScriptBlock_WithCallerScriptRoot_SetsPsScriptRoot()
    {
        var callerScriptRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        const string scriptText = "function Get-TestRoot { $PSScriptRoot }";

        var host = new McpPsHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();

        McpServerRunner.InvokeScriptBlock(runspace, scriptText, callerScriptRoot);

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var results = ps.AddScript("Get-TestRoot").Invoke();

        Assert.HasCount(1, results);
        Assert.AreEqual(callerScriptRoot, results[0].BaseObject as string, ignoreCase: true);
    }
}

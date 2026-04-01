using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Integration tests for <see cref="PowerShellTool"/>.
/// Each test creates a real PowerShell runspace so that actual command
/// invocation is exercised, matching the production code path.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PowerShellToolTests : IDisposable
{
    private readonly Runspace _runspace;
    private readonly PowerShellToolFactory _factory;
    // A minimal server created from null streams is sufficient to satisfy
    // RequestContext's constructor; InvokeAsync never calls back to the server.
    private McpServer _dummyServer = null!;

    public PowerShellToolTests()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
        _factory = new PowerShellToolFactory(_runspace, jsonDepth: 2);
    }

    [TestInitialize]
    public async Task InitializeAsync()
    {
        var transport = new StreamServerTransport(Stream.Null, Stream.Null);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test-server", Version = "0.0" }
        };
        _dummyServer = McpServer.Create(transport, options, loggerFactory: null);
        await Task.CompletedTask;
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _dummyServer.DisposeAsync();

        _runspace.Close();
        _runspace.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _factory.Dispose();
        _runspace.Dispose();
    }

    // ── ProtocolTool properties ───────────────────────────────────────────────

    [TestMethod]
    public void ProtocolTool_Name_MatchesCommandName()
    {
        var tool = _factory.Create("Get-Date");

        Assert.AreEqual("Get-Date", tool.ProtocolTool.Name);
    }

    [TestMethod]
    public void ProtocolTool_Description_IsNotEmpty()
    {
        var tool = _factory.Create("Get-Date");

        Assert.IsFalse(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description));
    }

    [TestMethod]
    public void Metadata_ReturnsEmptyCollection()
    {
        var tool = _factory.Create("Get-Date");

        Assert.IsFalse(tool.Metadata.Any(), "Expected empty metadata collection.");
    }

    // ── InvokeAsync ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task InvokeAsync_WithNoArguments_ReturnsTextContent()
    {
        var tool = _factory.Create("Get-Date");
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Content.Count > 0, "Expected non-empty content.");
        Assert.IsInstanceOfType<TextContentBlock>(result.Content[0]);
        Assert.IsTrue(result.IsError is not true, "Get-Date should not return an error.");
    }

    [TestMethod]
    public async Task InvokeAsync_WithErrorCommand_SetsIsError()
    {
        // Write-Error always produces an error record.
        var tool = _factory.Create("Write-Error");
        var args = new Dictionary<string, JsonElement>
        {
            ["Message"] = JsonSerializer.SerializeToElement("deliberate-test-error")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsTrue(result.IsError == true, "Write-Error should produce an error result.");
    }

    [TestMethod]
    public async Task InvokeAsync_WithStringArgument_IncludesArgumentValueInOutput()
    {
        // Write-Output passes a string through the pipeline.
        var tool = _factory.Create("Write-Output");
        var args = new Dictionary<string, JsonElement>
        {
            ["InputObject"] = JsonSerializer.SerializeToElement("hello-from-test")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        var text = ((TextContentBlock)result.Content[0]).Text;
        StringAssert.Contains(text, "hello-from-test");
    }

    [TestMethod]
    public async Task InvokeAsync_WhenCancelled_ReturnsIsError()
    {
        var tool = _factory.Create("Get-Process");
        var ctx = BuildContext(tool, arguments: null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await tool.InvokeAsync(ctx, cts.Token);

        Assert.IsTrue(result.IsError == true, "A cancelled invocation should return IsError=true.");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private RequestContext<CallToolRequestParams> BuildContext(
        PowerShellTool tool,
        IDictionary<string, JsonElement>? arguments)
    {
        var jsonRpcRequest = new JsonRpcRequest { Method = "tools/call" };
        var callParams = new CallToolRequestParams
        {
            Name = tool.ProtocolTool.Name,
            Arguments = arguments
        };
        return new RequestContext<CallToolRequestParams>(_dummyServer, jsonRpcRequest, callParams);
    }
}

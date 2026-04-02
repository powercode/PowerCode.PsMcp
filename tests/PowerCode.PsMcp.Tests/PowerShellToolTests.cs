using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
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
    private static readonly string[] IncludedProperties = ["Id", "Name"];
    private static readonly string[] ExcludedProperties = ["SafeHandle", "Handle"];

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
        Assert.IsNotEmpty(result.Content, "Expected non-empty content.");
        Assert.IsInstanceOfType<TextContentBlock>(result.Content[0]);
        Assert.AreNotEqual(true, result.IsError, "Get-Date should not return an error.");
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

        Assert.IsTrue(result.IsError, "Write-Error should produce an error result.");
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
        Assert.Contains("hello-from-test", text);
    }

    [TestMethod]
    public async Task InvokeAsync_WhenCancelled_ReturnsIsError()
    {
        var tool = _factory.Create("Get-Process");
        var ctx = BuildContext(tool, arguments: null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await tool.InvokeAsync(ctx, cts.Token);

        Assert.IsTrue(result.IsError, "A cancelled invocation should return IsError=true.");
    }

    // ── Structured content envelope ───────────────────────────────────────────

    [TestMethod]
    public async Task InvokeAsync_WithStringOutput_StructuredContentHasItemsEnvelope()
    {
        var tool = _factory.Create("Write-Output");
        var args = new Dictionary<string, JsonElement>
        {
            ["InputObject"] = JsonSerializer.SerializeToElement("envelope-test")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);
        Assert.AreEqual("envelope-test", items[0].GetString());
    }

    [TestMethod]
    public async Task InvokeAsync_WithOutputSchema_EmptyPipeline_ReturnsEmptyItemsEnvelope()
    {
        DefineEmptyPipelineFunction();

        var tool = _factory.Create("Test-EmptyPipeline");
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent, "Expected structuredContent when outputSchema is set.");
        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);
        Assert.AreEqual(0, items.GetArrayLength(), "Expected empty items array for empty pipeline.");
    }

    [TestMethod]
    public async Task InvokeAsync_WithPropertySelection_ReturnsOnlySelectedProperties()
    {
        DefinePropertySelectionFunction();

        var tool = _factory.Create("Get-TestRecord", new McpToolConfig { Property = IncludedProperties });
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        var item = result.StructuredContent.Value.GetProperty("items")[0];
        var propertyNames = item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.HasCount(IncludedProperties.Length, propertyNames);
        foreach (var propertyName in IncludedProperties)
        {
            Assert.Contains(propertyName, propertyNames);
        }

        Assert.AreEqual("demo", item.GetProperty("Name").GetString());
    }

    [TestMethod]
    public async Task InvokeAsync_WithExcludeProperty_OmitsExcludedProperties()
    {
        DefinePropertySelectionFunction();

        var tool = _factory.Create("Get-TestRecord", new McpToolConfig { ExcludeProperty = ExcludedProperties });
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        var item = result.StructuredContent.Value.GetProperty("items")[0];

        Assert.IsTrue(item.TryGetProperty("Id", out _));
        Assert.IsTrue(item.TryGetProperty("Name", out _));
        Assert.IsFalse(item.TryGetProperty("SafeHandle", out _));
        Assert.IsFalse(item.TryGetProperty("Handle", out _));
    }

    [TestMethod]
    public async Task InvokeAsync_WithNoPropertySelection_ReturnsFullOutput()
    {
        DefinePropertySelectionFunction();

        var tool = _factory.Create("Get-TestRecord");
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        var item = result.StructuredContent.Value.GetProperty("items")[0];

        Assert.IsTrue(item.TryGetProperty("Id", out _));
        Assert.IsTrue(item.TryGetProperty("Name", out _));
        Assert.IsTrue(item.TryGetProperty("SafeHandle", out _));
        Assert.IsTrue(item.TryGetProperty("Handle", out _));
    }

    private void DefineEmptyPipelineFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-EmptyPipeline {
    [OutputType([string])]
    param()
    # Intentionally writes nothing.
}");
        ps.Invoke();
    }

    private void DefinePropertySelectionFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Get-TestRecord {
    [OutputType([pscustomobject])]
    param()

    [pscustomobject]@{
        Id = 42
        Name = 'demo'
        SafeHandle = 'safe-handle'
        Handle = 'raw-handle'
        Nested = [pscustomobject]@{
            Value = 'nested'
        }
    }
}");
        ps.Invoke();
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

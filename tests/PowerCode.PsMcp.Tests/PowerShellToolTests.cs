using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private static readonly string[] s_includedProperties = ["Id", "Name"];
    private static readonly string[] s_excludedProperties = ["SafeHandle", "Handle"];

    private readonly Runspace _runspace;
    private readonly PowerShellToolFactory _factory;
    // A minimal server created from null streams is sufficient to satisfy
    // RequestContext's constructor; InvokeAsync never calls back to the server.
    private McpServer _dummyServer = null!;

    public PowerShellToolTests()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
        _factory = new PowerShellToolFactory(_runspace, new McpPsHost(), NullLoggerFactory.Instance, jsonDepth: 2);
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
        Assert.IsNotNull(result.StructuredContent, "Write-Error should include structured error content.");
        var errors = result.StructuredContent.Value.GetProperty("errors");
        Assert.AreEqual(JsonValueKind.Array, errors.ValueKind);
        Assert.IsGreaterThan(0, errors.GetArrayLength(), "Expected at least one structured error.");
        Assert.Contains("deliberate-test-error", errors[0].GetProperty("message").GetString()!);
    }

    [TestMethod]
    public async Task InvokeAsync_WithWriteError_StructuredContentContainsErrorDetails()
    {
        var tool = _factory.Create("Write-Error");
        var args = new Dictionary<string, JsonElement>
        {
            ["Message"] = JsonSerializer.SerializeToElement("structured-non-terminating-error")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsNotNull(result.StructuredContent);

        var errors = result.StructuredContent.Value.GetProperty("errors");
        Assert.AreEqual(JsonValueKind.Array, errors.ValueKind);
        Assert.IsGreaterThan(0, errors.GetArrayLength());

        var firstError = errors[0];
        Assert.IsTrue(firstError.TryGetProperty("errorId", out var errorId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(errorId.GetString()));
        Assert.IsTrue(firstError.TryGetProperty("category", out var category));
        Assert.IsFalse(string.IsNullOrWhiteSpace(category.GetString()));

        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("ERROR:", text);
    }

    [TestMethod]
    public async Task InvokeAsync_WithTerminatingError_StructuredContentContainsErrorDetails()
    {
        DefineThrowingFunction();

        var tool = _factory.Create("Test-ThrowingError");
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsNotNull(result.StructuredContent);

        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);

        var errors = result.StructuredContent.Value.GetProperty("errors");
        Assert.AreEqual(JsonValueKind.Array, errors.ValueKind);
        Assert.IsGreaterThan(0, errors.GetArrayLength());
        Assert.IsTrue(errors[0].TryGetProperty("message", out var errorMessage));
        Assert.IsFalse(string.IsNullOrWhiteSpace(errorMessage.GetString()));
    }

    [TestMethod]
    public async Task InvokeAsync_WithMixedOutputAndErrors_StructuredContentContainsBoth()
    {
        DefineMixedOutputFunction();

        var tool = _factory.Create("Test-MixedOutputAndError");
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsNotNull(result.StructuredContent);

        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);
        Assert.IsGreaterThan(0, items.GetArrayLength());

        var errors = result.StructuredContent.Value.GetProperty("errors");
        Assert.AreEqual(JsonValueKind.Array, errors.ValueKind);
        Assert.IsGreaterThan(0, errors.GetArrayLength());

        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("mixed-output-item", text);
        Assert.Contains("ERROR:", text);
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
    public async Task InvokeAsync_WithOutputCommand_LogsOutputAtDebugLevel()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(loggerProvider);
        });

        using var serializerInput = new PSDataCollection<PSObject>();
        var classifierType = typeof(PowerShellTool)
            .GetNestedType("PipelineClassifier", System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(classifierType, "Expected nested PipelineClassifier type.");

        var classifier = Activator.CreateInstance(
            classifierType,
            serializerInput,
            loggerFactory.CreateLogger("test-command"));

        Assert.IsNotNull(classifier, "Expected PipelineClassifier instance.");

        var onDataAdded = classifierType.GetMethod("OnDataAdded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(onDataAdded, "Expected OnDataAdded method.");

        using var commandOutput = new PSDataCollection<PSObject>();
        var handler = (EventHandler<DataAddedEventArgs>)Delegate.CreateDelegate(
            typeof(EventHandler<DataAddedEventArgs>),
            classifier,
            onDataAdded);
        commandOutput.DataAdded += handler;
        commandOutput.Add(new PSObject("debug-log-output"));
        commandOutput.DataAdded -= handler;

        var debugLogText = string.Join(
            Environment.NewLine,
            loggerProvider.Entries
                .Where(entry => entry.Level == LogLevel.Debug)
                .Select(entry => entry.Message));

        Assert.Contains("debug-log-output", debugLogText, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task InvokeAsync_WithWarningCommand_LogsToInjectedStderrLogger()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(loggerProvider);
        });

        using var factory = new PowerShellToolFactory(_runspace, new McpPsHost(), loggerFactory, jsonDepth: 2);
        var tool = factory.Create("Write-Warning");
        var args = new Dictionary<string, JsonElement>
        {
            ["Message"] = JsonSerializer.SerializeToElement("stderr-warning-test")
        };
        var ctx = BuildContext(tool, args);

        await tool.InvokeAsync(ctx, CancellationToken.None);

        var warningLogText = string.Join(
            Environment.NewLine,
            loggerProvider.Entries
                .Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Message));

        Assert.Contains("stderr-warning-test", warningLogText, StringComparison.Ordinal);
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

    [TestMethod]
    public async Task InvokeAsync_WithParameterSetNameArgument_StripsDiscriminatorAndSucceeds()
    {
        DefineMultiSetEchoFunction();

        var tool = _factory.Create("Test-MultiSetEcho");
        var args = new Dictionary<string, JsonElement>
        {
            ["ParameterSetName"] = JsonSerializer.SerializeToElement("ByName"),
            ["Name"] = JsonSerializer.SerializeToElement("test-value")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.AreNotEqual(true, result.IsError,
            "ParameterSetName should be ignored and should not cause parameter binding failure.");

        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.Contains("ByName:test-value", text);
        Assert.DoesNotContain("ParameterSetName", text);
        Assert.DoesNotContain("A parameter cannot be found that matches parameter name", text);
    }

    [TestMethod]
    public async Task InvokeAsync_WithParameterSetNameArgument_StructuredContentIncludesParameterSetName()
    {
        DefineMultiSetEchoFunction();

        var tool = _factory.Create("Test-MultiSetEcho");
        var args = new Dictionary<string, JsonElement>
        {
            ["ParameterSetName"] = JsonSerializer.SerializeToElement("ByName"),
            ["Name"] = JsonSerializer.SerializeToElement("test-value")
        };
        var ctx = BuildContext(tool, args);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        Assert.AreEqual("ByName", result.StructuredContent.Value.GetProperty("ParameterSetName").GetString());

        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);
        Assert.AreEqual("ByName:test-value", items[0].GetString());
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

        var tool = _factory.Create("Get-TestRecord", new McpToolConfig { Property = s_includedProperties });
        var ctx = BuildContext(tool, arguments: null);

        var result = await tool.InvokeAsync(ctx, CancellationToken.None);

        Assert.IsNotNull(result.StructuredContent);
        var item = result.StructuredContent.Value.GetProperty("items")[0];
        var propertyNames = item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.HasCount(s_includedProperties.Length, propertyNames);
        foreach (var propertyName in s_includedProperties)
        {
            Assert.Contains(propertyName, propertyNames);
        }

        Assert.AreEqual("demo", item.GetProperty("Name").GetString());
    }

    [TestMethod]
    public async Task InvokeAsync_WithExcludeProperty_OmitsExcludedProperties()
    {
        DefinePropertySelectionFunction();

        var tool = _factory.Create("Get-TestRecord", new McpToolConfig { ExcludeProperty = s_excludedProperties });
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
        ps.AddScript("""

                     function Test-EmptyPipeline {
                         [OutputType([string])]
                         param()
                         # Intentionally writes nothing.
                     }
                     """);
        ps.Invoke();
    }

    private void DefinePropertySelectionFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

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
                     }
                     """);
        ps.Invoke();
    }

    private void DefineThrowingFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-ThrowingError {
                         [OutputType([string])]
                         param()

                         throw 'deliberate-terminating-error'
                     }
                     """);
        ps.Invoke();
    }

    private void DefineMixedOutputFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-MixedOutputAndError {
                         [OutputType([string])]
                         param()

                         Write-Output 'mixed-output-item'
                         Write-Error 'mixed-error-item'
                     }
                     """);
        ps.Invoke();
    }

    private void DefineMultiSetEchoFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-MultiSetEcho {
                         [CmdletBinding(DefaultParameterSetName = 'ByName')]
                         param(
                             [Parameter(ParameterSetName = 'ByName', Mandatory = $true)]
                             [string]$Name,

                             [Parameter(ParameterSetName = 'ById', Mandatory = $true)]
                             [int]$Id
                         )

                         if ($PSCmdlet.ParameterSetName -eq 'ByName') {
                             Write-Output ("ByName:{0}" -f $Name)
                             return
                         }

                         Write-Output ("ById:{0}" -f $Id)
                     }
                     """);
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

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly object _syncRoot = new();

        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new TestLogger(this, categoryName);

        public void Dispose()
        {
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                Entries.Clear();
            }
        }

        private void Add(LogEntry entry)
        {
            lock (_syncRoot)
            {
                Entries.Add(entry);
            }
        }

        private sealed class TestLogger(TestLoggerProvider provider, string categoryName) : ILogger
        {
            private readonly TestLoggerProvider _provider = provider;
            private readonly string _categoryName = categoryName;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _provider.Add(new LogEntry(_categoryName, logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);
}

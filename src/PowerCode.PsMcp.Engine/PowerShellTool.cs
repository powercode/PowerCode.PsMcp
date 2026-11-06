using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// <see cref="McpServerTool"/> subclass that invokes a single PowerShell command and
/// returns a <see cref="CallToolResult"/> containing both human-readable text content
/// and machine-parseable structured JSON.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline execution.</b>
/// All output streams (error, warning, verbose, debug, information, progress) are merged
/// into the success stream via <c>*&gt;&amp;1</c> so that a single
/// <see cref="System.Management.Automation.PowerShell.InvokeAsync{TInput,TOutput}"/> call
/// captures everything. The command runs asynchronously; if the
/// <see cref="System.Threading.CancellationToken"/> fires,
/// <see cref="System.Management.Automation.PowerShell.Stop"/> is called to interrupt it.
/// </para>
/// <para>
/// <b>Streaming classification.</b>
/// Results are classified by <see cref="PipelineClassifier"/> via the
/// <see cref="System.Management.Automation.PSDataCollection{T}.DataAdded"/> event as each
/// object arrives — without waiting for the command to finish. This means diagnostic
/// records (errors, warnings, verbose lines, etc.) are written to stderr and also forwarded
/// to local diagnostic logs in real time during long-running commands.
/// True output objects are forwarded into a serializer input collection for later processing.
/// </para>
/// <para>
/// <b>Serialization is necessarily batched.</b>
/// After the command completes, output objects are piped through an optional
/// <c>Select-Object</c> and then <c>ConvertTo-Json -AsArray</c>. Because
/// <c>ConvertTo-Json -AsArray</c> must see all input before it can emit the enclosing
/// <c>[…]</c>, serialization is inherently batch. The streaming above therefore benefits
/// diagnostics and observability, not throughput.
/// </para>
/// <para>
/// <b>Structured content envelope.</b>
/// The JSON array is wrapped in an <c>{"items":[…]}</c> object so that
/// <see cref="ModelContextProtocol.Protocol.CallToolResult.StructuredContent"/> always
/// satisfies the MCP requirement of a root <c>"type":"object"</c> schema, matching
/// PowerShell pipeline semantics of zero-or-more output objects.
/// </para>
/// </remarks>
internal sealed partial class PowerShellTool : McpServerTool
{
    private readonly string _commandName;
    private readonly Tool _protocolTool;
    private readonly Runspace? _runspace;
    private readonly SemaphoreSlim? _runspaceLock;
    private readonly ManagedRunspacePool? _runspacePool;
    private readonly int _jsonDepth;
    private readonly McpPsHost _host;
    private readonly ILogger _stderrLogger;
    private readonly string[]? _selectProperty;
    private readonly string[]? _excludeProperty;

    /// <summary>
    /// Initialises a new tool that wraps <paramref name="commandName"/> using a single shared runspace.
    /// </summary>
    /// <param name="commandName">The PowerShell command name to invoke.</param>
    /// <param name="protocolTool">MCP protocol representation of the tool.</param>
    /// <param name="runspace">Shared runspace; access is serialised by <paramref name="runspaceLock"/>.</param>
    /// <param name="runspaceLock">Semaphore ensuring single-threaded runspace use.</param>
    /// <param name="jsonDepth">Depth passed to <c>ConvertTo-Json</c>.</param>
    /// <param name="host">
    ///   The <see cref="McpPsHost"/> associated with <paramref name="runspace"/>.
    ///   When provided, <see cref="System.Management.Automation.ProgressRecord"/> updates emitted
    ///   during tool execution are forwarded to the MCP client as progress notifications,
    ///   provided the caller included a progress token in the request.
    /// </param>
    /// <param name="stderrLogger">Process-local logger used to write tool execution diagnostics to stderr.</param>
    /// <param name="selectProperty">Properties to include in tool output via <c>Select-Object -Property</c>.</param>
    /// <param name="excludeProperty">Properties to exclude from tool output via <c>Select-Object -ExcludeProperty</c>.</param>
    public PowerShellTool(
        string commandName,
        Tool protocolTool,
        Runspace runspace,
        SemaphoreSlim runspaceLock,
        int jsonDepth,
        McpPsHost host,
        ILogger stderrLogger,
        string[]? selectProperty = null,
        string[]? excludeProperty = null)
        : this(commandName, protocolTool, runspace, runspaceLock, runspacePool: null, jsonDepth, host, stderrLogger, selectProperty, excludeProperty)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        ArgumentNullException.ThrowIfNull(runspaceLock);
    }

    /// <summary>
    /// Initialises a new tool that wraps <paramref name="commandName"/> using a <see cref="ManagedRunspacePool"/>
    /// for parallel execution.
    /// </summary>
    /// <param name="commandName">The PowerShell command name to invoke.</param>
    /// <param name="protocolTool">MCP protocol representation of the tool.</param>
    /// <param name="runspacePool">Pool of individually-initialized runspaces for concurrent tool invocations.</param>
    /// <param name="jsonDepth">Depth passed to <c>ConvertTo-Json</c>.</param>
    /// <param name="host">
    ///   The <see cref="McpPsHost"/> associated with the pool.
    ///   <see cref="System.Management.Automation.ProgressRecord"/> updates are forwarded
    ///   to the MCP client as progress notifications using <see cref="AsyncLocal{T}"/>.
    /// </param>
    /// <param name="stderrLogger">Process-local logger used to write tool execution diagnostics to stderr.</param>
    /// <param name="selectProperty">Properties to include in tool output via <c>Select-Object -Property</c>.</param>
    /// <param name="excludeProperty">Properties to exclude from tool output via <c>Select-Object -ExcludeProperty</c>.</param>
    public PowerShellTool(
        string commandName,
        Tool protocolTool,
        ManagedRunspacePool runspacePool,
        int jsonDepth,
        McpPsHost host,
        ILogger stderrLogger,
        string[]? selectProperty = null,
        string[]? excludeProperty = null)
        : this(commandName, protocolTool, runspace: null, runspaceLock: null, runspacePool, jsonDepth, host, stderrLogger, selectProperty, excludeProperty)
    {
        ArgumentNullException.ThrowIfNull(runspacePool);
    }

    /// <summary>Private delegating constructor shared by both public overloads.</summary>
    private PowerShellTool(
        string commandName,
        Tool protocolTool,
        Runspace? runspace,
        SemaphoreSlim? runspaceLock,
        ManagedRunspacePool? runspacePool,
        int jsonDepth,
        McpPsHost host,
        ILogger stderrLogger,
        string[]? selectProperty,
        string[]? excludeProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(protocolTool);
        ArgumentNullException.ThrowIfNull(stderrLogger);

        _commandName = commandName;
        _protocolTool = protocolTool;
        _runspace = runspace;
        _runspaceLock = runspaceLock;
        _runspacePool = runspacePool;
        _jsonDepth = jsonDepth;
        _host = host;
        _stderrLogger = stderrLogger;
        _selectProperty = selectProperty;
        _excludeProperty = excludeProperty;
    }

    /// <inheritdoc />
    public override Tool ProtocolTool => _protocolTool;

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata => [];

    /// <summary>
    /// Invokes the PowerShell command with the provided arguments.
    /// All pipeline streams are merged into the output stream (<c>*&gt;&amp;1</c>),
    /// then output objects are converted to JSON for <see cref="CallToolResult.StructuredContent"/>.
    /// </summary>
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _runspacePool is not null
            ? await InvokeWithPoolAsync(request, cancellationToken).ConfigureAwait(false)
            : await InvokeWithLockAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the tool using a <see cref="ManagedRunspacePool"/> — rents a runspace for the duration.</summary>
    private async ValueTask<CallToolResult> InvokeWithPoolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        IProgress<ProgressNotificationValue>? progress = null;
        var progressToken = request.Params.ProgressToken;
        if (progressToken.HasValue)
            progress = new McpProgressReporter(request.Server, progressToken.Value);

        var runspace = await _runspacePool!.RentAsync(cancellationToken).ConfigureAwait(false);
        McpPsHost.SetProgressReporter(progress);
        try
        {
            return await InvokeCoreAsync(request, runspace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            McpPsHost.SetProgressReporter(null);
            _runspacePool.Return(runspace);
        }
    }

    /// <summary>Runs the tool using a single runspace protected by <see cref="_runspaceLock"/>.</summary>
    private async ValueTask<CallToolResult> InvokeWithLockAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runspaceLock!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Operation was cancelled." }],
                IsError = true
            };
        }

        try
        {
            IProgress<ProgressNotificationValue>? progress = null;
            var progressToken = request.Params.ProgressToken;
            if (progressToken.HasValue)
                progress = new McpProgressReporter(request.Server, progressToken.Value);

            McpPsHost.SetProgressReporter(progress);
            try
            {
                return await InvokeCoreAsync(request, _runspace!, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                McpPsHost.SetProgressReporter(null);
            }
        }
        finally
        {
            _runspaceLock!.Release();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request,
        Runspace runspace,
        CancellationToken cancellationToken)
    {
        var logger = _stderrLogger;

        using var serializerInput = new PSDataCollection<PSObject>();
        var classifier = new PipelineClassifier(serializerInput, logger);

        string? parameterSetName;

        using (var commandPs = PowerShell.Create())
        using (var commandOutput = new PSDataCollection<PSObject>())
        {
            commandOutput.DataAdded += classifier.OnDataAdded;

            commandPs.Runspace = runspace;
            parameterSetName = ConfigurePipelineCommand(commandPs, request.Params.Arguments);

            try
            {
                await RunCommandAsync(commandPs, commandOutput, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    classifier.TextLines.Add("CANCELLED: Tool invocation was cancelled.");
                    serializerInput.Complete();
                    return BuildResult(serializerInput, classifier.TextLines, hasErrors: true, classifier.ErrorRecords, parameterSetName, runspace);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var errorRecord = ex switch
                {
                    ActionPreferenceStopException actionPreferenceStopException => actionPreferenceStopException.ErrorRecord,
                    CmdletInvocationException cmdletInvocationException => cmdletInvocationException.ErrorRecord,
                    RuntimeException runtimeException => runtimeException.ErrorRecord,
                    _ => null
                };

                if (errorRecord is not null)
                {
                    classifier.ErrorRecords.Add(errorRecord);
                }

                classifier.TextLines.Add($"ERROR: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(errorRecord?.ScriptStackTrace))
                {
                    classifier.TextLines.Add(errorRecord.ScriptStackTrace);
                }

                serializerInput.Complete();
                return BuildResult(serializerInput, classifier.TextLines, hasErrors: true, classifier.ErrorRecords, parameterSetName, runspace);
            }
        }

        serializerInput.Complete();
        return BuildResult(serializerInput, classifier.TextLines, classifier.HasErrors, classifier.ErrorRecords, parameterSetName, runspace);
    }

    /// <summary>Creates a configured serializer pipeline (Select-Object + ConvertTo-Json).</summary>
    private PowerShell CreateSerializerPs(Runspace runspace)
    {
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        AddPropertySelectionCommand(ps);
        AddConvertToJsonCommand(ps);
        return ps;
    }

    /// <summary>
    /// Invokes <paramref name="commandPs"/> asynchronously, streaming results into
    /// <paramref name="output"/>. <c>commandPs.Stop()</c> is called when
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    private static async Task RunCommandAsync(
        PowerShell commandPs,
        PSDataCollection<PSObject> output,
        CancellationToken cancellationToken)
    {
        await using (cancellationToken.Register(commandPs.Stop))
        {
            await commandPs.InvokeAsync<PSObject, PSObject>(input: null, output).ConfigureAwait(false);
        }
    }

    private CallToolResult BuildResult(
        PSDataCollection<PSObject> serializerInput,
        List<string> textLines,
        bool hasErrors,
        List<ErrorRecord> errorRecords,
        string? parameterSetName,
        Runspace runspace)
    {
        // Produce structured JSON in an {"items": [...]} envelope matching pipeline semantics.
        // When outputSchema is defined, always include structuredContent (even for empty pipelines).
        // serializerPs is created lazily here — only when there are output objects to serialize.
        JsonElement? structuredContent = null;
        if (serializerInput.Count > 0 || _protocolTool.OutputSchema is not null || errorRecords.Count > 0)
        {
            var itemsElement = serializerInput.Count == 0
                ? JsonSerializer.SerializeToElement(Array.Empty<object>())
                : SerializeToJsonArray(CreateSerializerPs(runspace), serializerInput);

            structuredContent = ConvertToJsonEnvelope(itemsElement, parameterSetName, errorRecords);
        }

        var textContent = (textLines.Count, serializerInput.Count) switch
        {
            (> 0, > 0) => string.Join(
                Environment.NewLine,
                serializerInput.Select(o => o.ToString()).Concat(textLines)),
            (> 0, _) => string.Join(Environment.NewLine, textLines),
            (_, > 0) => string.Join(Environment.NewLine, serializerInput.Select(o => o.ToString())),
            _ => "(no output)"
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = textContent }],
            StructuredContent = structuredContent,
            IsError = hasErrors ? true : null
        };
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds <see cref="_commandName"/> to <paramref name="ps"/>, binds all MCP
    /// arguments, and redirects all output streams into the success stream (<c>*&gt;&amp;1</c>).
    /// </summary>
    private string? ConfigurePipelineCommand(PowerShell ps, IDictionary<string, JsonElement>? arguments)
    {
        ps.AddCommand(_commandName);

        string? parameterSetName = null;

        // Map MCP JSON arguments to .NET types that PowerShell understands.
        if (arguments is { Count: > 0 })
        {
            foreach (var (key, value) in arguments)
            {
                if (string.Equals(key, "ParameterSetName", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        parameterSetName = value.GetString();
                    }

                    continue;
                }

                ps.AddParameter(key, ConvertJsonElement(value));
            }
        }

        // Merge all output streams into the success output stream (*>&1).
        if (ps.Commands.Commands.Count > 0)
        {
            ps.Commands.Commands[0].MergeMyResults(
                PipelineResultTypes.All,
                PipelineResultTypes.Output);
        }

        return parameterSetName;
    }

    /// <summary>
    /// Adds <c>Select-Object</c> to the pipeline if property selection is configured.
    /// </summary>
    private void AddPropertySelectionCommand(PowerShell ps)
    {
        if (_selectProperty is null && _excludeProperty is null)
        {
            return;
        }

        ps.AddCommand("Microsoft.PowerShell.Utility\\Select-Object");

        if (_selectProperty is not null)
        {
            ps.AddParameter("Property", _selectProperty);
        }

        if (_excludeProperty is not null)
        {
            ps.AddParameter("ExcludeProperty", _excludeProperty);
        }
    }

    /// <summary>
    /// Adds <c>ConvertTo-Json</c> to the pipeline with the configured depth and <c>-AsArray</c>.
    /// </summary>
    private void AddConvertToJsonCommand(PowerShell ps)
    {
        ps.AddCommand("Microsoft.PowerShell.Utility\\ConvertTo-Json")
            .AddParameter("Compress", true)
            .AddParameter("Depth", _jsonDepth)
            .AddParameter("AsArray", true);
    }

    /// <summary>
    /// Serializes pipeline output objects into a JSON envelope: <c>{"items": [...]}</c>.
    /// </summary>
    private static JsonElement ConvertToJsonEnvelope(
        JsonElement itemsElement,
        string? parameterSetName,
        List<ErrorRecord> errorRecords)
    {
        // Wrap the items array in {"items": [...]} to form the envelope root object.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            if (!string.IsNullOrWhiteSpace(parameterSetName))
            {
                writer.WriteString("ParameterSetName", parameterSetName);
            }

            writer.WritePropertyName("items");
            itemsElement.WriteTo(writer);

            if (errorRecords.Count > 0)
            {
                writer.WritePropertyName("errors");
                SerializeErrorRecords(errorRecords).WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Invokes a pre-configured serializer pipeline and parses the resulting JSON array.
    /// </summary>
    private static JsonElement SerializeToJsonArray(PowerShell serializerPs, PSDataCollection<PSObject> input)
    {
        using (serializerPs)
        {
            var jsonText = serializerPs.Invoke<string>(input).First();

            using var doc = JsonDocument.Parse(jsonText);
            return doc.RootElement.Clone();
        }
    }

    private static JsonElement SerializeErrorRecords(List<ErrorRecord> errors)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var errorRecord in errors)
            {
                writer.WriteStartObject();
                writer.WriteString("message", errorRecord.Exception?.Message ?? errorRecord.ToString());
                if (errorRecord.Exception is not null)
                {
                    writer.WriteString("exceptionType", errorRecord.Exception.GetType().FullName);
                }

                writer.WriteString("errorId", errorRecord.FullyQualifiedErrorId);
                writer.WriteString("category", errorRecord.CategoryInfo.Category.ToString());

                if (!string.IsNullOrWhiteSpace(errorRecord.CategoryInfo.TargetName))
                {
                    writer.WriteString("targetName", errorRecord.CategoryInfo.TargetName);
                }

                if (!string.IsNullOrWhiteSpace(errorRecord.ScriptStackTrace))
                {
                    writer.WriteString("scriptStackTrace", errorRecord.ScriptStackTrace);
                }

                if (!string.IsNullOrWhiteSpace(errorRecord.InvocationInfo?.Line))
                {
                    writer.WriteString("invocationLine", errorRecord.InvocationInfo.Line.Trim());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>Converts a <see cref="JsonElement"/> to a .NET type suitable for PS parameters.</summary>
    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement)
                .ToArray(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => null
        };

    // ── Nested types ─────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies pipeline results as they arrive via the <c>DataAdded</c> event.
    /// Diagnostic records are logged and added to <see cref="TextLines"/>; true output
    /// objects are forwarded to the serializer input collection.
    /// </summary>
    private sealed partial class PipelineClassifier(PSDataCollection<PSObject> serializerInput, ILogger logger)
    {
        public List<string> TextLines { get; } = [];
        public List<ErrorRecord> ErrorRecords { get; } = [];
        public bool HasErrors { get; private set; }

        public void OnDataAdded(object? sender, DataAddedEventArgs e)
        {
            var collection = (PSDataCollection<PSObject>)sender!;
            var result = collection[e.Index];

            switch (result.BaseObject)
            {
                case ErrorRecord er:
                    HasErrors = true;
                    ErrorRecords.Add(er);
                    var errorMessage = er.Exception?.Message ?? er.ToString();
                    TextLines.Add($"ERROR: {errorMessage}");
                    LogMessages.ErrorRecord(logger, errorMessage, er.Exception);
                    break;

                case ProgressRecord:
                    // Progress is already captured by McpPsHostUserInterface.WriteProgress.
                    break;

                case VerboseRecord vr:
                    TextLines.Add($"VERBOSE: {vr.Message}");
                    LogMessages.VerboseRecord(logger, vr.Message);
                    break;

                case WarningRecord wr:
                    TextLines.Add($"WARNING: {wr.Message}");
                    LogMessages.WarningRecord(logger, wr.Message);
                    break;

                case DebugRecord dr:
                    TextLines.Add($"DEBUG: {dr.Message}");
                    LogMessages.DebugRecord(logger, dr.Message);
                    break;

                case InformationRecord ir:
                    var infoText = ir.MessageData?.ToString() ?? string.Empty;
                    TextLines.Add($"INFO: {infoText}");
                    LogMessages.InformationRecord(logger, infoText);
                    break;

                default:
                    // Log command output records only when diagnostic logging is enabled.
#pragma warning disable CA1873 // Value is computed only when Debug logging is enabled.
                    LogMessages.CommandOutputRecord(logger, result.ToString() ?? string.Empty);
#pragma warning restore CA1873

                    serializerInput.Add(result);
                    break;
            }
        }

        private static partial class LogMessages
        {
            [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "{message}")]
            public static partial void ErrorRecord(ILogger logger, string message, Exception? exception);

            [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "{message}")]
            public static partial void VerboseRecord(ILogger logger, string message);

            [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "{message}")]
            public static partial void WarningRecord(ILogger logger, string message);

            [LoggerMessage(EventId = 3003, Level = LogLevel.Trace, Message = "{message}")]
            public static partial void DebugRecord(ILogger logger, string message);

            [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "{message}")]
            public static partial void InformationRecord(ILogger logger, string message);

            [LoggerMessage(EventId = 3005, Level = LogLevel.Debug, Message = "{message}", SkipEnabledCheck = true)]
            public static partial void CommandOutputRecord(ILogger logger, string message);

            public static void CommandOutputRecord(ILogger logger, object? output)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    string message = output?.ToString() ?? string.Empty;
                    CommandOutputRecord(logger, message: message);
                }
            }
        }
    }

    /// <summary>
    /// Bridges <see cref="IProgress{T}"/> to <see cref="McpServer.NotifyProgressAsync"/>.
    /// <see cref="IProgress{T}.Report"/> is fire-and-forget because progress notifications
    /// are advisory: losing one is acceptable, but blocking the PS pipeline thread is not.
    /// </summary>
    private sealed class McpProgressReporter(McpServer server, ProgressToken token)
        : IProgress<ProgressNotificationValue>
    {
        void IProgress<ProgressNotificationValue>.Report(ProgressNotificationValue value) =>
            // Intentional fire-and-forget: do not block the PowerShell pipeline thread.
            _ = server.NotifyProgressAsync(token, value, options: null, CancellationToken.None);
    }
}

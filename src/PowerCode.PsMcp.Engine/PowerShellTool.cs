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
/// records (errors, warnings, verbose lines, etc.) are forwarded to the MCP client as
/// <c>notifications/message</c> log entries in real time during long-running commands.
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
internal sealed class PowerShellTool : McpServerTool
{
    private readonly string _commandName;
    private readonly Tool _protocolTool;
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _runspaceLock;
    private readonly int _jsonDepth;
    private readonly McpPsHost _host;
    private readonly string[]? _selectProperty;
    private readonly string[]? _excludeProperty;

    /// <summary>
    /// Initialises a new tool that wraps <paramref name="commandName"/>.
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
    /// <param name="selectProperty">Properties to include in tool output via <c>Select-Object -Property</c>.</param>
    /// <param name="excludeProperty">Properties to exclude from tool output via <c>Select-Object -ExcludeProperty</c>.</param>
    public PowerShellTool(
        string commandName,
        Tool protocolTool,
        Runspace runspace,
        SemaphoreSlim runspaceLock,
        int jsonDepth,
        McpPsHost host,
        string[]? selectProperty = null,
        string[]? excludeProperty = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(protocolTool);
        ArgumentNullException.ThrowIfNull(runspace);
        ArgumentNullException.ThrowIfNull(runspaceLock);

        _commandName = commandName;
        _protocolTool = protocolTool;
        _runspace = runspace;
        _runspaceLock = runspaceLock;
        _jsonDepth = jsonDepth;
        _host = host;
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

        try
        {
            await _runspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            // Build a progress reporter for this request when the client supplied a
            // progress token. McpServer extends McpSession, so NotifyProgressAsync
            // is available on it. Progress is routed through the host because
            // WriteProgress is called in real-time by the PS engine during execution.
            IProgress<ProgressNotificationValue>? progress = null;
            var progressToken = request.Params?.ProgressToken;
            if (progressToken.HasValue)
                progress = new McpProgressReporter(request.Server, progressToken.Value);

            _host.SetProgressReporter(progress);
            try
            {
                return await InvokeCoreAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _host.SetProgressReporter(null);
            }
        }
        finally
        {
            _runspaceLock.Release();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<CallToolResult> InvokeCoreAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        using var loggerProvider = request.Server.AsClientLoggerProvider();
        var logger = loggerProvider.CreateLogger(_commandName);

        using var serializerInput = new PSDataCollection<PSObject>();
        var classifier = new PipelineClassifier(serializerInput, logger);

        using var commandOutput = new PSDataCollection<PSObject>();
        commandOutput.DataAdded += classifier.OnDataAdded;

        using var commandPs = PowerShell.Create();
        commandPs.Runspace = _runspace;
        ConfigurePipelineCommand(commandPs, request.Params?.Arguments);
        
        try
        {
            await RunCommandAsync(commandPs, commandOutput, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return ErrorResult("Tool invocation was cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ErrorResult($"Unhandled exception executing '{_commandName}': {ex}");
        }

        serializerInput.Complete();
        return BuildResult(serializerInput, classifier.TextLines, classifier.HasErrors);
    }

    /// <summary>Creates a configured serializer pipeline (Select-Object + ConvertTo-Json).</summary>
    private PowerShell CreateSerializerPs()
    {
        var ps = PowerShell.Create();
        ps.Runspace = _runspace;
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
        bool hasErrors)
    {
        // Produce structured JSON in an {"items": [...]} envelope matching pipeline semantics.
        // When outputSchema is defined, always include structuredContent (even for empty pipelines).
        // serializerPs is created lazily here — only when there are output objects to serialize.
        JsonElement? structuredContent = null;
        if (serializerInput.Count > 0 || _protocolTool.OutputSchema is not null)
        {
            using var serializerPs = CreateSerializerPs();
            structuredContent = ConvertToJsonEnvelope(serializerPs, serializerInput);
            if (serializerInput.Count > 0)
            {
                textLines.Add(structuredContent.Value.GetRawText());
            }
        }

        var textContent = textLines.Count > 0
            ? string.Join(Environment.NewLine, textLines)
            : "(no output)";

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
    private void ConfigurePipelineCommand(PowerShell ps, IDictionary<string, JsonElement>? arguments)
    {
        ps.AddCommand(_commandName);

        // Map MCP JSON arguments to .NET types that PowerShell understands.
        if (arguments is { Count: > 0 })
        {
            foreach (var (key, value) in arguments)
            {
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
    /// Serialises pipeline output objects into a JSON envelope: <c>{"items": [...]}</c>.
    /// <paramref name="serializerPs"/> must already have <c>Select-Object</c> and/or
    /// <c>ConvertTo-Json</c> configured; <paramref name="input"/> must be completed.
    /// </summary>
    private static JsonElement ConvertToJsonEnvelope(PowerShell serializerPs, PSDataCollection<PSObject> input)
    {
        var itemsElement = input.Count == 0
            ? JsonSerializer.SerializeToElement(Array.Empty<object>())
            : SerializeToJsonArray(serializerPs, input);

        // Wrap the items array in {"items": [...]} to form the envelope root object.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("items");
            itemsElement.WriteTo(writer);
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
        var jsonText = serializerPs.Invoke<string>(input).First();

        using var doc = JsonDocument.Parse(jsonText);
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

    private static CallToolResult ErrorResult(string message) =>
        new()
        {
            Content = [new TextContentBlock { Text = message }],
            IsError = true
        };

    // ── Nested types ─────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies pipeline results as they arrive via the <c>DataAdded</c> event.
    /// Diagnostic records are logged and added to <see cref="TextLines"/>; true output
    /// objects are forwarded to the serializer input collection.
    /// </summary>
    private sealed class PipelineClassifier(PSDataCollection<PSObject> serializerInput, ILogger logger)
    {
        public List<string> TextLines { get; } = [];
        public bool HasErrors { get; private set; }

        public void OnDataAdded(object? sender, DataAddedEventArgs e)
        {
            var collection = (PSDataCollection<PSObject>)sender!;
            var result = collection[e.Index];

            switch (result.BaseObject)
            {
                case ErrorRecord er:
                    HasErrors = true;
                    var errorMessage = er.Exception?.Message ?? er.ToString();
                    TextLines.Add($"ERROR: {errorMessage}");
                    logger.Log(LogLevel.Error, 0, errorMessage, er.Exception, static (s, _) => s);
                    break;

                case ProgressRecord:
                    // Progress is already captured by McpPsHostUserInterface.WriteProgress.
                    break;

                case VerboseRecord vr:
                    TextLines.Add($"VERBOSE: {vr.Message}");
                    logger.Log(LogLevel.Information, 0, vr.Message, null, static (s, _) => s);
                    break;

                case WarningRecord wr:
                    TextLines.Add($"WARNING: {wr.Message}");
                    logger.Log(LogLevel.Warning, 0, wr.Message, null, static (s, _) => s);
                    break;

                case DebugRecord dr:
                    TextLines.Add($"DEBUG: {dr.Message}");
                    logger.Log(LogLevel.Trace, 0, dr.Message, null, static (s, _) => s);
                    break;

                case InformationRecord ir:
                    var infoText = ir.MessageData?.ToString() ?? string.Empty;
                    TextLines.Add($"INFO: {infoText}");
                    logger.Log(LogLevel.Information, 0, infoText, null, static (s, _) => s);
                    break;

                default:
                    serializerInput.Add(result);
                    break;
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
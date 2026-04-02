using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// <see cref="McpServerTool"/> subclass that invokes a single PowerShell command,
/// collects all output and stream records via the <c>*&gt;&amp;1</c> merge technique,
/// and returns a <see cref="CallToolResult"/> containing both human-readable text
/// content and machine-parseable structured JSON.
/// </summary>
internal sealed class PowerShellTool : McpServerTool
{
    private readonly string _commandName;
    private readonly Tool _protocolTool;
    private readonly Runspace _runspace;
    private readonly SemaphoreSlim _runspaceLock;
    private readonly int _jsonDepth;
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
    /// <param name="selectProperty">Properties to include in tool output via <c>Select-Object -Property</c>.</param>
    /// <param name="excludeProperty">Properties to exclude from tool output via <c>Select-Object -ExcludeProperty</c>.</param>
    public PowerShellTool(
        string commandName,
        Tool protocolTool,
        Runspace runspace,
        SemaphoreSlim runspaceLock,
        int jsonDepth,
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
            return await InvokeCoreAsync(request.Params.Arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _runspaceLock.Release();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<CallToolResult> InvokeCoreAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ConfigurePipelineCommand(ps, arguments);

        System.Collections.ObjectModel.Collection<PSObject> results;

        try
        {
            // Run the synchronous Invoke() on a thread-pool thread so that the
            // async MCP server loop remains responsive.  ps.Stop() is wired to
            // the cancellation token to interrupt a long-running command.
            await using (cancellationToken.Register(ps.Stop))
            {
                results = await Task.Run(ps.Invoke, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ErrorResult("Tool invocation was cancelled.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ErrorResult($"Unhandled exception executing '{_commandName}': {ex.Message}");
        }

        return BuildResult(results);
    }

    private CallToolResult BuildResult(
        System.Collections.ObjectModel.Collection<PSObject> results)
    {
        var (outputObjects, textLines, hasErrors) = ClassifyPipelineResults(results);
        outputObjects = ApplyPropertySelection(outputObjects);

        // Produce structured JSON in an {"items": [...]} envelope matching pipeline semantics.
        // When outputSchema is defined, always include structuredContent (even for empty pipelines).
        JsonElement? structuredContent = null;
        if (outputObjects.Count > 0 || _protocolTool.OutputSchema is not null)
        {
            try
            {
                structuredContent = ConvertToJsonEnvelope(outputObjects);
                if (outputObjects.Count > 0)
                {
                    textLines.Add(structuredContent.Value.GetRawText());
                }
            }
            catch (Exception ex)
            {
                // Fall back to .ToString() if JSON conversion fails.
                foreach (var obj in outputObjects)
                    textLines.Add(obj.ToString() ?? string.Empty);
                Console.Error.WriteLine($"ConvertTo-Json failed for '{_commandName}': {ex.Message}");
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
    /// Partitions raw pipeline <paramref name="results"/> into pipeline output objects,
    /// human-readable diagnostic lines, and an error flag.
    /// Each stream type (Error, Verbose, Warning, Debug, Information, Progress) is
    /// converted to a labelled text line; only true data objects are returned in
    /// <c>OutputObjects</c>.
    /// </summary>
    private static (List<PSObject> OutputObjects, List<string> TextLines, bool HasErrors)
        ClassifyPipelineResults(System.Collections.ObjectModel.Collection<PSObject> results)
    {
        var outputObjects = new List<PSObject>();
        var textLines = new List<string>();
        var hasErrors = false;

        foreach (var result in results)
        {
            switch (result.BaseObject)
            {
                case ErrorRecord er:
                    hasErrors = true;
                    textLines.Add($"ERROR: {er.Exception?.Message ?? er.ToString()}");
                    break;

                case ProgressRecord:
                    // Progress is already captured by McpPsHostUserInterface.WriteProgress.
                    break;

                case VerboseRecord vr:
                    textLines.Add($"VERBOSE: {vr.Message}");
                    break;

                case WarningRecord wr:
                    textLines.Add($"WARNING: {wr.Message}");
                    break;

                case DebugRecord dr:
                    textLines.Add($"DEBUG: {dr.Message}");
                    break;

                case InformationRecord ir:
                    textLines.Add($"INFO: {ir.MessageData}");
                    break;

                default:
                    outputObjects.Add(result);
                    break;
            }
        }

        return (outputObjects, textLines, hasErrors);
    }

    /// <summary>
    /// Applies any configured property selection to the pipeline output before JSON serialization.
    /// </summary>
    private List<PSObject> ApplyPropertySelection(List<PSObject> objects)
    {
        if (_selectProperty is null && _excludeProperty is null)
        {
            return objects;
        }

        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddCommand("Select-Object");

        if (_selectProperty is not null)
        {
            ps.AddParameter("Property", _selectProperty);
        }

        if (_excludeProperty is not null)
        {
            ps.AddParameter("ExcludeProperty", _excludeProperty);
        }

        return ps.Invoke<PSObject>(objects).ToList();
    }

    /// <summary>
    /// Invokes <c>ConvertTo-Json</c> on <paramref name="objects"/> and returns the
    /// resulting <see cref="JsonElement"/> (an array when there are multiple objects,
    /// a single-element array when <c>-AsArray</c> is needed for a single object).
    /// </summary>
    private JsonElement SerializeObjectsToJsonArray(List<PSObject> objects)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;

        // Passing List<PSObject> as InputObject lets ConvertTo-Json serialise each
        // element in order, producing a JSON array regardless of element count.
        ps.AddCommand("ConvertTo-Json")
            .AddParameter("Compress", true)
            .AddParameter("Depth", _jsonDepth)
            .AddParameter("InputObject", objects);

        var jsonText = ps.Invoke<string>().First();

        using var innerDoc = JsonDocument.Parse(jsonText);
        return innerDoc.RootElement.Clone();
    }

    /// <summary>
    /// Converts pipeline output objects to a JSON envelope: <c>{"items": [...]}</c>.
    /// This matches PowerShell pipeline semantics where commands write zero or more
    /// objects, and ensures conformance with the MCP <c>outputSchema</c> (which must
    /// have <c>"type": "object"</c> at the root).
    /// </summary>
    private JsonElement ConvertToJsonEnvelope(List<PSObject> objects)
    {
        var itemsElement = objects.Count == 0
            ? JsonSerializer.SerializeToElement(Array.Empty<object>())
            : SerializeObjectsToJsonArray(objects);

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
}
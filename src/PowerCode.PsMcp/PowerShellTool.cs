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

    /// <summary>
    /// Initialises a new tool that wraps <paramref name="commandName"/>.
    /// </summary>
    /// <param name="commandName">The PowerShell command name to invoke.</param>
    /// <param name="protocolTool">MCP protocol representation of the tool.</param>
    /// <param name="runspace">Shared runspace; access is serialised by <paramref name="runspaceLock"/>.</param>
    /// <param name="runspaceLock">Semaphore ensuring single-threaded runspace use.</param>
    /// <param name="jsonDepth">Depth passed to <c>ConvertTo-Json</c>.</param>
    public PowerShellTool(
        string commandName,
        Tool protocolTool,
        Runspace runspace,
        SemaphoreSlim runspaceLock,
        int jsonDepth)
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
            return await InvokeCoreAsync(request.Params?.Arguments, cancellationToken)
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

        System.Collections.ObjectModel.Collection<PSObject> results;

        try
        {
            // Run the synchronous Invoke() on a thread-pool thread so that the
            // async MCP server loop remains responsive.  ps.Stop() is wired to
            // the cancellation token to interrupt a long-running command.
            using (cancellationToken.Register(() => ps.Stop()))
            {
                results = await Task.Run(() => ps.Invoke(), CancellationToken.None)
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

        return BuildResult(results, ps.HadErrors);
    }

    private CallToolResult BuildResult(
        System.Collections.ObjectModel.Collection<PSObject> results,
        bool hadErrors)
    {
        var outputObjects = new List<PSObject>();
        var textLines = new List<string>();
        var hasErrors = hadErrors;

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
                    textLines.Add(result.ToString());
                    break;
            }
        }

        // Attempt to produce structured JSON from the output objects.
        JsonElement? structuredContent = null;
        if (outputObjects.Count > 0)
        {
            try
            {
                structuredContent = ConvertToJson(outputObjects);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DEBUG: ConvertTo-Json failed for '{_commandName}': {ex.Message}");
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

    private JsonElement ConvertToJson(List<PSObject> objects)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;

        // Pass an object array so ConvertTo-Json always produces a JSON array for
        // consistency, regardless of whether there is one output object or many.
        var inputArray = objects.Count == 1
            ? objects[0].BaseObject
            : (object)objects.Select(o => o.BaseObject).ToArray();

        ps.AddCommand("ConvertTo-Json")
          .AddParameter("InputObject", inputArray)
          .AddParameter("Compress", true)
          .AddParameter("Depth", _jsonDepth);

        var jsonResults = ps.Invoke<string>();
        var jsonText = jsonResults.FirstOrDefault() ?? "null";

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
                .Select(e => ConvertJsonElement(e))
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

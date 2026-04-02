using System.Collections;
using System.Management.Automation;
using Microsoft.Extensions.Logging;

namespace PowerCode.PsMcp;

/// <summary>
/// <para>
///   <b>Start-McpServer</b> exposes one or more PowerShell commands as MCP tools over
///   JSON-RPC/stdio.  The cmdlet blocks until the MCP client disconnects or
///   <see cref="StopProcessing"/> is called (e.g., Ctrl+C).
/// </para>
/// <para>
///   This cmdlet is intentionally thin: it gathers parameters and delegates all
///   business logic to <see cref="McpServerRunner"/>.
/// </para>
/// </summary>
/// <example>
///   <code>Start-McpServer -Module MyModule</code>
///   <code>Start-McpServer -ScriptBlock { function Get-Foo { 'bar' } } -Include Get-Foo, Get-Date</code>
///   <code>$config = @{ "Get-Process" = (New-McpToolConfig -Name "list-procs" -Property Id, Name -Depth 3) }</code>
///   <code>Start-McpServer -Module MyModule -Include "Get-*" -Exclude "Get-Secret" -CommandConfiguration $config</code>
/// </example>
[Cmdlet(VerbsLifecycle.Start, "McpServer")]
[OutputType(typeof(void))]
public sealed class StartMcpServerCommand : PSCmdlet, IDisposable
{
    // ── Parameters ───────────────────────────────────────────────────────────

    /// <summary>Module name(s) or path(s) to import into the runspace before command discovery.</summary>
    [Parameter(Position = 0,
        HelpMessage = "Module name(s) or path(s) to import before command discovery")]
    [ValidateNotNullOrEmpty]
    public string[]? Module { get; set; }

    /// <summary>
    /// Wildcard patterns for commands to include. Matching commands are included even if they
    /// existed before module import (i.e., baseline commands can be pulled in explicitly).
    /// </summary>
    [Parameter(HelpMessage = "Wildcard patterns for commands to include")]
    public string[]? Include { get; set; }

    /// <summary>Wildcard patterns for commands to exclude from the discovered set.</summary>
    [Parameter(HelpMessage = "Wildcard patterns for commands to exclude")]
    public string[]? Exclude { get; set; }

    /// <summary>
    /// Maps PowerShell command names (keys) to <see cref="McpToolConfig"/> values created by
    /// <c>New-McpToolConfig</c>.
    /// </summary>
    [Parameter(HelpMessage = "Per-command configuration: keys are command names, values are McpToolConfig objects from New-McpToolConfig")]
#pragma warning disable CA2227 // Setter required for PowerShell parameter binding
    public Hashtable? CommandConfiguration { get; set; }
#pragma warning restore CA2227

    // ── Shared parameters ────────────────────────────────────────────────────

    /// <summary>
    /// Optional path to a .ps1 script that is dot-sourced into the runspace before the
    /// server starts.  Use this to load helper functions that are not yet in a module.
    /// </summary>
    [Parameter(HelpMessage = "Path to a .ps1 script to dot-source before starting the server")]
    [ValidateNotNullOrEmpty]
    public string? ScriptPath { get; set; }

    /// <summary>
    /// Optional script block invoked in the runspace before the server starts.
    /// Mutually exclusive with <see cref="ScriptPath"/> (last one wins if both are set).
    /// </summary>
    [Parameter(HelpMessage = "Script block to invoke before starting the server (mutually exclusive with ScriptPath)")]
    public ScriptBlock? ScriptBlock { get; set; }

    /// <summary>
    /// Maximum serialisation depth passed to <c>ConvertTo-Json</c> when building the
    /// structured output for each tool result.  Higher values capture more nested
    /// object detail at the cost of larger payloads.  Defaults to 2.
    /// </summary>
    [Parameter(HelpMessage = "Maximum depth for JSON output (default: 2)")]
    [ValidateRange(1, 10)]
    public int Depth { get; set; } = 2;

    // ── Private state ────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    // ── Cmdlet lifecycle ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void ProcessRecord()
    {
        _cts = new CancellationTokenSource();

        using var loggerFactory = CreateLoggerFactory();
        var runner = new McpServerRunner();

        try
        {
            // Validate that at least one source is provided.
            if (Module is null && ScriptBlock is null && ScriptPath is null)
            {
                var ex = new PSArgumentException(
                    "At least one of -Module, -ScriptBlock, or -ScriptPath must be provided.",
                    nameof(Module));
                ThrowTerminatingError(new ErrorRecord(ex, "NoDiscoverySource", ErrorCategory.InvalidArgument, null));
                return;
            }

            // Convert the Hashtable CommandConfiguration to a case-insensitive IDictionary<string, McpToolConfig>.
            Dictionary<string, McpToolConfig>? configDict = null;
            if (CommandConfiguration is not null)
            {
                configDict = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in CommandConfiguration)
                {
                    var commandName = entry.Key.ToString()!;
                    try
                    {
                        configDict[commandName] = LanguagePrimitives.ConvertTo<McpToolConfig>(entry.Value);
                    }
                    catch (PSInvalidCastException ex)
                    {
                        ThrowTerminatingError(new ErrorRecord(
                            new PSArgumentException(
                                $"Value for key '{commandName}' must be a McpToolConfig (use New-McpToolConfig).", ex),
                            "InvalidCommandConfiguration",
                            ErrorCategory.InvalidArgument,
                            entry.Value));
                    }
                }
            }

#pragma warning disable CA2025 // GetResult() blocks synchronously; loggerFactory outlives the task
            runner.RunAsync(Module, ScriptPath, ScriptBlock?.ToString(), Include, Exclude, configDict, Depth, loggerFactory, _cts.Token)
                  .GetAwaiter()
                  .GetResult();
#pragma warning restore CA2025
        }
        catch (OperationCanceledException)
        {
            WriteVerbose("MCP server stopped.");
        }
        catch (ArgumentException ex)
        {
            WriteError(new ErrorRecord(ex, "ToolNotFound", ErrorCategory.ObjectNotFound, ex.ParamName));
        }
        catch (InvalidOperationException ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "McpServerError", ErrorCategory.InvalidOperation, null));
        }
    }

    /// <inheritdoc />
    /// <summary>Cancels the running MCP server when the user interrupts the cmdlet.</summary>
    protected override void StopProcessing()
    {
        _cts?.Cancel();
    }

    /// <inheritdoc />
    protected override void EndProcessing()
    {
        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a logger factory that writes to stderr, preserving stdout for JSON-RPC.
    /// </summary>
    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder =>
            builder.AddConsole(options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace));
}
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
///   <code>Start-McpServer -ToolName Get-Process, Get-Service</code>
///   <code>Start-McpServer -ToolName Invoke-Grep -ScriptPath ./tools.ps1 -Depth 5</code>
/// </example>
[Cmdlet(VerbsLifecycle.Start, "McpServer")]
[OutputType(typeof(void))]
public sealed class StartMcpServerCommand : PSCmdlet, IDisposable
{
    // ── Parameters ───────────────────────────────────────────────────────────

    /// <summary>Names of PowerShell commands to expose as MCP tools.</summary>
    [Parameter(Mandatory = true, Position = 0, HelpMessage = "Names of PowerShell commands to expose as MCP tools")]
    [ValidateNotNullOrEmpty]
    public string[] ToolName { get; set; } = [];

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
#pragma warning disable CA2025 // GetResult() blocks synchronously; loggerFactory outlives the task
            runner.RunAsync(ToolName, ScriptPath, ScriptBlock?.ToString(), Depth, loggerFactory, _cts.Token)
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
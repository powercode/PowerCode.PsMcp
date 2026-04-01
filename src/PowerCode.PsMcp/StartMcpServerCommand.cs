using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// <para>
///   <b>Start-McpServer</b> exposes one or more PowerShell commands as MCP tools over
///   JSON-RPC/stdio.  The cmdlet blocks until the MCP client disconnects or
///   <see cref="StopProcessing"/> is called (e.g., Ctrl+C).
/// </para>
/// <para>
///   This cmdlet is intentionally thin: it gathers parameters, constructs the required
///   service objects (<see cref="McpPsHost"/>, <see cref="Runspace"/>,
///   <see cref="PowerShellToolFactory"/>, <see cref="McpServerRunner"/>), and then
///   delegates all business logic to those services.  No transport, server option
///   wiring, command resolution, or invocation logic lives here.
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

    private Runspace? _runspace;
    private CancellationTokenSource? _cts;

    // ── Cmdlet lifecycle ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void BeginProcessing()
    {
        var host = new McpPsHost();
        _runspace = RunspaceFactory.CreateRunspace(host);
        _runspace.Open();

        // Dot-source a helper script or invoke a script block before starting the server.
        if (ScriptPath is not null)
        {
            DotSourceScript(_runspace, ScriptPath);
        }
        else if (ScriptBlock is not null)
        {
            InvokeScriptBlock(_runspace, ScriptBlock);
        }
    }

    /// <inheritdoc />
    protected override void ProcessRecord()
    {
        _cts = new CancellationTokenSource();

        // Resolve each tool name into a PowerShellTool instance.
        var factory = new PowerShellToolFactory(_runspace!, Depth);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();

        foreach (var name in ToolName)
        {
            try
            {
                toolCollection.Add(factory.Create(name));
                WriteVerbose($"Registered tool: {name}");
            }
            catch (ArgumentException ex)
            {
                WriteError(new ErrorRecord(ex, "ToolNotFound", ErrorCategory.ObjectNotFound, name));
            }
        }

        if (toolCollection.Count == 0)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "No valid tools were found. Ensure the command names are correct and any required modules are loaded."),
                "NoValidTools",
                ErrorCategory.InvalidOperation,
                null));

            return;
        }

        WriteVerbose($"Starting MCP server with {toolCollection.Count} tool(s) on stdio…");

        using var loggerFactory = CreateLoggerFactory();
        var runner = new McpServerRunner();

        try
        {
            runner.RunAsync(toolCollection, loggerFactory, _cts.Token)
                  .GetAwaiter()
                  .GetResult();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown triggered by StopProcessing or CancellationToken.
            WriteVerbose("MCP server stopped.");
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
        CleanupRunspace();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
        _runspace?.Dispose();
        _runspace = null;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void CleanupRunspace()
    {
        if (_runspace is null)
        {
            return;
        }

        try
        {
            _runspace.Close();
            _runspace.Dispose();
        }
        catch (Exception ex)
        {
            WriteDebug($"Error closing runspace: {ex.Message}");
        }
        finally
        {
            _runspace = null;
        }
    }

    private static void DotSourceScript(Runspace runspace, string scriptPath)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Escape single quotes in the path to prevent script injection.
        ps.AddScript($". '{scriptPath.Replace("'", "''")}'");
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to dot-source '{scriptPath}': {errors}");
        }
    }

    private static void InvokeScriptBlock(Runspace runspace, ScriptBlock scriptBlock)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(scriptBlock.ToString());
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to invoke script block: {errors}");
        }
    }

    /// <summary>
    /// Creates a logger factory that writes to stderr, preserving stdout for JSON-RPC.
    /// </summary>
    private static ILoggerFactory CreateLoggerFactory() =>
        LoggerFactory.Create(builder =>
            builder.AddConsole(options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace));
}

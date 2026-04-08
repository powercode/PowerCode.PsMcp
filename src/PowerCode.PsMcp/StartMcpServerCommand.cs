using System.Collections;
using System.IO;
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

    /// <summary>
    /// Enables startup diagnostics for command discovery and schema mapping.
    /// When enabled, startup logs are emitted at <see cref="LogLevel.Debug"/>.
    /// </summary>
    [Parameter(HelpMessage = "Enable startup diagnostic logging for discovery and schema mapping")]
    public SwitchParameter DiagnosticLogging { get; set; }

    // ── Private state ────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    // ── Cmdlet lifecycle ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void ProcessRecord()
    {
        _cts = new CancellationTokenSource();

        var runner = new McpServerRunner();

        try
        {
            if (!ValidateDiscoverySource())
            {
                return;
            }

            var configDict = BuildCommandConfiguration();

            var callerScriptRoot = ResolveCallerScriptRoot();

            var startupLogLevel = DiagnosticLogging.IsPresent ? LogLevel.Trace : LogLevel.Warning;

#pragma warning disable CA2025 // GetResult() blocks synchronously by design for cmdlet execution.
            runner.RunAsync(Module, ScriptPath, ScriptBlock?.ToString(), callerScriptRoot, Include, Exclude, configDict, Depth, startupLogLevel, _cts.Token)
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

    /// <summary>
    /// Converts the PowerShell hashtable parameter into a case-insensitive dictionary
    /// with strongly typed <see cref="McpToolConfig"/> values.
    /// </summary>
    /// <returns>
    /// A dictionary keyed by command name, or <see langword="null"/> when
    /// <see cref="CommandConfiguration"/> is not provided.
    /// </returns>
    private Dictionary<string, McpToolConfig>? BuildCommandConfiguration()
    {
        if (CommandConfiguration is null)
        {
            return null;
        }

        var configDict = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase);
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

        return configDict;
    }

    /// <summary>
    /// Validates that at least one discovery source parameter is provided.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a discovery source is configured; otherwise
    /// emits a terminating error and returns <see langword="false"/>.
    /// </returns>
    private bool ValidateDiscoverySource()
    {
        if (Module is not null || ScriptBlock is not null || ScriptPath is not null)
        {
            return true;
        }

        var ex = new PSArgumentException(
            "At least one of -Module, -ScriptBlock, or -ScriptPath must be provided.",
            nameof(Module));
        ThrowTerminatingError(new ErrorRecord(ex, "NoDiscoverySource", ErrorCategory.InvalidArgument, null));
        return false;
    }

    /// <summary>
    /// Resolves the caller script root used to interpret relative paths from an
    /// inline script block invocation context.
    /// </summary>
    /// <returns>
    /// The script directory when <see cref="ScriptBlock"/> has a backing file,
    /// <see cref="InvocationInfo.PSScriptRoot"/> for interactive callers, or
    /// <see langword="null"/> when no script block was provided.
    /// </returns>
    private string? ResolveCallerScriptRoot()
    {
        if (ScriptBlock is null)
        {
            return null;
        }

        return !string.IsNullOrEmpty(ScriptBlock.File)
            ? Path.GetDirectoryName(ScriptBlock.File)
            : MyInvocation.PSScriptRoot;
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

}
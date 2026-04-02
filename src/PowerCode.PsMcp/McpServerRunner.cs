using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// Owns the full MCP server lifecycle: creates a <see cref="StreamServerTransport"/>
/// from raw stdin/stdout (bypassing PowerShell's pipeline), configures
/// <see cref="McpServerOptions"/>, and runs the server until the provided
/// <see cref="CancellationToken"/> fires.
/// <para>
/// Using raw stdin/stdout (via <c>Console.OpenStandardInput</c> /
/// <c>Console.OpenStandardOutput</c>) is intentional: the cmdlet runs
/// inside an existing PowerShell session whose pipeline is occupied, so we
/// bypass it and communicate directly over the OS-level handles for JSON-RPC.
/// </para>
/// </summary>
internal sealed class McpServerRunner
{
    private readonly Stream _input;
    private readonly Stream _output;

    /// <summary>
    /// Initialises the runner with explicit input/output streams.
    /// </summary>
    /// <param name="input">Stream to read JSON-RPC messages from.</param>
    /// <param name="output">Stream to write JSON-RPC messages to.</param>
    public McpServerRunner(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Initialises the runner using the process's raw stdin/stdout handles,
    /// bypassing PowerShell's occupied pipeline.
    /// </summary>
    public McpServerRunner() : this(Console.OpenStandardInput(), Console.OpenStandardOutput()) { }
    /// <summary>
    /// Starts the MCP server and blocks until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="toolCollection">Pre-built collection of tools to expose.</param>
    /// <param name="loggerFactory">Optional logger factory; logs go to stderr via console.</param>
    /// <param name="cancellationToken">Token that triggers graceful shutdown.</param>
    public async Task RunAsync(
        McpServerPrimitiveCollection<McpServerTool> toolCollection,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCollection);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transport = new StreamServerTransport(_input, _output);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "PowerCode PowerShell MCP",
                Version = "1.0.0"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability()
            },
            ToolCollection = toolCollection
        };

        await using var server = McpServer.Create(transport, options, loggerFactory);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the MCP server with the full lifecycle: creates a <see cref="McpPsHost"/>,
    /// opens a runspace, optionally loads a script, builds tools via
    /// <see cref="PowerShellToolFactory"/>, and runs until <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(
        string[] toolNames,
        string? scriptPath,
        string? scriptBlockText,
        int jsonDepth,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        var host = new McpPsHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();

        if (scriptPath is not null)
            DotSourceScript(runspace, scriptPath);
        else if (scriptBlockText is not null)
            InvokeScriptBlock(runspace, scriptBlockText);

        using var factory = new PowerShellToolFactory(runspace, jsonDepth);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var name in toolNames)
            toolCollection.Add(factory.Create(name));

        await RunAsync(toolCollection, loggerFactory, cancellationToken);
    }

    internal static void DotSourceScript(Runspace runspace, string scriptPath)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Escape single quotes in the path to prevent script injection.
        ps.AddScript($". '{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'");
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to dot-source '{scriptPath}': {errors}");
        }
    }

    internal static void InvokeScriptBlock(Runspace runspace, string scriptBlockText)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(scriptBlockText);
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to invoke script block: {errors}");
        }
    }
}

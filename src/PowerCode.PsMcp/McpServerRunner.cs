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
}

using System.Globalization;
using System.Management.Automation.Host;

namespace PowerCode.PsMcp;

/// <summary>
/// Custom <see cref="PSHost"/> for the PowerShell MCP server. Uses
/// <see cref="McpPsHostUserInterface"/> to route all PS output to stderr and provides
/// sensible defaults for host metadata. A single instance is shared for the lifetime
/// of one <c>Start-McpServer</c> invocation.
/// </summary>
internal sealed class McpPsHost : PSHost
{
    private readonly McpPsHostUserInterface _ui = new();
    private readonly Guid _instanceId = Guid.NewGuid();

    /// <inheritdoc />
    public override string Name => "PowerShell MCP Host";

    /// <inheritdoc />
    public override Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public override Guid InstanceId => _instanceId;

    /// <inheritdoc />
    public override PSHostUserInterface UI => _ui;

    /// <inheritdoc />
    public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

    /// <inheritdoc />
    public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;

    /// <inheritdoc />
    public override void EnterNestedPrompt() =>
        throw new NotSupportedException("Nested prompts are not supported in MCP server mode.");

    /// <inheritdoc />
    public override void ExitNestedPrompt() { }

    /// <inheritdoc />
    public override void NotifyBeginApplication() { }

    /// <inheritdoc />
    public override void NotifyEndApplication() { }

    /// <inheritdoc />
    /// <remarks>No-op; the MCP server lifecycle is controlled by <see cref="CancellationToken"/>.</remarks>
    public override void SetShouldExit(int exitCode) { }
}

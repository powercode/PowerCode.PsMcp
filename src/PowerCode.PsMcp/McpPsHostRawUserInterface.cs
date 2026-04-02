using System.Management.Automation.Host;

namespace PowerCode.PsMcp;

/// <summary>
/// Minimal stub implementation of <see cref="PSHostRawUserInterface"/> for use with the MCP
/// server host. Provides sensible console-dimension defaults and no-ops for all console
/// interaction methods, because stdout and stdin are reserved for MCP JSON-RPC communication.
/// </summary>
internal sealed class McpPsHostRawUserInterface : PSHostRawUserInterface
{
    /// <inheritdoc />
    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.White;

    /// <inheritdoc />
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;

    /// <inheritdoc />
    public override Coordinates CursorPosition { get; set; } = new(0, 0);

    /// <inheritdoc />
    public override Coordinates WindowPosition { get; set; } = new(0, 0);

    /// <inheritdoc />
    public override int CursorSize
    {
        get => CursorSize1;
        set => CursorSize1 = value;
    }

    /// <inheritdoc />
    public override Size BufferSize { get; set; } = new(120, 9999);

    /// <inheritdoc />
    public override Size WindowSize { get; set; } = new(120, 50);

    /// <inheritdoc />
    public override Size MaxWindowSize => new(120, 50);

    /// <inheritdoc />
    public override Size MaxPhysicalWindowSize => new(120, 50);

    /// <inheritdoc />
    public override bool KeyAvailable => false;

    /// <inheritdoc />
    public override string WindowTitle { get; set; } = "PowerShell MCP Host";
    public int CursorSize1 { get; set; } = 25;

    /// <inheritdoc />
    public override KeyInfo ReadKey(ReadKeyOptions options) =>
        new(0, ' ', (ControlKeyStates)0, false);

    /// <inheritdoc />
    public override void FlushInputBuffer() { }

    /// <inheritdoc />
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }

    /// <inheritdoc />
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }

    /// <inheritdoc />
#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional for performance; but PSHostRawUserInterface requires 2D arrays, so we have to disable this warning.
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) =>
        new BufferCell[0, 0];
#pragma warning restore CA1814
    /// <inheritdoc />
    public override void ScrollBufferContents(
        Rectangle source,
        Coordinates destination,
        Rectangle clip,
        BufferCell fill) { }
}

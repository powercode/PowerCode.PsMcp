using System.Management.Automation.Host;

namespace PowerCode.PsMcp;

/// <summary>
/// Minimal stub implementation of <see cref="PSHostRawUserInterface"/> for use with the MCP
/// server host. Provides sensible console-dimension defaults and no-ops for all console
/// interaction methods, because stdout and stdin are reserved for MCP JSON-RPC communication.
/// </summary>
internal sealed class McpPsHostRawUserInterface : PSHostRawUserInterface
{
    private ConsoleColor _foregroundColor = ConsoleColor.White;
    private ConsoleColor _backgroundColor = ConsoleColor.Black;
    private Coordinates _cursorPosition = new(0, 0);
    private Coordinates _windowPosition = new(0, 0);
    private int _cursorSize = 25;
    private Size _bufferSize = new(120, 9999);
    private Size _windowSize = new(120, 50);

    /// <inheritdoc />
    public override ConsoleColor ForegroundColor
    {
        get => _foregroundColor;
        set => _foregroundColor = value;
    }

    /// <inheritdoc />
    public override ConsoleColor BackgroundColor
    {
        get => _backgroundColor;
        set => _backgroundColor = value;
    }

    /// <inheritdoc />
    public override Coordinates CursorPosition
    {
        get => _cursorPosition;
        set => _cursorPosition = value;
    }

    /// <inheritdoc />
    public override Coordinates WindowPosition
    {
        get => _windowPosition;
        set => _windowPosition = value;
    }

    /// <inheritdoc />
    public override int CursorSize
    {
        get => CursorSize1;
        set => CursorSize1 = value;
    }

    /// <inheritdoc />
    public override Size BufferSize
    {
        get => _bufferSize;
        set => _bufferSize = value;
    }

    /// <inheritdoc />
    public override Size WindowSize
    {
        get => _windowSize;
        set => _windowSize = value;
    }

    /// <inheritdoc />
    public override Size MaxWindowSize => new(120, 50);

    /// <inheritdoc />
    public override Size MaxPhysicalWindowSize => new(120, 50);

    /// <inheritdoc />
    public override bool KeyAvailable => false;

    /// <inheritdoc />
    public override string WindowTitle { get; set; } = "PowerShell MCP Host";
    public int CursorSize1 { get => _cursorSize; set => _cursorSize = value; }

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
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) =>
        new BufferCell[0, 0];

    /// <inheritdoc />
    public override void ScrollBufferContents(
        Rectangle source,
        Coordinates destination,
        Rectangle clip,
        BufferCell fill) { }
}

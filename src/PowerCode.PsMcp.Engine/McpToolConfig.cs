namespace PowerCode.PsMcp;

/// <summary>
/// Represents per-command configuration for how a PowerShell command is exposed as an MCP tool.
/// </summary>
public sealed class McpToolConfig
{
    /// <summary>
    /// Gets the friendly MCP tool name. When not set, the PowerShell command name is used.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the properties to include in tool output via Select-Object -Property.
    /// </summary>
    public string[]? Property { get; init; }

    /// <summary>
    /// Gets the properties to exclude from tool output via Select-Object -ExcludeProperty.
    /// </summary>
    public string[]? ExcludeProperty { get; init; }

    /// <summary>
    /// Gets the per-command JSON serialization depth override. When not set, the global depth is used.
    /// </summary>
    public int? Depth { get; init; }

    /// <summary>
    /// Gets the parameter-set names used to filter the MCP input schema.
    /// When set, only parameters that belong to at least one of the named sets are included.
    /// An <see cref="ArgumentException"/> is thrown at tool-creation time if any name does not
    /// match a set declared by the command.
    /// </summary>
    public string[]? ParameterSets { get; init; }
}
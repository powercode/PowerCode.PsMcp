using System.Management.Automation;

namespace PowerCode.PsMcp;

/// <summary>
/// Creates an <see cref="McpToolConfig"/> instance for configuring how a command is exposed as an MCP tool.
/// </summary>
[Cmdlet(VerbsCommon.New, "McpToolConfig")]
[OutputType(typeof(McpToolConfig))]
public sealed class NewMcpToolConfigCommand : PSCmdlet
{
    /// <summary>
    /// Gets or sets the friendly MCP tool name.
    /// </summary>
    [Parameter(Position = 0, HelpMessage = "Friendly MCP tool name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the properties to include in tool output.
    /// </summary>
    [Parameter(HelpMessage = "Properties to include in tool output")]
    public string[]? Property { get; set; }

    /// <summary>
    /// Gets or sets the properties to exclude from tool output.
    /// </summary>
    [Parameter(HelpMessage = "Properties to exclude from tool output")]
    public string[]? ExcludeProperty { get; set; }

    /// <summary>
    /// Gets or sets the per-command JSON serialization depth override.
    /// </summary>
    [Parameter(HelpMessage = "JSON serialization depth override (1-10)")]
    [ValidateRange(1, 10)]
    public int Depth { get; set; }

    /// <summary>
    /// Gets or sets the parameter-set names used to filter the MCP input schema.
    /// Only parameters that belong to at least one of the named sets are included.
    /// An error is raised at tool-creation time if any name does not match a declared set.
    /// </summary>
    [Parameter(HelpMessage = "Parameter-set names to filter the input schema to")]
    public string[]? ParameterSets { get; set; }

    /// <summary>
    /// Writes the configured <see cref="McpToolConfig"/> instance to the pipeline.
    /// </summary>
    protected override void ProcessRecord()
    {
        int? depth = MyInvocation.BoundParameters.ContainsKey(nameof(Depth))
            ? Depth
            : null;

        WriteObject(new McpToolConfig
        {
            Name = Name,
            Property = Property,
            ExcludeProperty = ExcludeProperty,
            Depth = depth,
            ParameterSets = ParameterSets
        });
    }
}
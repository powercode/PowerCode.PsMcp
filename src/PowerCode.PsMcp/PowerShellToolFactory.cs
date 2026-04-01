using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PowerCode.PsMcp;

/// <summary>
/// Builds <see cref="PowerShellTool"/> instances from PowerShell command metadata.
/// Uses <c>Get-Command</c> to resolve a <see cref="CommandInfo"/>, <c>Get-Help</c>
/// for synopsis text, and <see cref="ParameterMetadata"/> for the JSON input schema.
/// </summary>
internal sealed class PowerShellToolFactory : IDisposable
{
    private readonly Runspace _runspace;
    private readonly int _jsonDepth;

    // A single semaphore serialises all runspace access across every tool produced
    // by this factory, since PowerShell runspaces are single-threaded.
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    // Common PS parameters that are never exposed as MCP tool parameters.
    private static readonly HashSet<string> CommonParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction",
        "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable",
        "OutBuffer", "PipelineVariable", "WhatIf", "Confirm", "ProgressAction"
    };

    // Types that are security-sensitive and must be excluded from the MCP schema.
    private static readonly HashSet<Type> ExcludedTypes =
    [
        typeof(PSCredential),
        typeof(System.Security.SecureString)
    ];

    /// <summary>
    /// Initialises a new factory backed by <paramref name="runspace"/>.
    /// </summary>
    /// <param name="runspace">An already-opened runspace used for command discovery.</param>
    /// <param name="jsonDepth">
    ///   Depth passed to <c>ConvertTo-Json</c> when serialising tool output.
    ///   Defaults to 2.
    /// </param>
    public PowerShellToolFactory(Runspace runspace, int jsonDepth = 2)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        _runspace = runspace;
        _jsonDepth = jsonDepth;
    }

    /// <summary>
    /// Creates a <see cref="PowerShellTool"/> for <paramref name="commandName"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="commandName"/> cannot be resolved by <c>Get-Command</c>.
    /// </exception>
    public PowerShellTool Create(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        var commandInfo = GetCommandInfo(commandName);
        var description = GetHelpDescription(commandName);
        var inputSchema = BuildInputSchema(commandInfo);

        var protocolTool = new Tool
        {
            Name = commandName,
            Description = description,
            InputSchema = inputSchema
        };

        return new PowerShellTool(commandName, protocolTool, _runspace, _runspaceLock, _jsonDepth);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private CommandInfo GetCommandInfo(string commandName)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddCommand("Get-Command").AddParameter("Name", commandName);

        var results = ps.Invoke<CommandInfo>();
        return results.FirstOrDefault()
            ?? throw new ArgumentException(
                $"Command '{commandName}' was not found. Verify the command name and ensure the module is loaded.",
                nameof(commandName));
    }

    private string GetHelpDescription(string commandName)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Help").AddParameter("Name", commandName);

            var results = ps.Invoke();
            var helpObject = results.FirstOrDefault();

            if (helpObject is null)
            {
                return commandName;
            }

            // Prefer Synopsis (short description) from help.
            var synopsis = helpObject.Properties["Synopsis"]?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(synopsis) && synopsis != commandName)
            {
                return synopsis.Trim();
            }

            // Fall back to the details.description block.
            var details = helpObject.Properties["details"]?.Value as PSObject;
            if (details is not null)
            {
                var desc = details.Properties["description"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DEBUG: Could not retrieve help for '{commandName}': {ex.Message}");
        }

        return commandName;
    }

    private static JsonElement BuildInputSchema(CommandInfo commandInfo)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();

        foreach (var (paramName, paramMeta) in commandInfo.Parameters)
        {
            // Skip common/automatic parameters.
            if (CommonParameters.Contains(paramName))
            {
                continue;
            }

            // Exclude security-sensitive parameter types.
            if (ExcludedTypes.Contains(paramMeta.ParameterType))
            {
                continue;
            }

            var property = BuildPropertySchema(paramMeta);
            properties[paramName] = property;

            // A parameter is required if it is Mandatory in any parameter set.
            if (paramMeta.Attributes.OfType<ParameterAttribute>().Any(p => p.Mandatory))
            {
                required.Add(paramName);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
        {
            schema["required"] = required.ToArray();
        }

        var json = JsonSerializer.Serialize(schema);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, object?> BuildPropertySchema(ParameterMetadata paramMeta)
    {
        var property = new Dictionary<string, object?>();

        var paramType = Nullable.GetUnderlyingType(paramMeta.ParameterType) ?? paramMeta.ParameterType;

        // Handle array types: expose as JSON array with the element type.
        if (paramType.IsArray)
        {
            var elementType = paramType.GetElementType() ?? typeof(string);
            var (elementJsonType, _) = MapToJsonSchemaType(elementType);
            property["type"] = "array";
            property["items"] = new Dictionary<string, object?> { ["type"] = elementJsonType };
        }
        else
        {
            var (jsonType, jsonFormat) = MapToJsonSchemaType(paramType);
            property["type"] = jsonType;

            if (jsonFormat is not null)
            {
                property["format"] = jsonFormat;
            }
        }

        // Expose enum values from ValidateSet attributes.
        var validateSet = paramMeta.Attributes.OfType<ValidateSetAttribute>().FirstOrDefault();
        if (validateSet is not null)
        {
            property["enum"] = validateSet.ValidValues.Cast<object>().ToArray();
        }

        // Use HelpMessage as the property description when available.
        var helpMessage = paramMeta.Attributes
            .OfType<ParameterAttribute>()
            .Select(p => p.HelpMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        if (helpMessage is not null)
        {
            property["description"] = helpMessage;
        }

        return property;
    }

    /// <inheritdoc />
    public void Dispose() => _runspaceLock.Dispose();

    /// <summary>
    /// Maps a .NET <paramref name="type"/> to a JSON Schema type string and optional format.
    /// </summary>
    private static (string type, string? format) MapToJsonSchemaType(Type type) =>
        type switch
        {
            _ when type == typeof(string) => ("string", null),
            _ when type == typeof(int) || type == typeof(long)
                   || type == typeof(short) || type == typeof(uint)
                   || type == typeof(ulong) || type == typeof(byte) => ("integer", null),
            _ when type == typeof(double) || type == typeof(float)
                   || type == typeof(decimal) => ("number", null),
            _ when type == typeof(bool) => ("boolean", null),
            _ when type == typeof(SwitchParameter) => ("boolean", null),
            _ when type == typeof(DateTime) => ("string", "date-time"),
            _ when type == typeof(System.Collections.Hashtable) => ("object", null),
            // Default: any unrecognised type is represented as a string
            // and the caller passes a stringified value.
            _ => ("string", null)
        };
}

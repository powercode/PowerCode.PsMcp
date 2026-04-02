using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PowerCode.PsMcp;

/// <summary>
/// Captures structured help information extracted from a single <c>Get-Help</c> call.
/// </summary>
/// <param name="Synopsis">The synopsis text, or the command name when no synopsis is available.</param>
/// <param name="ParameterDescriptions">
///   Maps parameter names (case-insensitive) to their <c>.PARAMETER</c> help descriptions.
/// </param>
internal sealed record HelpInfo(
    string Synopsis,
    IReadOnlyDictionary<string, string> ParameterDescriptions)
{
    /// <summary>Returns a <see cref="HelpInfo"/> with just the command name and no descriptions.</summary>
    public static HelpInfo Empty(string commandName) =>
        new(commandName,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Builds <see cref="PowerShellTool"/> instances from PowerShell command metadata.
/// Uses <c>Get-Command</c> to resolve a <see cref="CommandInfo"/>, <c>Get-Help</c>
/// for synopsis, parameter descriptions and examples, and <see cref="ParameterMetadata"/>
/// for the JSON input schema.
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
        var helpInfo = GetHelpInfo(commandName);
        var syntax = GetCommandSyntax(commandName);
        var description = BuildDescription(helpInfo, syntax);
        var inputSchema = BuildInputSchema(commandInfo, helpInfo.ParameterDescriptions);

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

    /// <summary>
    /// Returns the parameter-set syntax for <paramref name="commandName"/> by invoking
    /// <c>Get-Command -Name &lt;name&gt; -Syntax</c>.
    /// Using <c>-Syntax</c> is necessary because <c>CommandInfo.Definition</c> returns the
    /// script-block body for functions rather than the parameter set syntax.
    /// Returns an empty string on any failure.
    /// </summary>
    private string GetCommandSyntax(string commandName)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-Command")
              .AddParameter("Name", commandName)
              .AddParameter("Syntax");

            var results = ps.Invoke<string>();
            return results.FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds the tool description from the synopsis and the parameter-set syntax.
    /// Format: <c>"{Synopsis}\n\nSyntax:\n{syntax}"</c> when syntax is available,
    /// or just the synopsis when it is not.
    /// </summary>
    private static string BuildDescription(HelpInfo helpInfo, string syntax)
    {
        if (string.IsNullOrWhiteSpace(syntax))
        {
            return helpInfo.Synopsis;
        }

        return $"{helpInfo.Synopsis}\n\nSyntax:\n{syntax}";
    }

    /// <summary>
    /// Retrieves structured help for <paramref name="commandName"/> via <c>Get-Help</c>,
    /// extracting the synopsis, per-parameter descriptions, and examples.
    /// Returns <see cref="HelpInfo.Empty"/> on any failure.
    /// </summary>
    private HelpInfo GetHelpInfo(string commandName)
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
                return HelpInfo.Empty(commandName);
            }

            // Prefer Synopsis (short description) from help.
            var synopsis = helpObject.Properties["Synopsis"]?.Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(synopsis) || synopsis == commandName)
            {
                // Fall back to the details.description block.
                var details = helpObject.Properties["details"]?.Value as PSObject;
                var detailsDesc = details?.Properties["description"]?.Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(detailsDesc))
                {
                    synopsis = detailsDesc;
                }
            }

            if (string.IsNullOrWhiteSpace(synopsis))
            {
                synopsis = commandName;
            }

            // Extract per-parameter descriptions from .PARAMETER help blocks.
            var paramDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (helpObject.Properties["parameters"]?.Value is PSObject parametersNode)
            {
                foreach (var paramPso in NormaliseToArray(parametersNode.Properties["parameter"]?.Value))
                {
                    var paramName = paramPso.Properties["name"]?.Value?.ToString();

                    if (string.IsNullOrWhiteSpace(paramName))
                    {
                        continue;
                    }

                    var descText = ExtractTextArray(paramPso.Properties["description"]?.Value);

                    if (!string.IsNullOrWhiteSpace(descText))
                    {
                        paramDescriptions[paramName] = descText;
                    }
                }
            }

            return new HelpInfo(synopsis, paramDescriptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DEBUG: Could not retrieve help for '{commandName}': {ex.Message}");
            return HelpInfo.Empty(commandName);
        }
    }

    /// <summary>
    /// Normalises a raw PSObject-or-array value into a list of <see cref="PSObject"/> instances.
    /// PowerShell returns a single <see cref="PSObject"/> when there is one element, and an array otherwise.
    /// </summary>
    private static List<PSObject> NormaliseToArray(object? raw)
    {
        var result = new List<PSObject>();

        switch (raw)
        {
            case null:
                break;
            case PSObject[] psoArray:
                result.AddRange(psoArray);
                break;
            case object[] objArray:
                foreach (var item in objArray)
                {
                    if (item is PSObject pso)
                    {
                        result.Add(pso);
                    }
                }
                break;
            case PSObject single:
                result.Add(single);
                break;
        }

        return result;
    }

    /// <summary>
    /// Joins the <c>.Text</c> values from a PSObject(-array) as returned by <c>Get-Help</c>
    /// description fields into a single trimmed string.
    /// </summary>
    private static string? ExtractTextArray(object? raw)
    {
        var items = NormaliseToArray(raw);

        if (items.Count == 0)
        {
            return null;
        }

        var joined = string.Join(" ",
            items
                .Select(item => item.Properties["Text"]?.Value?.ToString()?.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static JsonElement BuildInputSchema(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, string> parameterDescriptions)
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

            parameterDescriptions.TryGetValue(paramName, out var helpDescription);
            var property = BuildPropertySchema(paramMeta, helpDescription);
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

    private static Dictionary<string, object?> BuildPropertySchema(
        ParameterMetadata paramMeta,
        string? helpDescription)
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

        // Prefer Get-Help .PARAMETER description; fall back to the [Parameter(HelpMessage=...)] attribute.
        var description = helpDescription;

        if (string.IsNullOrWhiteSpace(description))
        {
            description = paramMeta.Attributes
                .OfType<ParameterAttribute>()
                .Select(p => p.HelpMessage)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }

        if (description is not null)
        {
            property["description"] = description;
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

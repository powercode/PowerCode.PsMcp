using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
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
    private readonly McpPsHost _host;

    // A single semaphore serialises all runspace access across every tool produced
    // by this factory, since PowerShell runspaces are single-threaded.
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    // Common PS parameters that are never exposed as MCP tool parameters.
    private static readonly HashSet<string> s_commonParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction",
        "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable",
        "OutBuffer", "PipelineVariable", "WhatIf", "Confirm", "ProgressAction"
    };

    // Types that are security-sensitive and must be excluded from the MCP schema.
    private static readonly HashSet<Type> s_excludedTypes =
    [
        typeof(PSCredential),
        typeof(System.Security.SecureString)
    ];

    private static readonly string[] s_outputVariantRequiredProperties = ["ParameterSetName", "items"];

    /// <summary>
    /// Initialises a new factory backed by <paramref name="runspace"/>.
    /// </summary>
    /// <param name="runspace">An already-opened runspace used for command discovery.</param>
    /// <param name="host">
    ///     The <see cref="McpPsHost"/> associated with <paramref name="runspace"/>.
    ///     When provided, tools will forward <see cref="System.Management.Automation.ProgressRecord"/>
    ///     events to the MCP client as progress notifications.
    /// </param>
    /// <param name="jsonDepth">
    ///     Depth passed to <c>ConvertTo-Json</c> when serialising tool output.
    ///     Defaults to 2.
    /// </param>
    public PowerShellToolFactory(Runspace runspace, McpPsHost host, int jsonDepth = 2)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        _runspace = runspace;
        _jsonDepth = jsonDepth;
        _host = host;
    }

    /// <summary>
    /// Creates a <see cref="PowerShellTool"/> for <paramref name="commandName"/>.
    /// </summary>
    /// <param name="commandName">The PowerShell command to invoke when the tool is called.</param>
    /// <param name="config">
    ///   Optional per-command MCP configuration. When no name is supplied, <paramref name="commandName"/>
    ///   is used as the tool name. When no depth override is supplied, the factory default is used.
    /// </param>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="commandName"/> cannot be resolved by <c>Get-Command</c>.
    /// </exception>
    public PowerShellTool Create(string commandName, McpToolConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        var commandInfo = GetCommandInfo(commandName);
        var helpInfo = GetHelpInfo(commandName);
        var parameterSets = ResolveParameterSets(commandInfo, commandName, config?.ParameterSets);
        var syntax = BuildSyntax(commandName, parameterSets);
        var description = BuildDescription(helpInfo, syntax);
        var inputSchema = BuildSafeInputSchema(commandInfo, commandName, helpInfo.ParameterDescriptions, parameterSets);
        var outputSchema = BuildOutputSchema(commandInfo, parameterSets);
        var toolName = config?.Name ?? commandName;
        var toolDepth = config?.Depth ?? _jsonDepth;

        var protocolTool = new Tool
        {
            Name = toolName,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema
        };

        return new PowerShellTool(
            commandName,
            protocolTool,
            _runspace,
            _runspaceLock,
            toolDepth,
            _host,
            config?.Property,
            config?.ExcludeProperty);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="requestedSets"/> against the command's declared parameter sets
    /// and returns the matching <see cref="CommandParameterSetInfo"/> list.
    /// Returns all parameter sets when <paramref name="requestedSets"/> is <see langword="null"/> or empty.
    /// Throws <see cref="ArgumentException"/> for any name that does not exist on the command.
    /// </summary>
    private static IReadOnlyList<CommandParameterSetInfo> ResolveParameterSets(
        CommandInfo commandInfo,
        string commandName,
        string[]? requestedSets)
    {
        if (requestedSets is null || requestedSets.Length == 0)
            return commandInfo.ParameterSets;

        var byName = commandInfo.ParameterSets
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<CommandParameterSetInfo>(requestedSets.Length);
        foreach (var name in requestedSets)
        {
            if (!byName.TryGetValue(name, out var setInfo))
                throw new ArgumentException(
                    $"Command '{commandName}' does not have a parameter set named '{name}'. " +
                    $"Available sets: {string.Join(", ", byName.Keys)}",
                    nameof(requestedSets));
            result.Add(setInfo);
        }

        return result;
    }

    /// <summary>
    /// Builds the JSON input schema for a command, returning a minimal object schema
    /// when parameter metadata cannot be fully resolved.
    /// </summary>
    private static JsonElement BuildSafeInputSchema(
        CommandInfo commandInfo,
        string commandName,
        IReadOnlyDictionary<string, string> parameterDescriptions,
        IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        try
        {
            return BuildInputSchema(commandInfo, parameterDescriptions, parameterSets);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DEBUG: Could not build input schema for '{commandName}': {ex.Message}");
            var fallbackSchema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>()
            };

            return JsonSerializer.SerializeToElement(fallbackSchema);
        }
    }

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
    /// Builds the syntax block for the tool description from the active <paramref name="parameterSets"/>.
    /// Each <see cref="CommandParameterSetInfo"/> is formatted as
    /// <c>"{commandName} {parameterSetInfo}"</c>, matching the output of
    /// <c>Get-Command -Syntax</c> but restricted to the sets actually exposed by the tool.
    /// </summary>
    private static string BuildSyntax(string commandName, IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        if (parameterSets.Count == 0)
            return string.Empty;

        return string.Join(Environment.NewLine,
            parameterSets.Select(s => $"{commandName} {s}".TrimEnd()));
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
            ps.AddCommand("Get-Help").AddParameter("Name", commandName).AddParameter("ProgressAction", ActionPreference.Ignore);

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
        IReadOnlyDictionary<string, string> parameterDescriptions,
        IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        // Multi-set commands get a oneOf schema with a per-set discriminator.
        if (parameterSets.Count > 1)
            return BuildOneOfInputSchema(commandInfo, parameterDescriptions, parameterSets);

        var properties = new Dictionary<string, object?>();
        var required = new List<string>();

        // Build a set of parameter names that appear in at least one of the active parameter sets.
        // When all sets are active this equals all parameters; when filtered it is a strict subset.
        var allowedNames = new HashSet<string>(
            parameterSets.SelectMany(s => s.Parameters.Select(p => p.Name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (paramName, paramMeta) in commandInfo.Parameters)
        {
            // Skip common/automatic parameters.
            if (s_commonParameters.Contains(paramName))
                continue;

            // Exclude security-sensitive parameter types.
            if (s_excludedTypes.Contains(paramMeta.ParameterType))
                continue;

            // When a parameter-set filter is active, skip parameters not in any of the chosen sets.
            if (!allowedNames.Contains(paramName))
                continue;

            parameterDescriptions.TryGetValue(paramName, out var helpDescription);
            var property = BuildPropertySchema(paramMeta, helpDescription);
            properties[paramName] = property;

            // A parameter is required only when it is Mandatory in every active parameter set
            // that contains it. If it is absent from or optional in any active set, the caller
            // can omit it by choosing that set, so it must not be listed as required.
            if (IsMandatoryInAllParameterSets(paramMeta, parameterSets))
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

    private static JsonElement BuildOneOfInputSchema(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, string> parameterDescriptions,
        IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        var variants = new List<Dictionary<string, object?>>(parameterSets.Count);

        foreach (var parameterSet in parameterSets)
        {
            var properties = new Dictionary<string, object?>
            {
                ["ParameterSetName"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["const"] = parameterSet.Name,
                    ["description"] = $"Selects the '{parameterSet.Name}' parameter set."
                }
            };

            var required = new List<string> { "ParameterSetName" };

            foreach (var parameterInfo in parameterSet.Parameters)
            {
                if (s_commonParameters.Contains(parameterInfo.Name))
                    continue;

                if (!commandInfo.Parameters.TryGetValue(parameterInfo.Name, out var paramMeta))
                    continue;

                if (s_excludedTypes.Contains(paramMeta.ParameterType))
                    continue;

                parameterDescriptions.TryGetValue(parameterInfo.Name, out var helpDescription);
                properties[parameterInfo.Name] = BuildPropertySchema(paramMeta, helpDescription);

                if (parameterInfo.IsMandatory)
                    required.Add(parameterInfo.Name);
            }

            variants.Add(new Dictionary<string, object?>
            {
                ["description"] = parameterSet.IsDefault
                    ? $"Parameter set '{parameterSet.Name}' (default)"
                    : $"Parameter set '{parameterSet.Name}'",
                ["properties"] = properties,
                ["required"] = required.ToArray()
            });
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["oneOf"] = variants.ToArray()
        };

        var json = JsonSerializer.Serialize(schema);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="paramMeta"/> is mandatory in
    /// every parameter set in <paramref name="allParameterSets"/>.
    /// A parameter that is absent from, or optional in, any set can always be omitted by
    /// choosing that set, so it must not be marked as globally required.
    /// </summary>
    private static bool IsMandatoryInAllParameterSets(
        ParameterMetadata paramMeta,
        IReadOnlyList<CommandParameterSetInfo> allParameterSets)
    {
        if (allParameterSets.Count == 0)
            return false;

        // The "__AllParameterSets" sentinel is only stored for parameters declared without an
        // explicit ParameterSetName (i.e. they truly belong to every set). Parameters with at
        // least one explicit set entry must NOT fall back to the sentinel for sets they are not
        // in — doing so would incorrectly mark them as required in all sets.
        bool hasExplicitSets = paramMeta.ParameterSets
            .Keys.Any(k => !string.Equals(k, ParameterAttribute.AllParameterSets, StringComparison.OrdinalIgnoreCase));

        foreach (var parameterSet in allParameterSets)
        {
            if (paramMeta.ParameterSets.TryGetValue(parameterSet.Name, out var setMeta))
            {
                if (!setMeta.IsMandatory)
                    return false;
            }
            else if (!hasExplicitSets &&
                     paramMeta.ParameterSets.TryGetValue(ParameterAttribute.AllParameterSets, out var allSetMeta))
            {
                // Parameter declared for all sets — use the sentinel entry's mandatory flag.
                if (!allSetMeta.IsMandatory)
                    return false;
            }
            else
            {
                // Not present in this set → caller can pick this set and omit the parameter.
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Builds an MCP <c>outputSchema</c> from the command's <see cref="CommandInfo.OutputType"/> attribute.
    /// The schema describes an <c>{"items": [...]}</c> envelope matching PowerShell pipeline
    /// semantics where commands write zero or more objects to the pipeline.
    /// Returns <see langword="null"/> when no output types are declared.
    /// </summary>
    private JsonElement? BuildOutputSchema(
        CommandInfo commandInfo,
        IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        if (commandInfo.OutputType.Count == 0)
            return null;

        var perSetTypes = ResolvePerSetOutputTypes(commandInfo);

        if (perSetTypes is not null && parameterSets.Count > 1)
        {
            var mappedSetCount = 0;
            PSTypeName[]? singleMappedSetTypes = null;

            foreach (var parameterSet in parameterSets)
            {
                if (perSetTypes.TryGetValue(parameterSet.Name, out var setTypes)
                    || perSetTypes.TryGetValue(ParameterAttribute.AllParameterSets, out setTypes))
                {
                    mappedSetCount++;
                    singleMappedSetTypes = setTypes;
                }
            }

            if (mappedSetCount > 1)
                return BuildOneOfOutputSchema(perSetTypes, parameterSets);

            if (mappedSetCount == 1 && singleMappedSetTypes is not null)
            {
                return BuildFlatOutputSchema(
                    BuildArrayItemsSchema(singleMappedSetTypes.Select(static t => t.Type).ToArray()));
            }
        }

        if (perSetTypes is not null && parameterSets.Count == 1)
        {
            var setName = parameterSets[0].Name;
            if (perSetTypes.TryGetValue(setName, out var setTypes)
                || perSetTypes.TryGetValue(ParameterAttribute.AllParameterSets, out setTypes))
            {
                return BuildFlatOutputSchema(
                    BuildArrayItemsSchema(setTypes.Select(static t => t.Type).ToArray()));
            }
        }

        var outputTypes = commandInfo.OutputType
            .Select(static outputType => outputType.Type)
            .ToArray();

        return BuildFlatOutputSchema(BuildArrayItemsSchema(outputTypes));
    }

    private JsonElement BuildOneOfOutputSchema(
        Dictionary<string, PSTypeName[]> perSetTypes,
        IReadOnlyList<CommandParameterSetInfo> parameterSets)
    {
        var variants = new List<Dictionary<string, object?>>();

        foreach (var parameterSet in parameterSets)
        {
            if (!perSetTypes.TryGetValue(parameterSet.Name, out var setTypes)
                && !perSetTypes.TryGetValue(ParameterAttribute.AllParameterSets, out setTypes))
            {
                continue;
            }

            variants.Add(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["ParameterSetName"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["const"] = parameterSet.Name
                    },
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = BuildArrayItemsSchema(setTypes.Select(static typeName => typeName.Type).ToArray())
                    }
                },
                ["required"] = s_outputVariantRequiredProperties
            });
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["oneOf"] = variants
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement BuildFlatOutputSchema(object itemsSchema)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = itemsSchema
                }
            }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    private object BuildArrayItemsSchema(IReadOnlyList<Type?> types)
    {
        var itemSchemas = types
            .Select(BuildItemSchema)
            .Cast<object>()
            .ToArray();

        if (itemSchemas.Length == 1)
            return itemSchemas[0];

        return new Dictionary<string, object?>
        {
            ["oneOf"] = itemSchemas
        };
    }

    private static Dictionary<string, PSTypeName[]>? ResolvePerSetOutputTypes(CommandInfo commandInfo)
    {
        IEnumerable<OutputTypeAttribute> outputTypeAttributes = commandInfo switch
        {
            FunctionInfo functionInfo => functionInfo.ScriptBlock.Attributes.OfType<OutputTypeAttribute>(),
            CmdletInfo cmdletInfo => cmdletInfo.ImplementingType.GetCustomAttributes<OutputTypeAttribute>(inherit: true),
            _ => []
        };

        var attributes = outputTypeAttributes.ToArray();
        if (attributes.Length == 0)
            return null;

        var bySet = new Dictionary<string, List<PSTypeName>>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputTypeAttribute in attributes)
        {
            var setNames = outputTypeAttribute.ParameterSetName is { Length: > 0 }
                ? outputTypeAttribute.ParameterSetName
                : [ParameterAttribute.AllParameterSets];

            foreach (var setName in setNames)
            {
                var key = string.IsNullOrWhiteSpace(setName)
                    ? ParameterAttribute.AllParameterSets
                    : setName;

                if (!bySet.TryGetValue(key, out var typeNames))
                {
                    typeNames = [];
                    bySet[key] = typeNames;
                }

                typeNames.AddRange(outputTypeAttribute.Type);
            }
        }

        if (bySet.Count == 0)
            return null;

        var hasNonAllParameterSet = bySet.Keys.Any(
            key => !string.Equals(key, ParameterAttribute.AllParameterSets, StringComparison.OrdinalIgnoreCase));

        if (!hasNonAllParameterSet)
            return null;

        return bySet.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a JSON Schema fragment describing a single pipeline object of the given <paramref name="type"/>.
    /// For basic types, returns the corresponding JSON Schema primitive.
    /// For complex types, reflects public CLR instance properties and merges in any ETS-defined
    /// properties registered via <c>Update-TypeData</c> or PS1XML files.
    /// </summary>
    private Dictionary<string, object?> BuildItemSchema(Type? type)
    {
        if (type is null || type == typeof(void))
            return new Dictionary<string, object?> { ["type"] = "object" };

        type = Nullable.GetUnderlyingType(type) ?? type;

        // Opaque types with no meaningful compile-time property info.
        if (type == typeof(object) || type == typeof(PSObject))
            return new Dictionary<string, object?> { ["type"] = "object" };

        // Basic/primitive types: use the existing JSON Schema mapping.
        if (IsJsonPrimitiveType(type))
        {
            var (jsonType, jsonFormat) = MapToJsonSchemaType(type);
            var s = new Dictionary<string, object?> { ["type"] = jsonType };
            if (jsonFormat is not null) s["format"] = jsonFormat;
            return s;
        }

        // Array types: describe element type.
        if (type.IsArray)
        {
            var elemType = type.GetElementType() ?? typeof(object);
            return new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = BuildItemSchema(elemType)
            };
        }

        var propSchemas = new Dictionary<string, object?>();

        // Reflect public CLR instance properties.
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (propType.IsArray)
            {
                var elemType = propType.GetElementType() ?? typeof(string);
                var (elt, _) = MapToJsonSchemaType(elemType);
                propSchemas[prop.Name] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = elt }
                };
            }
            else
            {
                var (jt, jf) = MapToJsonSchemaType(propType);
                var ps = new Dictionary<string, object?> { ["type"] = jt };
                if (jf is not null) ps["format"] = jf;
                propSchemas[prop.Name] = ps;
            }
        }

        // Merge ETS-defined properties (alias, code, note, script) registered for this type.
        // ETS entries supplement and can override CLR property schemas.
        foreach (var (name, schema) in GetEtsProperties(type))
            propSchemas[name] = schema;

        if (propSchemas.Count == 0)
            return new Dictionary<string, object?> { ["type"] = "object" };

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = propSchemas
        };
    }

    /// <summary>
    /// Queries <c>Get-TypeData</c> for ETS members registered on <paramref name="type"/> and yields
    /// name/schema pairs for each property member. Internal PS members prefixed with <c>__</c> are skipped.
    /// </summary>
    private IEnumerable<(string Name, Dictionary<string, object?> Schema)> GetEtsProperties(Type type)
    {
        if (type.FullName is null) yield break;

        TypeData? typeData;
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddCommand("Get-TypeData").AddParameter("TypeName", type.FullName);
            typeData = ps.Invoke().FirstOrDefault()?.BaseObject as TypeData;
        }
        catch
        {
            yield break;
        }

        if (typeData is null) yield break;

        foreach (var (name, member) in typeData.Members)
        {
            // Skip internal/metadata members such as __NounName.
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;

            var schema = member switch
            {
                AliasPropertyData alias => BuildAliasPropertySchema(alias, type),
                CodePropertyData code   => BuildCodePropertySchema(code),
                NotePropertyData note   => BuildNotePropertySchema(note),
                // Script properties have no static type information.
                ScriptPropertyData      => new Dictionary<string, object?> { ["type"] = "object" },
                _                       => null
            };

            if (schema is not null) yield return (name, schema);
        }
    }

    /// <summary>
    /// Resolves an <see cref="AliasPropertyData"/> to a JSON Schema by looking up the referenced
    /// CLR property on <paramref name="declaringType"/>.
    /// Falls back to <c>{"type":"object"}</c> when the reference cannot be resolved.
    /// </summary>
    private static Dictionary<string, object?> BuildAliasPropertySchema(
        AliasPropertyData alias, Type declaringType)
    {
        var referenced = declaringType.GetProperty(
            alias.ReferencedMemberName,
            BindingFlags.Public | BindingFlags.Instance);

        if (referenced is null)
            return new Dictionary<string, object?> { ["type"] = "object" };

        var refType = Nullable.GetUnderlyingType(referenced.PropertyType) ?? referenced.PropertyType;
        var (jt, jf) = MapToJsonSchemaType(refType);
        var schema = new Dictionary<string, object?> { ["type"] = jt };
        if (jf is not null) schema["format"] = jf;
        return schema;
    }

    /// <summary>
    /// Derives a JSON Schema from a <see cref="CodePropertyData"/> getter's return type.
    /// Falls back to <c>{"type":"object"}</c> when no getter is available.
    /// </summary>
    private static Dictionary<string, object?> BuildCodePropertySchema(CodePropertyData code)
    {
        if (code.GetCodeReference is null)
            return new Dictionary<string, object?> { ["type"] = "object" };

        var returnType = Nullable.GetUnderlyingType(code.GetCodeReference.ReturnType)
                         ?? code.GetCodeReference.ReturnType;
        var (jt, jf) = MapToJsonSchemaType(returnType);
        var schema = new Dictionary<string, object?> { ["type"] = jt };
        if (jf is not null) schema["format"] = jf;
        return schema;
    }

    /// <summary>
    /// Derives a JSON Schema from the runtime type of a <see cref="NotePropertyData"/>'s value.
    /// Falls back to <c>{"type":"object"}</c> for null values.
    /// </summary>
    private static Dictionary<string, object?> BuildNotePropertySchema(NotePropertyData note)
    {
        if (note.Value is null)
            return new Dictionary<string, object?> { ["type"] = "object" };

        var valType = Nullable.GetUnderlyingType(note.Value.GetType()) ?? note.Value.GetType();
        var (jt, jf) = MapToJsonSchemaType(valType);
        var schema = new Dictionary<string, object?> { ["type"] = jt };
        if (jf is not null) schema["format"] = jf;
        return schema;
    }

    /// <summary>
    /// Returns <see langword="true"/> for types that map to a JSON Schema primitive,
    /// as opposed to complex types that should have their properties reflected.
    /// </summary>
    private static bool IsJsonPrimitiveType(Type type) =>
        type == typeof(string)
        || type == typeof(int) || type == typeof(long)
        || type == typeof(short) || type == typeof(uint)
        || type == typeof(ulong) || type == typeof(byte)
        || type == typeof(double) || type == typeof(float)
        || type == typeof(decimal)
        || type == typeof(bool) || type == typeof(SwitchParameter)
        || type == typeof(DateTime)
        || type == typeof(System.Collections.Hashtable);

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

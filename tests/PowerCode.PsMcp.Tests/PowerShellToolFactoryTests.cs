using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Integration-style tests for <see cref="PowerShellToolFactory"/>.
/// Tests run against real PowerShell commands (Get-Process, Get-Date) that are
/// always available in the .NET SDK build environment.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PowerShellToolFactoryTests : IDisposable
{
    private readonly Runspace _runspace;
    private readonly PowerShellToolFactory _factory;

    public PowerShellToolFactoryTests()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
        _factory = new PowerShellToolFactory(_runspace, new McpPsHost(), jsonDepth: 2);
    }

    public void Dispose()
    {
        _runspace.Close();
        _runspace.Dispose();
    }

    // ── Tool name ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Create_WithGetProcess_ReturnsToolWithMatchingName()
    {
        var tool = _factory.Create("Get-Process");

        Assert.AreEqual("Get-Process", tool.ProtocolTool.Name);
    }

    [TestMethod]
    public void Create_WithToolName_SetsProtocolToolNameToProvidedName()
    {
        var tool = _factory.Create("Get-Process", new McpToolConfig { Name = "list-procs" });

        Assert.AreEqual("list-procs", tool.ProtocolTool.Name);
    }

    [TestMethod]
    public void Create_WithNullToolName_UsesCommandNameAsDefault()
    {
        var tool = _factory.Create("Get-Process", config: null);

        Assert.AreEqual("Get-Process", tool.ProtocolTool.Name);
    }

    [TestMethod]
    public void Create_WithConfigDepth_OverridesFactoryDefault()
    {
        var tool = _factory.Create("Get-Process", new McpToolConfig { Depth = 4 });
        var jsonDepthField = typeof(PowerShellTool).GetField("_jsonDepth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(jsonDepthField);
        Assert.AreEqual(4, jsonDepthField.GetValue(tool));
    }

    // ── Input schema ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Create_WithGetProcess_InputSchemaHasObjectType()
    {
        var tool = _factory.Create("Get-Process");
        var schema = tool.ProtocolTool.InputSchema;

        Assert.AreEqual("object", schema.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_WithGetProcess_HasNamePropertyInSchema()
    {
        var tool = _factory.Create("Get-Process");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // Get-Process -Name is a string[] parameter; should appear in properties.
        Assert.IsTrue(properties.TryGetProperty("Name", out _),
            "Expected 'Name' to appear in the input schema properties.");
    }

    // ── Type mapping ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Create_StringArrayParameter_MapsToArrayType()
    {
        var tool = _factory.Create("Get-Process");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // Get-Process -Name is string[] → JSON array with string items.
        var nameProp = properties.GetProperty("Name");
        Assert.AreEqual("array", nameProp.GetProperty("type").GetString());
        Assert.AreEqual("string", nameProp.GetProperty("items").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_IntArrayParameter_MapsToArrayWithIntegerItems()
    {
        var tool = _factory.Create("Get-Process");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // Get-Process -Id is int[] → JSON array with integer items.
        Assert.IsTrue(properties.TryGetProperty("Id", out var idProp),
            "Expected 'Id' to appear in the input schema properties.");
        Assert.AreEqual("array", idProp.GetProperty("type").GetString());
        Assert.AreEqual("integer", idProp.GetProperty("items").GetProperty("type").GetString());
    }

    // ── Common parameters are excluded ───────────────────────────────────────

    [TestMethod]
    public void Create_CommonParameters_AreExcludedFromSchema()
    {
        var tool = _factory.Create("Get-Process");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        var commonParams = new[] { "Verbose", "Debug", "ErrorAction", "WhatIf", "Confirm" };
        foreach (var param in commonParams)
        {
            Assert.IsFalse(properties.TryGetProperty(param, out _),
                $"Common parameter '{param}' should not appear in the schema.");
        }
    }

    // ── Invalid command ──────────────────────────────────────────────────────

    [TestMethod]
    public void Create_WithUnknownCommand_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            _factory.Create("Invoke-ThisCommandDoesNotExist_99999"));
    }

    // ── Advanced function comment-based help ─────────────────────────────────
    //
    // The helper method below defines Get-CurrentWeather in the shared runspace
    // so that the tests can exercise comment-based help extraction end-to-end.

    private void DefineWeatherFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Get-CurrentWeather {
    <#
    .SYNOPSIS
        Retrieves the current weather for a specified location.
    .DESCRIPTION
        The Get-CurrentWeather function queries weather data for a given location.
    .PARAMETER location
        The name of the location (city, country, etc.) to retrieve weather for.
    .PARAMETER unit
        The temperature unit to use. Valid values are 'celsius' and 'fahrenheit'.
    .EXAMPLE
        Get-CurrentWeather -location ""New York"" -unit ""celsius""
        Retrieves the current weather for New York in Celsius.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$location,
        [Parameter(Mandatory = $true)]
        [ValidateSet('celsius', 'fahrenheit')]
        [string]$unit
    )
}");
        ps.Invoke();
    }

    [TestMethod]
    public void Create_AdvancedFunctionWithCommentHelp_DescriptionContainsSynopsis()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");

        Assert.IsNotNull(tool.ProtocolTool.Description);
        Assert.Contains("Retrieves the current weather", tool.ProtocolTool.Description);
    }

    [TestMethod]
    public void Create_AdvancedFunctionWithCommentHelp_DescriptionContainsSyntax()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");

        Assert.IsNotNull(tool.ProtocolTool.Description);
        Assert.Contains("Syntax:", tool.ProtocolTool.Description);
        Assert.Contains("[-location]", tool.ProtocolTool.Description);
        StringAssert.DoesNotMatch(tool.ProtocolTool.Description,
            new System.Text.RegularExpressions.Regex("Examples:"));
    }

    [TestMethod]
    public void Create_AdvancedFunctionWithCommentHelp_ParameterHasHelpDescription()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        var locationDesc = properties.GetProperty("location").GetProperty("description").GetString();

        Assert.IsNotNull(locationDesc);
        Assert.Contains("The name of the location", locationDesc);
    }

    [TestMethod]
    public void Create_AdvancedFunctionWithCommentHelp_ValidateSetAppearsAsEnum()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        var enumValues = properties.GetProperty("unit").GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        CollectionAssert.Contains(enumValues, "celsius");
        CollectionAssert.Contains(enumValues, "fahrenheit");
    }

    [TestMethod]
    public void Create_AdvancedFunctionWithCommentHelp_MandatoryParametersAreRequired()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");
        var required = tool.ProtocolTool.InputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        CollectionAssert.Contains(required, "location");
        CollectionAssert.Contains(required, "unit");
    }
    [TestMethod]
    [TestCategory("LocalHelpInstalled")]
    public void Create_WithGetDate_DescriptionContainsSynopsisAndSyntax()
    {
        var tool = _factory.Create("Get-Date");
        var description = tool.ProtocolTool.Description;

        Assert.IsNotNull(description);
        Assert.Contains("Gets the current date and time", description);
        Assert.Contains("Syntax:", description);
        Assert.Contains("[-Format <string>]", description);
        StringAssert.DoesNotMatch(description,
            new System.Text.RegularExpressions.Regex("Examples:"));
        Assert.IsLessThan(1000, description.Length,
            $"Expected description length < 1000 but was {description.Length}");
    }
    // ── HelpMessage attribute fallback ───────────────────────────────────────

    [TestMethod]
    public void Create_FunctionWithHelpMessageAttribute_UsesHelpMessageAsDescription()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-HelpMessageFallback {
    param(
        [Parameter(Mandatory = $true, HelpMessage = 'The name to greet')]
        [string]$Name
    )
}");
        ps.Invoke();

        var tool = _factory.Create("Test-HelpMessageFallback");
        var nameDesc = tool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name")
            .GetProperty("description")
            .GetString();

        Assert.AreEqual("The name to greet", nameDesc);
    }

    [TestMethod]
    public void Create_FunctionWithBothHelpSources_PrefersGetHelpDescription()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-HelpPriority {
    <#
    .PARAMETER Name
        The full name of the person from comment-based help.
    #>
    param(
        [Parameter(Mandatory = $true, HelpMessage = 'HelpMessage attribute value')]
        [string]$Name
    )
}");
        ps.Invoke();

        var tool = _factory.Create("Test-HelpPriority");
        var nameDesc = tool.ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name")
            .GetProperty("description")
            .GetString();

        Assert.IsNotNull(nameDesc);
        Assert.Contains("full name of the person from comment-based help", nameDesc);
    }

    // ── Output schema ────────────────────────────────────────────────────────

    [TestMethod]
    public void Create_WithNoOutputType_OutputSchemaIsNull()
    {
        DefineWeatherFunction();

        var tool = _factory.Create("Get-CurrentWeather");

        Assert.IsNull(tool.ProtocolTool.OutputSchema);
    }

    [TestMethod]
    public void Create_WithStringOutputType_OutputSchemaHasStringItemsArray()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-StringOutput {
    [OutputType([string])]
    param()
    'hello'
}");
        ps.Invoke();

        var tool = _factory.Create("Test-StringOutput");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var schema = tool.ProtocolTool.OutputSchema!.Value;
        Assert.AreEqual("object", schema.GetProperty("type").GetString());
        var items = schema.GetProperty("properties").GetProperty("items");
        Assert.AreEqual("array", items.GetProperty("type").GetString());
        Assert.AreEqual("string", items.GetProperty("items").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_WithIntOutputType_OutputSchemaHasIntegerItemsArray()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-IntOutput {
    [OutputType([int])]
    param()
    42
}");
        ps.Invoke();

        var tool = _factory.Create("Test-IntOutput");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var items = tool.ProtocolTool.OutputSchema!.Value
            .GetProperty("properties").GetProperty("items");
        Assert.AreEqual("integer", items.GetProperty("items").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_WithMultipleOutputTypes_OutputSchemaUsesLastDeclaredType()
    {
        // Get-Process declares three output types for different parameter sets:
        // ProcessModule (-Module), FileVersionInfo (-FileVersionInfo), Process (default).
        var tool = _factory.Create("Get-Process");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var items = tool.ProtocolTool.OutputSchema!.Value
            .GetProperty("properties").GetProperty("items");
        Assert.AreEqual("array", items.GetProperty("type").GetString());

        // Last declared output type is used as the primary/default schema.
        var processSchema = items.GetProperty("items");
        Assert.AreEqual("object", processSchema.GetProperty("type").GetString());

        var properties = processSchema.GetProperty("properties");
        Assert.IsTrue(properties.TryGetProperty("ProcessName", out _), "Expected ProcessName in Process schema properties.");
        Assert.IsTrue(properties.TryGetProperty("Id", out _), "Expected Id in Process schema properties.");
    }

    [TestMethod]
    public void Create_WithDateTimeOutputType_OutputSchemaHasDateTimeInItems()
    {
        // Get-Date declares [OutputType([string])] and [OutputType([DateTime])].
        var tool = _factory.Create("Get-Date");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var items = tool.ProtocolTool.OutputSchema!.Value
            .GetProperty("properties").GetProperty("items");

        var dateTimeSchema = items.GetProperty("items");
        Assert.AreEqual("string", dateTimeSchema.GetProperty("type").GetString());
        Assert.AreEqual("date-time", dateTimeSchema.GetProperty("format").GetString());
    }

    // ── Multiple parameter sets ──────────────────────────────────────────────

    // Defines Test-MultiSet with two parameter sets:
    //   ByName  – mandatory -Name  (string), optional -Verbose
    //   ById    – mandatory -Id    (int),    optional -Verbose
    private void DefineMultiSetFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript(@"
function Test-MultiSet {
    <#
    .SYNOPSIS
        A command with two parameter sets.
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    param(
        [Parameter(ParameterSetName = 'ByName', Mandatory = $true)]
        [string]$Name,

        [Parameter(ParameterSetName = 'ById', Mandatory = $true)]
        [int]$Id
    )
}");
        ps.Invoke();
    }

    [TestMethod]
    public void Create_MultipleParameterSets_DescriptionContainsAllSyntaxLines()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet");
        var description = tool.ProtocolTool.Description;

        Assert.IsNotNull(description);
        // Both parameter-set syntax lines must appear.
        Assert.Contains("-Name", description,
            "Expected syntax for the ByName parameter set in the description.");
        Assert.Contains("-Id", description,
            "Expected syntax for the ById parameter set in the description.");
    }

    [TestMethod]
    public void Create_MultipleParameterSets_ParameterMandatoryInOnlyOneSetIsNotRequired()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet");

        // Neither Name nor Id is mandatory in every set, so the required array must be absent
        // (or empty) – each is only required when the caller picks that set.
        Assert.IsFalse(
            tool.ProtocolTool.InputSchema.TryGetProperty("required", out var req) &&
            req.EnumerateArray().Any(),
            "Parameters that are mandatory in only one parameter set must not appear in 'required'.");
    }

    // ── ParameterSets filtering ──────────────────────────────────────────────

    [TestMethod]
    public void Create_WithParameterSetFilter_OnlyIncludesParametersFromNamedSet()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet", new McpToolConfig { ParameterSets = ["ByName"] });
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        Assert.IsTrue(properties.TryGetProperty("Name", out _),
            "Expected 'Name' in schema when filtering to ByName set.");
        Assert.IsFalse(properties.TryGetProperty("Id", out _),
            "Expected 'Id' to be absent when filtering to ByName set only.");
    }

    [TestMethod]
    public void Create_WithParameterSetFilter_MandatoryParameterInFilteredSetIsRequired()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet", new McpToolConfig { ParameterSets = ["ByName"] });
        var required = tool.ProtocolTool.InputSchema.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        // When only ByName is active, Name is mandatory in every active set → required.
        CollectionAssert.Contains(required, "Name");
        CollectionAssert.DoesNotContain(required, "Id");
    }

    [TestMethod]
    public void Create_WithMultipleParameterSetFilters_IncludesParametersFromAllNamedSets()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet", new McpToolConfig { ParameterSets = ["ByName", "ById"] });
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        Assert.IsTrue(properties.TryGetProperty("Name", out _));
        Assert.IsTrue(properties.TryGetProperty("Id", out _));
    }

    [TestMethod]
    public void Create_WithNonExistentParameterSet_ThrowsArgumentException()
    {
        DefineMultiSetFunction();

        Assert.ThrowsExactly<ArgumentException>(() =>
            _factory.Create("Test-MultiSet", new McpToolConfig { ParameterSets = ["DoesNotExist"] }));
    }

    // ── Get-Process with real parameter sets (integration scenario) ──────────
    //
    // Mirrors the user-facing pattern:
    //   Start-McpServer -Module Microsoft.PowerShell.Management -Include Get-Process
    //     -CommandConfiguration @{
    //       'Get-Process' = New-McpToolConfig -Name 'process'
    //           -Property Id, Name, CPU, StartTime, Path, CommandLine
    //           -Depth 1 -ParameterSets Name, Id
    //     }

    [TestMethod]
    public void Create_GetProcessWithNameAndIdSets_OnlyNameAndIdParametersInSchema()
    {
        var config = new McpToolConfig
        {
            Name = "process",
            Property = ["Id", "Name", "CPU", "StartTime", "Path", "CommandLine"],
            Depth = 1,
            ParameterSets = ["Name", "Id"]
        };

        var tool = _factory.Create("Get-Process", config);

        Assert.AreEqual("process", tool.ProtocolTool.Name);

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // Name and Id belong to the filtered sets – both must be present.
        Assert.IsTrue(properties.TryGetProperty("Name", out _),
            "Expected 'Name' in schema.");
        Assert.IsTrue(properties.TryGetProperty("Id", out _),
            "Expected 'Id' in schema.");

        // InputObject is exclusive to the InputObject/InputObjectWithUserName sets – must be absent.
        Assert.IsFalse(properties.TryGetProperty("InputObject", out _),
            "Expected 'InputObject' to be absent when filtered to Name and Id sets.");

        // IncludeUserName is exclusive to the *WithUserName sets – must be absent.
        Assert.IsFalse(properties.TryGetProperty("IncludeUserName", out _),
            "Expected 'IncludeUserName' to be absent when filtered to Name and Id sets.");
    }

    [TestMethod]
    public void Create_GetProcessWithNameAndIdSets_NeitherNameNorIdIsRequired()
    {
        // Name is mandatory only in the Name set; Id only in the Id set.
        // Neither is mandatory in every active set, so neither should be required.
        var config = new McpToolConfig
        {
            ParameterSets = ["Name", "Id"]
        };

        var tool = _factory.Create("Get-Process", config);

        var hasRequired = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var req) &&
                          req.EnumerateArray().Any();

        Assert.IsFalse(hasRequired,
            "Neither Name nor Id is mandatory in every active set – required array must be absent.");
    }
}

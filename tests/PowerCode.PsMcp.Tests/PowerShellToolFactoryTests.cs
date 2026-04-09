using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

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
    private static readonly string[] s_byNameRequired = ["Name"];
    private static readonly string[] s_byIdRequired = ["Id"];
    private static readonly string[] s_stringAndNullTypes = ["string", "null"];
    private static readonly string[] s_arrayAndNullTypes = ["array", "null"];

    private readonly Runspace _runspace;
    private readonly PowerShellToolFactory _factory;

    public PowerShellToolFactoryTests()
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();
        _factory = new PowerShellToolFactory(_runspace, new McpPsHost(), NullLoggerFactory.Instance, jsonDepth: 2);
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
        // Get-Process -Name is a string[] parameter; it should appear in at least one variant.
        Assert.IsTrue(AnySchemaPropertiesContain(tool.ProtocolTool.InputSchema, "Name"),
            "Expected 'Name' to appear in the input schema properties.");
    }

    // ── Type mapping ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Create_StringArrayParameter_MapsToArrayType()
    {
        var tool = _factory.Create("Get-Process");
        var nameProp = FindPropertySchema(tool.ProtocolTool.InputSchema, "Name");

        // Get-Process -Name is string[] → JSON array with string items.
        Assert.AreEqual("array", nameProp.GetProperty("type").GetString());
        Assert.AreEqual("string", nameProp.GetProperty("items").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_IntArrayParameter_MapsToArrayWithIntegerItems()
    {
        var tool = _factory.Create("Get-Process");
        var idProp = FindPropertySchema(tool.ProtocolTool.InputSchema, "Id");

        // Get-Process -Id is int[] → JSON array with integer items.
        Assert.AreEqual("array", idProp.GetProperty("type").GetString());
        Assert.AreEqual("integer", idProp.GetProperty("items").GetProperty("type").GetString());
    }

    // ── Common parameters are excluded ───────────────────────────────────────

    [TestMethod]
    public void Create_CommonParameters_AreExcludedFromSchema()
    {
        var tool = _factory.Create("Get-Process");
        var propertySets = EnumerateSchemaProperties(tool.ProtocolTool.InputSchema);

        var commonParams = new[] { "Verbose", "Debug", "ErrorAction", "WhatIf", "Confirm" };
        foreach (var param in commonParams)
        {
            Assert.IsTrue(propertySets.All(properties => !properties.TryGetProperty(param, out _)),
                $"Common parameter '{param}' should not appear in the schema.");
        }
    }

    // ── Required parameters ───────────────────────────────────────────────────

    [TestMethod]
    public void Create_WithGetProcess_NoParametersAreRequired()
    {
        // Get-Process has three separate parameter sets (Name, Id, InputObject). Each
        // mandatory parameter is mandatory in only one set, so none is globally required.
        // Regression test: the old Attributes-based implementation incorrectly marked
        // Id and InputObject as required for compiled cmdlets.
        var tool = _factory.Create("Get-Process");

        var hasRequired = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var req) &&
                          req.EnumerateArray().Any();

        Assert.IsFalse(hasRequired,
            "No Get-Process parameter is mandatory in every parameter set; " +
            "'required' must be absent or empty.");
    }

    [TestMethod]
    public void Create_ScriptFunctionWithSeparateParameterSets_NeitherMandatoryParamIsRequired()
    {
        // Regression test: ParameterMetadata.ParameterSets for script-function parameters
        // may include an "__AllParameterSets" entry in addition to the explicit set entry.
        // The __AllParameterSets fallback must not be used for sets the parameter is not
        // explicitly part of, or every mandatory param would incorrectly appear required.
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Get-ProcessFileVersion {
                         [CmdletBinding()]
                         param(
                             [Parameter(Mandatory = $true, ParameterSetName = 'Name')]
                             [string[]] $Name,
                             [Parameter(Mandatory = $true, ParameterSetName = 'Id')]
                             [int[]] $Id
                         )
                     }
                     """);
        ps.Invoke();

        var tool = _factory.Create("Get-ProcessFileVersion");

        var hasRequired = tool.ProtocolTool.InputSchema.TryGetProperty("required", out var req) &&
                          req.EnumerateArray().Any();

        Assert.IsFalse(hasRequired,
            "Neither Name nor Id is mandatory in every parameter set of a script function; " +
            "'required' must be absent or empty.");
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
        ps.AddScript("""

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
                             Get-CurrentWeather -location "New York" -unit "celsius"
                             Retrieves the current weather for New York in Celsius.
                         #>
                         param(
                             [Parameter(Mandatory = $true)]
                             [string]$location,
                             [Parameter(Mandatory = $true)]
                             [ValidateSet('celsius', 'fahrenheit')]
                             [string]$unit
                         )
                     }
                     """);
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
        Assert.DoesNotMatchRegex(new System.Text.RegularExpressions.Regex("Examples:"), tool.ProtocolTool.Description);
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

        Assert.Contains("celsius", enumValues);
        Assert.Contains("fahrenheit", enumValues);
    }

    [TestMethod]
    public void Create_FunctionWithStringArrayValidateSet_EmitsEnumOnItems()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-StringArrayValidateSet {
                         param(
                             [ValidateSet('Alpha', 'Beta')]
                             [string[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var valuesProp = _factory.Create("Test-StringArrayValidateSet").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values");

        Assert.AreEqual("array", valuesProp.GetProperty("type").GetString());
        Assert.AreEqual("string", valuesProp.GetProperty("items").GetProperty("type").GetString());

        var itemEnum = valuesProp.GetProperty("items").GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        Assert.Contains("Alpha", itemEnum);
        Assert.Contains("Beta", itemEnum);
        Assert.IsFalse(valuesProp.TryGetProperty("enum", out _),
            "ValidateSet on arrays must be applied to items.enum, not the array itself.");
    }

    [TestMethod]
    public void Create_FunctionWithValidatePattern_AppearsAsPatternInSchema()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-PostalCode {
                         param(
                             [Parameter(Mandatory = $true)]
                             [ValidatePattern('^\d{5}(-\d{4})?$')]
                             [string]$PostalCode
                         )
                     }
                     """);
        ps.Invoke();

        var tool = _factory.Create("Test-PostalCode");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        var pattern = properties.GetProperty("PostalCode").GetProperty("pattern").GetString();

        Assert.AreEqual(@"^\d{5}(-\d{4})?$", pattern);
    }

    [TestMethod]
    public void Create_FunctionWithValidateRange_EmitsMinimumAndMaximum()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeMinMax {
                         param(
                             [ValidateRange(1,10)]
                             [int]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeMinMax").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.AreEqual(1d, valueProp.GetProperty("minimum").GetDouble());
        Assert.AreEqual(10d, valueProp.GetProperty("maximum").GetDouble());
    }

    [TestMethod]
    public void Create_FunctionWithValidateRangeKindPositive_EmitsExclusiveMinimum()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeKindPositive {
                         param(
                             [ValidateRange([System.Management.Automation.ValidateRangeKind]::Positive)]
                             [int]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeKindPositive").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.AreEqual(0d, valueProp.GetProperty("exclusiveMinimum").GetDouble());
    }

    [TestMethod]
    public void Create_FunctionWithValidateRangeKindNonNegative_EmitsMinimumZero()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeKindNonNegative {
                         param(
                             [ValidateRange([System.Management.Automation.ValidateRangeKind]::NonNegative)]
                             [int]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeKindNonNegative").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.AreEqual(0d, valueProp.GetProperty("minimum").GetDouble());
    }

    [TestMethod]
    public void Create_FunctionWithValidateRangeKindNegative_EmitsExclusiveMaximum()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeKindNegative {
                         param(
                             [ValidateRange([System.Management.Automation.ValidateRangeKind]::Negative)]
                             [int]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeKindNegative").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.AreEqual(0d, valueProp.GetProperty("exclusiveMaximum").GetDouble());
    }

    [TestMethod]
    public void Create_FunctionWithValidateRangeKindNonPositive_EmitsMaximumZero()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeKindNonPositive {
                         param(
                             [ValidateRange([System.Management.Automation.ValidateRangeKind]::NonPositive)]
                             [int]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeKindNonPositive").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.AreEqual(0d, valueProp.GetProperty("maximum").GetDouble());
    }

    [TestMethod]
    public void Create_FunctionWithValidateRangeOnString_DoesNotEmitMinMax()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-RangeString {
                         param(
                             [ValidateRange(1,10)]
                             [string]$Value
                         )
                     }
                     """);
        ps.Invoke();

        var valueProp = _factory.Create("Test-RangeString").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Value");

        Assert.IsFalse(valueProp.TryGetProperty("minimum", out _));
        Assert.IsFalse(valueProp.TryGetProperty("maximum", out _));
    }

    [TestMethod]
    public void Create_FunctionWithValidateLength_EmitsMinLengthAndMaxLength()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-StringLength {
                         param(
                             [ValidateLength(3,50)]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var nameProp = _factory.Create("Test-StringLength").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name");

        Assert.AreEqual(3, nameProp.GetProperty("minLength").GetInt32());
        Assert.AreEqual(50, nameProp.GetProperty("maxLength").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateLengthZeroMin_OmitsMinLength()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-StringLengthZeroMin {
                         param(
                             [ValidateLength(0,100)]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var nameProp = _factory.Create("Test-StringLengthZeroMin").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name");

        Assert.IsFalse(nameProp.TryGetProperty("minLength", out _));
        Assert.AreEqual(100, nameProp.GetProperty("maxLength").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateCount_EmitsMinItemsAndMaxItems()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-ArrayCount {
                         param(
                             [ValidateCount(1,5)]
                             [int[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var valuesProp = _factory.Create("Test-ArrayCount").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values");

        Assert.AreEqual(1, valuesProp.GetProperty("minItems").GetInt32());
        Assert.AreEqual(5, valuesProp.GetProperty("maxItems").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateCountZeroMin_OmitsMinItems()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-ArrayCountZeroMin {
                         param(
                             [ValidateCount(0,10)]
                             [string[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var valuesProp = _factory.Create("Test-ArrayCountZeroMin").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values");

        Assert.IsFalse(valuesProp.TryGetProperty("minItems", out _));
        Assert.AreEqual(10, valuesProp.GetProperty("maxItems").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateNotNullOrEmpty_String_EmitsMinLength1()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-NotNullOrEmptyString {
                         param(
                             [ValidateNotNullOrEmpty()]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var nameProp = _factory.Create("Test-NotNullOrEmptyString").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name");

        Assert.AreEqual(1, nameProp.GetProperty("minLength").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateNotNullOrEmpty_Array_EmitsMinItems1()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-NotNullOrEmptyArray {
                         param(
                             [ValidateNotNullOrEmpty()]
                             [string[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var valuesProp = _factory.Create("Test-NotNullOrEmptyArray").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values");

        Assert.AreEqual(1, valuesProp.GetProperty("minItems").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithValidateNotNullOrEmptyAndAllowEmptyString_SuppressesMinLength()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-AllowEmptyString {
                         param(
                             [ValidateNotNullOrEmpty()]
                             [AllowEmptyString()]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var nameProp = _factory.Create("Test-AllowEmptyString").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name");

        Assert.IsFalse(nameProp.TryGetProperty("minLength", out _));
    }

    [TestMethod]
    public void Create_FunctionWithValidateNotNullOrEmptyAndAllowEmptyCollection_SuppressesMinItems()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-AllowEmptyCollection {
                         param(
                             [ValidateNotNullOrEmpty()]
                             [AllowEmptyCollection()]
                             [int[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var valuesProp = _factory.Create("Test-AllowEmptyCollection").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values");

        Assert.IsFalse(valuesProp.TryGetProperty("minItems", out _));
    }

    [TestMethod]
    public void Create_FunctionWithValidateNotNullOrEmptyAndValidateLength_PreservesHigherMinLength()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-NotNullOrEmptyWithLength {
                         param(
                             [ValidateNotNullOrEmpty()]
                             [ValidateLength(3,50)]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var nameProp = _factory.Create("Test-NotNullOrEmptyWithLength").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name");

        Assert.AreEqual(3, nameProp.GetProperty("minLength").GetInt32());
    }

    [TestMethod]
    public void Create_FunctionWithAllowNull_String_EmitsTypeUnion()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-AllowNullString {
                         param(
                             [AllowNull()]
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var typeArray = _factory.Create("Test-AllowNullString").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name")
            .GetProperty("type");

        Assert.AreEqual(JsonValueKind.Array, typeArray.ValueKind);
        var types = typeArray.EnumerateArray().Select(element => element.GetString()).ToList();
        CollectionAssert.AreEquivalent(s_stringAndNullTypes, types);
    }

    [TestMethod]
    public void Create_FunctionWithAllowNull_Array_EmitsTypeUnion()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-AllowNullArray {
                         param(
                             [AllowNull()]
                             [int[]]$Values
                         )
                     }
                     """);
        ps.Invoke();

        var typeArray = _factory.Create("Test-AllowNullArray").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Values")
            .GetProperty("type");

        Assert.AreEqual(JsonValueKind.Array, typeArray.ValueKind);
        var types = typeArray.EnumerateArray().Select(element => element.GetString()).ToList();
        CollectionAssert.AreEquivalent(s_arrayAndNullTypes, types);
    }

    [TestMethod]
    public void Create_FunctionWithoutAllowNull_TypeIsSingleString()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-WithoutAllowNull {
                         param(
                             [string]$Name
                         )
                     }
                     """);
        ps.Invoke();

        var typeValue = _factory.Create("Test-WithoutAllowNull").ProtocolTool.InputSchema
            .GetProperty("properties")
            .GetProperty("Name")
            .GetProperty("type");

        Assert.AreEqual(JsonValueKind.String, typeValue.ValueKind);
        Assert.AreEqual("string", typeValue.GetString());
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

        Assert.Contains("location", required);
        Assert.Contains("unit", required);
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
        Assert.DoesNotMatchRegex(new System.Text.RegularExpressions.Regex("Examples:"),
description);
        Assert.IsLessThan(1000, description.Length,
            $"Expected description length < 1000 but was {description.Length}");
    }
    // ── HelpMessage attribute fallback ───────────────────────────────────────

    [TestMethod]
    public void Create_FunctionWithHelpMessageAttribute_UsesHelpMessageAsDescription()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-HelpMessageFallback {
                         param(
                             [Parameter(Mandatory = $true, HelpMessage = 'The name to greet')]
                             [string]$Name
                         )
                     }
                     """);
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
        ps.AddScript("""

                     function Test-HelpPriority {
                         <#
                         .PARAMETER Name
                             The full name of the person from comment-based help.
                         #>
                         param(
                             [Parameter(Mandatory = $true, HelpMessage = 'HelpMessage attribute value')]
                             [string]$Name
                         )
                     }
                     """);
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
    public void Create_WithNestedClassArrayOutputType_ReflectsNestedProperties()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     class Selection {
                         [string]$Urvalsgrupp
                         [string]$Poang
                         [string]$Antagna
                     }
                     class SearchResult {
                         [string]$Skola
                         [string]$Program
                         [Selection[]]$Urval
                     }
                     function Get-SearchResult {
                         [OutputType([SearchResult])]
                         param()
                     }
                     """);
        ps.Invoke();

        var outputSchema = _factory.Create("Get-SearchResult").ProtocolTool.OutputSchema!.Value;
        var itemProps = outputSchema
            .GetProperty("properties")
            .GetProperty("items")
            .GetProperty("items")
            .GetProperty("properties");

        // Urval must be an array of objects with nested properties, not an array of strings.
        var urval = itemProps.GetProperty("Urval");
        Assert.AreEqual("array", urval.GetProperty("type").GetString());

        var urvalItemProps = urval.GetProperty("items").GetProperty("properties");
        Assert.AreEqual("string", urvalItemProps.GetProperty("Urvalsgrupp").GetProperty("type").GetString());
        Assert.AreEqual("string", urvalItemProps.GetProperty("Poang").GetProperty("type").GetString());
        Assert.AreEqual("string", urvalItemProps.GetProperty("Antagna").GetProperty("type").GetString());
    }

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
        ps.AddScript("""

                     function Test-StringOutput {
                         [OutputType([string])]
                         param()
                         'hello'
                     }
                     """);
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
        ps.AddScript("""

                     function Test-IntOutput {
                         [OutputType([int])]
                         param()
                         42
                     }
                     """);
        ps.Invoke();

        var tool = _factory.Create("Test-IntOutput");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var items = tool.ProtocolTool.OutputSchema!.Value
            .GetProperty("properties").GetProperty("items");
        Assert.AreEqual("integer", items.GetProperty("items").GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_WithMultipleOutputTypes_OutputSchemaUsesUnionOfAllTypes()
    {
        // Get-Process output metadata only uses __AllParameterSets, so it falls back
        // to a union of all declared output types.
        var tool = _factory.Create("Get-Process");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var itemsSchema = tool.ProtocolTool.OutputSchema!.Value
            .GetProperty("properties")
            .GetProperty("items")
            .GetProperty("items");

        Assert.IsTrue(itemsSchema.TryGetProperty("oneOf", out var oneOf));
        Assert.AreEqual(3, oneOf.GetArrayLength());

        var processSchema = oneOf.EnumerateArray()
            .First(schema => schema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("ProcessName", out _)
                && properties.TryGetProperty("Id", out _));

        Assert.AreEqual("object", processSchema.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_WithDateTimeOutputType_OutputSchemaHasDateTimeInItems()
    {
        // Get-Date can now resolve per-set output metadata, so this assertion accepts
        // either flat or oneOf output schema as long as date-time metadata is present.
        var tool = _factory.Create("Get-Date");

        Assert.IsNotNull(tool.ProtocolTool.OutputSchema);
        var outputSchema = tool.ProtocolTool.OutputSchema!.Value;

        if (outputSchema.TryGetProperty("oneOf", out var variants))
        {
            var hasDateTimeVariant = variants.EnumerateArray().Any(variant =>
                ContainsSchemaWithTypeAndFormat(
                    variant.GetProperty("properties").GetProperty("items").GetProperty("items"),
                    "string",
                    "date-time"));

            Assert.IsTrue(hasDateTimeVariant, "Expected at least one output variant to map to date-time.");
            return;
        }

        var itemsSchema = outputSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.IsTrue(ContainsSchemaWithTypeAndFormat(itemsSchema, "string", "date-time"));
    }

    [TestMethod]
    public void Create_MultiSetFunctionWithPerSetOutputType_OutputSchemaHasPerSetOneOf()
    {
        DefineOutputPerSetFunction();

        var tool = _factory.Create("Test-OutputPerSet");
        var variants = GetOutputVariantsByParameterSetName(tool.ProtocolTool.OutputSchema!.Value);

        Assert.HasCount(2, variants);

        var byNameItems = variants["ByName"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.AreEqual("string", byNameItems.GetProperty("type").GetString());

        var byIdItems = variants["ById"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.AreEqual("integer", byIdItems.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_MultiSetFunctionWithAllParameterSetsOutputType_OutputSchemaUsesUnionItems()
    {
        DefineOutputAllSetsFunction();

        var tool = _factory.Create("Test-OutputAllSets");
        var outputSchema = tool.ProtocolTool.OutputSchema!.Value;

        Assert.IsFalse(outputSchema.TryGetProperty("oneOf", out _));

        var itemsSchema = outputSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.IsTrue(itemsSchema.TryGetProperty("oneOf", out var oneOf));
        Assert.AreEqual(2, oneOf.GetArrayLength());

        var itemTypes = oneOf.EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToList();

        Assert.Contains("string", itemTypes);
        Assert.Contains("integer", itemTypes);
    }

    [TestMethod]
    public void Create_MultiSetFunctionWithPartialPerSetOutputType_OutputSchemaFillsFromAllParameterSets()
    {
        DefineOutputPartialSetsFunction();

        var tool = _factory.Create("Test-OutputPartialSets");
        var variants = GetOutputVariantsByParameterSetName(tool.ProtocolTool.OutputSchema!.Value);

        var byNameItems = variants["ByName"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.AreEqual("string", byNameItems.GetProperty("type").GetString());

        var byIdItems = variants["ById"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.AreEqual("object", byIdItems.GetProperty("type").GetString());
    }

    [TestMethod]
    public void Create_MultiSetFunctionWithNoOutputType_OutputSchemaIsNull()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet");

        Assert.IsNull(tool.ProtocolTool.OutputSchema);
    }

    [TestMethod]
    public void Create_SingleSetFunctionWithOutputType_OutputSchemaIsFlatWithoutOneOf()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""
                     function Test-SingleSetOutput {
                         [OutputType([string])]
                         param([string]$Name)
                         $Name
                     }
                     """);
        ps.Invoke();

        var tool = _factory.Create("Test-SingleSetOutput");
        var outputSchema = tool.ProtocolTool.OutputSchema!.Value;

        Assert.IsFalse(outputSchema.TryGetProperty("oneOf", out _));
        Assert.AreEqual("string", outputSchema
            .GetProperty("properties")
            .GetProperty("items")
            .GetProperty("items")
            .GetProperty("type")
            .GetString());
    }

    [TestMethod]
    public void Create_SingleSetFunctionWithMultiTypePerSetOutputType_OutputSchemaUsesItemsOneOf()
    {
        DefineOutputMultiTypePerSetFunction();

        var tool = _factory.Create("Test-OutputMultiTypePerSet");
        var outputSchema = tool.ProtocolTool.OutputSchema!.Value;

        Assert.IsFalse(outputSchema.TryGetProperty("oneOf", out _));

        var itemsSchema = outputSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.IsTrue(itemsSchema.TryGetProperty("oneOf", out var oneOf));
        Assert.AreEqual(2, oneOf.GetArrayLength());

        var types = oneOf.EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToList();
        Assert.Contains("string", types);
        Assert.Contains("integer", types);
    }

    [TestMethod]
    public void Create_WithParameterSetFilter_OutputSchemaNarrowsToSelectedSetTypes()
    {
        DefineOutputPerSetFunction();

        var tool = _factory.Create("Test-OutputPerSet", new McpToolConfig { ParameterSets = ["ByName"] });
        var outputSchema = tool.ProtocolTool.OutputSchema!.Value;

        Assert.IsFalse(outputSchema.TryGetProperty("oneOf", out _));

        var byNameType = outputSchema
            .GetProperty("properties")
            .GetProperty("items")
            .GetProperty("items")
            .GetProperty("type")
            .GetString();

        Assert.AreEqual("string", byNameType);
    }

    [TestMethod]
    public void Create_WithGetUptime_OutputSchemaUsesPerSetCmdletOutputType()
    {
        if (!CommandExists("Get-Uptime"))
        {
            Assert.Inconclusive("Get-Uptime is not available in this PowerShell environment.");
        }

        var tool = _factory.Create("Get-Uptime");
        var outputSchema = tool.ProtocolTool.OutputSchema;

        Assert.IsNotNull(outputSchema);
        Assert.IsTrue(outputSchema.Value.TryGetProperty("oneOf", out _),
            "Expected Get-Uptime output schema to use per-set oneOf variants.");

        var variants = GetOutputVariantsByParameterSetName(outputSchema.Value);
        Assert.IsTrue(variants.ContainsKey("Timespan"));
        Assert.IsTrue(variants.ContainsKey("Since"));

        var timespanItems = variants["Timespan"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.IsTrue(
            ContainsSchemaWithTypeAndFormat(timespanItems, "object")
            || ContainsSchemaWithTypeAndFormat(timespanItems, "string"));

        var sinceItems = variants["Since"].GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.IsTrue(ContainsSchemaWithTypeAndFormat(sinceItems, "string", "date-time"));
    }

    // ── Multiple parameter sets ──────────────────────────────────────────────

    // Defines Test-MultiSet with two parameter sets:
    //   ByName  – mandatory -Name  (string), optional -Verbose
    //   ById    – mandatory -Id    (int),    optional -Verbose
    private void DefineMultiSetFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

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
                     }
                     """);
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
    public void Create_MultipleParameterSets_SchemaHasOneOfWithTwoVariants()
    {
        DefineMultiSetFunction();

        var inputSchema = _factory.Create("Test-MultiSet").ProtocolTool.InputSchema;

        Assert.IsTrue(inputSchema.TryGetProperty("oneOf", out var oneOf));
        Assert.AreEqual(2, oneOf.GetArrayLength());
    }

    [TestMethod]
    public void Create_MultipleParameterSets_VariantRequiredIncludesDiscriminatorAndMandatoryParams()
    {
        DefineMultiSetFunction();

        var variants = GetVariantsByDescription(_factory.Create("Test-MultiSet").ProtocolTool.InputSchema);

        var byNameRequired = variants["ByName (default)"].GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();
        var byIdRequired = variants["ById"].GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();
        
        CollectionAssert.AreEquivalent(s_byNameRequired, byNameRequired);
        CollectionAssert.AreEquivalent(s_byIdRequired, byIdRequired);
    }

    [TestMethod]
    public void Create_MultipleParameterSets_DefaultSetVariantDescriptionHasDefaultSuffix()
    {
        DefineMultiSetFunction();

        var variants = GetVariantsByDescription(_factory.Create("Test-MultiSet").ProtocolTool.InputSchema);

        Assert.AreEqual("ByName (default)", variants["ByName (default)"].GetProperty("description").GetString());
        Assert.AreEqual("ById", variants["ById"].GetProperty("description").GetString());
    }

    [TestMethod]
    public void Create_MultipleParameterSets_SharedParameterAppearsInAllVariants()
    {
        DefineMultiSetWithSharedFunction();

        var variants = GetVariantsByDescription(_factory.Create("Test-MultiSetWithShared").ProtocolTool.InputSchema);

        Assert.IsTrue(variants["ByName (default)"].GetProperty("properties").TryGetProperty("Force", out _));
        Assert.IsTrue(variants["ById"].GetProperty("properties").TryGetProperty("Force", out _));
    }

    [TestMethod]
    public void Create_MultipleParameterSets_NoTopLevelPropertiesOrRequired()
    {
        DefineMultiSetFunction();

        var inputSchema = _factory.Create("Test-MultiSet").ProtocolTool.InputSchema;

        Assert.AreEqual("object", inputSchema.GetProperty("type").GetString());
        Assert.IsTrue(inputSchema.TryGetProperty("oneOf", out _));
        Assert.IsFalse(inputSchema.TryGetProperty("properties", out _));
        Assert.IsFalse(inputSchema.TryGetProperty("required", out _));
    }

    [TestMethod]
    public void Create_SingleParameterSet_SchemaHasNoOneOf()
    {
        DefineWeatherFunction();

        var inputSchema = _factory.Create("Get-CurrentWeather").ProtocolTool.InputSchema;

        Assert.IsFalse(inputSchema.TryGetProperty("oneOf", out _));
        Assert.IsTrue(inputSchema.TryGetProperty("properties", out _));
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
        Assert.Contains("Name", required);
        Assert.DoesNotContain("Id", required);
    }

    [TestMethod]
    public void Create_WithMultipleParameterSetFilters_IncludesParametersFromAllNamedSets()
    {
        DefineMultiSetFunction();

        var tool = _factory.Create("Test-MultiSet", new McpToolConfig { ParameterSets = ["ByName", "ById"] });
        var variants = GetVariantsByDescription(tool.ProtocolTool.InputSchema);

        Assert.IsTrue(variants["ByName (default)"].GetProperty("properties").TryGetProperty("Name", out _));
        Assert.IsTrue(variants["ById"].GetProperty("properties").TryGetProperty("Id", out _));
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

        var variants = GetVariantsByDescription(tool.ProtocolTool.InputSchema);
        var byNameProperties = variants["Name (default)"].GetProperty("properties");
        var byIdProperties = variants["Id"].GetProperty("properties");  

        Assert.IsTrue(byNameProperties.TryGetProperty("Name", out _),
            "Expected 'Name' in Name parameter-set schema.");
        Assert.IsTrue(byIdProperties.TryGetProperty("Id", out _),
            "Expected 'Id' in Id parameter-set schema.");

        Assert.IsFalse(byNameProperties.TryGetProperty("InputObject", out _),
            "Expected 'InputObject' to be absent when filtered to Name and Id sets.");
        Assert.IsFalse(byIdProperties.TryGetProperty("InputObject", out _),
            "Expected 'InputObject' to be absent when filtered to Name and Id sets.");

        Assert.IsFalse(byNameProperties.TryGetProperty("IncludeUserName", out _),
            "Expected 'IncludeUserName' to be absent when filtered to Name and Id sets.");
        Assert.IsFalse(byIdProperties.TryGetProperty("IncludeUserName", out _),
            "Expected 'IncludeUserName' to be absent when filtered to Name and Id sets.");
    }

    [TestMethod]
    public void Create_GetProcessWithNameAndIdSets_NeitherNameNorIdIsRequired()
    {
        // Name and Id must never be required in the opposite parameter-set variant.
        var config = new McpToolConfig
        {
            ParameterSets = ["Name", "Id"]
        };

        var tool = _factory.Create("Get-Process", config);
        var variants = GetVariantsByDescription(tool.ProtocolTool.InputSchema);
        var nameRequired = variants["Name (default)"].GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();
        var idRequired = variants["Id"].GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        Assert.Contains("Id", idRequired);
        Assert.DoesNotContain("Id", nameRequired);
        Assert.DoesNotContain("Name", idRequired);
    }

    private void DefineMultiSetWithSharedFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-MultiSetWithShared {
                         [CmdletBinding(DefaultParameterSetName = 'ByName')]
                         param(
                             [Parameter(ParameterSetName = 'ByName', Mandatory = $true)]
                             [string]$Name,

                             [Parameter(ParameterSetName = 'ById', Mandatory = $true)]
                             [int]$Id,

                             [switch]$Force
                         )
                     }
                     """);
        ps.Invoke();
    }

    private void DefineOutputPerSetFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-OutputPerSet {
                         [CmdletBinding(DefaultParameterSetName='ByName')]
                         [OutputType([string], ParameterSetName='ByName')]
                         [OutputType([int], ParameterSetName='ById')]
                         param(
                             [Parameter(Mandatory, ParameterSetName='ByName')]
                             [string]$Name,

                             [Parameter(Mandatory, ParameterSetName='ById')]
                             [int]$Id
                         )

                         if ($PSCmdlet.ParameterSetName -eq 'ByName') { $Name; return }
                         $Id
                     }
                     """);
        ps.Invoke();
    }

    private void DefineOutputAllSetsFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-OutputAllSets {
                         [CmdletBinding(DefaultParameterSetName='ByName')]
                         [OutputType([string])]
                         [OutputType([int])]
                         param(
                             [Parameter(Mandatory, ParameterSetName='ByName')]
                             [string]$Name,

                             [Parameter(Mandatory, ParameterSetName='ById')]
                             [int]$Id
                         )

                         if ($PSCmdlet.ParameterSetName -eq 'ByName') { $Name; return }
                         $Id
                     }
                     """);
        ps.Invoke();
    }

    private void DefineOutputPartialSetsFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-OutputPartialSets {
                         [CmdletBinding(DefaultParameterSetName='ByName')]
                         [OutputType([string], ParameterSetName='ByName')]
                         [OutputType([hashtable])]
                         param(
                             [Parameter(Mandatory, ParameterSetName='ByName')]
                             [string]$Name,

                             [Parameter(Mandatory, ParameterSetName='ById')]
                             [int]$Id
                         )

                         if ($PSCmdlet.ParameterSetName -eq 'ByName') { $Name; return }
                         @{ Id = $Id }
                     }
                     """);
        ps.Invoke();
    }

    private void DefineOutputMultiTypePerSetFunction()
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddScript("""

                     function Test-OutputMultiTypePerSet {
                         [CmdletBinding(DefaultParameterSetName='Multi')]
                         [OutputType([string], [int], ParameterSetName='Multi')]
                         param(
                             [Parameter(Mandatory, ParameterSetName='Multi')]
                             [string]$Input
                         )

                         $Input
                     }
                     """);
        ps.Invoke();
    }

    private static List<JsonElement> EnumerateSchemaProperties(JsonElement inputSchema)
    {
        if (inputSchema.TryGetProperty("oneOf", out var oneOf))
        {
            return oneOf.EnumerateArray()
                .Select(variant => variant.GetProperty("properties"))
                .ToList();
        }

        return [inputSchema.GetProperty("properties")];
    }

    private static bool AnySchemaPropertiesContain(JsonElement inputSchema, string propertyName)
    {
        return EnumerateSchemaProperties(inputSchema)
            .Any(properties => properties.TryGetProperty(propertyName, out _));
    }

    private static JsonElement FindPropertySchema(JsonElement inputSchema, string propertyName)
    {
        foreach (var properties in EnumerateSchemaProperties(inputSchema))
        {
            if (properties.TryGetProperty(propertyName, out var propertySchema))
            {
                return propertySchema;
            }
        }

        Assert.Fail($"Expected '{propertyName}' property schema to be present.");
        return default;
    }

    private static Dictionary<string, JsonElement> GetVariantsByDescription(JsonElement inputSchema)
    {
        Assert.IsTrue(inputSchema.TryGetProperty("oneOf", out var oneOf),
            "Expected 'oneOf' schema for multiple parameter sets.");

        return oneOf.EnumerateArray().ToDictionary(
            variant => variant
                .GetProperty("description")
                .GetString()!,
            variant => variant,
            StringComparer.OrdinalIgnoreCase);
    }

    private bool CommandExists(string commandName)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = _runspace;
        ps.AddCommand("Get-Command").AddParameter("Name", commandName).AddParameter("ErrorAction", ActionPreference.SilentlyContinue);
        var results = ps.Invoke();
        return results.Count > 0;
    }

    private static Dictionary<string, JsonElement> GetOutputVariantsByParameterSetName(JsonElement outputSchema)
    {
        Assert.IsTrue(outputSchema.TryGetProperty("oneOf", out var oneOf),
            "Expected output schema oneOf for multiple parameter-set output mappings.");

        return oneOf.EnumerateArray().ToDictionary(
            variant => variant
                .GetProperty("properties")
                .GetProperty("ParameterSetName")
                .GetProperty("const")
                .GetString()!,
            variant => variant,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsSchemaWithTypeAndFormat(JsonElement schema, string type, string? format = null)
    {
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            return oneOf.EnumerateArray().Any(item => ContainsSchemaWithTypeAndFormat(item, type, format));
        }

        if (!schema.TryGetProperty("type", out var schemaType))
        {
            return false;
        }

        if (!string.Equals(schemaType.GetString(), type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (format is null)
        {
            return true;
        }

        return schema.TryGetProperty("format", out var schemaFormat)
               && string.Equals(schemaFormat.GetString(), format, StringComparison.OrdinalIgnoreCase);
    }
}

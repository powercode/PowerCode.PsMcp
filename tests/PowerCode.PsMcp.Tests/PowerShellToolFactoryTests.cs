using System.Management.Automation;
using System.Management.Automation.Runspaces;

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
        _factory = new PowerShellToolFactory(_runspace, jsonDepth: 2);
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
        var tool = _factory.Create("Get-Process", "list-procs");

        Assert.AreEqual("list-procs", tool.ProtocolTool.Name);
    }

    [TestMethod]
    public void Create_WithNullToolName_UsesCommandNameAsDefault()
    {
        var tool = _factory.Create("Get-Process", null);

        Assert.AreEqual("Get-Process", tool.ProtocolTool.Name);
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
}

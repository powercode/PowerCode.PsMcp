using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Integration-style tests for <see cref="PowerShellToolFactory"/>.
/// Tests run against real PowerShell commands (Get-Process, Get-Date) that are
/// always available in the .NET SDK build environment.
/// </summary>
[TestClass]
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
}

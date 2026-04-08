using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PowerCode.PsMcp.Tests;

[TestClass]
[TestCategory("System")]
public sealed class McpServerSystemTests
{
    private const string GetCurrentWeatherDefinition = """
        function Get-CurrentWeather {
            <#
            .SYNOPSIS
            Gets the current weather for a location.

            .DESCRIPTION
            Calls a weather service to retrieve current conditions.

            .PARAMETER Location
            The city or region to get weather for.

            .PARAMETER Units
            Temperature units: Celsius or Fahrenheit.

            .EXAMPLE
            Get-CurrentWeather -Location "Seattle" -Units Celsius

            Gets Seattle weather in Celsius.
            #>
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)]
                [string]$Location,

                [ValidateSet('Celsius','Fahrenheit')]
                [string]$Units = 'Celsius'
            )
            @{ Location = $Location; Temperature = 72; Units = $Units }
        }
        """;

    [TestMethod]
    public async Task ListToolsAsync_AdvancedFunction_ReturnsToolWithMetadata()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var runner = new McpServerRunner(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        var serverTask = runner.RunAsync(
            modules: null,
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            callerScriptRoot: null,
            include: ["Get-CurrentWeather"],
            exclude: null,
            commandConfig: null,
            jsonDepth: 2,
            cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(options: null, cts.Token);

        Assert.HasCount(1, tools);
        var tool = tools[0];
        Assert.AreEqual("Get-CurrentWeather", tool.Name);
        Assert.IsTrue(tool.Description.Contains("Gets the current weather", StringComparison.OrdinalIgnoreCase));

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task CallToolAsync_AdvancedFunction_ReturnsStructuredOutput()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var runner = new McpServerRunner(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        var serverTask = runner.RunAsync(
            modules: null,
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            callerScriptRoot: null,
            include: ["Get-CurrentWeather"],
            exclude: null,
            commandConfig: null,
            jsonDepth: 2,
            cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var result = await client.CallToolAsync(
            "Get-CurrentWeather",
            new Dictionary<string, object?> { ["Location"] = "Seattle", ["Units"] = "Celsius" },
            cancellationToken: cts.Token);

        var content = result.Content.Count > 0 ? ((TextContentBlock)result.Content[0]).Text : "(no content)";
        Assert.AreNotEqual(true, result.IsError, $"Tool returned IsError=true. Content: {content}");

        // Actual output objects land in StructuredContent; text content is ToString() fallback.
        Assert.IsNotNull(result.StructuredContent, "Expected structured content from tool output.");
        var items = result.StructuredContent.Value.GetProperty("items");
        Assert.AreEqual(JsonValueKind.Array, items.ValueKind);
        Assert.IsGreaterThan(0, items.GetArrayLength(), "Expected at least one item in structured output.");

        var item = items[0];
        Assert.AreEqual("Seattle",
            item.GetProperty("Location").GetString(),
            $"Expected 'Seattle' in structured Location property. Full items: {items}");

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task ListToolsAsync_DiscoveryMode_ExposesOnlyDiscoveredCommands()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var runner = new McpServerRunner(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        // Use discovery mode with a scriptblock; no explicit tool names.
        var serverTask = runner.RunAsync(
            modules: null,
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            callerScriptRoot: null,
            include: null,
            exclude: null,
            commandConfig: null,
            jsonDepth: 2,
            cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(options: null, cts.Token);
        var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The custom function should be discovered.
        Assert.Contains("Get-CurrentWeather", toolNames,
            "'Get-CurrentWeather' should be discovered via scriptblock.");

        // Baseline commands like Get-Process should NOT appear.
        Assert.DoesNotContain("Get-Process", toolNames,
            "'Get-Process' is a baseline command and must not appear in discovery mode.");

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task ListToolsAsync_DiscoveryModeWithInclude_FiltersToIncludedCommands()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var runner = new McpServerRunner(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        // Include "Get-Process" explicitly — must also import its module so it enters the include pool.
        var serverTask = runner.RunAsync(
            modules: ["Microsoft.PowerShell.Management"],
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            callerScriptRoot: null,
            include: ["Get-Process"],
            exclude: null,
            commandConfig: null,
            jsonDepth: 2,
            cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(options: null, cts.Token);
        var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Get-Process", toolNames,
            "'Get-Process' should appear because it was explicitly included.");
        Assert.DoesNotContain("Get-CurrentWeather", toolNames,
            "'Get-CurrentWeather' should be filtered out because it does not match -Include Get-Process.");

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task ListToolsAsync_DiscoveryModeWithCommandConfiguration_ExposesToolWithFriendlyName()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var runner = new McpServerRunner(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        var commandConfig = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Get-CurrentWeather"] = new McpToolConfig { Name = "get-weather" }
        };

        var serverTask = runner.RunAsync(
            modules: null,
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            callerScriptRoot: null,
            include: null,
            exclude: null,
            commandConfig: commandConfig,
            jsonDepth: 2,
            cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(options: null, cts.Token);
        var toolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The tool must appear under the friendly name from CommandConfiguration.
        Assert.Contains("get-weather", toolNames,
            "Tool should appear as 'get-weather' after CommandConfiguration mapping.");

        // The original command name must not be exposed directly.
        Assert.DoesNotContain("Get-CurrentWeather", toolNames,
            "'Get-CurrentWeather' must not appear directly when mapped to a friendly name.");

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}

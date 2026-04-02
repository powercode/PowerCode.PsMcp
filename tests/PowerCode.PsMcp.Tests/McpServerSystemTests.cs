using System.IO.Pipelines;
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
            ["Get-CurrentWeather"],
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            jsonDepth: 2,
            loggerFactory: null,
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
            ["Get-CurrentWeather"],
            scriptPath: null,
            scriptBlockText: GetCurrentWeatherDefinition,
            jsonDepth: 2,
            loggerFactory: null,
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
        Assert.IsTrue(content.Contains("Seattle", StringComparison.Ordinal), $"Expected 'Seattle' in content: {content}");

        await cts.CancelAsync();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}

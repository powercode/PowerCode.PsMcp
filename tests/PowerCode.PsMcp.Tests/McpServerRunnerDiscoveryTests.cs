using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace PowerCode.PsMcp.Tests;

/// <summary>
/// Unit tests for the static command-discovery helpers on <see cref="McpServerRunner"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class McpServerRunnerDiscoveryTests
{
    // ── GetDefaultCommandNames ───────────────────────────────────────────────

    [TestMethod]
    public void GetDefaultCommandNames_ReturnsNonEmptySet()
    {
        var baseline = McpServerRunner.GetDefaultCommandNames();

        Assert.IsNotEmpty(baseline, "Expected baseline to contain at least one command.");
    }

    [TestMethod]
    public void GetDefaultCommandNames_ContainsGetCommand()
    {
        var baseline = McpServerRunner.GetDefaultCommandNames();

        Assert.Contains("Get-Command", baseline,
            "Expected baseline to contain 'Get-Command'.");
    }

    // ── DiscoverCommands ─────────────────────────────────────────────────────

    [TestMethod]
    public void DiscoverCommands_NoFilters_ReturnsDeltaOnly()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C" }; // delta only

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, null, null);

        Assert.HasCount(1, result);
        Assert.Contains("C", result);
    }

    [TestMethod]
    public void DiscoverCommands_WithInclude_FiltersToMatchingCommandsFromExplicitPool()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };
        // includePool = delta (C) + explicitly imported module commands (A)
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" };
        var include = new[] { new WildcardPattern("A*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, include, null);

        // Include acts as a filter: only A matches A*.
        Assert.HasCount(1, result);
        Assert.Contains("A", result);
        Assert.DoesNotContain("C", result);
    }

    [TestMethod]
    public void DiscoverCommands_WithInclude_DoesNotAddBaselineCommandNotInPool()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };
        // includePool = delta only; A is not in pool because its module was not imported
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C" };
        var include = new[] { new WildcardPattern("A*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, include, null);

        // A is not in includePool, so the pattern cannot select it.
        Assert.DoesNotContain("A", result, "A must not be added when it is not in the include pool.");
        Assert.DoesNotContain("C", result, "C does not match the include pattern and should be filtered out.");
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void DiscoverCommands_WithExclude_RemovesMatchingCommands()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B", "C" }; // delta
        var exclude = new[] { new WildcardPattern("C*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, null, exclude);

        Assert.HasCount(1, result);
        Assert.Contains("B", result);
        Assert.DoesNotContain("C", result);
    }

    [TestMethod]
    public void DiscoverCommands_WithIncludeAndExclude_AppliesBoth()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C", "D" };
        // includePool = delta (C, D) + explicit module commands (A)
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C", "D" };
        var include = new[] { new WildcardPattern("A*", WildcardOptions.IgnoreCase) };
        var exclude = new[] { new WildcardPattern("D*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, include, exclude);

        // Include filter selects A only; Exclude then has nothing else to remove.
        Assert.Contains("A", result, "A should be included via Include pattern (it is in the pool).");
        Assert.DoesNotContain("C", result, "C does not match include and should be filtered out.");
        Assert.DoesNotContain("D", result, "D should be removed by Exclude pattern.");
        Assert.DoesNotContain("B", result, "B was in baseline and not in pool.");
        Assert.HasCount(1, result);
    }

    [TestMethod]
    public void DiscoverCommands_AllExcluded_ReturnsEmpty()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B" }; // delta
        var exclude = new[] { new WildcardPattern("*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, null, exclude);

        Assert.IsEmpty(result);
    }

    // ── Command configuration lookup ────────────────────────────────────────

    [TestMethod]
    public void CommandConfiguration_EmptyDictionary_HasNoConfiguredEntry()
    {
        var commandConfiguration = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase);
        var found = commandConfiguration.TryGetValue("Get-Process", out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void CommandConfiguration_MatchingCommandName_ReturnsConfiguredFriendlyName()
    {
        var commandConfiguration = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Get-Process"] = new McpToolConfig { Name = "list-procs" }
        };

        var found = commandConfiguration.TryGetValue("Get-Process", out var config);

        Assert.IsTrue(found);
        Assert.AreEqual("list-procs", config!.Name);
    }

    [TestMethod]
    public void CommandConfiguration_UnconfiguredCommand_HasNoConfiguredEntry()
    {
        var commandConfiguration = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Get-Process"] = new McpToolConfig { Name = "list-procs" }
        };

        var found = commandConfiguration.TryGetValue("Get-Date", out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public void CommandConfiguration_CaseInsensitiveCommandKey_ReturnsConfiguredFriendlyName()
    {
        var commandConfiguration = new Dictionary<string, McpToolConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["Get-Process"] = new McpToolConfig { Name = "list-procs" }
        };

        var found = commandConfiguration.TryGetValue("get-process", out var config);

        Assert.IsTrue(found);
        Assert.AreEqual("list-procs", config!.Name);
    }

    // ── ImportModules ────────────────────────────────────────────────────────

    [TestMethod]
    public void ImportModules_WithMicrosoftPowerShellUtility_ImportsSuccessfully()
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();

        // Microsoft.PowerShell.Utility is a built-in module always available.
        McpServerRunner.ImportModules(runspace, ["Microsoft.PowerShell.Utility"]);

        // Verify no exception was thrown and module commands are accessible.
        var allNames = McpServerRunner.GetAllCommandNames(runspace);
        Assert.Contains("Format-Table", allNames,
            "Expected 'Format-Table' to be available after importing Microsoft.PowerShell.Utility.");
    }
}

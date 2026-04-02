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
    public void DiscoverCommands_WithInclude_AddsCommandsFromExplicitPool()
    {
        var baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B" };
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" };
        // includePool = delta (C) + explicitly imported module commands (A)
        var includePool = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" };
        var include = new[] { new WildcardPattern("A*", WildcardOptions.IgnoreCase) };

        var result = McpServerRunner.DiscoverCommands(baseline, all, includePool, include, null);

        // C is in delta; A is pulled in from includePool by include pattern.
        Assert.HasCount(2, result);
        Assert.Contains("A", result);
        Assert.Contains("C", result);
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

        // A is not in includePool, so the pattern cannot add it.
        Assert.DoesNotContain("A", result, "A must not be added when it is not in the include pool.");
        Assert.Contains("C", result);
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

        // Delta: C, D. Include adds A from pool. Exclude removes D.
        Assert.Contains("A", result, "A should be included via Include pattern (it is in the pool).");
        Assert.Contains("C", result, "C should be in delta.");
        Assert.DoesNotContain("D", result, "D should be removed by Exclude pattern.");
        Assert.DoesNotContain("B", result, "B was in baseline and not in pool.");
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

    // ── ResolveToolName ──────────────────────────────────────────────────────

    [TestMethod]
    public void ResolveToolName_NullNameMap_ReturnsCommandName()
    {
        var result = McpServerRunner.ResolveToolName("Get-Process", null);

        Assert.AreEqual("Get-Process", result);
    }

    [TestMethod]
    public void ResolveToolName_MatchInMap_ReturnsFriendlyName()
    {
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-procs"] = "Get-Process"
        };

        var result = McpServerRunner.ResolveToolName("Get-Process", nameMap);

        Assert.AreEqual("list-procs", result);
    }

    [TestMethod]
    public void ResolveToolName_NoMatchInMap_ReturnsCommandName()
    {
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-procs"] = "Get-Process"
        };

        var result = McpServerRunner.ResolveToolName("Get-Date", nameMap);

        Assert.AreEqual("Get-Date", result);
    }

    [TestMethod]
    public void ResolveToolName_CaseInsensitiveMatch_ReturnsFriendlyName()
    {
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-procs"] = "Get-Process"
        };

        // Command name with different casing should still match.
        var result = McpServerRunner.ResolveToolName("get-process", nameMap);

        Assert.AreEqual("list-procs", result);
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

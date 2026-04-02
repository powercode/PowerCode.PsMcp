using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// Owns the full MCP server lifecycle: creates a <see cref="StreamServerTransport"/>
/// from raw stdin/stdout (bypassing PowerShell's pipeline), configures
/// <see cref="McpServerOptions"/>, and runs the server until the provided
/// <see cref="CancellationToken"/> fires.
/// <para>
/// Using raw stdin/stdout (via <c>Console.OpenStandardInput</c> /
/// <c>Console.OpenStandardOutput</c>) is intentional: the cmdlet runs
/// inside an existing PowerShell session whose pipeline is occupied, so we
/// bypass it and communicate directly over the OS-level handles for JSON-RPC.
/// </para>
/// </summary>
internal sealed class McpServerRunner
{
    private readonly Stream _input;
    private readonly Stream _output;

    /// <summary>
    /// Initialises the runner with explicit input/output streams.
    /// </summary>
    /// <param name="input">Stream to read JSON-RPC messages from.</param>
    /// <param name="output">Stream to write JSON-RPC messages to.</param>
    public McpServerRunner(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Initialises the runner using the process's raw stdin/stdout handles,
    /// bypassing PowerShell's occupied pipeline.
    /// </summary>
    public McpServerRunner() : this(Console.OpenStandardInput(), Console.OpenStandardOutput()) { }
    /// <summary>
    /// Starts the MCP server and blocks until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="toolCollection">Pre-built collection of tools to expose.</param>
    /// <param name="loggerFactory">Optional logger factory; logs go to stderr via console.</param>
    /// <param name="cancellationToken">Token that triggers graceful shutdown.</param>
    public async Task RunAsync(
        McpServerPrimitiveCollection<McpServerTool> toolCollection,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCollection);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transport = new StreamServerTransport(_input, _output);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "PowerCode PowerShell MCP",
                Version = "1.0.0"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability()
            },
            ToolCollection = toolCollection
        };

        await using var server = McpServer.Create(transport, options, loggerFactory);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the MCP server with the full lifecycle: creates a <see cref="McpPsHost"/>,
    /// opens a runspace, optionally loads a script, builds tools via
    /// <see cref="PowerShellToolFactory"/>, and runs until <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(
        string[] toolNames,
        string? scriptPath,
        string? scriptBlockText,
        int jsonDepth,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        var host = new McpPsHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();

        if (scriptPath is not null)
            DotSourceScript(runspace, scriptPath);
        else if (scriptBlockText is not null)
            InvokeScriptBlock(runspace, scriptBlockText);

        using var factory = new PowerShellToolFactory(runspace, jsonDepth);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var name in toolNames)
            toolCollection.Add(factory.Create(name));

        await RunAsync(toolCollection, loggerFactory, cancellationToken);
    }

    /// <summary>
    /// Starts the MCP server using command discovery: snapshots baseline commands, configures
    /// a runspace, discovers new commands, applies Include/Exclude filters and NameMap, then
    /// runs until <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///   Thrown when no commands are discovered after filtering.
    /// </exception>
    public async Task RunAsync(
        string[]? modules,
        string? scriptPath,
        string? scriptBlockText,
        string[]? include,
        string[]? exclude,
        IDictionary<string, string>? nameMap,
        int jsonDepth,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        var baseline = GetDefaultCommandNames();
        using var runspace = CreateConfiguredRunspace(modules, scriptPath, scriptBlockText);
        var toolCollection = BuildDiscoveredToolCollection(baseline, runspace, modules, include, exclude, nameMap, jsonDepth);
        await RunAsync(toolCollection, loggerFactory, cancellationToken);
    }

    /// <summary>
    /// Creates a new runspace, imports modules and dot-sources/invokes any script,
    /// and returns the ready-to-use runspace.
    /// </summary>
    private static Runspace CreateConfiguredRunspace(string[]? modules, string? scriptPath, string? scriptBlockText)
    {
        var host = new McpPsHost();
        var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();

        if (modules is { Length: > 0 })
            ImportModules(runspace, modules);

        if (scriptPath is not null)
            DotSourceScript(runspace, scriptPath);
        else if (scriptBlockText is not null)
            InvokeScriptBlock(runspace, scriptBlockText);

        return runspace;
    }

    /// <summary>
    /// Collects all commands from <paramref name="runspace"/>, diffs against <paramref name="baseline"/>,
    /// applies wildcard Include/Exclude filters, maps names via <paramref name="nameMap"/>,
    /// and builds the <see cref="McpServerPrimitiveCollection{T}"/> ready for the transport layer.
    /// </summary>
    /// <remarks>
    /// Include patterns match only against the delta plus commands from explicitly imported
    /// <paramref name="modules"/>. This means a baseline command (e.g. <c>Get-Date</c>) can only
    /// be pulled in via <c>-Include</c> when its module (<c>Microsoft.PowerShell.Utility</c>) is
    /// also listed in <paramref name="modules"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no commands survive filtering.</exception>
    private static McpServerPrimitiveCollection<McpServerTool> BuildDiscoveredToolCollection(
        HashSet<string> baseline,
        Runspace runspace,
        string[]? modules,
        string[]? include,
        string[]? exclude,
        IDictionary<string, string>? nameMap,
        int jsonDepth)
    {
        var allCommands = GetAllCommandNames(runspace);

        // Build the include pool: delta + commands from explicitly imported modules.
        // Include patterns are restricted to this pool so that a baseline command can only
        // be included when its owning module is also declared via -Module.
        var delta = new HashSet<string>(allCommands, StringComparer.OrdinalIgnoreCase);
        delta.ExceptWith(baseline);

        var includePool = new HashSet<string>(delta, StringComparer.OrdinalIgnoreCase);
        if (modules is { Length: > 0 })
            includePool.UnionWith(GetCommandsFromModules(runspace, modules));

        var includePatterns = include?.Select(p => new WildcardPattern(p, WildcardOptions.IgnoreCase)).ToArray();
        var excludePatterns = exclude?.Select(p => new WildcardPattern(p, WildcardOptions.IgnoreCase)).ToArray();

        var discovered = DiscoverCommands(baseline, allCommands, includePool, includePatterns, excludePatterns);

        if (discovered.Count == 0)
            throw new InvalidOperationException(
                "No commands were discovered. Ensure at least one module, script, or ScriptBlock introduces new commands, " +
                "or that Include patterns match commands from an explicitly imported -Module.");

        using var factory = new PowerShellToolFactory(runspace, jsonDepth);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var cmdName in discovered)
        {
            var toolName = ResolveToolName(cmdName, nameMap);
            toolCollection.Add(factory.Create(cmdName, toolName));
        }

        return toolCollection;
    }

    internal static void DotSourceScript(Runspace runspace, string scriptPath)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        // Escape single quotes in the path to prevent script injection.
        ps.AddScript($". '{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'");
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to dot-source '{scriptPath}': {errors}");
        }
    }

    internal static void InvokeScriptBlock(Runspace runspace, string scriptBlockText)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(scriptBlockText);
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to invoke script block: {errors}");
        }
    }

    /// <summary>
    /// Snapshots the command names available in a fresh default runspace (no module imports).
    /// Used as the baseline for discovery diffing.
    /// </summary>
    internal static HashSet<string> GetDefaultCommandNames()
    {
        using var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        return GetAllCommandNames(rs);
    }

    /// <summary>
    /// Returns all command names currently available in <paramref name="runspace"/>.
    /// </summary>
    internal static HashSet<string> GetAllCommandNames(Runspace runspace)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-Command");
        var cmds = ps.Invoke<CommandInfo>();
        return new HashSet<string>(cmds.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Imports one or more modules into <paramref name="runspace"/> using <c>Import-Module -Name</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when any import fails.</exception>
    internal static void ImportModules(Runspace runspace, string[] modules)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Import-Module").AddParameter("Name", modules);
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to import module(s) '{string.Join(", ", modules)}': {errors}");
        }
    }

    /// <summary>
    /// Returns all command names whose <c>ModuleName</c> is in <paramref name="moduleNames"/>.
    /// Used to build the include pool so that baseline commands can only be pulled in when
    /// their owning module is explicitly declared via <c>-Module</c>.
    /// </summary>
    internal static HashSet<string> GetCommandsFromModules(Runspace runspace, string[] moduleNames)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-Command").AddParameter("Module", moduleNames);
        var cmds = ps.Invoke<CommandInfo>();
        return new HashSet<string>(cmds.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the set of commands to expose as MCP tools using a baseline-diff-filter algorithm.
    /// </summary>
    /// <param name="baseline">Command names present before any module or script was loaded.</param>
    /// <param name="allCommands">Command names after module/script loading.</param>
    /// <param name="includePool">
    ///   The pool of commands that include patterns are allowed to pull in.
    ///   Should be the union of the delta and commands from explicitly imported modules,
    ///   so that baseline commands can only be included when their module is declared via <c>-Module</c>.
    /// </param>
    /// <param name="includePatterns">
    ///   When non-empty, commands from <paramref name="includePool"/> matching any pattern are added.
    /// </param>
    /// <param name="excludePatterns">
    ///   When non-empty, commands matching any pattern are removed from the candidates.
    /// </param>
    internal static HashSet<string> DiscoverCommands(
        HashSet<string> baseline,
        HashSet<string> allCommands,
        HashSet<string> includePool,
        WildcardPattern[]? includePatterns,
        WildcardPattern[]? excludePatterns)
    {
        // Start with commands introduced after baseline (the delta).
        var candidates = new HashSet<string>(allCommands, StringComparer.OrdinalIgnoreCase);
        candidates.ExceptWith(baseline);

        // Include patterns pull in additional commands, but only from the explicit include pool.
        // This ensures baseline commands can only be included when their module is declared via -Module.
        if (includePatterns is { Length: > 0 })
        {
            foreach (var cmd in includePool)
            {
                if (includePatterns.Any(p => p.IsMatch(cmd)))
                    candidates.Add(cmd);
            }
        }

        // Exclude patterns remove matching commands from the candidate set.
        if (excludePatterns is { Length: > 0 })
            candidates.RemoveWhere(cmd => excludePatterns.Any(p => p.IsMatch(cmd)));

        return candidates;
    }

    /// <summary>
    /// Resolves the MCP tool name for <paramref name="commandName"/> using an optional <paramref name="nameMap"/>.
    /// </summary>
    /// <param name="commandName">The real PowerShell command name.</param>
    /// <param name="nameMap">
    ///   Maps friendly tool names (keys) to real command names (values). Case-insensitive value matching.
    ///   When <see langword="null"/>, <paramref name="commandName"/> is returned unchanged.
    /// </param>
    internal static string ResolveToolName(string commandName, IDictionary<string, string>? nameMap)
    {
        if (nameMap is null)
            return commandName;

        foreach (var (friendlyName, realName) in nameMap)
        {
            if (string.Equals(realName, commandName, StringComparison.OrdinalIgnoreCase))
                return friendlyName;
        }

        return commandName;
    }
}

using System.IO;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Reflection;
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
internal sealed partial class McpServerRunner
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
    /// <param name="cancellationToken">Token that triggers graceful shutdown.</param>
    /// <param name="startupLogLevel">Minimum log level to use during startup and discovery. Default is <see cref="LogLevel.Debug"/></param>
    public async Task RunAsync(
        McpServerPrimitiveCollection<McpServerTool> toolCollection,
        CancellationToken cancellationToken,
        LogLevel startupLogLevel = LogLevel.Debug)
    {
        ArgumentNullException.ThrowIfNull(toolCollection);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transport = new StreamServerTransport(_input, _output);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "PowerCode PowerShell MCP",
                Version = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Logging = new LoggingCapability()
            },
            ToolCollection = toolCollection
        };

        using var stderrLoggerFactory = McpLogging.CreateStderrLoggerFactory(startupLogLevel);
        await RunAsyncCore(transport, options, stderrLoggerFactory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the MCP server using command discovery: snapshots baseline commands, configures
    /// a runspace, discovers new commands, applies Include/Exclude filters and CommandConfiguration, then
    /// runs until <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///   Thrown when no commands are discovered after filtering.
    /// </exception>
    public async Task RunAsync(string[]? modules,
        string? scriptPath,
        string? scriptBlockText,
        string? callerScriptRoot,
        string[]? include,
        string[]? exclude,
        IDictionary<string, McpToolConfig>? commandConfig,
        int jsonDepth,
        LogLevel startupLogLevel,
        CancellationToken cancellationToken)
    {
        var baseline = GetDefaultCommandNames();
        var (runspace, host) = CreateConfiguredRunspace(modules, scriptPath, scriptBlockText, callerScriptRoot);
        using var _ = runspace;
        if (startupLogLevel != LogLevel.Debug)
        {
            throw new InvalidOperationException("Debug");
        }
        using var stderrLoggerFactory = McpLogging.CreateStderrLoggerFactory(startupLogLevel);
        var discoveryLogger = stderrLoggerFactory.CreateLogger<PowerShellToolFactory>();
        using var factory = new PowerShellToolFactory(runspace, host, stderrLoggerFactory, jsonDepth);

        var toolCollection = BuildDiscoveredToolCollection(
            baseline,
            runspace,
            factory,
            discoveryLogger,
            modules,
            include,
            exclude,
            commandConfig);

        await using var transport = new StreamServerTransport(_input, _output);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "PowerCode PowerShell MCP",
                Version = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Logging = new LoggingCapability()
            },
            ToolCollection = toolCollection
        };

        await RunAsyncCore(transport, options, stderrLoggerFactory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the server using a caller-owned stderr logger factory so process-local diagnostics
    /// and tool execution logs share the same stderr logging channel.
    /// </summary>
    private static async Task RunAsyncCore(
        StreamServerTransport transport,
        McpServerOptions options,
        ILoggerFactory stderrLoggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stderrLoggerFactory);

        cancellationToken.ThrowIfCancellationRequested();

        await using var server = McpServer.Create(transport, options, stderrLoggerFactory);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new runspace, imports modules and dot-sources/invokes any script,
    /// and returns the ready-to-use runspace.
    /// </summary>
    private static (Runspace Runspace, McpPsHost Host) CreateConfiguredRunspace(string[]? modules, string? scriptPath, string? scriptBlockText, string? callerScriptRoot)
    {
        var host = new McpPsHost();
        var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();

        if (modules is { Length: > 0 })
            ImportModules(runspace, modules);

        if (scriptPath is not null)
            DotSourceScript(runspace, scriptPath);
        else if (scriptBlockText is not null)
            InvokeScriptBlock(runspace, scriptBlockText, callerScriptRoot);

        return (runspace, host);
    }

    /// <summary>
    /// Collects all commands from <paramref name="runspace"/>, diffs against <paramref name="baseline"/>,
    /// applies wildcard Include/Exclude filters, applies per-command configuration from <paramref name="commandConfig"/>,
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
        PowerShellToolFactory factory,
        ILogger logger,
        string[]? modules,
        string[]? include,
        string[]? exclude,
        IDictionary<string, McpToolConfig>? commandConfig)
    {
        LogMessages.DiscoveryBaseline(logger, baseline.Count);

        var allCommands = GetAllCommandNames(runspace);

        // Build the include pool: delta + commands from explicitly imported modules.
        // Include patterns are restricted to this pool so that a baseline command can only
        // be included when its owning module is also declared via -Module.
        var delta = new HashSet<string>(allCommands, StringComparer.OrdinalIgnoreCase);
        delta.ExceptWith(baseline);
        LogMessages.DiscoveryPostLoad(logger, allCommands.Count, delta.Count);

        var includePool = new HashSet<string>(delta, StringComparer.OrdinalIgnoreCase);
        if (modules is { Length: > 0 })
            includePool.UnionWith(GetCommandsFromModules(runspace, modules));

        var includePatterns = include?.Select(p => new WildcardPattern(p, WildcardOptions.IgnoreCase)).ToArray();
        var excludePatterns = exclude?.Select(p => new WildcardPattern(p, WildcardOptions.IgnoreCase)).ToArray();

        LogMessages.DiscoveryIncludePatterns(logger, include);
        LogMessages.DiscoveryExcludePatterns(logger, exclude);

        var discovered = DiscoverCommands(baseline, allCommands, includePool, includePatterns, excludePatterns);

        if (discovered.Count == 0)
            throw new InvalidOperationException(
                "No commands were discovered. Ensure at least one module, script, or ScriptBlock introduces new commands, " +
                "or that Include patterns match commands from an explicitly imported -Module.");

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var cmdName in discovered)
        {
            McpToolConfig? config = null;
            commandConfig?.TryGetValue(cmdName, out config);
            toolCollection.Add(factory.Create(cmdName, config));
        }

        LogMessages.DiscoveryComplete(logger, toolCollection.Count);

        return toolCollection;
    }

    private static partial class LogMessages
    {
        [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Discovery baseline: {baselineCount} commands")]
        public static partial void DiscoveryBaseline(ILogger logger, int baselineCount);

        [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Discovery post-load: {allCount} commands ({deltaCount} new)")]
        public static partial void DiscoveryPostLoad(ILogger logger, int allCount, int deltaCount);

        [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Discovery include patterns: {includePatterns}", SkipEnabledCheck = true)]
        private static partial void DiscoveryIncludePatternsCore(ILogger logger, string includePatterns);

        public static void DiscoveryIncludePatterns(ILogger logger, string[]? includePatterns)
        {
            if (includePatterns is null || includePatterns.Length == 0)
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                string includePatternString = string.Join(", ", includePatterns);
                DiscoveryIncludePatternsCore(logger, includePatternString);
            }
        }

        [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Discovery exclude patterns: {excludePatterns}")]
        private static partial void DiscoveryExcludePatternsCore(ILogger logger, string excludePatterns);

        public static void DiscoveryExcludePatterns(ILogger logger, string[]? excludePatterns)
        {
            if (excludePatterns is null || excludePatterns.Length == 0)
            {
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                string excludePatternString = string.Join(", ", excludePatterns);
                DiscoveryExcludePatternsCore(logger, excludePatternString);
            }
        }

        [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Discovery complete: {toolCount} tools registered")]
        public static partial void DiscoveryComplete(ILogger logger, int toolCount);
    }

    private static void InvokeAndThrowOnError(Runspace runspace, Action<PowerShell> configure, string failureContext)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        configure(ps);
        ps.Invoke();

        if (ps.HadErrors)
        {
            var errors = string.Join("; ", ps.Streams.Error.Select(e => e.Exception.Message));
            throw new InvalidOperationException($"Failed to {failureContext}: {errors}");
        }
    }

    internal static void DotSourceScript(Runspace runspace, string scriptPath)
    {
        // Escape single quotes in the path to prevent script injection.
        InvokeAndThrowOnError(
            runspace,
            ps => ps.AddScript($". '{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'"),
            $"dot-source '{scriptPath}'");
    }

    internal static void InvokeScriptBlock(Runspace runspace, string scriptBlockText, string? callerScriptRoot = null)
    {
        if (!string.IsNullOrEmpty(callerScriptRoot))
        {
            var virtualFile = Path.Combine(callerScriptRoot, "__psmcp_init__.ps1");
            var ast = Parser.ParseInput(scriptBlockText, virtualFile, out _, out var parseErrors);
            if (parseErrors.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Failed to parse script block: {string.Join("; ", parseErrors.Select(error => error.Message))}");
            }

            var scriptBlock = ast.GetScriptBlock();
            InvokeAndThrowOnError(
                runspace,
                ps => ps.AddScript(". $args[0]").AddArgument(scriptBlock),
                "invoke script block");
        }
        else
        {
            InvokeAndThrowOnError(
                runspace,
                ps => ps.AddScript(scriptBlockText),
                "invoke script block");
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
        InvokeAndThrowOnError(
            runspace,
            ps => ps.AddCommand("Import-Module").AddParameter("Name", modules),
            $"import module(s) '{string.Join(", ", modules)}'");
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
    ///   When non-empty, only commands from <paramref name="includePool"/> matching any pattern are kept.
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
        // Default behavior (no include): expose the discovery delta only.
        // With include patterns: treat include as a filter over the explicit include pool.
        // This keeps behavior intuitive for wildcard filtering while still allowing baseline
        // commands only when their module is declared via -Module.
        HashSet<string> candidates;
        if (includePatterns is { Length: > 0 })
        {
            candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cmd in includePool)
            {
                if (includePatterns.Any(p => p.IsMatch(cmd)))
                    candidates.Add(cmd);
            }
        }
        else
        {
            candidates = new HashSet<string>(allCommands, StringComparer.OrdinalIgnoreCase);
            candidates.ExceptWith(baseline);
        }

        // Exclude patterns remove matching commands from the candidate set.
        if (excludePatterns is { Length: > 0 })
            candidates.RemoveWhere(cmd => excludePatterns.Any(p => p.IsMatch(cmd)));

        return candidates;
    }

}

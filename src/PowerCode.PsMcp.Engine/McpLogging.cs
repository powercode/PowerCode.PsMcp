using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerCode.PsMcp;

/// <summary>
/// Centralises the two logging channels used by the MCP host:
/// process-local diagnostics written to stderr, and client-visible notifications
/// emitted through the MCP session for a single tool invocation.
/// </summary>
internal static class McpLogging
{
    /// <summary>
    /// Creates a logger factory that writes diagnostics to stderr, preserving stdout for JSON-RPC.
    /// </summary>
    public static ILoggerFactory CreateStderrLoggerFactory(LogLevel minLevel) =>
        LoggerFactory.Create(builder =>
            builder.AddConsole(options => options.LogToStandardErrorThreshold = minLevel));

    /// <summary>
    /// Creates a per-request logger scope that writes to stderr and forwards the same log
    /// records to the active MCP client session.
    /// </summary>
    public static ToolLoggerScope CreateToolLoggerScope(
        RequestContext<CallToolRequestParams> request,
        ILogger stderrLogger,
        string categoryName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stderrLogger);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);

        var loggerProvider = request.Server.AsClientLoggerProvider();
        var clientLogger = loggerProvider.CreateLogger(categoryName);
        var logger = new FanOutLogger(stderrLogger, clientLogger);
        return new ToolLoggerScope(loggerProvider, logger);
    }

    /// <summary>
    /// Owns the MCP client logger provider for a single tool invocation.
    /// </summary>
    internal readonly struct ToolLoggerScope : IDisposable
    {
        private readonly ILoggerProvider _loggerProvider;

        public ToolLoggerScope(ILoggerProvider loggerProvider, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(loggerProvider);
            ArgumentNullException.ThrowIfNull(logger);

            _loggerProvider = loggerProvider;
            Logger = logger;
        }

        /// <summary>
        /// Logger that emits notifications to the active MCP client session.
        /// </summary>
        public ILogger Logger { get; }

        /// <inheritdoc />
        public void Dispose() => _loggerProvider.Dispose();
    }

    /// <summary>
    /// Broadcasts a single log event to stderr diagnostics and the active MCP client session.
    /// </summary>
    private sealed class FanOutLogger(ILogger stderrLogger, ILogger clientLogger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var stderrScope = stderrLogger.BeginScope(state);
            var clientScope = clientLogger.BeginScope(state);
            return new FanOutScope(stderrScope, clientScope);
        }

        public bool IsEnabled(LogLevel logLevel) =>
            stderrLogger.IsEnabled(logLevel) || clientLogger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (stderrLogger.IsEnabled(logLevel))
            {
                stderrLogger.Log(logLevel, eventId, state, exception, formatter);
            }

            if (clientLogger.IsEnabled(logLevel))
            {
                clientLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }

    /// <summary>
    /// Disposes both underlying scopes when a fan-out logger scope ends.
    /// </summary>
    private sealed class FanOutScope(IDisposable? stderrScope, IDisposable? clientScope) : IDisposable
    {
        public void Dispose()
        {
            clientScope?.Dispose();
            stderrScope?.Dispose();
        }
    }
}
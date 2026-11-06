using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PowerCode.PsMcp;

/// <summary>Creates process-local diagnostics without writing to the JSON-RPC stdout stream.</summary>
internal static class McpLogging
{
    public static ILoggerFactory CreateLoggerFactory(LogLevel minLevel, string? logPath = null) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLevel);
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            if (logPath is not null)
            {
                // Register a factory so the logging service provider owns and disposes the file.
                builder.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(logPath));
            }
        });
}

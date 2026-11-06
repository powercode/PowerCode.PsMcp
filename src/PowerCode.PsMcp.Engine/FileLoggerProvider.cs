using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PowerCode.PsMcp;

/// <summary>Serializes append-only UTF-8 logging and retains five size-rolled backups.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private FileStream? _stream;

    public FileLoggerProvider(string path, long maxBytes = 10 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        try
        {
            _stream = Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot open diagnostic log '{_path}'. Ensure its directory exists and is writable, and use a separate path for each server process.", ex);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private FileStream Open() => new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);

    private void Write(string message)
    {
        lock (_gate)
        {
            if (_stream is null)
            {
                return;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
                if (_stream.Length > 0 && _stream.Length + bytes.Length > _maxBytes)
                {
                    _stream.Dispose();
                    _stream = null;
                    for (var i = 4; i >= 1; i--)
                    {
                        var source = _path + "." + i.ToString(CultureInfo.InvariantCulture);
                        if (File.Exists(source))
                        {
                            File.Move(source, _path + "." + (i + 1).ToString(CultureInfo.InvariantCulture), overwrite: true);
                        }
                    }

                    File.Move(_path, _path + ".1", overwrite: true);
                    _stream = Open();
                }

                _stream.Write(bytes);
                _stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failing diagnostic destination must not fail the tool invocation.
                _stream?.Dispose();
                _stream = null;
                Console.Error.WriteLine($"File logging disabled for '{_path}': {ex.Message}");
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}[{eventId.Id}] {formatter(state, exception)}");
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            provider.Write(message);
        }
    }
}

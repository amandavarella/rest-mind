using Microsoft.Extensions.Logging;

namespace RestMind.Service;

/// <summary>
/// A deliberately small day-rotated file logger. The service runs unattended on someone
/// else's machine, so having a plain text log next to the config beats digging through the
/// Windows event log remotely.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxRetainedDays = 14;

    private readonly string _directory;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        PruneOldLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_writeLock)
        {
            try
            {
                var path = Path.Combine(_directory, $"restmind-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the enforcer down.
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-MaxRetainedDays);
            foreach (var file in Directory.GetFiles(_directory, "restmind-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = _category[(_category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {shortCategory}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}

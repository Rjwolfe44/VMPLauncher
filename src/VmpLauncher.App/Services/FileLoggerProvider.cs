using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VmpLauncher.App.Services;

/// <summary>
/// Tiny rolling file logger: one file per day, keeps the last seven. No external dependency on purpose.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _basePath;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writer;
    private readonly CancellationTokenSource _cts = new();

    public FileLoggerProvider(string basePathWithoutExtension)
    {
        _basePath = basePathWithoutExtension;
        Directory.CreateDirectory(Path.GetDirectoryName(_basePath)!);
        Prune();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "vmpl-log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Enqueue);

    private void Enqueue(string line)
    {
        if (!_queue.TryAdd(line))
        {
            // Drop rather than block the UI when the disk is slow.
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                var path = $"{_basePath}-{DateTime.UtcNow:yyyyMMdd}.log";
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException)
                {
                    // ignore; next line will try again
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Prune()
    {
        try
        {
            var dir = Path.GetDirectoryName(_basePath)!;
            var prefix = Path.GetFileName(_basePath) + "-";
            foreach (var f in new DirectoryInfo(dir).GetFiles(prefix + "*.log").OrderByDescending(f => f.Name).Skip(7))
            {
                f.Delete();
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed class FileLogger(string category, Action<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" [").Append(logLevel.ToString()[..4].ToUpperInvariant()).Append("] ")
              .Append(ShortCategory(category)).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }
            sink(sb.ToString());
        }

        private static string ShortCategory(string c)
        {
            var i = c.LastIndexOf('.');
            return i < 0 ? c : c[(i + 1)..];
        }
    }
}

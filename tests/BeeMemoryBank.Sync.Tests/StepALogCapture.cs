using System.Text;
using BeeMemoryBank.TestSupport;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Every log line a Step A test wrote, at every level, with its arguments and exceptions: what the log canary scan
/// (docs/design/step-a-spec.md 16.2 item 9) reads.
/// </summary>
public sealed class StepALogCapture
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

    public ILogger<T> For<T>() => new Logger<T>(this);

    public List<CanaryHit> Scan(IEnumerable<string> canaries) =>
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join('\n', Lines)), canaries, "log");

    private void Add(string line) { lock (_lines) _lines.Add(line); }

    private sealed class Logger<T>(StepALogCapture owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner.Add("scope " + state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = new StringBuilder($"{logLevel} {typeof(T).Name}: {formatter(state, exception)}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var (key, value) in values) line.Append($" [{key}={value}]");
            if (exception is not null) line.Append(' ').Append(exception);
            owner.Add(line.ToString());
        }
    }
}

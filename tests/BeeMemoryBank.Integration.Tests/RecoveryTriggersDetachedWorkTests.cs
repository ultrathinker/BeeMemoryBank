using BeeMemoryBank.Api.Services.Recovery;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The recovery work a sign-in or a rotation starts runs detached and may outlive the host: a test disposes its host (and with it the
/// Windows event log provider) while an Argon2 derivation of that work is still running. Its failure is then logged through a logger
/// whose provider is gone, and Microsoft.Extensions.Logging reports that as an <see cref="AggregateException"/> ("An error occurred while
/// writing to logger(s).") around the <see cref="ObjectDisposedException"/>, not as the bare one. The detached task must still end
/// without a fault: a faulted fire-and-forget task is an unobserved exception that took a test host down beside an Admin group.
/// </summary>
public class RecoveryTriggersDetachedWorkTests
{
    private sealed class DisposedProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new DisposedLogger();
        public void Dispose() { }
    }

    private sealed class DisposedLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        // What the event log provider does once the host has disposed it (EventLogInternal.OpenForWrite).
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new ObjectDisposedException("EventLogInternal");
    }

    [Fact]
    public async Task Work_that_fails_after_the_host_disposed_its_log_ends_without_a_fault()
    {
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new DisposedProvider()));
        var logger = loggers.CreateLogger<RecoveryTriggers>();
        FluentActions.Invoking(() => logger.LogWarning("probe")).Should().Throw<AggregateException>(
            "the logging pipeline wraps a provider's failure; this is the shape the detached work meets after the host is gone");

        // RunDetached touches nothing but the logger.
        var triggers = new RecoveryTriggers(null!, null!, null!, null!, logger);
        var work = triggers.RunDetached(() => throw new InvalidOperationException("the vault folder is gone"),
            "Recovery work at login failed; it runs again at the next login");

        await FluentActions.Awaiting(() => work).Should().NotThrowAsync();
        work.IsFaulted.Should().BeFalse();
    }
}

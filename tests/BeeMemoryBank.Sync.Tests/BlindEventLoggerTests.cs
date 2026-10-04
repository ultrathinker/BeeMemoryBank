using System.Reflection;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The blind host registers <see cref="BlindEventLogger"/> instead of the full EventLogger (which signs with the master key and is vault code).
/// What the full logger did on a blind node - refuse, with a fixed message - is now this class's whole job, so every member of
/// <see cref="IEventLogger"/> is checked by reflection: a method added to the interface and implemented as a no-op turns this red.
/// </summary>
public class BlindEventLoggerTests
{
    private sealed class CountingTrigger : ISyncTrigger
    {
        public int Signals;
        public void Signal() => Signals++;
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    [Fact]
    public async Task EveryLogMethod_ReturnsAFaultedTask_WithTheBlindNodeRefusal()
    {
        var logger = new BlindEventLogger(new CountingTrigger());
        var methods = typeof(IEventLogger).GetMethods().Where(m => m.Name != nameof(IEventLogger.SignalSync)).ToList();
        methods.Count.Should().BeGreaterThan(15, "the interface has about twenty Log* methods; the scan must see them");

        foreach (var method in methods)
        {
            var args = method.GetParameters().Select(p => p.ParameterType == typeof(string) ? (object?)"x" : DefaultOf(p.ParameterType)).ToArray();
            Task task;
            try { task = (Task)method.Invoke(logger, args)!; }
            catch (TargetInvocationException e) { throw new Exception(method.Name + " threw at the call instead of returning a faulted task", e.InnerException); }

            var act = async () => await task;
            var ex = await act.Should().ThrowAsync<InvalidOperationException>(method.Name);
            ex.Which.Message.Should().StartWith("Refusing to log a ").And.EndWith(" event: this is a blind node, and blind nodes never author events.", method.Name);
        }
    }

    [Fact]
    public async Task TheRefusalNamesTheEventType_AsTheFullLoggerDid()
    {
        var logger = new BlindEventLogger(new CountingTrigger());
        var act = () => logger.LogHardDeleteAsync("article", "id");
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(EventTypes.HardDelete);
    }

    [Fact]
    public void SignalSync_StillWakesThePushLoop()
    {
        var trigger = new CountingTrigger();
        new BlindEventLogger(trigger).SignalSync();
        trigger.Signals.Should().Be(1);
    }
}

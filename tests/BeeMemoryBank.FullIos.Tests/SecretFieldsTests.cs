using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>The password and passphrase fields of the full iPhone app are emptied on every way out of the operation that used them (review revios F3).</summary>
public class SecretFieldsTests
{
    private sealed class Field(string text)
    {
        public string Text { get; set; } = text;
    }

    [Fact]
    public async Task OnSuccess_TheFieldsAreEmptied_AndTheResultIsReturned()
    {
        var password = new Field("hunter2-Hunter2");
        var repeat = new Field("hunter2-Hunter2");

        var result = await SecretFields.RunAsync(() => Task.FromResult(42), () => password.Text = "", () => repeat.Text = "");

        result.Should().Be(42);
        (password.Text, repeat.Text).Should().Be(("", ""));
    }

    [Fact]
    public async Task WhenTheOperationThrows_TheFieldIsStillEmptied_AndTheExceptionReachesTheScreen()
    {
        var password = new Field("hunter2-Hunter2");

        var act = () => SecretFields.RunAsync<bool>(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("a keychain, a database or the network said no");
        }, () => password.Text = "");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("a keychain*");
        password.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheOperationIsCancelled_TheFieldIsEmptied()
    {
        var passphrase = new Field("open sesame");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => SecretFields.RunAsync(() => Task.Delay(Timeout.Infinite, cts.Token), () => passphrase.Text = "");

        await act.Should().ThrowAsync<OperationCanceledException>();
        passphrase.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheOperationFailsBeforeItReturnsATask_TheFieldIsStillEmptied()
    {
        var password = new Field("hunter2-Hunter2");

        var act = () => SecretFields.RunAsync((Func<Task>)(() => throw new IOException("failed while starting")), () => password.Text = "");

        await act.Should().ThrowAsync<IOException>();
        password.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task AFieldThatCannotBeEmptied_NeitherSkipsTheOthers_NorHidesTheOperationsOwnFailure()
    {
        var repeat = new Field("hunter2-Hunter2");

        var act = () => SecretFields.RunAsync<bool>(
            () => throw new InvalidOperationException("the real reason"),
            () => throw new ObjectDisposedException("the control is gone"),
            () => repeat.Text = "");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("the real reason");
        repeat.Text.Should().BeEmpty();
    }
}

using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Guards the property the typed exceptions exist for: the status code an error becomes is decided
/// by its TYPE, and rewording a message can never move it.
///
/// <para>
/// The API used to decide status codes with <c>ex.Message.Contains("in progress")</c> and
/// <c>ex.Message.Contains("disk space")</c>. Nothing failed when a message was reworded — the
/// endpoint just started answering a different status, and the first person to notice was whoever
/// hit it in production. These tests are the thing that fails instead.
/// </para>
/// </summary>
public class ExceptionStatusMapTests
{
    [Fact]
    public void EachTypeMapsToItsOwnStatus()
    {
        ExceptionStatusMap.Map(new SessionLockedException("Session is locked.")).StatusCode.Should().Be(403);
        ExceptionStatusMap.Map(new InsufficientDiskSpaceException("need ~500MB")).StatusCode.Should().Be(507);
        ExceptionStatusMap.Map(new ConflictException("Another rotation is in progress.")).StatusCode.Should().Be(409);
        ExceptionStatusMap.Map(new UsernameConflictException("taken")).StatusCode.Should().Be(409);
        ExceptionStatusMap.Map(new KeyNotFoundException("no such article")).StatusCode.Should().Be(404);
        ExceptionStatusMap.Map(new ArgumentException("bad path")).StatusCode.Should().Be(400);
        ExceptionStatusMap.Map(new UnauthorizedAccessException("nope")).StatusCode.Should().Be(403);
        ExceptionStatusMap.Map(new ReadOnlyAccessException("/Locked")).StatusCode.Should().Be(403);
        ExceptionStatusMap.Map(new InvalidOperationException("something else")).StatusCode.Should().Be(409);
    }

    /// <summary>
    /// The whole point. Every message below is chosen to match the substring the old code keyed on
    /// for a DIFFERENT status, so any reintroduced <c>Message.Contains</c> would answer wrongly.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("in progress")]
    [InlineData("Insufficient disk space for snapshot")]
    [InlineData("Session is locked")]
    [InlineData("not found")]
    public void MessageTextNeverChangesTheStatus(string message)
    {
        ExceptionStatusMap.Map(new ConflictException(message)).StatusCode.Should().Be(409);
        ExceptionStatusMap.Map(new SessionLockedException(message)).StatusCode.Should().Be(403);
        ExceptionStatusMap.Map(new InsufficientDiskSpaceException(message)).StatusCode.Should().Be(507);
    }

    /// <summary>
    /// All three typed exceptions derive from <see cref="InvalidOperationException"/> so that the
    /// dozens of un-migrated <c>catch (InvalidOperationException)</c> handlers keep working. That
    /// makes arm order in the switch load-bearing: move the base arm up and all three silently
    /// collapse to 409 with nothing else failing.
    /// </summary>
    [Fact]
    public void DerivedTypesAreMatchedBeforeTheirBase()
    {
        new SessionLockedException("x").Should().BeAssignableTo<InvalidOperationException>();
        new InsufficientDiskSpaceException("x").Should().BeAssignableTo<InvalidOperationException>();
        new ConflictException("x").Should().BeAssignableTo<InvalidOperationException>();

        var baseStatus = ExceptionStatusMap.Map(new InvalidOperationException("x")).StatusCode;
        ExceptionStatusMap.Map(new SessionLockedException("x")).StatusCode.Should().NotBe(baseStatus);
        ExceptionStatusMap.Map(new InsufficientDiskSpaceException("x")).StatusCode.Should().NotBe(baseStatus);
    }

    /// <summary>
    /// UsernameConflictException is a ConflictException so an unmigrated handler treats it as one;
    /// UserService still has to tell it apart, because it retries only on that specific failure.
    /// </summary>
    [Fact]
    public void UsernameConflictIsAConflictButStaysDistinguishable()
    {
        new UsernameConflictException("x").Should().BeAssignableTo<ConflictException>();
        new ConflictException("x").Should().NotBeAssignableTo<UsernameConflictException>();
    }

    [Fact]
    public void UnrecognisedExceptionsDoNotLeakTheirMessage()
    {
        var (status, message) = ExceptionStatusMap.Map(
            new NullReferenceException("connection string 'Data Source=/srv/vault.db'"));

        status.Should().Be(500);
        message.Should().Be("Internal server error");
    }

    [Fact]
    public void NoExceptionIsStillAServerError()
    {
        ExceptionStatusMap.Map(null).Should().Be((500, "Internal server error"));
    }

    /// <summary>
    /// A typed exception with a vague message is a regression: the restore endpoint puts
    /// <c>ex.Message</c> straight into its 400 body, so this text is what an operator reads.
    /// </summary>
    [Fact]
    public void OperatorFacingMessagesArePassedThroughUnchanged()
    {
        const string detailed = "Insufficient disk space for snapshot: need ~512MB in C:\\, have 40MB";
        ExceptionStatusMap.Map(new InsufficientDiskSpaceException(detailed)).Message.Should().Be(detailed);
    }
}

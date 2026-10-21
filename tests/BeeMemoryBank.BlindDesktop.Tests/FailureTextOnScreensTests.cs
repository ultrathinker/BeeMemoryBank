using System.Collections;
using System.Reflection;
using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// What a failure looks like on the screens of the desktop head: the KIND of failure (<see cref="BlindFailureText"/>), never the message of the exception, which can carry a local path, an
/// address, a token or a word of a library. The raw exception goes to the log callback and nowhere else. Every case uses canaries (a path with the name of a key file, an address with a token in its
/// query) and checks the notice AND every string the view models expose.
/// </summary>
public sealed class FailureTextOnScreensTests
{
    private const string PathCanary = @"C:\secret\bmb_blind_seed_v1 canary-123";
    private const string UrlCanary = "GET https://hub.canary.example:5610/api/sync/invite/redeem?token=tok-canary-456 failed";

    private static readonly string[] Canaries = ["canary-123", "bmb_blind_seed_v1", @"C:\secret", "tok-canary-456", "hub.canary.example", "/api/sync", "token="];

    public static TheoryData<string> Kinds => new() { "file", "http", "http-inside-a-sentence", "aggregate" };

    private static Exception Failure(string kind) => kind switch
    {
        "file" => new IOException(PathCanary),
        "http" => new HttpRequestException(UrlCanary),
        "http-inside-a-sentence" => new InvalidOperationException("The call did not go through", new HttpRequestException(UrlCanary)),
        "aggregate" => new AggregateException("The removal is not complete: " + PathCanary, new IOException(PathCanary)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A window over scripted accounts: one active account with a backup, the add page, autostart, and the log callback.</summary>
    private sealed class Window
    {
        public FakeAccounts Accounts { get; } = new();
        public FakeAutostart Autostart { get; } = new();
        public List<(string What, Exception Error)> Problems { get; } = [];
        public BlindAccountEntry Entry { get; }
        public FakeApp App { get; }
        public MainViewModel Vm { get; }

        public Window(Func<string, byte[]>? qr = null)
        {
            (Entry, App) = Accounts.AddAccount("Berlin office", status: FakeApp.StatusOf(backups: [new BlindAppBackup("a.bmbbackup", 10)]));
            Vm = new MainViewModel(Accounts, _ => new FakeWorkRequests(), Autostart, qr ?? (_ => [1]), problem: (what, ex) => Problems.Add((what, ex)), zone: TimeZoneInfo.Utc);
        }

        public async Task<Window> ShownAsync()
        {
            await Vm.RefreshAsync();
            return this;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    /// <summary>Every string a view model (and the view models inside it) exposes through a public property.</summary>
    private static IEnumerable<string> ExposedStrings(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>([root]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (var property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                object? value;
                try { value = property.GetValue(current); }
                catch (TargetInvocationException) { continue; }
                switch (value)
                {
                    case null:
                        break;
                    case string text:
                        yield return text;
                        break;
                    case byte[]:
                        break;
                    case IEnumerable items:
                        foreach (var item in items)
                        {
                            if (item is string line) yield return line;
                            else if (IsOurs(item)) queue.Enqueue(item);
                        }
                        break;
                    default:
                        if (IsOurs(value)) queue.Enqueue(value);
                        break;
                }
            }
        }
    }

    private static bool IsOurs(object? value) => value is not null && value.GetType().Assembly == typeof(MainViewModel).Assembly;

    private static void NothingOfTheFailureIsShown(Window window, params string?[] notices) => NothingOfTheFailureIsShown("", window, notices);

    private static void NothingOfTheFailureIsShown(string where, Window window, params string?[] notices)
    {
        foreach (var notice in notices)
        {
            notice.Should().NotBeNullOrEmpty($"the person is told that it did not work {where}");
            foreach (var canary in Canaries) notice!.Should().NotContainEquivalentOf(canary, "the notice tells the kind of failure, not its message");
        }
        foreach (var text in ExposedStrings(window.Vm))
            foreach (var canary in Canaries)
                text.Should().NotContainEquivalentOf(canary, "no string of any view model carries the message of an exception");
    }

    private static void TheRawExceptionWentToTheLog(Window window, Exception expected) =>
        window.Problems.Should().Contain(p => ReferenceEquals(p.Error, expected), "the raw exception is for the log, which is not a screen");

    // ---- the invitation ---------------------------------------------------------------------------------------------------------------

    private const string Invitation = "bmb-invite:?v=1&a=https%3A%2F%2Fmem.example.org";

    private static BlindInvitationPreview Good() =>
        new("Alex", "mem.example.org", BlindAccountDefaults.ForInvited(isPhone: false), new ManualTimeProvider().GetUtcNow().AddMinutes(28));

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnInvitationThatCannotBeAdded_IsToldByItsKind_NotByItsMessage(string kind)
    {
        var window = await new Window().ShownAsync();
        var failure = Failure(kind);
        window.Accounts.Previews[Invitation] = Good();
        window.Accounts.Throws["AddFromInvitation"] = failure;
        window.Vm.Add.InviteText = Invitation;

        window.Vm.Add.AddInvitationCommand.Execute(null);
        await WaitFor(() => window.Vm.Add.HasError);

        NothingOfTheFailureIsShown(window, window.Vm.Add.Error);
        TheRawExceptionWentToTheLog(window, failure);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnInvitationThatCannotBePreviewed_IsToldByItsKind_NotByItsMessage(string kind)
    {
        var window = await new Window().ShownAsync();
        var failure = Failure(kind);
        window.Accounts.Throws["Preview"] = failure;

        window.Vm.Add.InviteText = Invitation;

        NothingOfTheFailureIsShown(window, window.Vm.Add.Error);
        TheRawExceptionWentToTheLog(window, failure);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnAddWithAPairingCodeThatFails_IsToldByItsKind_NotByItsMessage(string kind)
    {
        var window = await new Window().ShownAsync();
        var failure = Failure(kind);
        window.Accounts.Throws["AddAsync"] = failure;

        window.Vm.Add.AddWithCodeCommand.Execute(null);
        await WaitFor(() => window.Vm.Add.HasError);

        NothingOfTheFailureIsShown(window, window.Vm.Add.Error);
        TheRawExceptionWentToTheLog(window, failure);
    }

    [Fact]
    public async Task ACommandOfTheAddPageThatThrows_IsToldByItsKind_NotByItsMessage()
    {
        var window = await new Window().ShownAsync();
        var failure = Failure("file");
        window.Vm.Add.PasteRequested += () => throw failure;

        window.Vm.Add.PasteCommand.Execute(null);

        NothingOfTheFailureIsShown(window, window.Vm.Add.Error);
        TheRawExceptionWentToTheLog(window, failure);
    }

    // ---- the removal -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnAccountThatCannotBeRemoved_IsToldByItsKind_NotByItsMessage(string kind)
    {
        var window = await new Window().ShownAsync();
        var failure = Failure(kind);
        window.Accounts.OnRemove = _ => throw failure;
        var detail = window.Vm.Detail!;
        detail.RemoveCommand.Execute(null);

        detail.ConfirmRemoveCommand.Execute(null);
        await WaitFor(() => !detail.IsBusy && detail.Notice is not null);

        NothingOfTheFailureIsShown(window, detail.Notice);
        TheRawExceptionWentToTheLog(window, failure);
        detail.RemovePanelOpen.Should().BeTrue("the confirmation stays: the person can press it again");
        if (kind == "aggregate") detail.Notice.Should().Contain("press Remove again", "a removal that did not finish says that pressing it again finishes it");
    }

    // ---- every other notice of the head -------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheNoticesOfTheWindow_AreToldByTheirKind_NotByTheirMessage(string kind)
    {
        // reading the accounts
        var reading = await new Window().ShownAsync();
        var failure = Failure(kind);
        reading.Accounts.Throws["Snapshot"] = failure;
        await reading.Vm.RefreshAsync();
        NothingOfTheFailureIsShown(reading, reading.Vm.Notice);
        TheRawExceptionWentToTheLog(reading, failure);

        // changing the start-up setting
        var autostart = await new Window().ShownAsync();
        autostart.Autostart.Failure = failure;
        autostart.Vm.AutostartOn = true;
        NothingOfTheFailureIsShown(autostart, autostart.Vm.Notice);
        TheRawExceptionWentToTheLog(autostart, failure);

        // a command of the account's card
        var command = await new Window().ShownAsync();
        command.Accounts.Throws["Rename"] = failure;
        command.Vm.Detail!.RenameText = "Paris";
        command.Vm.Detail.RenameCommand.Execute(null);
        NothingOfTheFailureIsShown(command, command.Vm.Detail.Notice);
        TheRawExceptionWentToTheLog(command, failure);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheNoticesOfTheAccountsPanel_AreToldByTheirKind_NotByTheirMessage(string kind)
    {
        var failure = Failure(kind);

        // reading the status
        var status = await new Window().ShownAsync();
        status.App.StatusFailure = failure;
        await status.Vm.Detail!.Panel!.RefreshAsync();
        NothingOfTheFailureIsShown("reading the status", status, status.Vm.Detail.Panel.Notice);
        TheRawExceptionWentToTheLog(status, failure);

        // starting a re-pairing
        var rePair = await new Window().ShownAsync();
        rePair.App.RePairFailure = failure;
        rePair.Vm.Detail!.Panel!.RePairCommand.Execute(null);
        NothingOfTheFailureIsShown("starting a re-pairing", rePair, rePair.Vm.Detail.Panel.Notice);
        TheRawExceptionWentToTheLog(rePair, failure);

        // saving a backup
        var save = await new Window().ShownAsync();
        save.App.ExportBody = _ => throw failure;
        var panel = save.Vm.Detail!.Panel!;
        panel.Backups[0].SaveCommand.Execute(null);
        await WaitFor(() => panel.Notice?.Contains("backup") == true);
        NothingOfTheFailureIsShown("saving a backup", save, panel.Notice);
        TheRawExceptionWentToTheLog(save, failure);

        // making the QR picture of the pairing code
        var qr = new Window(_ => throw failure);
        qr.App.Status = FakeApp.StatusOf(paired: false, loaded: false, awaiting: true);
        qr.App.PairingCodeValue = "bmb-blind-phone:?n=1";
        await qr.Vm.RefreshAsync();
        await qr.Vm.Detail!.Panel!.RefreshAsync();
        await WaitFor(() => qr.Vm.Detail.Panel.Notice is not null);   // the panel's first read (made when it was built) may still be making the picture
        NothingOfTheFailureIsShown("making the QR picture", qr, qr.Vm.Detail.Panel.Notice);
        TheRawExceptionWentToTheLog(qr, failure);
    }

    [Fact]
    public async Task ANoticeOfTheDesktopHead_SpeaksOfADevice_NotOfAPhone()
    {
        var window = await new Window().ShownAsync();
        window.Accounts.Throws["AddAsync"] = Failure("file");

        window.Vm.Add.AddWithCodeCommand.Execute(null);
        await WaitFor(() => window.Vm.Add.HasError);

        window.Vm.Add.Error.Should().Be("That did not work: a file or the storage of this device could not be read or written.");
    }

    [Fact]
    public void Control_TheScan_SeesTheCanariesWhenTheyAreThere()
    {
        var window = new Window();

        var control = () => NothingOfTheFailureIsShown(window, "That did not work: " + PathCanary);

        control.Should().Throw<Exception>("a notice that carries the message of the exception must fail the check");
    }
}

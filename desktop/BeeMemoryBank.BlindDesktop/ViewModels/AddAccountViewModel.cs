using System.Windows.Input;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>
/// "Add account": a paste box for the one-time invitation (<c>bmb-invite:...</c>) with the preview of what it is (<see cref="BlindInvitationPreview"/>: whose copy, from which computer, the
/// limits the account will start with, how long the invitation works - or why it is not taken), and the old way, a pairing code, as a second button. Paste is the only entry on a
/// desktop (no camera). The preview needs no network and writes nothing; "Add" does the work (<see cref="IBlindAccountsController.AddFromInvitationAsync"/>) and every refusal comes back as
/// the controller's own sentence. The text of the invitation is never kept after it has been used.
/// </summary>
public sealed class AddAccountViewModel : ObservableObject
{
    /// <summary>
    /// The most the paste box holds (it is the TextBox's own limit): twice the longest invitation there can be (<see cref="BlindInvitation.MaxTextLength"/>), so that a paste that was cut at this
    /// length still reads as too long and is refused, not taken for a shorter text.
    /// </summary>
    public const int MaxPasteLength = 2 * BlindInvitation.MaxTextLength;

    /// <summary>What the box says when a paste is longer than an invitation can be. Head-owned: the controller never sees such a text.</summary>
    public const string TooLongText = "That is far too long to be an invitation (an invitation is about a thousand characters). Nothing was kept: copy the invitation again.";

    private readonly IBlindAccountsController _accounts;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception>? _problem;
    private string _inviteText = "";
    private string _nameText = "";
    private BlindInvitationPreview? _preview;
    private string? _error;
    private bool _busy;

    public AddAccountViewModel(IBlindAccountsController accounts, TimeProvider? time = null, Action<string, Exception>? problem = null)
    {
        _accounts = accounts;
        _time = time ?? TimeProvider.System;
        _problem = problem;
        PasteCommand = new RelayCommand(() => PasteRequested?.Invoke(), () => !_busy, Failed);
        AddInvitationCommand = new RelayCommand(() => _ = AddFromInvitationAsync(), () => !_busy && _preview is { CanAdd: true }, Failed);
        AddWithCodeCommand = new RelayCommand(() => _ = AddWithCodeAsync(), () => !_busy, Failed);
    }

    /// <summary>Raised when the person presses "Paste"; the window reads the clipboard and sets <see cref="InviteText"/>.</summary>
    public event Action? PasteRequested;

    /// <summary>Raised with the id of the account that was just made, so the window can select it.</summary>
    public event Action<string>? Added;

    public ICommand PasteCommand { get; }
    public ICommand AddInvitationCommand { get; }
    public ICommand AddWithCodeCommand { get; }

    /// <summary>The pasted invitation. Setting it makes the preview; a text longer than an invitation can be is refused first (<see cref="TooLongText"/>) and not kept.</summary>
    public string InviteText
    {
        get => _inviteText;
        set
        {
            value ??= "";
            if (IsTooLong(value))
            {
                // Before the text is kept, copied by a trim or handed to the controller: a hostile or careless clipboard value costs a length check, not a parse on the UI thread.
                _inviteText = "";
                _preview = null;
                Raise();
                RaisePreview();
                Error = TooLongText;
                return;
            }
            if (!Set(ref _inviteText, value)) return;
            Error = null;
            Refresh();
        }
    }

    /// <summary>True when <paramref name="text"/> cannot be an invitation by its length alone; blank space around an invitation is not counted (and is not copied to find out).</summary>
    private static bool IsTooLong(string text) =>
        text.Length > MaxPasteLength || (text.Length > BlindInvitation.MaxTextLength && text.AsSpan().Trim().Length > BlindInvitation.MaxTextLength);

    /// <summary>What to call an account that is paired with a code (blank: "Account N").</summary>
    public string NameText { get => _nameText; set => Set(ref _nameText, value); }

    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>A slow operation (the identity probe and the redemption go over the network) is running; no second one can start.</summary>
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RaiseCommands(); } }

    // ---- the preview, all of it from the view record ----------------------------------------------------------------------------------------------

    public bool HasPreview => _preview is not null;

    /// <summary>"A copy of Alex's Bee Memory Bank, kept encrypted: this device cannot read it. It comes from mem.example.org. This invitation is a one-time code, not an encrypted one."</summary>
    public string PreviewDescription => _preview is { Refusal: null } preview ? preview.Description : "";

    /// <summary>"It will use at most 2 GB of storage, 200 MB a day."</summary>
    public string PreviewLimits => _preview is { Refusal: null } preview ? preview.Limits : "";

    /// <summary>"Works for 28 more minutes."</summary>
    public string PreviewValidity => _preview is { Refusal: null } preview ? preview.Validity(_time.GetUtcNow()) : "";

    /// <summary>Why the invitation is not taken, in the controller's sentence; empty when it is.</summary>
    public string PreviewRefusal => _preview?.Refusal ?? "";

    public bool HasRefusal => !string.IsNullOrEmpty(PreviewRefusal);

    public bool CanAddFromInvitation => _preview is { CanAdd: true };

    /// <summary>Makes the preview again (the clock moved, the accounts changed).</summary>
    public void Refresh()
    {
        var text = _inviteText.Trim();
        _preview = text.Length == 0 ? null : SafePreview(text);
        RaisePreview();
    }

    private void RaisePreview()
    {
        foreach (var name in new[] { nameof(HasPreview), nameof(PreviewDescription), nameof(PreviewLimits), nameof(PreviewValidity), nameof(PreviewRefusal), nameof(HasRefusal), nameof(CanAddFromInvitation) })
            Raise(name);
        RaiseCommands();
    }

    private BlindInvitationPreview? SafePreview(string text)
    {
        try { return _accounts.PreviewInvitation(text); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = FailureNotice.Say("That did not work", ex);
            _problem?.Invoke("Previewing an invitation failed", ex);
            return null;
        }
    }

    /// <summary>Forgets the pasted text and the preview (the window was left, or the account was made).</summary>
    public void Clear()
    {
        _nameText = "";
        Raise(nameof(NameText));
        InviteText = "";
        Error = null;
    }

    private async Task AddFromInvitationAsync()
    {
        if (_busy) return;
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _accounts.AddFromInvitationAsync(_inviteText.Trim());
            Finish(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = FailureNotice.Say("That did not work", ex);
            _problem?.Invoke("Adding an account from an invitation failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddWithCodeAsync()
    {
        if (_busy) return;
        IsBusy = true;
        Error = null;
        try
        {
            var result = await _accounts.AddAsync(string.IsNullOrWhiteSpace(_nameText) ? null : _nameText.Trim());
            Finish(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = FailureNotice.Say("That did not work", ex);
            _problem?.Invoke("Adding an account failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Finish(BlindAddResult result)
    {
        if (result.AccountId is { } id)
        {
            // Made (also when it could not be started: the account's card says so, and so does this page). The pasted text is gone; the secret in it was spent or never stored.
            InviteText = "";
            NameText = "";
            Error = result.Error;
            Added?.Invoke(id);
            return;
        }
        Error = result.Error ?? "The account could not be added.";
    }

    private void RaiseCommands()
    {
        ((RelayCommand)PasteCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddInvitationCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddWithCodeCommand).RaiseCanExecuteChanged();
    }

    private void Failed(Exception ex)
    {
        Error = FailureNotice.Say("That did not work", ex);
        _problem?.Invoke("A command failed", ex);
    }
}

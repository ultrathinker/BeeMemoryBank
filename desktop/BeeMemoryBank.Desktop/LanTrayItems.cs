using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop;

/// <summary>
/// The tray menu's items for LAN sync. "Sync on this network: trusted (Change...)" opens the card of the networks (Admin, Devices on my
/// network). While a network waits for an answer there is also an item "Sync with your paired devices on ...?" with the two answers, so the
/// question is not lost with a banner that was dismissed, or that the system had no way to show with buttons. There are no items while
/// there is nothing to say (no node yet, an older node): they are put into the menu and taken out again, like the profiles' submenu, rather
/// than hidden.
/// </summary>
internal sealed class LanTrayItems
{
    /// <summary>The questions the menu can show at once; the prompt asks about one network at a time, so one is the usual number.</summary>
    private const int MaxQuestions = 2;

    private readonly MainWindow _window;
    private readonly NativeMenu _menu;
    private readonly NativeMenuItem _after;
    private readonly NativeMenuItem _status = new("Sync on this network");
    private readonly List<NativeMenuItem> _questions = [];
    private readonly string?[] _fingerprints = new string?[MaxQuestions];

    /// <param name="menu">The tray menu.</param>
    /// <param name="after">The item of <paramref name="menu"/> the LAN items follow.</param>
    public LanTrayItems(MainWindow window, NativeMenu menu, NativeMenuItem after)
    {
        _window = window;
        _menu = menu;
        _after = after;
        _status.Click += (_, _) => Dispatcher.UIThread.Post(() => _window.ShowAdminPage(LanNetworkPrompt.SettingsPath));

        for (var i = 0; i < MaxQuestions; i++)
        {
            var index = i;
            var yes = new NativeMenuItem("Yes");
            yes.Click += (_, _) => Answer(index, trusted: true);
            var no = new NativeMenuItem("Not this network");
            no.Click += (_, _) => Answer(index, trusted: false);
            _questions.Add(new NativeMenuItem("Sync with your paired devices on this network?")
            {
                Menu = new NativeMenu { Items = { yes, no } },
            });
        }

        // Raised on the polling thread of the prompt.
        _window.LanTrayChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Puts the items the state calls for into the menu, and takes the others out. UI thread.</summary>
    public void Refresh()
    {
        var state = _window.LanTrayState;
        var wanted = new List<NativeMenuItem>();
        if (state.Header is { } header)
        {
            _status.Header = header;
            wanted.Add(_status);
        }
        for (var i = 0; i < MaxQuestions; i++)
        {
            var question = i < state.Questions.Count ? state.Questions[i] : null;
            _fingerprints[i] = question?.Fingerprint;
            if (question == null) continue;
            _questions[i].Header = $"Sync with your paired devices on {question.Label}?";
            wanted.Add(_questions[i]);
        }

        foreach (var item in _questions.Append(_status).Where(_menu.Items.Contains).ToList()) _menu.Items.Remove(item);
        var at = _menu.Items.IndexOf(_after) + 1;
        if (at == 0) return; // the anchor is not in the menu: nothing to follow
        foreach (var item in wanted) _menu.Items.Insert(at++, item);
    }

    private void Answer(int index, bool trusted)
    {
        var fingerprint = _fingerprints[index];
        if (fingerprint is null) return;
        _ = AnswerAsync(fingerprint, trusted);
    }

    private async Task AnswerAsync(string fingerprint, bool trusted)
    {
        try { await _window.AnswerLanNetworkAsync(fingerprint, trusted); }
        catch (Exception ex) { Console.Error.WriteLine($"[LanTrayItems] The answer about a network failed ({ex.GetType().Name})."); }
    }
}

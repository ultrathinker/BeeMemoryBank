#if DEBUG || BMB_E2E
using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// The end-to-end check's hands (tools/ios-e2e/ios_full_e2e.py), compiled only into Debug and <c>-p:BmbE2E=true</c> builds: a script
/// cannot tap a simulator, so the steps arrive as environment variables at launch and run through the very services the pages call
/// (FullVault, NotesService, FullSync), then the app shows the screen the check photographs. Every step prints one <c>BMB-E2E</c> line to
/// the console the simulator attaches. The test vault's throwaway password comes the same way; it is never written anywhere.
/// <list type="bullet">
/// <item><c>BMB_E2E_SELFCHECK=1</c> the measuring run (FullSelfCheck) on a scratch vault first.</item>
/// <item><c>BMB_E2E_PASSWORD</c> the test vault's password; <c>BMB_E2E_NAME</c> this node's name.</item>
/// <item><c>BMB_E2E_CREATE=1</c> a new vault; <c>BMB_E2E_JOIN=&lt;join code&gt;</c> join one (only on a phone without a vault).</item>
/// <item><c>BMB_E2E_UNLOCK=1</c> open a locked vault with the password; <c>BMB_E2E_QUICK_UNLOCK=1</c> with Face ID instead;
/// <c>BMB_E2E_QUICK_ENABLE=1</c> turn Face ID unlock on.</item>
/// <item><c>BMB_E2E_WRITE=&lt;title&gt;|&lt;folder&gt;|&lt;text&gt;</c> write a note (or change the one with that title).</item>
/// <item><c>BMB_E2E_SYNC=1</c> one sync round; <c>BMB_E2E_FIND=&lt;title&gt;</c> print that note's folder and text.</item>
/// <item><c>BMB_E2E_LOCK=1</c> lock at the end; <c>BMB_E2E_SHOW=notes|sync|settings|search:&lt;q&gt;|note:&lt;title&gt;</c> the screen to leave open.</item>
/// </list>
/// </summary>
internal static class FullE2E
{
    public static async Task<bool> RunAsync(IServiceProvider services, AppFlow flow)
    {
        var vars = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("BMB_E2E_", StringComparison.Ordinal)).ToList();
        if (vars.Count == 0) return false;
        flow.SuppressAutoPrompt = true;

        if (Get("BMB_E2E_SELFCHECK") == "1")
            await Step("selfcheck", async () =>
            {
                var lines = await FullSelfCheck.RunAsync(Path.GetTempPath(), line => Console.WriteLine("BMB-SELFCHECK " + line), Platforms.iOS.IosMemory.Read);
                return lines.Last();
            });

        await flow.StartAsync();
        var vault = services.GetRequiredService<FullVault>();
        var notes = services.GetRequiredService<NotesService>();
        var sync = services.GetRequiredService<FullSync>();
        var password = Get("BMB_E2E_PASSWORD") ?? "";
        var name = Get("BMB_E2E_NAME") ?? "iPhone (e2e)";

        if (Get("BMB_E2E_CREATE") == "1")
            await Step("create", async () =>
            {
                var key = await Task.Run(() => vault.CreateAsync(name, password));
                return $"recovery key of {key.Length} characters shown";
            });
        if (Get("BMB_E2E_JOIN") is { } code)
            await Step("join", async () =>
            {
                await Task.Run(() => vault.JoinAsync(name, code, password));
                return (await vault.IdentityAsync())?.NodeId.ToString() ?? "?";
            });
        if (Get("BMB_E2E_UNLOCK") == "1")
            await Step("unlock", async () => (await Task.Run(() => vault.UnlockAsync(password))).ToString());
        if (Get("BMB_E2E_QUICK_UNLOCK") == "1")
            await Step("quick-unlock", async () =>
            {
                // The check answers the Face ID prompt from outside (the simulator's "matching face" notification).
                var quick = services.GetRequiredService<QuickUnlock>();
                return $"enabled={quick.IsEnabled} result={await quick.UnlockAsync("Open your memory bank")} unlocked={vault.IsUnlocked}";
            });
        if (Get("BMB_E2E_QUICK_ENABLE") == "1")
            await Step("quick-enable", () =>
            {
                var quick = services.GetRequiredService<QuickUnlock>();
                quick.Enable();
                return Task.FromResult($"available={quick.IsAvailable} biometry={quick.BiometryName} enabled={quick.IsEnabled}");
            });
        if (Get("BMB_E2E_WRITE") is { } write)
            await Step("write", async () =>
            {
                var parts = write.Split('|', 3);
                var title = parts[0];
                var folder = parts.Length > 1 ? parts[1] : "/";
                var text = parts.Length > 2 ? parts[2] : "";
                var id = await notes.SaveAsync(await notes.FindByTitleAsync(title), title, folder, text);
                return id.ToString();
            });
        if (Get("BMB_E2E_SYNC") == "1")
            await Step("sync", async () =>
            {
                var round = await sync.RoundAsync();
                return $"reached={round.Reached} failed={round.Failed} applied={round.Applied} problem={round.Problem ?? "none"}";
            });
        if (Get("BMB_E2E_FIND") is { } find)
            await Step("find", async () =>
            {
                if (await notes.FindByTitleAsync(find) is not { } id) return "absent";
                var note = await notes.GetAsync(id);
                return $"present path={note!.Path} text={note.Body.Replace('\n', ' ')}";
            });
        await Step("count", async () =>
        {
            if (!vault.IsUnlocked) return "locked";
            var top = await notes.ListFolderAsync("/");
            return $"state={await vault.GetStateAsync()} top-level={top.Count}";
        });

        if (vault.IsUnlocked) flow.ShowMain();
        if (Get("BMB_E2E_SHOW") is { } show && vault.IsUnlocked) await Step("show", () => ShowAsync(services, notes, show));
        if (Get("BMB_E2E_LOCK") == "1") await Step("lock", () => { vault.Lock(); return Task.FromResult((!vault.IsUnlocked).ToString()); });
        Console.WriteLine("BMB-E2E done");
        return true;
    }

    private static async Task<string> ShowAsync(IServiceProvider services, NotesService notes, string show)
    {
        await Task.Delay(500);
        if (Shell.Current is not { } shell) return "no shell";
        var (screen, argument) = show.Contains(':') ? (show[..show.IndexOf(':')], show[(show.IndexOf(':') + 1)..]) : (show, "");
        switch (screen)
        {
            case "sync":
            case "settings":
            case "notes":
                await shell.GoToAsync("//" + screen);
                return screen;
            case "search":
                await shell.GoToAsync("//search");
                for (var i = 0; i < 20 && shell.CurrentPage is not SearchPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is not SearchPage search) return "no search page";
                search.Find(argument);
                return "search " + argument;
            case "note":
                if (await notes.FindByTitleAsync(argument) is not { } id) return "absent";
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<NotePage>().For(id));
                return "note " + argument;
            default:
                return "unknown screen " + screen;
        }
    }

    private static async Task Step(string name, Func<Task<string>> step)
    {
        try
        {
            Console.WriteLine($"BMB-E2E step={name} ok {await step()}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BMB-E2E step={name} failed {ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}");
        }
    }

    private static string? Get(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
#endif

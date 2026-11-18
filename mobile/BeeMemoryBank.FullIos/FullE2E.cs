#if DEBUG || BMB_E2E
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// The end-to-end check's hands (tools/ios-e2e/ios_full_e2e.py), compiled only into Debug and <c>-p:BmbE2E=true</c> builds: a script
/// cannot tap a simulator, so the steps arrive as environment variables at launch and run through the very services the pages call
/// (FullVault, NotesService, FullSync), then the app shows the screen the check photographs. Every step prints one <c>BMB-E2E</c> line to
/// the console the simulator attaches. The test vault's throwaway password comes the same way; it is never written anywhere.
/// <list type="bullet">
/// <item><c>BMB_E2E_SELFCHECK=1</c> the measuring run (FullSelfCheck) on a scratch vault first.</item>
/// <item><c>BMB_E2E_PASSWORD</c> the test vault's password; <c>BMB_E2E_NAME</c> this node's name;
/// <c>BMB_E2E_HINT</c> the password hint (a fixed sentence when absent);
/// <c>BMB_E2E_THEME=dark|light|system</c> the saved appearance, over the system's.</item>
/// <item><c>BMB_E2E_CREATE=1</c> a new vault; <c>BMB_E2E_JOIN=&lt;join code&gt;</c> join one (only on a phone without a vault).</item>
/// <item><c>BMB_E2E_UNLOCK=1</c> open a locked vault with the password; <c>BMB_E2E_QUICK_UNLOCK=1</c> with Face ID instead;
/// <c>BMB_E2E_QUICK_ENABLE=1</c> turn Face ID unlock on.</item>
/// <item><c>BMB_E2E_WRITE=&lt;title&gt;|&lt;folder&gt;|&lt;text&gt;</c> write a note (or change the one with that title);
/// <c>BMB_E2E_MKFOLDER=&lt;name&gt;</c> an empty top-level folder.</item>
/// <item><c>BMB_E2E_SYNC=1</c> one sync round; <c>BMB_E2E_FIND=&lt;title&gt;</c> print that note's folder and text.</item>
/// <item>parity part 1: <c>BMB_E2E_RENAME_FOLDER=old|new</c>, <c>BMB_E2E_MOVE_FOLDER=path|dest</c>, <c>BMB_E2E_DELETE_FOLDER=path</c>,
/// <c>BMB_E2E_RENAME_ARTICLE=title|new</c>, <c>BMB_E2E_MOVE_ARTICLE=title|dest</c>, <c>BMB_E2E_TAG_ADD=title|tag</c>,
/// <c>BMB_E2E_TAG_REMOVE=title|tag</c>, <c>BMB_E2E_TAG_RENAME=old|new</c>, <c>BMB_E2E_TAG_MERGE=src|dst</c>,
/// <c>BMB_E2E_COMMENT_ADD=title|text</c>, <c>BMB_E2E_COMMENT_DELETE=title|text</c> - each runs through the pages' own services and prints its result.</item>
/// <item>parity part 2: <c>BMB_E2E_MOVE_ROOT=title</c>, <c>BMB_E2E_PROTECT=title|pass[|hint]</c>, <c>BMB_E2E_UNPROTECT=title|pass</c>,
/// <c>BMB_E2E_ADD_IMAGE=title</c>, <c>BMB_E2E_FORMAT=title|heading,bold,list,link</c>, <c>BMB_E2E_EXPANDED=0|1</c> - each prints its result.</item>
/// <item><c>BMB_E2E_PEER=1</c> seed one active peer with an address typed on this iPhone (test data for the peer and sync cards, through the stores
/// those pages read); <c>BMB_E2E_PEER=0</c> removes the typed address again; <c>BMB_E2E_PEER_ADDR</c> overrides the typed address
/// (e.g. https://127.0.0.1:1, refused at once, for a fast unreachable round).</item>
/// <item><c>BMB_E2E_LOCK=1</c> lock at the end; <c>BMB_E2E_SHOW=start|welcome|create|join|join-filled|unlock|unlock-hint|unlock-wrong|notes|sync|settings|settings-bottom|settings-recovery|search:&lt;q&gt;|note:&lt;title&gt;|folder:&lt;path&gt;|actions|editor|trash|picker|tags|tagarticles:&lt;tag&gt;|rename-popup|grace-sheet|idle-sheet|peer|faceid-question|recovery|encrypt|dialog|sheet|input</c>
/// (the gates photograph with the vault locked: start holds the splash, unlock-wrong runs the page's own unlock path with a wrong password);
/// the screen to leave open (dialog / sheet / input / actions / the choice sheets leave the app's own modal open over the page, for the design checks);
/// <c>BMB_E2E_THEME_LATE=light,dark</c> switches the appearance while the app runs, after the show step.</item>
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

        // The design check photographs the saved appearance over the system's: light on a dark
        // phone and the other way round, the status bar included. Set before the first screen,
        // so the gates wear it too.
        if (Get("BMB_E2E_THEME") is { } theme) ThemeManager.Set(theme);
        // The list density for the compact/expanded row shots (the tree and search share it).
        if (Get("BMB_E2E_EXPANDED") is { } expanded) ListDensity.Expanded = expanded == "1";
        // The splash gate, photographed before the database prep replaces it.
        if (Get("BMB_E2E_SHOW") == "start")
        {
            await Task.Delay(300);
            Console.WriteLine("BMB-E2E done");
            return true;
        }
        await flow.StartAsync();
        var vault = services.GetRequiredService<FullVault>();
        var notes = services.GetRequiredService<NotesService>();
        var sync = services.GetRequiredService<FullSync>();
        var password = Get("BMB_E2E_PASSWORD") ?? "";
        var name = Get("BMB_E2E_NAME") ?? "iPhone (e2e)";
        // The e2e hook is not a person at a screen, so it uses a fixed sentence: it passes PasswordHintPolicy, does not
        // contain the test password or the node name, and the runner can override it with BMB_E2E_HINT.
        var hint = Get("BMB_E2E_HINT") ?? "the blue folder by the window";

        if (Get("BMB_E2E_CREATE") == "1")
            await Step("create", async () =>
            {
                var key = await Task.Run(() => vault.CreateAsync(name, password, hint));
                return $"recovery key of {key.Length} characters shown";
            });
        if (Get("BMB_E2E_JOIN") is { } code)
            await Step("join", async () =>
            {
                await Task.Run(() => vault.JoinAsync(name, code, password, hint));
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
            await Step("quick-enable", async () =>
            {
                var quick = services.GetRequiredService<QuickUnlock>();
                await quick.EnableAsync();
                return $"available={quick.IsAvailable} biometry={quick.BiometryName} enabled={quick.IsEnabled}";
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
        if (Get("BMB_E2E_MKFOLDER") is { } mkfolder)
            await Step("mkfolder", async () =>
            {
                foreach (var name in mkfolder.Split(','))
                    await notes.CreateFolderAsync("/", name.Trim().Trim('/'));
                return mkfolder;
            });
        if (Get("BMB_E2E_TRASH") is { } trash)
            await Step("trash", async () =>
            {
                if (await notes.FindByTitleAsync(trash) is not { } id) return "absent";
                await notes.DeleteAsync(id);
                return id.ToString();
            });
        // Parity part 1 proofs: each action really runs through the same services the pages call.
        if (Get("BMB_E2E_RENAME_FOLDER") is { } renameFolder)
            await Step("rename-folder", async () =>
            {
                var p = renameFolder.Split('|', 2);
                await notes.RenameFolderAsync(p[0], p[1]);
                return renameFolder;
            });
        if (Get("BMB_E2E_MOVE_FOLDER") is { } moveFolder)
            await Step("move-folder", async () =>
            {
                var p = moveFolder.Split('|', 2);
                await notes.EnsureFolderAsync(p[1]);
                await notes.MoveFolderAsync(p[0], p[1]);
                return moveFolder;
            });
        if (Get("BMB_E2E_DELETE_FOLDER") is { } deleteFolder)
            await Step("delete-folder", async () =>
            {
                await notes.DeleteFolderAsync(deleteFolder);
                return deleteFolder;
            });
        if (Get("BMB_E2E_RENAME_ARTICLE") is { } renameArticle)
            await Step("rename-article", async () =>
            {
                var p = renameArticle.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                await notes.RenameArticleAsync(id, p[1]);
                return renameArticle;
            });
        if (Get("BMB_E2E_MOVE_ARTICLE") is { } moveArticle)
            await Step("move-article", async () =>
            {
                var p = moveArticle.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                await notes.EnsureFolderAsync(p[1]);
                await notes.MoveArticleAsync(id, p[1]);
                return moveArticle;
            });
        if (Get("BMB_E2E_TAG_ADD") is { } tagAdd)
            await Step("tag-add", async () =>
            {
                var p = tagAdd.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                var tags = await notes.TagsOfAsync(id);
                foreach (var t in p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!tags.Contains(t)) tags.Add(t);
                await notes.SetTagsAsync(id, tags);
                return $"{p[1]} on {p[0]}: {string.Join(",", await notes.TagsOfAsync(id))}";
            });
        if (Get("BMB_E2E_TAG_REMOVE") is { } tagRemove)
            await Step("tag-remove", async () =>
            {
                var p = tagRemove.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                var tags = await notes.TagsOfAsync(id);
                foreach (var t in p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tags.Remove(t);
                await notes.SetTagsAsync(id, tags);
                return $"{p[1]} off {p[0]}: {string.Join(",", await notes.TagsOfAsync(id))}";
            });
        if (Get("BMB_E2E_TAG_RENAME") is { } tagRename)
            await Step("tag-rename", async () =>
            {
                var p = tagRename.Split('|', 2);
                await notes.RenameTagAsync(p[0], p[1]);
                return tagRename;
            });
        if (Get("BMB_E2E_TAG_MERGE") is { } tagMerge)
            await Step("tag-merge", async () =>
            {
                var p = tagMerge.Split('|', 2);
                await notes.MergeTagAsync(p[0], p[1]);
                return tagMerge;
            });
        if (Get("BMB_E2E_COMMENT_ADD") is { } commentAdd)
            await Step("comment-add", async () =>
            {
                var p = commentAdd.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                await notes.AddCommentAsync(id, p[1], null);
                return $"{(await notes.CommentsAsync(id, null)).Count} comments on {p[0]}";
            });
        if (Get("BMB_E2E_COMMENT_DELETE") is { } commentDelete)
            await Step("comment-delete", async () =>
            {
                var p = commentDelete.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                var comments = await notes.CommentsAsync(id, null);
                if (comments.FirstOrDefault(c => c.Text == p[1]) is not { } comment) return "absent";
                await notes.DeleteCommentAsync(comment.Id);
                return $"{(await notes.CommentsAsync(id, null)).Count} comments left on {p[0]}";
            });
        // Parity part 2 proofs: each write really runs through the same services the pages call.
        if (Get("BMB_E2E_MOVE_ROOT") is { } moveRoot)
            await Step("move-root", async () =>
            {
                if (await notes.FindByTitleAsync(moveRoot) is not { } id) return "absent";
                await notes.MoveArticleAsync(id, "/");
                return $"{moveRoot} path={(await notes.GetAsync(id))!.Path}";
            });
        if (Get("BMB_E2E_PROTECT") is { } protect)
            await Step("protect", async () =>
            {
                var p = protect.Split('|', 3);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                var note = await notes.GetAsync(id);
                if (note is null) return "absent";
                var tags = await notes.TagsOfAsync(id);
                var hint = p.Length > 2 ? p[2] : null;
                await notes.SaveAsync(id, note.Title, note.Path, note.Body, null, tags, new ProtectSpec(true, p[1], hint));
                return $"{p[0]} protected={(await notes.GetAsync(id))!.IsProtected}";
            });
        if (Get("BMB_E2E_UNPROTECT") is { } unprotect)
            await Step("unprotect", async () =>
            {
                var p = unprotect.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                await notes.UnprotectAsync(id, p[1]);
                return $"{p[0]} protected={(await notes.GetAsync(id))!.IsProtected}";
            });
        if (Get("BMB_E2E_ADD_IMAGE") is { } addImage)
            await Step("add-image", async () =>
            {
                if (await notes.FindByTitleAsync(addImage) is not { } id) return "absent";
                var note = await notes.GetAsync(id);
                if (note is null) return "absent";
                var bytes = Convert.FromBase64String(E2EImagePng);
                var mediaId = await notes.AddImageAsync(id, "image.png", "image/png", bytes);
                var body = EditorText.InsertImageMarkdown(note.Body, note.Body.Length, mediaId);
                var tags = await notes.TagsOfAsync(id);
                await notes.SaveAsync(id, note.Title, note.Path, body, null, tags);
                return $"{addImage} media={mediaId} hasRef={(await notes.GetAsync(id))!.Body.Contains("/api/media/")}";
            });
        if (Get("BMB_E2E_FORMAT") is { } format)
            await Step("format", async () =>
            {
                var p = format.Split('|', 2);
                if (await notes.FindByTitleAsync(p[0]) is not { } id) return "absent";
                var note = await notes.GetAsync(id);
                if (note is null) return "absent";
                var body = note.Body;
                foreach (var kind in p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var snippet = kind switch
                    {
                        "heading" => AppStrings.Get("SnippetHeading"),
                        "bold" => AppStrings.Get("SnippetBold"),
                        "list" => AppStrings.Get("SnippetList"),
                        "link" => AppStrings.Get("SnippetLink"),
                        _ => "",
                    };
                    body = EditorText.InsertSnippet(body, body.Length, snippet);
                }
                var tags = await notes.TagsOfAsync(id);
                await notes.SaveAsync(id, note.Title, note.Path, body, null, tags);
                return $"{p[0]} formatted={(await notes.GetAsync(id))!.Body.Replace('\n', ' ')}";
            });
        // Parity part 3 proofs: each write really runs through the same services the pages call.
        var security = services.GetRequiredService<SecurityOps>();
        if (Get("BMB_E2E_ENCRYPT") == "1")
            await Step("encrypt", async () =>
            {
                services.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>().MarkActivated();
                var seal = services.GetRequiredService<BeeMemoryBank.Vault.Sealed.SealMigrationService>();
                var done = await seal.RunAsync(new BeeMemoryBank.Vault.Sealed.SealOptions(AcceptNoSnapshot: true));
                return $"sealed={done.Phase}";
            });
        // The automatic encryption the app runs after every open (PhoneSealing): its outcome, or the reason it refused.
        if (Get("BMB_E2E_AUTOSEAL") == "1")
            await Step("autoseal", async () =>
            {
                var sealing = services.GetRequiredService<PhoneSealing>();
                var needed = sealing.IsNeeded;
                await sealing.SealAsync(startsNetwork: true);
                return $"needed={needed} sealed={services.GetRequiredService<BeeMemoryBank.Vault.Sealed.PhoneVaultLifecycle>().IsSealed} needed-now={sealing.IsNeeded}";
            });
        if (Get("BMB_E2E_CHANGE_PASSWORD") is { } changePwd)
            await Step("change-password", async () =>
            {
                var p = changePwd.Split('|', 3);
                var hint = p.Length > 2 ? p[2] : null;
                await security.ChangeMasterAsync(p[0], p[1], hint);
                vault.Lock();
                var oldOk = await vault.UnlockAsync(p[0]);
                var newOk = await vault.UnlockAsync(p[1]);
                var quick = services.GetRequiredService<QuickUnlock>();
                return $"old={oldOk} new={newOk} faceId={quick.IsEnabled}";
            });
        if (Get("BMB_E2E_DEV_ADD") is { } devAdd)
            await Step("dev-add", async () =>
            {
                await security.AddDevSlotAsync(devAdd);
                return $"slots={(await security.SlotsAsync()).Count}";
            });
        if (Get("BMB_E2E_DEV_DELETE") == "1")
            await Step("dev-delete", async () =>
            {
                var slots = await security.SlotsAsync();
                if (slots.FirstOrDefault(s => s.CanDelete) is not { } del) return "none-deletable";
                await security.RemoveSlotAsync(del.SlotId);
                return $"slots={(await security.SlotsAsync()).Count}";
            });
        if (Get("BMB_E2E_INVISIBLE") is { } invisible)
            await Step("invisible", () =>
            {
                flow.Invisible = invisible == "1";
                return Task.FromResult($"invisible={flow.Invisible}");
            });
        if (Get("BMB_E2E_REMOVE_DEVICE") == "1")
            await Step("remove-device", async () =>
            {
                await security.RevokePeerAsync(E2EPeerId);
                return "revoked " + E2EPeerName;
            });
        if (Get("BMB_E2E_WIPE") == "1")
            await Step("wipe", async () =>
            {
                await flow.WipeAsync();
                return $"state={await vault.GetStateAsync()}";
            });
        if (Get("BMB_E2E_WIPE_CREATE") == "1")
            await Step("wipe-create", async () =>
            {
                await vault.CreateAsync(name, password, hint);
                return $"state={await vault.GetStateAsync()}";
            });
        if (Get("BMB_E2E_PEER") is { } peerSeed)
            await Step("peer-seed", async () =>
            {
                // Test data only: one active peer and one address typed on this iPhone, written through the very
                // stores the peer and sync cards read, so the design check photographs a populated peer page.
                using var scope = services.CreateScope();
                var sp = scope.ServiceProvider;
                var store = sp.GetRequiredService<PeerChannelStore>();
                // The check may point the peer at a port nothing listens on (refused at once) instead of the LAN address.
                var address = Get("BMB_E2E_PEER_ADDR") ?? E2EPeerAddress;
                if (peerSeed == "1")
                {
                    var whitelist = sp.GetRequiredService<IWhitelistRepository>();
                    if (await whitelist.GetByNodeIdAsync(E2EPeerId) is null)
                        await whitelist.CreateAsync(new WhitelistEntry
                        {
                            NodeId = E2EPeerId,
                            DisplayName = E2EPeerName,
                            Status = "A",
                            Ed25519PublicKey = new byte[32],
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        });
                    await store.AddManualAsync(E2EPeerId, address, DateTime.UtcNow);
                    return "seeded " + E2EPeerName + " at " + address;
                }
                await store.RemoveManualAsync(E2EPeerId, address);
                return "address removed";
            });
        if (Get("BMB_E2E_SYNC") == "1")
            await Step("sync", async () =>
            {
                var round = await sync.RoundAsync();
                return $"reached={round.Reached} failed={round.Failed} unreachable={round.Unreachable} peer={round.UnreachablePeer ?? "none"} applied={round.Applied} problem={round.Problem ?? "none"}";
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
        if (Get("BMB_E2E_SHOW") is { } show && (vault.IsUnlocked || IsGate(show))) await Step("show", () => ShowAsync(services, notes, show));
        // A theme switch while the app runs (dark -> light -> dark), so the design check can see
        // the badge and the icons re-picked on RequestedThemeChanged, without a restart.
        if (Get("BMB_E2E_THEME_LATE") is { } cycle)
            await Step("theme-late", async () =>
            {
                foreach (var choice in cycle.Split(','))
                {
                    ThemeManager.Set(choice.Trim());
                    await Task.Delay(1200);
                }
                return cycle;
            });
        if (Get("BMB_E2E_LOCK") == "1") await Step("lock", () => { vault.Lock(); return Task.FromResult((!vault.IsUnlocked).ToString()); });
        Console.WriteLine("BMB-E2E done");
        return true;
    }

    /// <summary>The gates photograph with the vault locked (or absent): they are the roots AppFlow shows, not Shell pages.</summary>
    private static bool IsGate(string show) => show is "welcome" or "create" or "join" or "join-filled" or "unlock" or "unlock-hint" or "unlock-wrong";

    private static async Task<string> ShowAsync(IServiceProvider services, NotesService notes, string show)
    {
        await Task.Delay(500);
        var (screen, argument) = show.Contains(':') ? (show[..show.IndexOf(':')], show[(show.IndexOf(':') + 1)..]) : (show, "");
        if (IsGate(screen))
        {
            var flow = services.GetRequiredService<AppFlow>();
            switch (screen)
            {
                case "welcome":
                    flow.ShowWelcome();
                    return "welcome";
                case "create":
                case "join":
                case "join-filled":
                    flow.ShowWelcome();
                    await Task.Delay(300);
                    if (Application.Current?.Windows.FirstOrDefault()?.Page is NavigationPage welcomeNav)
                    {
                        if (screen == "create")
                        {
                            await welcomeNav.PushAsync(services.GetRequiredService<CreateVaultPage>());
                        }
                        else
                        {
                            var join = services.GetRequiredService<JoinPage>();
                            // A sample code, only so the design check photographs the filled field.
                            if (screen == "join-filled")
                                join.UseCode("bmb-join:?a=https://192.168.1.20:5311&k=DemoDemoDemoDemoDemoDemoDemoDemoDemoDemo");
                            await welcomeNav.PushAsync(join);
                        }
                    }
                    return screen;
                case "unlock-wrong":
                    flow.ShowUnlock();
                    await Task.Delay(300);
                    if (Application.Current?.Windows.FirstOrDefault()?.Page is NavigationPage { RootPage: UnlockPage wrong })
                        wrong.WrongPasswordForE2E();
                    return screen;
                default:
                    flow.ShowUnlock();
                    return screen;
            }
        }
        if (Shell.Current is not { } shell) return "no shell";
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
            case "note-more":
                if (await notes.FindByTitleAsync(argument) is not { } moreId) return "absent";
                await shell.GoToAsync("//notes");
                var morePage = services.GetRequiredService<NotePage>().For(moreId);
                await shell.Navigation.PushAsync(morePage);
                await Task.Delay(700);
                await morePage.ShowMoreMenuForE2EAsync();
                return "note-more " + argument;
            case "note-raw":
                if (await notes.FindByTitleAsync(argument) is not { } rawId) return "absent";
                await shell.GoToAsync("//notes");
                var rawPage = services.GetRequiredService<NotePage>().For(rawId);
                await shell.Navigation.PushAsync(rawPage);
                await Task.Delay(700);
                await rawPage.ShowRawForE2EAsync();
                return "note-raw " + argument;
            case "note-comments":
                if (await notes.FindByTitleAsync(argument) is not { } comId) return "absent";
                await shell.GoToAsync("//notes");
                var comPage = services.GetRequiredService<NotePage>().For(comId);
                await shell.Navigation.PushAsync(comPage);
                await Task.Delay(700);
                await comPage.ScrollToCommentsForE2EAsync();
                return "note-comments " + argument;
            case "editor-protect":
                if (await notes.FindByTitleAsync(argument) is not { } protId) return "absent";
                var protNote = await notes.GetAsync(protId);
                if (protNote is null) return "absent";
                var protPass = Get("BMB_E2E_EDIT_PASS") ?? "";
                var protText = NotesService.OpenProtected(protNote.Body, protPass) ?? protNote.Body;
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<EditNotePage>().Existing(protNote, protText, protPass));
                return "editor-protect " + argument;
            case "settings-bottom":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is SettingsPage settings) await settings.ScrollToEndAsync();
                return "settings-bottom";
            case "settings-recovery":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is SettingsPage recoveryScroll) await recoveryScroll.ScrollToRecoveryAsync();
                return "settings-recovery";
            case "settings-slots":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is SettingsPage slotsScroll) await slotsScroll.ScrollToSlotsAsync();
                return "settings-slots";
            case "wipe-confirm":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is SettingsPage wipePage) _ = wipePage.ShowWipeConfirmForE2EAsync();
                return "wipe-confirm";
            case "change-master":
            case "change-master-error":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                var dialog = new Controls.ChangeMasterPasswordDialog(services.GetRequiredService<SecurityOps>());
                await shell.Navigation.PushModalAsync(dialog);
                await Task.Delay(300);
                if (screen == "change-master-error") dialog.ShowErrorForE2E();
                return screen;
            case "remove-device":
                await shell.GoToAsync("//sync");
                var peerPage = services.GetRequiredService<PeerPage>().For(E2EPeerId, E2EPeerName);
                await shell.Navigation.PushAsync(peerPage);
                await Task.Delay(500);
                _ = peerPage.ShowRemoveConfirmForE2EAsync();
                return "remove-device";
            case "folder":
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<FolderPage>().At(argument));
                return "folder " + argument;
            case "actions":
                await shell.GoToAsync("//notes");
                for (var i = 0; i < 20 && shell.CurrentPage is not FolderPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is FolderPage folderPage) await folderPage.ShowFirstRowActionsAsync();
                return "actions";
            case "editor":
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<EditNotePage>().New("/"));
                return "editor";
            case "trash":
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<TrashPage>());
                return "trash";
            case "picker":
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<FolderPickerPage>());
                return "picker";
            case "editnote":
                if (await notes.FindByTitleAsync(argument) is not { } editId) return "absent";
                var editNote = await notes.GetAsync(editId);
                if (editNote is null) return "absent";
                await shell.GoToAsync("//notes");
                await shell.Navigation.PushAsync(services.GetRequiredService<EditNotePage>().Existing(editNote, editNote.Body, null));
                return "editnote " + argument;
            case "tags":
                await shell.GoToAsync("//search");
                await shell.Navigation.PushAsync(services.GetRequiredService<TagsPage>());
                return "tags";
            case "tagarticles":
                await shell.GoToAsync("//search");
                await shell.Navigation.PushAsync(services.GetRequiredService<TagArticlesPage>().For(argument));
                return "tagarticles " + argument;
            case "rename-popup":
                await shell.GoToAsync("//notes");
                for (var i = 0; i < 20 && shell.CurrentPage is not FolderPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is FolderPage renamePage)
                    _ = renamePage.ShowInputPopupAsync(AppStrings.Get("RenameTitle"), AppStrings.Get("NewNameLabel"),
                        AppStrings.Get("Save"), AppStrings.Get("Cancel"), initialValue: "Projects", maxLength: 200);
                return "rename-popup";
            case "grace-sheet":
            case "idle-sheet":
                await shell.GoToAsync("//settings");
                for (var i = 0; i < 20 && shell.CurrentPage is not SettingsPage; i++) await Task.Delay(100);
                if (shell.CurrentPage is SettingsPage settingsPage)
                    await (screen == "grace-sheet" ? settingsPage.ShowGraceSheetAsync() : settingsPage.ShowIdleSheetAsync());
                return screen;
            case "peer":
                await shell.GoToAsync("//sync");
                await shell.Navigation.PushAsync(services.GetRequiredService<PeerPage>().For(E2EPeerId, E2EPeerName));
                return "peer";
            case "faceid-question":
                // The quick-unlock question, left open for the photograph. The real offer first; on a
                // simulator without biometrics the very same question (same words, same alert) directly.
                await shell.GoToAsync("//notes");
                await Task.Delay(300);
                var offerFlow = services.GetRequiredService<AppFlow>();
                var offerQuick = services.GetRequiredService<QuickUnlock>();
                var offerAvailable = offerQuick.IsAvailable && !offerQuick.IsEnabled;
                offerFlow.SuppressAutoPrompt = false;
                if (offerAvailable) _ = offerFlow.OfferQuickUnlockAsync(shell.CurrentPage);
                else _ = offerFlow.AskQuickUnlockAsync(shell.CurrentPage);
                return $"available={offerQuick.IsAvailable} enabled={offerQuick.IsEnabled} real={offerAvailable}";
            case "recovery":
                await shell.GoToAsync("//settings");
                var recovery = services.GetRequiredService<RecoveryKeyPage>();
                // The same shape the service produces: 32 bytes, plain Base64, 44 characters.
                recovery.Show(Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()), firstTime: false);
                await shell.Navigation.PushModalAsync(new NavigationPage(recovery));
                return "recovery";
            case "dialog":
                // The app's own dialog over the tree, left open for the photograph.
                await shell.GoToAsync("//notes");
                await Task.Delay(300);
                _ = AppDialog.ConfirmAsync(shell.CurrentPage, AppStrings.Get("NewFolder"), AppStrings.Get("FolderNameLabel"),
                    AppStrings.Get("Create"), AppStrings.Get("Cancel"));
                return "dialog";
            case "sheet":
                // The appearance choices as a bottom sheet; a tap really sets the theme.
                await shell.GoToAsync("//settings");
                await Task.Delay(300);
                var choices = new VerticalStackLayout { Spacing = 8 };
                foreach (var (key, choice) in new[] { ("ThemeDark", ThemeManager.Dark), ("ThemeLight", ThemeManager.Light), ("ThemeSystem", ThemeManager.System) })
                {
                    var button = new Button { Text = AppStrings.Get(key), HorizontalOptions = LayoutOptions.Fill };
                    if (Application.Current?.Resources.TryGetValue("SecondaryButton", out var st) == true && st is Style style) button.Style = style;
                    var picked = choice;
                    button.Clicked += (_, _) => ThemeManager.Set(picked);
                    choices.Children.Add(button);
                }
                _ = BottomSheet.ShowAsync(shell.CurrentPage, choices, AppStrings.Get("Appearance"));
                return "sheet";
            case "input":
                // The app's input popup (the new-folder field), left open for the photograph.
                await shell.GoToAsync("//notes");
                await Task.Delay(300);
                if (shell.CurrentPage is ContentPage page)
                    _ = page.ShowInputPopupAsync(AppStrings.Get("NewFolder"), AppStrings.Get("NewFolderParentRoot"),
                        AppStrings.Get("Create"), AppStrings.Get("Cancel"), AppStrings.Get("FolderNamePlaceholder"));
                return "input";
            default:
                return "unknown screen " + screen;
        }
    }

    /// <summary>The seeded peer of <c>BMB_E2E_PEER=1</c>: a fixed id and a LAN address nothing answers at, so the cards show a typed address that never worked.</summary>
    private static readonly Guid E2EPeerId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private const string E2EPeerName = "MacBook (e2e)";
    private const string E2EPeerAddress = "https://192.168.1.20:5311";

    // A tiny amber PNG (160x100) for the add-image proof, so the note shows a real picture.
    private const string E2EImagePng = "iVBORw0KGgoAAAANSUhEUgAAAKAAAABkCAIAAACO1KzYAAAAwUlEQVR42u3RAQkAMAgAQaMuj8nWaqZQmBx8gr/IE1qcBYAFWIAFWDPA7+rXAAMGDFiABViABViAAQMGLMACLMACLMCAAQMWYAEWYAEWYAEGDBiwAAuwAAuwAAMGDFiABViABViAAQMGLMACLMACLMACDBgwYAEWYAEWYAEGDBiwAAuwAAuwAAMGDNgmwAIswAIswAIMGDBgARZgARZgAQYMGLAAC7AAC7AACzBgwIAFWIAFWF3A2pQFgAVYgAVYDRWfOhJMJhetFQAAAABJRU5ErkJggg==";

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

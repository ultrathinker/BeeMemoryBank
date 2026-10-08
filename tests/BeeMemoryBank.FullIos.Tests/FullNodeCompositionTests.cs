using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The app's full-node composition (FullNodeServices) does a whole round on any OS: the self-check the app runs on the simulator and the
/// phone - schema, FTS5 and WAL, a new vault, unlock, a recovery key, notes, both searches, signatures, lock - passes here too.
/// </summary>
public class FullNodeCompositionTests
{
    [Fact]
    public async Task TheSelfCheck_PassesOnTheAppsOwnComposition()
    {
        var lines = await FullSelfCheck.RunAsync(TestFolders.New("selfcheck"));

        var values = lines.Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        values["sqlite_fts5"].Should().Be("True");
        values["sqlite_journal"].Should().Be("wal");
        values["unlock_ok"].Should().Be("True");
        values["recovery_key_chars"].Should().Be("44");
        values["search_title_hits"].Should().Be("1");
        values["search_body_hits"].Should().Be("1");
        values["locked"].Should().Be("True");
        values["result"].Should().Be("ok");
    }
}

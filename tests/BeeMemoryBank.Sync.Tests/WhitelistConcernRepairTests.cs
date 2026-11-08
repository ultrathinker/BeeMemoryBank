using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The startup repair on rows an older build left, now that each concern of a row has its own version (migration 045 starts every concern at
/// the row's one version). Where the node's own log shows that the row's version came from an event that did not write a concern, and holds the
/// row's whole history, the concern takes the version of the event that wrote it, and the revoke or demotion an older build dropped for a crossing
/// update applies. Where the log does not hold the whole history (a compaction, a join, a restore), or does not show what the event lost to,
/// nothing is revoked or demoted, and nothing is ever added or promoted.
/// </summary>
public class WhitelistConcernRepairTests : IAsyncLifetime
{
    private SyncTestFixture _author = null!;   // a superadmin peer that signs the events
    private LoggedFixture _node = null!;       // the node whose database is repaired

    private static readonly Guid Phone = Guid.Parse("eeeeeeee-0000-4000-8000-00000000c101");
    private readonly byte[] _phoneKey = Ed25519Signer.GenerateKeyPair().publicKey;

    public async Task InitializeAsync()
    {
        _author = await NodeAsync(new ConcreteFixture(), "Author");
        _node = await NodeAsync(new LoggedFixture(), "Repaired");
        var author = (await _author.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow.AddDays(-30);
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = author.NodeId, DisplayName = author.DisplayName, Ed25519PublicKey = author.Ed25519PublicKey,
            Status = WhitelistStatuses.Active, IsSuperadmin = true, CreatedAt = now, UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _author.DisposeAsync();
        await _node.DisposeAsync();
    }

    /// <summary>
    /// Case 1 as an older build left it: the add, then the rename of a node that had not seen the revoke, then the revoke, which lost to the
    /// rename's version. The log holds all three and nothing was cut from it: the phone is revoked at the revoke's version, the rename stays,
    /// and the change is a warning. A second run changes nothing.
    /// </summary>
    [Fact]
    public async Task ARevokeAnOlderBuildDroppedForACrossingUpdate_IsApplied_WhenTheLogHoldsTheWholeHistory()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, rename, revoke);   // the order they arrived in: the rename before the revoke
        await OldRowAsync(Phone, "Phone renamed", rename, isSuperadmin: true);

        var logger = new ListLogger();
        (await WhitelistOrderRepair.RunAsync(Scopes(), logger)).Should().Be(1);

        var row = (await RowAsync())!;
        (row.Status, row.VersionOf(WhitelistConcerns.Membership), row.DisplayName).Should().Be((WhitelistStatuses.Revoked, Version(revoke), "Phone renamed"));
        row.VersionOf(WhitelistConcerns.Name).Should().Be(Version(rename));
        logger.Lines.Should().Contain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Text.Contains(Phone.ToString()) && l.Text.Contains("-> R at"));

        (await WhitelistOrderRepair.RunAsync(Scopes(), logger)).Should().Be(0, "it runs once (the marker)");
        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty("and a second pass would change nothing anyway");
    }

    /// <summary>Case 2 as an older build left it: the demotion lost to the version of a newer rename that arrived first. It applies now.</summary>
    [Fact]
    public async Task ADemotionAnOlderBuildDroppedForACrossingRename_IsApplied_WhenTheLogHoldsTheWholeHistory()
    {
        var add = await AuthorAddAsync("Phone", isSuperadmin: true);
        var demote = await AuthorUpdateAsync(isSuperadmin: false);
        var rename = await AuthorUpdateAsync(displayName: "Phone renamed");
        await LogAsAppliedAsync(add, rename, demote);
        await OldRowAsync(Phone, "Phone renamed", rename, isSuperadmin: true);

        var changes = await _node.EventApplier.RepairWhitelistOrderAsync();

        var row = (await RowAsync())!;
        (row.Status, row.IsSuperadmin, row.DisplayName).Should().Be((WhitelistStatuses.Active, false, "Phone renamed"));
        row.VersionOf(WhitelistConcerns.Superadmin).Should().Be(Version(demote));
        changes.Should().ContainSingle(c => c.NodeId == Phone && !c.Membership && c.Before.Contains("superadmin") && !c.After.Contains("superadmin"));
    }

    /// <summary>A rename and an address change that crossed: the field the older build dropped is applied too (neither raises trust).</summary>
    [Fact]
    public async Task AFieldAnOlderBuildDroppedForACrossingUpdate_IsApplied()
    {
        var add = await AuthorAddAsync("Phone", address: "https://phone.example");
        var move = await AuthorUpdateAsync(address: "https://moved.example");
        var rename = await AuthorUpdateAsync(displayName: "Phone renamed");
        await LogAsAppliedAsync(add, rename, move);
        await OldRowAsync(Phone, "Phone renamed", rename, address: "https://phone.example");

        await _node.EventApplier.RepairWhitelistOrderAsync();

        (await RowAsync())!.ApiAddress.Should().Be("https://moved.example");
    }

    /// <summary>
    /// The same database after a compaction of this node's log that came after the row was written: what was compacted may hold a re-add newer
    /// than the revoke, so the row keeps its version, and the admin is told.
    /// </summary>
    [Fact]
    public async Task NothingIsRevoked_WhenACompactionCutTheRowsHistory()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, rename, revoke);
        await OldRowAsync(Phone, "Phone renamed", rename, createdAt: DateTime.UtcNow.AddDays(-2));
        await RecordCompactionAsync(DateTime.UtcNow.AddDays(-1));

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();

        (await RowAsync())!.Status.Should().Be(WhitelistStatuses.Active, "the log does not hold the whole history of this row");
        _node.Log.Lines.Should().Contain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Text.Contains("revoke") && l.Text.Contains("Admin > Devices"));
    }

    /// <summary>A compaction before the row was written cut nothing of its history: the repair goes ahead.</summary>
    [Fact]
    public async Task ACompactionBeforeTheRowWasWritten_DoesNotHoldTheRepairBack()
    {
        await RecordCompactionAsync(DateTime.UtcNow.AddDays(-3));
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, rename, revoke);
        await OldRowAsync(Phone, "Phone renamed", rename, createdAt: DateTime.UtcNow.AddDays(-2));

        await _node.EventApplier.RepairWhitelistOrderAsync();

        (await RowAsync())!.Status.Should().Be(WhitelistStatuses.Revoked);
    }

    /// <summary>A row a join wrote (the join raised the clock floor after it): the host's answer may stand on a re-add this log never held.</summary>
    [Fact]
    public async Task NothingIsRevoked_OnARowAJoinWrote()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, rename, revoke);
        await OldRowAsync(Phone, "Phone renamed", rename, createdAt: DateTime.UtcNow.AddDays(-2));
        // The join raised the clock floor (SnapshotJoin.CompleteAsync), after it wrote the host's peers.
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, '1', @at)",
                new { k = LamportFloor.Key, at = DateTime.UtcNow.AddDays(-1).ToString("O") });

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await RowAsync())!.Status.Should().Be(WhitelistStatuses.Active);
    }

    /// <summary>
    /// The revoke arrived before the update, so the older build revoked the row then, and an active row now means something this log does not
    /// hold re-activated it (a crash between the row and the log, an event lost with a restore): nothing is revoked.
    /// </summary>
    [Fact]
    public async Task NothingIsRevoked_WhenTheLogDoesNotShowWhatTheRevokeLostTo()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, revoke, rename);
        await OldRowAsync(Phone, "Phone renamed", rename);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        (await RowAsync())!.Status.Should().Be(WhitelistStatuses.Active);
    }

    /// <summary>The event that gave the row its version is not in the log (a row a join or a restore wrote, or compacted): the row is all the evidence.</summary>
    [Fact]
    public async Task NothingMoves_WhenTheEventThatGaveTheRowItsVersionIsNotInTheLog()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await LogAsAppliedAsync(add, revoke);
        await OldRowAsync(Phone, "Phone renamed", rename);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        var row = (await RowAsync())!;
        row.Status.Should().Be(WhitelistStatuses.Active);
        row.VersionOf(WhitelistConcerns.Membership).Should().Be(Version(rename));
        foreach (var field in new[] { WhitelistConcerns.Superadmin, WhitelistConcerns.Name, WhitelistConcerns.Address, WhitelistConcerns.Tls })
            row.IsUndated(field).Should().BeTrue($"{field}: the log names no writer of it");
    }

    /// <summary>
    /// The repair never promotes: a promotion an older build dropped for a crossing rename stays dropped (the row's clear flag keeps the row's
    /// version), whatever the log suggests. Promote the peer again if it should be one.
    /// </summary>
    [Fact]
    public async Task APromotionAnOlderBuildDropped_IsNeverApplied()
    {
        var add = await AuthorAddAsync("Phone", isSuperadmin: false);
        var promote = await AuthorUpdateAsync(isSuperadmin: true);
        var rename = await AuthorUpdateAsync(displayName: "Phone renamed");
        await LogAsAppliedAsync(add, rename, promote);
        await OldRowAsync(Phone, "Phone renamed", rename, isSuperadmin: false);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        var row = (await RowAsync())!;
        row.IsSuperadmin.Should().BeFalse();
        (row.VersionOf(WhitelistConcerns.Superadmin), row.IsUndated(WhitelistConcerns.Superadmin)).Should().Be((Version(rename), true),
            "a clear flag is never moved back (that could only let a promotion in); it stays undated at the row's version");
        row.VersionOf(WhitelistConcerns.Name).Should().Be(Version(rename), "the event that gave the row its version wrote the name: dated");
    }

    /// <summary>A row this version wrote has its versions per concern already: the repair changes nothing on it.</summary>
    [Fact]
    public async Task ARowThisVersionWrote_IsLeftAsItIs()
    {
        var (add, revoke, rename) = await CrossedRevokeAsync();
        await _node.ApplyFromAsync(_author, add);
        await _node.ApplyFromAsync(_author, rename);
        var before = (await RowAsync())!;
        before.Status.Should().Be(WhitelistStatuses.Active);

        (await _node.EventApplier.RepairWhitelistOrderAsync()).Should().BeEmpty();
        var after = (await RowAsync())!;
        foreach (var concern in WhitelistConcernSet.Each) after.VersionOf(concern).Should().Be(before.VersionOf(concern), concern.ToString());

        await _node.ApplyFromAsync(_author, revoke);
        (await RowAsync())!.Status.Should().Be(WhitelistStatuses.Revoked, "and the revoke that crosses the rename applies on arrival");
    }

    /// <summary>
    /// A row a join wrote under an older build (no superadmin, from the host), raised later by a rename to its version. A second add for the
    /// peer, newer than that rename, is in the log (the host's demotion that made the flag clear is not). The flag's writer is not known, so it
    /// is undated, and an add that is not a re-add does not change an undated field: the repair does not make the peer a superadmin, and nor
    /// does the add arriving again.
    /// </summary>
    [Fact]
    public async Task ALaterAddInTheLog_DoesNotPromoteARowAJoinWrote()
    {
        var rename = await AuthorUpdateAsync(displayName: "Joined peer renamed");
        var add = await AuthorAddAsync("Joined peer", isSuperadmin: true, address: "https://other.example");
        await LogAsAppliedAsync(rename, add);
        await OldRowAsync(Phone, "Joined peer renamed", rename, isSuperadmin: false, address: "https://host-knows.example", createdAt: DateTime.UtcNow.AddDays(-2));
        using (var conn = _node.Factory.CreateConnection())
            await conn.ExecuteAsync("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, '1', @at)",
                new { k = LamportFloor.Key, at = DateTime.UtcNow.AddDays(-1).ToString("O") });

        await _node.EventApplier.RepairWhitelistOrderAsync();
        await _node.ApplyFromAsync(_author, add);

        var row = (await RowAsync())!;
        (row.IsSuperadmin, row.ApiAddress).Should().Be((false, "https://host-knows.example"), "undated: no add but a re-add changes them");
        row.DisplayName.Should().Be("Joined peer", "the rename in the log dated the name, so the add newer than it writes it, by version");
        row.VersionOf(WhitelistConcerns.Membership).Should().Be(Version(add), "the add still dates membership");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The add, the revoke, and a rename newer than the revoke made without it (the author's clock is moved past the revoke).</summary>
    private async Task<(SyncEvent Add, SyncEvent Revoke, SyncEvent Rename)> CrossedRevokeAsync()
    {
        var add = await AuthorAddAsync("Phone", isSuperadmin: true);
        await _author.EventLogger.LogWhitelistRevokeAsync(Phone);
        var revoke = await LastAsync(EventTypes.WhitelistRevoke);
        var rename = await AuthorUpdateAsync(displayName: "Phone renamed");
        rename.LamportTs.Should().BeGreaterThan(revoke.LamportTs);
        return (add, revoke, rename);
    }

    private async Task<SyncEvent> AuthorAddAsync(string name, bool isSuperadmin = false, string? address = null)
    {
        await _author.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = Phone, DisplayName = name, Ed25519PublicKey = _phoneKey, IsSuperadmin = isSuperadmin, ApiAddress = address, Status = WhitelistStatuses.Active
        });
        return await LastAsync(EventTypes.WhitelistAdd);
    }

    private async Task<SyncEvent> AuthorUpdateAsync(string? displayName = null, bool? isSuperadmin = null, string? address = null)
    {
        await _author.EventLogger.LogWhitelistUpdateAsync(Phone, address, displayName, isSuperadmin);
        return await LastAsync(EventTypes.WhitelistUpdate);
    }

    private async Task<SyncEvent> LastAsync(string type) => (await _author.EventLogRepo.GetAfterSequenceAsync(0, 100_000)).Last(e => e.EventType == type);

    private static RowVersion Version(SyncEvent evt) => new(evt.LamportTs, evt.NodeId);

    private Task<WhitelistEntry?> RowAsync() => _node.WhitelistRepo.GetByNodeIdAsync(Phone, includeDeleted: true);

    /// <summary>The events in the repaired node's log, as an applier stores them (applied), in the order given (the order they arrived in).</summary>
    private async Task LogAsAppliedAsync(params SyncEvent[] events)
    {
        foreach (var evt in events) await _node.EventLogRepo.AppendIfNotExistsAsync(evt);
    }

    /// <summary>
    /// The row an older build left, as migration 045 turns it: one version (that of <paramref name="version"/>) on every concern, the fields
    /// undated (their writer is not known).
    /// </summary>
    private async Task OldRowAsync(Guid peer, string name, SyncEvent version, bool isSuperadmin = false, string? address = null, DateTime? createdAt = null)
    {
        var at = createdAt ?? DateTime.UtcNow;
        var row = new WhitelistEntry
        {
            NodeId = peer, DisplayName = name, Ed25519PublicKey = _phoneKey, Status = WhitelistStatuses.Active, IsSuperadmin = isSuperadmin,
            ApiAddress = address, CreatedAt = at, UpdatedAt = at
        };
        row.SetAllVersions(Version(version));
        row.MarkUndated(WhitelistEntry.Fields);
        await _node.WhitelistRepo.CreateAsync(row);
    }

    private async Task RecordCompactionAsync(DateTime at)
    {
        using var conn = _node.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason) VALUES (@at, 0, 0, 0, 'test')",
            new { at = at.ToString("o") });
    }

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddSingleton<IDbConnectionFactory>(_node.Factory)
        .AddSingleton(_node.NodeRepo)
        .AddSingleton(_node.EventApplier)
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    private static async Task<T> NodeAsync<T>(T node, string name) where T : SyncTestFixture
    {
        await node.InitializeAsync();
        await node.InitService.InitializeAsync("admin", name, "password" + name);
        await node.Session.UnlockAsync("password" + name);
        return node;
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    private sealed class TypedLogger(ListLogger inner) : Microsoft.Extensions.Logging.ILogger<EventApplier>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class ConcreteFixture : SyncTestFixture { }

    /// <summary>A node whose applier logs into a list the test reads.</summary>
    private sealed class LoggedFixture : SyncTestFixture
    {
        public ListLogger Log { get; } = new();
        protected override Microsoft.Extensions.Logging.ILogger<EventApplier> CreateApplierLogger() => new TypedLogger(Log);
    }
}

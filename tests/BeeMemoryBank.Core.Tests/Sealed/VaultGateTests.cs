using System.Diagnostics;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// The sealed database's lifecycle on a real sealed node (docs/design/step-b-spec.md 5.1-5.4, 5.7, 5.10; red tests 15.3.2 first half, 15.3.5,
/// 15.3.6 and 15.3.16): the factories refuse while it is closed, a door opens it, the keys exist only while it is open and are gone, with the
/// pools flushed, before the state is published as Closed.
/// </summary>
public sealed class VaultGateTests : IAsyncLifetime
{
    private SealedGateNode _node = null!;

    public async Task InitializeAsync() => _node = await SealedGateNode.CreateAsync(drainTimeout: TimeSpan.FromSeconds(2));

    public async Task DisposeAsync() => await _node.DisposeAsync();

    private VaultGate Gate => _node.Gate;

    private static string Sha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // ---- closed ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_sealed_gate_starts_closed_and_no_factory_makes_a_connection_or_touches_the_file()
    {
        var before = new[] { _node.VaultPath, _node.ChatPath, _node.BootPath }.ToDictionary(p => p, Sha);

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsSealed.Should().BeTrue();
        Gate.IsUsable.Should().BeFalse();
        Gate.IsOpen.Should().BeFalse();
        Gate.Mode.Should().Be(VaultStoreMode.Sealed);
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>();
        ((Action)(() => _node.Chat.CreateConnection())).Should().Throw<VaultSealedException>();
        ((Action)(() => _node.Vault.OpenFile(_node.VaultPath))).Should().Throw<VaultSealedException>();
        ((Action)(() => Gate.GetPageKey(DbPurposes.Vault))).Should().Throw<VaultSealedException>();
        ((Action)(() => Gate.GetDatabaseKey())).Should().Throw<VaultSealedException>();

        foreach (var (path, hash) in before) Sha(path).Should().Be(hash, "a closed gate reads and writes nothing: " + Path.GetFileName(path));
    }

    [Fact]
    public async Task A_plain_gate_answers_as_if_the_database_were_always_open()
    {
        await using var plain = await SealedGateNode.CreateAsync(mode: VaultStoreMode.Plain);
        var gate = plain.Gate;

        gate.State.Should().Be(VaultState.Plain);
        gate.IsSealed.Should().BeFalse();
        gate.IsUsable.Should().BeTrue();
        (await gate.OpenAsync(plain.MasterDek)).Should().Be(VaultOpenResult.Done);
        await gate.WaitUsableAsync(CancellationToken.None);
        await gate.CloseAsync("lock");
        gate.State.Should().Be(VaultState.Plain);
        (await plain.CountUsersAsync()).Should().Be(1, "a plain database stays readable after a lock");
        await ((Func<Task>)(() => gate.HoldClosedAsync("copy"))).Should().ThrowAsync<InvalidOperationException>();
        plain.Relay.Calls.Should().BeEmpty();
    }

    // ---- open ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_master_dek_opens_the_database_and_the_gate_stays_Opening_until_the_unlock_accepts_the_door()
    {
        _node.Steps.Add(new RecordingStep(100, _node.StepLog));
        _node.Steps.Add(new RecordingStep(50, _node.StepLog));

        var result = await Gate.OpenAsync(_node.MasterDek);

        result.Should().Be(VaultOpenResult.Done);
        Gate.State.Should().Be(VaultState.Opening);
        Gate.IsUsable.Should().BeFalse("requests still see Closed while the door is being checked");
        Gate.IsOpen.Should().BeTrue();
        Gate.OpenGeneration.Should().Be(1);
        Gate.DbkGeneration.Should().Be(1);
        (await _node.CountUsersAsync()).Should().Be(1, "the keyed factory reads the encrypted vault");
        using (var c = _node.Chat.CreateConnection()) c.ExecuteScalar<string>("SELECT title FROM chat_probe").Should().Be("gate-probe");
        _node.StepLog.Should().Equal("50:first", "100:first");
        _node.Relay.Calls.Should().Equal("opened");
        Gate.GetDatabaseKey().Should().Equal(_node.DatabaseKey);
        Gate.GetPageKey(DbPurposes.Vault).Should().Equal(_node.VaultPageKey);
        Gate.GetPageKey(DbPurposes.Chat).Should().Equal(_node.ChatPageKey);
    }

    [Fact]
    public async Task Complete_open_moves_to_Importing_before_its_first_await_then_imports_rewrites_and_opens()
    {
        await Gate.OpenAsync(_node.MasterDek);
        _node.Relay.ImportDelay = TimeSpan.FromMilliseconds(300);
        var rewritesBefore = _node.Spy.Rewrites;
        var states = new List<VaultState>();
        Gate.StateChanged += states.Add;

        var completing = Gate.CompleteOpenAsync();

        Gate.State.Should().Be(VaultState.Importing, "the state changes synchronously: a caller that does not await still serves requests");
        Gate.IsUsable.Should().BeTrue();
        completing.IsCompleted.Should().BeFalse("the import is still running");
        await completing;

        Gate.State.Should().Be(VaultState.Open);
        _node.Relay.Calls.Should().Equal("opened", "import-start", "import-end");
        _node.Relay.StateAtImport.Should().Be(VaultState.Importing, "the import runs while the state is Importing");
        _node.Spy.Rewrites.Should().Be(rewritesBefore + 1, "an unlock ends with a rewrite of the boot store");
        states.Should().Equal(VaultState.Importing, VaultState.Open);
        await Gate.CompleteOpenAsync();   // idempotent
        Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task Complete_open_on_a_closed_gate_is_refused()
    {
        var act = () => Gate.CompleteOpenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        Gate.State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task A_dek_without_a_wrapper_is_refused_and_nothing_opens()
    {
        var result = await Gate.OpenAsync(RandomNumberGenerator.GetBytes(32));

        result.Should().Be(VaultOpenResult.Refused(VaultOpenRefusals.NoWrapper));
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        _node.Relay.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_boot_store_of_another_database_is_refused_key_mismatch_loudly_and_the_node_stays_closed()
    {
        // A wrapper that unwraps (the boot store is intact) to a database key that is not this database's.
        var otherDbk = RandomNumberGenerator.GetBytes(32);
        var row = DatabaseKeyCrypto.Wrap(otherDbk, _node.MasterDek, _node.NodeId, 1);
        using (var c = _node.Boot.Connections.Open())
            c.Execute("UPDATE tbl_boot_db_key SET wrapped = @w, iv = @iv", new { w = row.Wrapped, iv = row.Iv });

        var result = await Gate.OpenAsync(_node.MasterDek);

        result.Should().Be(VaultOpenResult.Refused(VaultOpenRefusals.KeyMismatch));
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse("no key stays in memory after a refusal");
        _node.GateLog.Lines.Should().Contain(l => l.Contains("does not belong to this database"));
        _node.GateLog.Lines.Should().NotContain(l => l.Contains(Convert.ToHexString(otherDbk), StringComparison.OrdinalIgnoreCase));
        _node.Relay.Calls.Should().BeEmpty("the relay was never told the database opened, and is not told it closed");
    }

    [Fact]
    public async Task Open_steps_run_in_order_and_the_first_open_flag_is_true_only_once()
    {
        _node.Steps.Add(new RecordingStep(300, _node.StepLog));
        _node.Steps.Add(new RecordingStep(100, _node.StepLog));
        _node.Steps.Add(new RecordingStep(200, _node.StepLog));

        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        await Gate.CloseAsync("lock");
        await Gate.OpenAsync(_node.MasterDek);

        _node.StepLog.Should().Equal("100:first", "200:first", "300:first", "100:again", "200:again", "300:again");
    }

    [Fact]
    public async Task A_failing_open_step_closes_the_database_clears_every_key_and_the_failure_reaches_the_caller()
    {
        _node.Steps.Add(new RecordingStep(100, _node.StepLog, _ => throw new InvalidOperationException("migration failed")));

        var act = () => Gate.OpenAsync(_node.MasterDek);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("migration failed");
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>();
    }

    [Fact]
    public async Task Waiting_for_the_database_completes_when_it_is_usable_and_honours_the_token()
    {
        var waiting = Gate.WaitUsableAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("a closed node is waited for");

        using var cts = new CancellationTokenSource(50);
        await ((Func<Task>)(() => Gate.WaitUsableAsync(cts.Token))).Should().ThrowAsync<OperationCanceledException>();

        await Gate.OpenAsync(_node.MasterDek);
        waiting.IsCompleted.Should().BeFalse("Opening is not usable");
        var completing = Gate.CompleteOpenAsync();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await completing;
    }

    // ---- close -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_clears_every_key_flushes_the_pools_and_publishes_Closed_only_then()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        using (var c = _node.Vault.CreateConnection()) c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_user");
        using (var c = _node.Chat.CreateConnection()) c.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_probe");

        // A second thread samples the gate for the whole close: it must never see Closed while a key is still in memory.
        var violations = new List<string>();
        var stop = false;
        var sampler = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var state = Gate.State;
                var open = Gate.IsOpen;
                if (state == VaultState.Closed && open) violations.Add("Closed while IsOpen");
                if (state == VaultState.Closed)
                {
                    try { Gate.GetPageKey(DbPurposes.Vault); violations.Add("a page key was handed out in state Closed"); }
                    catch (VaultSealedException) { }
                }
            }
        });

        await Gate.CloseAsync("lock");
        await Task.Delay(50);
        Volatile.Write(ref stop, true);
        await sampler;

        violations.Should().BeEmpty();
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>();
        ((Action)(() => _node.Chat.CreateConnection())).Should().Throw<VaultSealedException>();
        _node.Relay.Calls.Should().Equal("opened", "import-start", "import-end", "closing");

        // The three key buffers the gate held (database key, vault page key, chat page key) were zeroed.
        _node.Cleared.Count(b => b.Length == 32 && b.All(x => x == 0)).Should().BeGreaterThanOrEqualTo(3);

        // No pooled handle is left: the files can be renamed (Windows refuses while a handle is open), and the WAL was folded back.
        foreach (var path in new[] { _node.VaultPath, _node.ChatPath })
        {
            var moved = path + ".moved";
            File.Move(path, moved);
            File.Move(moved, path);
            (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0).Should().Be(0, "the close checkpoints the WAL: " + Path.GetFileName(path));
        }
    }

    [Fact]
    public async Task A_locked_node_holds_no_key_and_no_key_bearing_string_in_any_field_cache_or_pool()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        using (var c = _node.Vault.CreateConnection()) c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_user");
        using (var c = _node.Chat.CreateConnection()) c.ExecuteScalar<long>("SELECT COUNT(*) FROM chat_probe");
        var held = new object[] { _node, _node.DatabaseKey, _node.VaultPageKey, _node.ChatPageKey, _node.MasterDek };   // the fixture keeps its own copies for the asserts
        var control = KeyGraphWalker.Find([("gate", Gate)], _node.DatabaseKey, doNotEnter: held);
        control.Should().NotBeEmpty("the control: the walker does find the database key while the gate holds it");

        await Gate.CloseAsync("lock");

        var roots = new (string, object?)[] { ("gate", Gate), ("vault", _node.Vault), ("chat", _node.Chat), ("providerPool", KeyGraphWalker.ProviderRegistry()) };
        foreach (var (name, key) in new[] { ("database key", _node.DatabaseKey), ("vault page key", _node.VaultPageKey), ("chat page key", _node.ChatPageKey), ("master DEK", _node.MasterDek) })
            KeyGraphWalker.Find(roots, key, doNotEnter: held).Should().BeEmpty(name + " must not be reachable from a closed gate, its factories or the provider's pool");
    }

    [Fact]
    public async Task A_connection_still_in_use_is_waited_for_up_to_the_limit_then_the_close_goes_on_and_the_straggler_cannot_be_reused()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        var straggler = _node.Vault.CreateConnection();
        var timer = Stopwatch.StartNew();

        await Gate.CloseAsync("lock");

        timer.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5), "the close waits for the open connection").And.BeLessThan(TimeSpan.FromSeconds(20));
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse("the keys are gone although a connection was still in use");
        _node.GateLog.Lines.Should().Contain(l => l.StartsWith("Warning") && l.Contains("still in use"));
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>("no new connection is handed out");
        straggler.Dispose();   // its owner returns it: closed, not pooled
        ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>();
    }

    [Fact]
    public async Task A_refused_close_does_not_rewrite_the_boot_store_but_a_normal_one_does_and_both_tell_the_relay()
    {
        await Gate.OpenAsync(_node.MasterDek);
        var rewrites = _node.Spy.Rewrites;
        await Gate.CloseAsync("refused");
        _node.Spy.Rewrites.Should().Be(rewrites, "a refused door must not rewrite the boot store from a database that may be the older of the pair");
        _node.Relay.Calls.Should().Equal("opened", "closing");

        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        var afterUnlock = _node.Spy.Rewrites;
        await Gate.CloseAsync("lock");

        _node.Spy.Rewrites.Should().Be(afterUnlock + 1);
        Gate.State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task Closing_a_closed_gate_does_nothing_and_shutdown_checkpoints_the_boot_store()
    {
        await Gate.CloseAsync("lock");
        _node.Relay.Calls.Should().BeEmpty();

        using (var c = _node.Boot.Connections.Open()) c.Execute("INSERT OR REPLACE INTO tbl_boot_meta (key, value) VALUES ('probe', '1')");
        await Gate.CloseAsync(VaultGate.ShutdownReason);

        (File.Exists(_node.BootPath + "-wal") ? new FileInfo(_node.BootPath + "-wal").Length : 0).Should().Be(0);
    }

    // ---- temporary opens (5.7) --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_temporary_open_opens_without_unlocking_and_closes_again_on_dispose()
    {
        var lease = await Gate.OpenTemporarilyAsync(_node.MasterDek);

        Gate.State.Should().Be(VaultState.Opening, "requests still see Closed during a temporary open");
        Gate.IsUsable.Should().BeFalse();
        (await _node.CountUsersAsync()).Should().Be(1);

        await lease.DisposeAsync();

        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
        await lease.DisposeAsync();   // a second dispose is harmless
    }

    [Fact]
    public async Task A_temporary_open_with_the_wrong_key_throws_and_leaves_the_gate_closed()
    {
        var act = () => Gate.OpenTemporarilyAsync(RandomNumberGenerator.GetBytes(32));

        await act.Should().ThrowAsync<VaultSealedException>();
        Gate.State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task A_temporary_open_of_a_database_the_session_has_open_changes_nothing_and_closes_nothing()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();

        var lease = await Gate.OpenTemporarilyAsync(_node.MasterDek);
        await lease.DisposeAsync();

        Gate.State.Should().Be(VaultState.Open);
    }

    [Fact]
    public async Task A_temporary_lease_does_not_close_a_database_an_unlock_is_using()
    {
        var lease = await Gate.OpenTemporarilyAsync(_node.MasterDek);
        (await Gate.OpenAsync(_node.MasterDek)).Should().Be(VaultOpenResult.Done);   // the unlock claims the open database

        await lease.DisposeAsync();

        Gate.State.Should().Be(VaultState.Opening);
        Gate.IsOpen.Should().BeTrue();
        await Gate.CompleteOpenAsync();
        Gate.State.Should().Be(VaultState.Open);
    }

    // ---- the hold (5.10, 15.3.16) -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hold_is_granted_only_while_closed_and_refuses_every_open_until_it_is_released()
    {
        var hold = await Gate.HoldClosedAsync("pre-update");

        Gate.IsHeld.Should().BeTrue();
        Gate.State.Should().Be(VaultState.Closed);
        (await Gate.OpenAsync(_node.MasterDek)).Should().Be(VaultOpenResult.Refused(VaultOpenRefusals.Busy));
        await ((Func<Task>)(() => Gate.OpenTemporarilyAsync(_node.MasterDek))).Should().ThrowAsync<VaultBusyException>();
        await ((Func<Task>)(() => Gate.HoldClosedAsync("again"))).Should().ThrowAsync<InvalidOperationException>("one hold at a time");
        Gate.State.Should().Be(VaultState.Closed);
        _node.Relay.Calls.Should().BeEmpty("a held database is not touched");
        Gate.IsOpen.Should().BeFalse();

        await hold.DisposeAsync();

        Gate.IsHeld.Should().BeFalse();
        (await Gate.OpenAsync(_node.MasterDek)).Should().Be(VaultOpenResult.Done, "releasing the hold lets the next unlock work");
    }

    [Fact]
    public async Task A_hold_lease_released_by_an_exception_path_still_lifts_the_hold()
    {
        try
        {
            await using var hold = await Gate.HoldClosedAsync("copy");
            throw new IOException("the copy failed");
        }
        catch (IOException) { }

        Gate.IsHeld.Should().BeFalse();
        (await Gate.OpenAsync(_node.MasterDek)).Should().Be(VaultOpenResult.Done);
    }

    [Fact]
    public async Task A_hold_is_refused_while_the_database_is_open()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();

        var act = () => Gate.HoldClosedAsync("copy");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*snapshot*");
        Gate.IsHeld.Should().BeFalse();
    }

    [Fact]
    public async Task A_hold_waits_for_an_unlock_in_progress_to_end_and_is_granted_when_the_door_is_refused()
    {
        _node.Steps.Add(new RecordingStep(100, _node.StepLog, _ => Task.Delay(400)));
        var opening = Gate.OpenAsync(_node.MasterDek);
        await Task.Delay(100);
        Gate.State.Should().Be(VaultState.Opening);

        var holding = Gate.HoldClosedAsync("copy");
        await Task.Delay(100);
        holding.IsCompleted.Should().BeFalse("an unlock is in progress");

        await opening;
        await Gate.CloseAsync("refused");   // the door check refused
        var hold = await holding.WaitAsync(TimeSpan.FromSeconds(5));

        Gate.IsHeld.Should().BeTrue();
        await hold.DisposeAsync();
    }

    // ---- review round 1, H4: Closed only after the key-bearing generation is ended -----------------------------------------------------

    /// <summary>The vault factory behind a recorder: what the gate asked, and the gate's state when it asked.</summary>
    private sealed class RecordingConnections(ISealableConnections inner, Func<VaultState> state) : ISealableConnections
    {
        public List<string> Calls { get; } = [];
        public VaultState? StateAtGenerationEnd { get; private set; }
        public bool FailGenerationEnd { get; set; }

        /// <summary>Another flow holds the quiesce: <c>BeginQuiesce</c> throws as the factory does ("already being switched").</summary>
        public bool QuiesceCollides { get; set; }

        public IDisposable BeginQuiesce()
        {
            Calls.Add("quiesce");
            if (QuiesceCollides) throw new InvalidOperationException("This database is already being switched.");
            return inner.BeginQuiesce();
        }

        public Task WaitDrainedAsync(TimeSpan timeout, CancellationToken ct = default) => inner.WaitDrainedAsync(timeout, ct);

        public async Task<bool> CloseForSealAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            Calls.Add("generation-end");
            StateAtGenerationEnd = state();
            if (FailGenerationEnd) throw new IOException("the pool could not be flushed");
            return await inner.CloseForSealAsync(timeout, ct);
        }
    }

    private RecordingConnections RecordVaultConnections()
    {
        var recorder = new RecordingConnections(_node.Vault, () => Gate.State);
        _node.WrapVaultConnections = _ => recorder;
        return recorder;
    }

    [Fact]
    public async Task A_close_that_finds_the_file_being_switched_by_another_flow_still_ends_the_key_generation_before_it_says_Closed()
    {
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        using (var c = _node.Vault.CreateConnection()) c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_user");   // a key-bearing connection string is pooled
        var held = new object[] { _node, _node.DatabaseKey, _node.VaultPageKey, _node.ChatPageKey, _node.MasterDek };
        // Another flow (a restore, a file switch) takes the quiesce of the vault file just as the close begins to quiesce, and keeps it longer than the close waits.
        IDisposable? switching = null;
        _node.Relay.DuringClosing = async () => switching = await Task.Run(() => _node.Vault.BeginQuiesce());
        try
        {
            await Gate.CloseAsync("lock");

            Gate.State.Should().Be(VaultState.Closed);
            _node.GateLog.Lines.Should().Contain(l => l.Contains("being switched by another flow"));
            ((Action)(() => _node.Vault.CreateConnection())).Should().Throw<VaultSealedException>("no connection is handed out for the ended generation");
            var roots = new (string, object?)[] { ("gate", Gate), ("vault", _node.Vault), ("chat", _node.Chat), ("providerPool", KeyGraphWalker.ProviderRegistry()) };
            foreach (var (name, key) in new[] { ("database key", _node.DatabaseKey), ("vault page key", _node.VaultPageKey), ("chat page key", _node.ChatPageKey) })
                KeyGraphWalker.Find(roots, key, doNotEnter: held).Should().BeEmpty(name + " must not be reachable after the close, whoever held the quiesce");
        }
        finally
        {
            switching?.Dispose();
        }
    }

    [Fact]
    public async Task A_quiesce_that_collides_for_the_whole_wait_does_not_stop_the_generation_from_being_ended_before_Closed()
    {
        var recorder = RecordVaultConnections();
        recorder.QuiesceCollides = true;
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();

        await Gate.CloseAsync("lock");

        recorder.Calls.Should().Contain("generation-end", "the generation is ended although the barrier could not be taken");
        recorder.StateAtGenerationEnd.Should().Be(VaultState.Closing);
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task The_generation_of_every_file_is_ended_while_the_state_is_still_Closing_and_Closed_follows()
    {
        var recorder = RecordVaultConnections();
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();

        await Gate.CloseAsync("lock");

        recorder.Calls.Should().Equal("quiesce", "generation-end");
        recorder.StateAtGenerationEnd.Should().Be(VaultState.Closing, "Closed is published after the generation ended, never before");
        Gate.State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task A_close_that_could_not_end_a_generation_does_not_say_Closed_clears_the_keys_and_the_next_close_finishes_it()
    {
        var recorder = RecordVaultConnections();
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        recorder.FailGenerationEnd = true;

        var act = () => Gate.CloseAsync("lock");

        await act.Should().ThrowAsync<IOException>();
        Gate.State.Should().NotBe(VaultState.Closed, "Closed is published only after every generation was ended");
        Gate.IsUsable.Should().BeFalse();
        Gate.IsOpen.Should().BeFalse("the keys are cleared whatever happened");

        recorder.FailGenerationEnd = false;
        await Gate.CloseAsync("lock");

        Gate.State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task An_open_that_finds_a_close_unfinished_finishes_it_first_and_then_opens()
    {
        var recorder = RecordVaultConnections();
        await Gate.OpenAsync(_node.MasterDek);
        await Gate.CompleteOpenAsync();
        recorder.FailGenerationEnd = true;
        await ((Func<Task>)(() => Gate.CloseAsync("lock"))).Should().ThrowAsync<IOException>();
        recorder.FailGenerationEnd = false;

        var result = await Gate.OpenAsync(_node.MasterDek);

        result.Should().Be(VaultOpenResult.Done);
        recorder.Calls.Count(c => c == "generation-end").Should().Be(2, "the unfinished close ended its generation before the new open began");
        (await _node.CountUsersAsync()).Should().Be(1);
    }

    // ---- review round 1, H1: no open over a readable plain leftover ----------------------------------------------------------------------

    [Fact]
    public async Task An_open_is_refused_while_a_plain_leftover_is_on_the_disk_and_nothing_is_opened()
    {
        var leftovers = new List<string> { "beememorybank.db.seal-old" };
        Gate.PlainLeftovers = () => leftovers.ToList();

        var refused = await Gate.OpenAsync(_node.MasterDek);

        refused.Opened.Should().BeFalse();
        refused.Refusal.Should().Be(VaultOpenRefusals.RepairRequired);
        Gate.State.Should().Be(VaultState.Closed);
        Gate.IsOpen.Should().BeFalse("no key was unwrapped for a node that is not allowed to open");
        _node.Relay.Calls.Should().BeEmpty();
        var temporary = async () => await Gate.OpenTemporarilyAsync(_node.MasterDek);
        await temporary.Should().ThrowAsync<VaultSealedException>("a temporary open is an open");

        leftovers.Clear();   // the erase went through
        (await Gate.OpenAsync(_node.MasterDek)).Should().Be(VaultOpenResult.Done);
        Gate.IsOpen.Should().BeTrue("the key is unwrapped once nothing is left to repair");
    }
}

using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Endpoints;

public static class JoinEndpoints
{
    public static void MapJoinEndpoints(this WebApplication app)
    {
        // POST /api/join — a new node joins the network.
        // Validates the master password, adds the node to the whitelist,
        // returns a key slot for obtaining the Master DEK.
        // The master password is sent in the request body. This is a known limitation.
        // The bootstrap node is the user's own server, not a third party. The password is needed
        // to derive the KEK and transfer the master DEK. A SPAKE2/SRP zero-knowledge protocol
        // would eliminate this but is a significant engineering effort for a self-hosted system.
        app.MapPost("/api/join", async (
            JoinRequest req,
            IKeySlotRepository keySlotRepo,
            INodeIdentityRepository nodeRepo,
            IWhitelistRepository whitelistRepo,
            IUserRepository userRepo,
            IEventLogger eventLogger,
            SessionService session) =>
        {
            // 1. Verify that this node is initialized
            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.Json(new ErrorResponse("Node is not initialized"), statusCode: 500);

            // A node must never join itself.
            if (req.NodeId == identity.NodeId)
                return Results.BadRequest(new ErrorResponse("A node cannot join itself"));

            // A blind node never holds the DEK, and this endpoint exists to hand it over. Every event
            // such a node signed would be refused mesh-wide by the authorship ban anyway (plan 3.2),
            // so a row for it here could only ever be a misconfiguration. Blind nodes are paired
            // through their own pairing code instead.
            if (BlindNodeId.IsBlind(req.NodeId))
                return Results.BadRequest(new ErrorResponse(
                    "A blind node cannot join with the master password; pair it with its blind pairing code"));

            // 2. Validate password: try EVERY password-bearing slot, not just the first one found.
            // Fresh nodes have a "user" slot instead of legacy "password". Accept both
            // types so multi-node join works on nodes of either vintage.
            //
            // A node can carry MULTIPLE "user" slots — one per superadmin, each independently
            // wrapping the SAME master DEK with that user's own password-derived KEK (see
            // UserService's promote-to-superadmin path). The first slot belongs to exactly one
            // superadmin, so every other superadmin would get "invalid master password" trying
            // to join a new node with THEIR OWN correct password. Try every
            // candidate slot and accept the first one the supplied password actually unwraps —
            // mirrors the same try-every-candidate-slot pattern KeyManagementService.
            // ChangePasswordAsync already uses for the equivalent "which slot is this password for"
            // problem.
            var (passwordSlot, noPasswordSlot) = await FindPasswordSlotAsync(req.MasterPassword, keySlotRepo, userRepo);
            if (noPasswordSlot)
                return Results.Json(new ErrorResponse("No password-bearing key slot found on this node"), statusCode: 500);
            if (passwordSlot == null)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 401);

            // The password is valid, but recording this peer means signing a whitelist_add event,
            // and the signature needs this node's master DEK — which is only in memory while the
            // vault is unlocked. If it is locked the join fails deep inside LogWhitelistAddAsync
            // with a bare "Session is locked" 403 that says nothing about which node or what to do.
            // Catch it here, where we can name the host node and the action. Found on the test mesh:
            // a fresh standalone node accepts /api/init/standalone but is still locked, so the very
            // next node's join fails with an error that points at the wrong end of the connection.
            if (!session.IsUnlocked)
                return Results.Json(new ErrorResponse(
                    "The node being joined is locked and cannot record new members. Unlock it " +
                    "(sign in on its web UI, or POST /api/session/unlock) and retry the join."),
                    statusCode: 409);

            // 3. Validate the public key of the new node
            byte[] publicKey;
            try { publicKey = Convert.FromBase64String(req.Ed25519PublicKeyB64); }
            catch { return Results.BadRequest(new ErrorResponse("Invalid Ed25519PublicKeyB64 format")); }

            if (publicKey.Length != CryptoConstants.Ed25519PublicKeySize)
                return Results.BadRequest(new ErrorResponse("Ed25519 public key must be 32 bytes"));

            // Normalize the peer's advertised address on the way IN, the same way
            // WhitelistEndpoints does when an operator edits it by hand. Every consumer builds
            // request URLs by string concatenation ($"{apiAddress}/api/sync/..."), so an address
            // stored with a trailing slash yields "https://peer:5300//api/sync/identity" — a
            // double slash that ASP.NET routing answers with 404, silently wedging sync with that
            // peer. The value also travels to every other node in a whitelist_add event, so one
            // unnormalized join propagates the breakage across the mesh.
            var apiAddress = string.IsNullOrWhiteSpace(req.ApiAddress)
                ? req.ApiAddress
                : req.ApiAddress!.Trim().TrimEnd('/');

            // 4. Add the new node to the whitelist (or update if already exists)
            // One step with the abort of the same join (JoinAttemptGate): the row is looked at and written under one gate, so two joins of
            // one new id cannot both create it, and a join the joiner has already aborted writes nothing.
            await JoinAttemptGate.Gate.WaitAsync();
            try
            {
                if (JoinAttemptGate.IsCancelled(req.NodeId))
                    return Results.Json(
                        new ErrorResponse("This join was cancelled by the joining node; start it again"),
                        statusCode: 409);

                var existing = await whitelistRepo.GetByNodeIdAsync(req.NodeId, includeDeleted: true);
                if (existing != null && existing.Status == "R")
                    return Results.Json(new { error = "Node has been revoked" }, statusCode: 403);

                if (existing != null)
                {
                    // Node already in whitelist — benign re-join (same key) or impersonation attempt (different key).
                    // NEVER replace Ed25519 public key: it is bound to NodeId at first registration.
                    // Replacing it via join would let anyone holding the master password take over
                    // an existing NodeId with a new key.
                    if (!existing.Ed25519PublicKey.AsSpan().SequenceEqual(publicKey))
                        return Results.Json(
                            new ErrorResponse("Node with this NodeId is already registered with a different public key"),
                            statusCode: 403);

                    // A re-join with the same key can change the peer's display name or address, and
                    // that has to reach the mesh like any other whitelist change — the new-peer branch
                    // below logs too, and a re-join that wrote only the local row would leave every
                    // other node with stale reachability for this peer forever.
                    //
                    // The re-join proved the master password again, so it also raises the peer to
                    // superadmin (BMB-42) — the way a device recorded content-only before that change
                    // gets there, and the mesh hears it in the same event.
                    //
                    // No address in the request means "not supplied", not "clear it": phones and `bmb join`
                    // never send one. Writing the null here while peers ignore a null address in the event
                    // left this node alone without a way to reach the peer. The known address is kept and
                    // announced, so every node ends up with the same one.
                    var isSuperadmin = JoinAuthority.ForPasswordPeer(req.NodeId);
                    var address = apiAddress ?? existing.ApiAddress;
                    var version = await eventLogger.LogWhitelistUpdateAsync(req.NodeId, address, req.DisplayName, isSuperadmin);

                    existing.DisplayName = req.DisplayName;
                    existing.ApiAddress = address;
                    existing.IsSuperadmin = isSuperadmin;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.LamportTs = version.LamportTs;
                    existing.SourceNodeId = version.SourceNodeId;
                    await whitelistRepo.UpdateAsync(existing);
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var entry = new WhitelistEntry
                    {
                        NodeId = req.NodeId,
                        DisplayName = req.DisplayName,
                        Ed25519PublicKey = publicKey,
                        ApiAddress = apiAddress,
                        Status = "A",
                        CreatedAt = now,
                        UpdatedAt = now,
                        // Whoever knows the master password is a superadmin (owner's decision, BMB-42).
                        // The password has just handed this node the DEK, which outweighs anything a
                        // superadmin-only event can do. Recording it content-only used to let it hard
                        // delete or change the password locally while this node refused the event —
                        // a divergence nothing ever repaired. See JoinAuthority.
                        IsSuperadmin = JoinAuthority.ForPasswordPeer(req.NodeId)
                    };
                    // Log first so the row carries the version of the add the mesh is told about;
                    // otherwise this row starts at version 0 and any later event beats it, including
                    // one that predates the join.
                    var version = await eventLogger.LogWhitelistAddAsync(entry);
                    entry.LamportTs = version.LamportTs;
                    entry.SourceNodeId = version.SourceNodeId;
                    await whitelistRepo.CreateAsync(entry);
                }
            }
            finally
            {
                JoinAttemptGate.Gate.Release();
            }

            // 5. Return this node's identity + key slot + the full whitelist (for bootstrap of the new node)
            var allEntries = await whitelistRepo.GetAllActiveAsync();
            var whitelistDto = allEntries
                .Where(e => e.Status == "A")
                .Select(e =>
                {
                    // The key each peer is dialled by travels with it: without the pin the joiner would check that peer through
                    // the public CAs, which a self-signed peer never passes and a CA-valid impostor at its address does.
                    // A peer whose pin is damaged or lost has no usable key: sent without TLS fields it would read as an old-style
                    // peer and be trusted unpinned, and a marker would be ignored by joiners of 2.0.x to 2.5.0. So it is left out.
                    if (!BeeMemoryBank.Sync.JoinTls.TryInherit(e.TlsTrust, e.TlsSpki, out var tlsTrust, out var tlsSpki))
                        return null;
                    return new JoinWhitelistEntry(
                        e.NodeId,
                        e.DisplayName,
                        Convert.ToBase64String(e.Ed25519PublicKey),
                        e.ApiAddress,
                        e.IsSuperadmin,
                        tlsTrust,
                        tlsSpki);
                })
                .OfType<JoinWhitelistEntry>()
                .ToList();

            return Results.Ok(new JoinResponse(
                RemoteNode: new JoinRemoteIdentity(
                    identity.NodeId,
                    identity.DisplayName,
                    Convert.ToBase64String(identity.Ed25519PublicKey),
                    BeeMemoryBank.Sync.SyncProtocolVersion.Current),
                KeySlot: new JoinKeySlot(
                    Convert.ToBase64String(passwordSlot.EncryptedMasterDek),
                    Convert.ToBase64String(passwordSlot.IV),
                    Convert.ToBase64String(passwordSlot.Salt!),
                    passwordSlot.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    passwordSlot.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    passwordSlot.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism),
                Whitelist: whitelistDto));
        }).WithTags("Join");

        // POST /api/join/abort - a joiner whose join failed AFTER this node answered it (a timeout, a bad step on its own side, a snapshot
        // it could not import) takes its row back. Without it this node keeps an active, possibly superadmin, never-synced peer that nobody
        // holds the key of: it clutters the node list and holds back compaction until an admin finds it and revokes it.
        //
        // Same proof as the join: the master password, checked by the same code (FindPasswordSlotAsync), and the join code's token at the
        // LAN door of a desktop node. What it may remove is narrow on purpose: the row the join made and nothing else. This node id, with
        // the key the join used, active, written within JoinAttemptGate.AbortWindow, and never synced (no push position, the test
        // compaction uses, and no pull position). Anything else answers 409 and changes nothing; a row that is already gone answers 200, so asking twice is fine.
        // The removal is a revoke like the admin's (signed, logged first, so it beats the whitelist_add the mesh has already heard).
        app.MapPost("/api/join/abort", async (
            JoinAbortRequest req,
            IKeySlotRepository keySlotRepo,
            INodeIdentityRepository nodeRepo,
            IWhitelistRepository whitelistRepo,
            IUserRepository userRepo,
            ISyncPushPositionRepository pushPositionRepo,
            ISyncPositionRepository syncPositionRepo,
            IEventLogger eventLogger,
            IAuditLogRepository auditRepo,
            ILoggerFactory loggerFactory,
            SessionService session) =>
        {
            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.Json(new ErrorResponse("Node is not initialized"), statusCode: 500);

            // Cheap refusals first, before a password derivation is spent on a request that could not succeed.
            if (req.NodeId == Guid.Empty || req.NodeId == identity.NodeId)
                return Results.BadRequest(new ErrorResponse("A node cannot abort its own membership"));
            if (BlindNodeId.IsBlind(req.NodeId))
                return Results.BadRequest(new ErrorResponse("A blind node never joined with the master password"));
            byte[] publicKey;
            try { publicKey = Convert.FromBase64String(req.Ed25519PublicKeyB64 ?? ""); }
            catch { return Results.BadRequest(new ErrorResponse("Invalid Ed25519PublicKeyB64 format")); }
            if (publicKey.Length != CryptoConstants.Ed25519PublicKeySize)
                return Results.BadRequest(new ErrorResponse("Ed25519 public key must be 32 bytes"));

            var (passwordSlot, noPasswordSlot) = await FindPasswordSlotAsync(req.MasterPassword ?? "", keySlotRepo, userRepo);
            if (noPasswordSlot)
                return Results.Json(new ErrorResponse("No password-bearing key slot found on this node"), statusCode: 500);
            if (passwordSlot == null)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 401);

            await JoinAttemptGate.Gate.WaitAsync();
            try
            {
                var existing = await whitelistRepo.GetByNodeIdAsync(req.NodeId, includeDeleted: true);
                if (existing == null || existing.Status != "A" || existing.DeletedAt != null)
                {
                    // Nothing to take back (the join never wrote its row, or it was revoked since). A join of this id that is
                    // still on its way must not write one after this answer.
                    JoinAttemptGate.Cancel(req.NodeId);
                    return Results.Ok(new { aborted = true, removed = false });
                }

                if (!existing.Ed25519PublicKey.AsSpan().SequenceEqual(publicKey))
                    return Results.Json(
                        new ErrorResponse("The node registered under this NodeId has another key; it is not the join being aborted"),
                        statusCode: 409);
                if (DateTime.UtcNow - existing.CreatedAt > JoinAttemptGate.AbortWindow)
                    return Results.Json(
                        new ErrorResponse("This node was recorded too long ago to be the join being aborted; revoke it from the Admin page if it is not wanted"),
                        statusCode: 409);
                // Never synced in either direction: nothing was served to it (the push position compaction reads) and nothing was pulled from it.
                if (await pushPositionRepo.GetAsync(req.NodeId) != null || await syncPositionRepo.GetAsync(req.NodeId) != null)
                    return Results.Json(
                        new ErrorResponse("This node has already synced and is a member; revoke it from the Admin page if it is not wanted"),
                        statusCode: 409);

                // Signing the revoke needs the master DEK, which is only in memory while the vault is unlocked (as for the join).
                if (!session.IsUnlocked)
                    return Results.Json(new ErrorResponse(
                        "The node being joined is locked and cannot record the removal. Unlock it " +
                        "(sign in on its web UI, or POST /api/session/unlock) and retry."),
                        statusCode: 409);

                // Log first: the revoke's version is what a later whitelist_add is compared against (see DELETE /api/whitelist/{nodeId}).
                var version = await eventLogger.LogWhitelistRevokeAsync(req.NodeId);
                await whitelistRepo.RevokeAsync(req.NodeId, version);
                JoinAttemptGate.Cancel(req.NodeId);

                var shownName = new string(existing.DisplayName.Where(c => !char.IsControl(c)).Take(100).ToArray());
                await auditRepo.LogAsync("whitelist", req.NodeId.ToString(), "join_aborted", "peer",
                    $"The join of '{shownName}' failed on the joining node; its never-synced row was revoked at its request");
                loggerFactory.CreateLogger("BeeMemoryBank.Api.JoinEndpoints")
                    .LogInformation("Join of node {NodeId} aborted by the joiner; its never-synced row was revoked", req.NodeId);
                return Results.Ok(new { aborted = true, removed = true });
            }
            finally
            {
                JoinAttemptGate.Gate.Release();
            }
        }).WithTags("Join");
    }

    /// <summary>The outcome of checking a master password against this node's key slots.</summary>
    /// <param name="Slot">The slot the password opens (and that may be used to join), or null.</param>
    /// <param name="NoPasswordSlot">True when the node has no password-bearing slot at all.</param>
    private readonly record struct PasswordSlotResult(MasterKeyStore? Slot, bool NoPasswordSlot);

    /// <summary>
    /// The master-password proof of a join and of its abort, in one place: tries every password-bearing slot (see the comments
    /// inside), so both endpoints accept exactly the same callers. <see cref="KdfBusyException"/> is not swallowed.
    /// </summary>
    private static async Task<PasswordSlotResult> FindPasswordSlotAsync(
        string masterPassword, IKeySlotRepository keySlotRepo, IUserRepository userRepo)
    {
        var slots = await keySlotRepo.GetAllAsync();
        var candidateSlots = slots.Where(s => s.SlotType == "user" || s.SlotType == "password").ToList();
        if (candidateSlots.Count == 0)
            return new PasswordSlotResult(null, NoPasswordSlot: true);

        MasterKeyStore? passwordSlot = null;
        foreach (var candidate in candidateSlots)
        {
            try
            {
                var candidateKek = KeyDerivation.DeriveKek(
                    masterPassword,
                    candidate.Salt!,
                    candidate.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    candidate.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    candidate.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism);
                // Attempt to decrypt — if the password is wrong for THIS slot, an exception is
                // thrown and we move on to the next candidate rather than failing outright.
                MasterKeyManager.UnwrapMasterDek(candidate.EncryptedMasterDek, candidate.IV, candidateKek);

                // SECURITY: joining hands the caller mesh membership and, with it, the master
                // DEK — a strictly larger capability than unlocking this node. So it gets the
                // same rule /api/session/unlock does (SessionService.UnlockCoreAsync): a
                // "user" slot counts only if its owner is a superadmin. Checked here, after
                // the unwrap has cryptographically proven which slot this password belongs to,
                // rather than from anything the caller says about itself.
                //
                // This endpoint matters more than the unlock one: /api/join deliberately skips
                // the internal-key gate (a joining node has no key yet) and is one of the few
                // routes a reverse proxy is expected to forward, so it is reachable from
                // outside in a way /api/session/unlock is not.
                //
                // Legacy "password" slots are exempt for the same reason as in
                // UnlockCoreAsync: they predate the user table entirely and ARE the
                // superadmin-equivalent credential until the migration converts them.
                if (candidate.SlotType == "user" && !await IsSuperadminSlotAsync(userRepo, candidate.SlotId))
                    continue;

                passwordSlot = candidate;
                break;
            }
            catch (KdfBusyException) { throw; }
            catch { /* wrong password for this slot — try the next candidate */ }
        }

        return new PasswordSlotResult(passwordSlot, NoPasswordSlot: false);
    }

    /// <summary>
    /// True if <paramref name="slotId"/> belongs to an ACTIVE superadmin. Deactivated accounts
    /// resolve to false: fail closed, matching SessionService.UnlockCoreAsync, which looks the
    /// owner up the same way. An orphaned slot — no user row points at it — is likewise false.
    /// </summary>
    private static async Task<bool> IsSuperadminSlotAsync(IUserRepository userRepo, int slotId)
    {
        var owner = (await userRepo.ListActiveAsync()).FirstOrDefault(u => u.KeySlotId == slotId);
        return owner != null && owner.Role == UserRoles.Superadmin;
    }
}

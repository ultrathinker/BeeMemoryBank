using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Metadata;

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
            IDbConnectionFactory dbFactory,
            IEventLogger eventLogger,
            SessionService session,
            ISyncProtocolPolicy protocolPolicy,
            BeeMemoryBank.Sync.PeerAddressService addresses,
            HttpContext httpCtx,
            IAuditLogRepository auditRepo,
            ILoggerFactory loggerFactory) =>
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

            // After activation a joiner must be able to read what it is about to be sent: the join snapshot is sealed, and the
            // node's own events are protocol 4 (docs/design/step-a-spec.md 11). A 2.5 joiner sends neither the protocol nor the
            // capability and is refused here, before the password is derived and before any row is written.
            if (protocolPolicy.SealsMetadata && !protocolPolicy.IsCompatiblePeer(req.ProtocolVersion, req.Capabilities))
                return Results.Json(
                    new ErrorResponse("This network uses encrypted metadata: update this device to 2.6 or newer."),
                    statusCode: StatusCodes.Status426UpgradeRequired);

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
            var slotResult = await FindPasswordSlotAsync(req.MasterPassword, keySlotRepo, userRepo, req.Login);
            if (slotResult.NoPasswordSlot)
                return Results.Json(new ErrorResponse("No password-bearing key slot found on this node"), statusCode: 500);
            if (slotResult.Slot is not { } passwordSlot)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 401);
            var transportSlot = slotResult.Transport ?? passwordSlot;

            string joinOwnership;
            Guid? joinOwnerPersonId = null;
            string? joinOwnerLabel = null;
            var joinNeedsReview = false;
            if (OwnershipKinds.NormalizeOrNull(req.Ownership) == OwnershipKinds.Shared)
            {
                joinOwnership = OwnershipKinds.Shared;
            }
            else if (!slotResult.Ambiguous && slotResult.Opener is { } opener && opener.PersonId != Guid.Empty)
            {
                joinOwnership = OwnershipKinds.Personal;
                joinOwnerPersonId = opener.PersonId;
                joinOwnerLabel = string.IsNullOrWhiteSpace(opener.DisplayName) ? opener.Username : opener.DisplayName;
            }
            else
            {
                joinOwnership = OwnershipKinds.Unknown;
                joinNeedsReview = true;
            }

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
            // The one policy (PeerAddressPolicy) decides what is stored: its canonical form, or a refusal before anything is written - an address with a user name,
            // a query or a link-local target never reaches a row or the event that carries it to every other node.
            string? apiAddress = null;
            if (!string.IsNullOrWhiteSpace(req.ApiAddress))
            {
                var canonical = BeeMemoryBank.Sync.PeerAddressPolicy.Canonicalize(req.ApiAddress);
                if (!canonical.Ok) return Results.BadRequest(new ErrorResponse(canonical.Refusal!));
                apiAddress = canonical.Address;
            }

            // 4. Add the new node to the whitelist (or update if already exists)
            // One step with the abort of the same join (JoinAttemptGate): the row is looked at and written under one gate, so two joins of
            // one new id cannot both create it, and a join the joiner has already aborted writes nothing.
            var rejoined = false;
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
                    // The one writer of a peer's row on a running node: the address is policed and canonical, the key on record is the key named (the check above, again),
                    // the event and the row carry the same address and version, a repeat that changes nothing writes nothing, and the loop forgets the old place.
                    var isSuperadmin = JoinAuthority.ForPasswordPeer(req.NodeId);
                    var applied = await addresses.ApplyProofAsync(existing,
                        new BeeMemoryBank.Sync.PeerProof(apiAddress, ProvenKey: publicKey, DisplayName: req.DisplayName, IsSuperadmin: isSuperadmin, Announce: true,
                            Ownership: OwnershipKinds.IsKnown(joinOwnership) ? joinOwnership : null,
                            OwnerPersonId: joinOwnerPersonId,
                            OwnerLabel: joinOwnerLabel),
                        "join", "join");
                    if (!applied.Succeeded)
                        return applied.Outcome == BeeMemoryBank.Sync.PeerAddressOutcome.WrongKey
                            ? Results.Json(new ErrorResponse("Node with this NodeId is already registered with a different public key"), statusCode: 403)
                            : Results.BadRequest(new ErrorResponse(applied.Message));
                    if (joinNeedsReview && !OwnershipKinds.IsKnown(applied.Entry?.Ownership) && applied.Entry is { NeedsReview: false } needsReviewRow)
                    {
                        needsReviewRow.NeedsReview = true;
                        await whitelistRepo.UpdateAsync(needsReviewRow);
                    }
                    rejoined = true;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var entry = new WhitelistEntry
                    {
                        NodeId = req.NodeId,
                        DisplayName = req.DisplayName,
                        Ed25519PublicKey = publicKey,
                        Status = "A",
                        CreatedAt = now,
                        UpdatedAt = now,
                        // Whoever knows the master password is a superadmin (BMB-42).
                        // The password has just handed this node the DEK, which outweighs anything a
                        // superadmin-only event can do. Recording it content-only used to let it hard
                        // delete or change the password locally while this node refused the event —
                        // a divergence nothing ever repaired. See JoinAuthority.
                        IsSuperadmin = JoinAuthority.ForPasswordPeer(req.NodeId),
                        Ownership = joinOwnership,
                        OwnerPersonId = joinOwnerPersonId,
                        OwnerLabel = joinOwnerLabel,
                        NeedsReview = joinNeedsReview
                    };
                    // The event is logged first, inside the one writer, so the row carries the version of the add the mesh is told about;
                    // otherwise this row starts at version 0 and any later event beats it, including one that predates the join.
                    var admitted = await addresses.AdmitAsync(entry, apiAddress, "join", "join");
                    if (!admitted.Succeeded) return Results.BadRequest(new ErrorResponse(admitted.Message));
                }
            }
            finally
            {
                JoinAttemptGate.Gate.Release();
            }

            // 4a. On record: a node was given the key to the vault. Which node (id and the name it gave), and whose password opened the door when that is known:
            // the slot's owner, which is a person; a legacy slot from before the user table has none. Never the password, the slot's bytes or the key.
            await auditRepo.LogAsync("whitelist", req.NodeId.ToString(), "node_joined", "peer",
                $"Node '{Printable(req.DisplayName)}' ({req.NodeId}) {(rejoined ? "re-joined" : "joined")}; the password opened " +
                await DescribeSlotOwnerAsync(userRepo, passwordSlot));

            if (joinOwnership == OwnershipKinds.Shared && httpCtx.RequestServices.GetService<ISuperadminDoorsNotifier>() is { } doorsNotifier)
                await doorsNotifier.NotifyChangedAsync(immediate: false);

            // 4b. What the joiner said about its LAN endpoints and TLS anchors (optional members, absent from older joiners): the row that only "calls us"
            // can be called from the first minute (docs/design/lan-peer-sync.md 8).
            var lanLogger = loggerFactory.CreateLogger("BeeMemoryBank.Api.JoinEndpoints");
            await Services.Lan.LanJoinExchange.NoteOtherSideAsync(httpCtx.RequestServices, req.NodeId, req.Endpoints, req.TlsLeafSpki, req.TlsCaKey, req.LanPort, lanLogger);
            var dataPath = Services.Lan.LanJoinExchange.DataPath(httpCtx.RequestServices);
            var offer = string.IsNullOrEmpty(dataPath) ? null : Services.Lan.LanJoinExchange.OwnOffer(dataPath, httpCtx.Connection.RemoteIpAddress);

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
                    var (wOwnership, wPersonId, wLabel) = OwnershipKinds.Resolve(e.Ownership, e.OwnerPersonId, e.OwnerLabel);
                    return new JoinWhitelistEntry(
                        e.NodeId,
                        e.DisplayName,
                        Convert.ToBase64String(e.Ed25519PublicKey),
                        e.ApiAddress,
                        e.IsSuperadmin,
                        tlsTrust,
                        tlsSpki,
                        e.LamportTs,
                        e.SourceNodeId,
                        BeeMemoryBank.Sync.JoinPeerVersions.From(e),
                        wOwnership,
                        wPersonId,
                        wLabel);
                })
                .OfType<JoinWhitelistEntry>()
                .ToList();

            // The revoked rows, tombstones included, with the version of each revoke: without them the joiner knows nothing of the revocations, and
            // the first old whitelist_add for one of those peers that reaches it from another node's log makes it an active peer there.
            var revokedPeers = (await whitelistRepo.GetAllRevokedAsync())
                .Where(e => e.NodeId != identity.NodeId && e.NodeId != req.NodeId)
                .Select(BeeMemoryBank.Sync.JoinRevokedPeer.From)
                .ToList();

            var selfOwnership = await BeeMemoryBank.Sync.WhitelistHistory.ResolveSelfOwnershipAsync(dbFactory, identity.NodeId);

            return Results.Ok(new JoinResponse(
                RemoteNode: new JoinRemoteIdentity(
                    identity.NodeId,
                    identity.DisplayName,
                    Convert.ToBase64String(identity.Ed25519PublicKey),
                    protocolPolicy.Declared,
                    selfOwnership?.Ownership,
                    selfOwnership?.OwnerPersonId,
                    selfOwnership?.OwnerLabel,
                    selfOwnership?.Version),
                KeySlot: new JoinKeySlot(
                    Convert.ToBase64String(transportSlot!.EncryptedMasterDek),
                    Convert.ToBase64String(transportSlot.IV),
                    Convert.ToBase64String(passwordSlot.Salt!),
                    passwordSlot.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    passwordSlot.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    passwordSlot.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism),
                Whitelist: whitelistDto,
                Endpoints: offer?.Endpoints,
                TlsLeafSpki: offer?.TlsLeafSpki,
                TlsCaKey: offer?.TlsCaKey,
                LanPort: offer?.LanPort,
                RevokedPeers: revokedPeers,
                JoinedAsDisplayName: slotResult.Opener?.DisplayName));
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
            SessionService session,
            HttpContext ctx) =>
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

            var slotResult = await FindPasswordSlotAsync(req.MasterPassword ?? "", keySlotRepo, userRepo);
            if (slotResult.NoPasswordSlot)
                return Results.Json(new ErrorResponse("No password-bearing key slot found on this node"), statusCode: 500);
            if (slotResult.Slot == null)
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
                        new ErrorResponse("This node was recorded too long ago to be the join being aborted; revoke it on its page under Admin > Devices if it is not wanted"),
                        statusCode: 409);
                // Never synced in either direction: nothing was served to it (the push position compaction reads) and nothing was pulled from it.
                if (await pushPositionRepo.GetAsync(req.NodeId) != null || await syncPositionRepo.GetAsync(req.NodeId) != null)
                    return Results.Json(
                        new ErrorResponse("This node has already synced and is a member; revoke it on its page under Admin > Devices if it is not wanted"),
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
                await WhitelistEndpoints.CleanupRevokedPeerAsync(ctx.RequestServices, req.NodeId);
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

    /// <summary>A name a peer chose, as one printable line of at most 100 characters: it goes into an audit entry, so no control character and no length of its own.</summary>
    private static string Printable(string? text) => new((text ?? "").Where(c => !char.IsControl(c)).Take(100).ToArray());

    /// <summary>Whose slot a password opened, in words for the audit log: the owner's user name and id, or that the slot is a legacy one with no owner.</summary>
    private static async Task<string> DescribeSlotOwnerAsync(IUserRepository userRepo, MasterKeyStore slot)
    {
        var owner = (await userRepo.ListActiveAsync()).FirstOrDefault(u => u.KeySlotId == slot.SlotId);
        return owner is null
            ? $"slot #{slot.SlotId} ({slot.SlotType}), which has no user"
            : $"the slot of user '{Printable(owner.Username)}' (#{owner.Id})";
    }

    /// <summary>The outcome of checking a master password against this node's key slots.</summary>
    /// <param name="Slot">The slot the password opens (and that may be used to join), or null.</param>
    /// <param name="NoPasswordSlot">True when the node has no password-bearing slot at all.</param>
    /// <param name="Transport">The key slot as the join answer carries it: the stored v1 wrap, or for a sealed (v2) door a v1 wrap of the same DEK under the
    /// same password-derived KEK, salt and parameters (ADR 0012) - the wire format of every earlier release, so a joiner of any version opens it.</param>
    /// <param name="Opener">The unique superadmin user whose slot opened, or null when ambiguous or legacy.</param>
    /// <param name="Ambiguous">True when multiple slots opened or an un-migrated legacy <c>password</c> slot opened.</param>
    private readonly record struct PasswordSlotResult(MasterKeyStore? Slot, bool NoPasswordSlot, MasterKeyStore? Transport = null, User? Opener = null, bool Ambiguous = false);

    /// <summary>
    /// The master-password proof of a join and of its abort, in one place: with <paramref name="login"/> tries that user's
    /// slot first and attributes cleanly; without <paramref name="login"/> tries every candidate slot instead of stopping at
    /// the first, returning whether the opener is unique or ambiguous. <see cref="KdfBusyException"/> is not swallowed.
    /// </summary>
    private static async Task<PasswordSlotResult> FindPasswordSlotAsync(
        string masterPassword, IKeySlotRepository keySlotRepo, IUserRepository userRepo, string? login = null)
    {
        var slots = await keySlotRepo.GetAllAsync();
        var candidateSlots = slots.Where(s => s.SlotType == "user" || s.SlotType == "password").ToList();
        if (candidateSlots.Count == 0)
            return new PasswordSlotResult(null, NoPasswordSlot: true);

        var activeUsers = await userRepo.ListActiveAsync();
        var usersBySlot = activeUsers.Where(u => u.KeySlotId.HasValue).ToDictionary(u => u.KeySlotId!.Value);

        (User? Owner, MasterKeyStore? Transport) TryOpenCandidate(MasterKeyStore candidate, out bool validOpener)
        {
            validOpener = false;
            try
            {
                var candidateKek = KeyDerivation.DeriveKek(
                    masterPassword,
                    candidate.Salt!,
                    candidate.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    candidate.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    candidate.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism);
                byte[]? opened = null;
                try
                {
                    opened = Doors.OpenSlot(candidate, candidateKek);

                    // SECURITY: joining hands the caller mesh membership and, with it, the master DEK - a strictly larger capability than
                    // unlocking this node. So only a superadmin's "user" slot counts (legacy "password" slots are exempt until migrated), and
                    // never the door of a person being removed (2.12): sign-in and the recovery reset refuse them too.
                    User? owner = null;
                    if (candidate.SlotType == "user"
                        && (!usersBySlot.TryGetValue(candidate.SlotId, out owner) || owner.Role != UserRoles.Superadmin || owner.IsBeingRemoved))
                        return (null, null);

                    var transport = candidate;
                    if (candidate.DoorVersion == Doors.V2)
                    {
                        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(opened, candidateKek);
                        transport = new MasterKeyStore
                        {
                            SlotId = candidate.SlotId, SlotType = candidate.SlotType, EncryptedMasterDek = wrapped, IV = iv, Salt = candidate.Salt,
                            ArgonMemory = candidate.ArgonMemory, ArgonIterations = candidate.ArgonIterations, ArgonParallelism = candidate.ArgonParallelism,
                        };
                    }
                    validOpener = true;
                    return (owner, transport);
                }
                finally
                {
                    if (opened is not null) Array.Clear(opened);
                    Array.Clear(candidateKek);
                }
            }
            catch (KdfBusyException) { throw; }
            catch { return (null, null); }
        }

        int? triedPreferredSlotId = null;
        if (!string.IsNullOrWhiteSpace(login))
        {
            var namedUser = activeUsers.FirstOrDefault(u => string.Equals(u.Username, login.Trim(), StringComparison.OrdinalIgnoreCase));
            if (namedUser is { Role: UserRoles.Superadmin, IsBeingRemoved: false, KeySlotId: { } preferredSlotId })
            {
                var preferred = candidateSlots.FirstOrDefault(s => s.SlotId == preferredSlotId);
                if (preferred is not null)
                {
                    triedPreferredSlotId = preferred.SlotId;
                    var (opener, transport) = TryOpenCandidate(preferred, out var valid);
                    if (valid && opener is not null)
                        return new PasswordSlotResult(preferred, NoPasswordSlot: false, transport, opener, Ambiguous: false);
                }
            }
        }

        var openedCandidates = new List<(MasterKeyStore Slot, MasterKeyStore? Transport, User? Owner)>();
        foreach (var candidate in candidateSlots)
        {
            if (triedPreferredSlotId == candidate.SlotId) continue;
            var (owner, transport) = TryOpenCandidate(candidate, out var valid);
            if (valid) openedCandidates.Add((candidate, transport, owner));
        }

        if (openedCandidates.Count == 0)
            return new PasswordSlotResult(null, NoPasswordSlot: false);

        if (openedCandidates.Count > 1 || openedCandidates[0].Slot.SlotType == "password" || openedCandidates[0].Owner is null)
            return new PasswordSlotResult(openedCandidates[0].Slot, NoPasswordSlot: false, openedCandidates[0].Transport, Opener: null, Ambiguous: true);

        return new PasswordSlotResult(openedCandidates[0].Slot, NoPasswordSlot: false, openedCandidates[0].Transport, openedCandidates[0].Owner, Ambiguous: false);
    }
}

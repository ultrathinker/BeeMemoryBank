using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Api.Endpoints;

public static class InitEndpoints
{
    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapInitEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/init").WithTags("Init").RequireInternalKey();

        // Intentionally anonymous: the Web startup middleware polls this before the node is
        // initialized (and before any user exists) to decide whether to redirect to /Setup.
        // Marked SkipInternalKey so the group filter does not gate it.
        group.MapGet("/status", async (InitializationService initSvc) =>
        {
            var initialized = await initSvc.IsInitializedAsync();
            return Results.Ok(new { initialized });
        }).WithMetadata(new SkipInternalKey());

        // POST /api/init/standalone — first-time node initialization (new network).
        // AUDIT NOTE: This endpoint is only callable from the Web UI server (localhost or X-Internal-Key).
        // It is NOT exposed to external clients. This prevents unauthorized initialization of the node
        // by external actors, while still allowing the setup flow before auth is configured.
        group.MapPost("/standalone", async (
            InitStandaloneRequest req,
            HttpContext ctx,
            InitializationService initSvc) =>
        {
            await _initLock.WaitAsync();
            try
            {
                if (await initSvc.IsInitializedAsync())
                    return Results.Conflict(new ErrorResponse("Node is already initialized."));

                if (string.IsNullOrWhiteSpace(req.AdminUsername))
                    return Results.BadRequest(new ErrorResponse("Admin username is required."));

                if (string.IsNullOrWhiteSpace(req.DisplayName))
                    return Results.BadRequest(new ErrorResponse("Display name is required."));

                if (string.IsNullOrWhiteSpace(req.Password))
                    return Results.BadRequest(new ErrorResponse("Password is required."));

                // Use the same complexity rules as ChangePassword/CreateUser. Without
                // this, the very first admin could be set up with a weaker password
                // than the system would later allow them to change to.
                try { Core.Services.UserService.ValidatePassword(req.Password); }
                catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }

                await initSvc.InitializeAsync(req.AdminUsername, req.DisplayName, req.Password);
                return Results.Ok(new { success = true });
            }
            finally
            {
                _initLock.Release();
            }
        });

        // POST /api/init/join — first-time node initialization (join existing network).
        // AUDIT NOTE: This endpoint is only callable from the Web UI server (localhost or X-Internal-Key).
        // The master password is sent in the request body to derive the KEK and transfer the master DEK.
        // This is the same known limitation as POST /api/join — see JoinEndpoints.cs for rationale.
        group.MapPost("/join", async (
            InitJoinRequest req,
            HttpContext ctx,
            InitializationService initSvc,
            INodeIdentityRepository nodeRepo,
            IKeySlotRepository keySlotRepo,
            IUserRepository userRepo,
            IWhitelistRepository whitelistRepo,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            Services.SnapshotService snapshotService,
            ISyncPositionRepository syncPositionRepo,
            ILamportClock lamportClock,
            IDbConnectionFactory dbConnFactory) =>
        {
            var logger = loggerFactory.CreateLogger("BeeMemoryBank.Api.InitEndpoints");

            await _initLock.WaitAsync();
            try
            {
                if (await initSvc.IsInitializedAsync())
                    return Results.Conflict(new ErrorResponse("Node is already initialized."));

                if (string.IsNullOrWhiteSpace(req.AdminUsername))
                    return Results.BadRequest(new ErrorResponse("Admin username is required."));

                if (string.IsNullOrWhiteSpace(req.DisplayName))
                    return Results.BadRequest(new ErrorResponse("Display name is required."));

                if (string.IsNullOrWhiteSpace(req.RemoteUrl))
                    return Results.BadRequest(new ErrorResponse("Remote URL is required."));

                if (string.IsNullOrWhiteSpace(req.Password))
                    return Results.BadRequest(new ErrorResponse("Password is required."));

                // Same complexity rules as the standalone-init path and as
                // ChangePassword — otherwise a node could join a network with
                // a weaker local KEK than the rest of the cluster requires.
                try { Core.Services.UserService.ValidatePassword(req.Password); }
                catch (ArgumentException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }

                if (!Uri.TryCreate(req.RemoteUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    return Results.BadRequest(new ErrorResponse("Remote URL must be a valid HTTP(S) URL."));
                }

                var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
                var nodeId = Guid.NewGuid();

                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);

                var joinRequest = new
                {
                    masterPassword = req.Password,
                    nodeId,
                    displayName = req.DisplayName,
                    ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
                    apiAddress = (string?)null
                };

                HttpResponseMessage response;
                try
                {
                    response = await http.PostAsJsonAsync(
                        $"{req.RemoteUrl.TrimEnd('/')}/api/join", joinRequest, JsonOptions);
                }
                catch (Exception ex)
                {
                    return Results.Json(
                        new ErrorResponse($"Cannot reach remote node: {ex.Message}"),
                        statusCode: 502);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    logger.LogWarning("Remote node rejected join request (HTTP {Status}): {Body}",
                        (int)response.StatusCode, errorBody);
                    return Results.Json(
                        new ErrorResponse($"Remote node rejected the join request (HTTP {(int)response.StatusCode})"),
                        statusCode: 502);
                }

                var joinResponse = await response.Content.ReadFromJsonAsync<JoinResponseDto>(JsonOptions);
                if (joinResponse == null)
                    return Results.Json(new ErrorResponse("Empty response from remote node"), statusCode: 502);

                if (joinResponse.RemoteNode.ProtocolVersion > BeeMemoryBank.Sync.SyncProtocolVersion.Current)
                {
                    return Results.Json(
                        new ErrorResponse($"Cannot join: remote node protocol version ({joinResponse.RemoteNode.ProtocolVersion}) is higher than local version ({BeeMemoryBank.Sync.SyncProtocolVersion.Current})"),
                        statusCode: 400);
                }

                var slot = joinResponse.KeySlot;
                var encryptedMasterDek = Convert.FromBase64String(slot.EncryptedMasterDekB64);
                var remoteIv = Convert.FromBase64String(slot.IvB64);
                var remoteSalt = Convert.FromBase64String(slot.SaltB64);

                byte[] masterDek;
                try
                {
                    var remoteKek = KeyDerivation.DeriveKek(req.Password, remoteSalt,
                        slot.ArgonMemory, slot.ArgonIterations, slot.ArgonParallelism);
                    masterDek = MasterKeyManager.UnwrapMasterDek(encryptedMasterDek, remoteIv, remoteKek);
                }
                catch
                {
                    return Results.Json(
                        new ErrorResponse("Could not decrypt Master DEK — wrong password?"),
                        statusCode: 400);
                }

                var localSalt = KeyDerivation.GenerateSalt();
                var localKek = KeyDerivation.DeriveKek(req.Password, localSalt);
                var (localEncryptedDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

                var now = DateTime.UtcNow;

                // Encrypt the Ed25519 seed with master DEK before persisting (v=1).
                // Note: the raw privateKey is kept on the stack until the challenge-response
                // handshake below is done; cleared at the end of the unlock try/finally.
                var (wrappedPk, pkIv) = NodeIdentityCrypto.EncryptPrivateKey(privateKey, masterDek, nodeId);

                var identity = new NodeIdentity
                {
                    NodeId = nodeId,
                    DisplayName = req.DisplayName,
                    Ed25519PublicKey = publicKey,
                    Ed25519PrivateKey = wrappedPk,
                    Ed25519PrivateKeyIV = pkIv,
                    Ed25519PrivateKeyV = 1,
                    InitialSyncCompleted = false,
                    CreatedAt = now
                };
                await nodeRepo.CreateAsync(identity);

                var localSlot = new MasterKeyStore
                {
                    SlotType = "user",
                    EncryptedMasterDek = localEncryptedDek,
                    IV = localIv,
                    Salt = localSalt,
                    ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations,
                    ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                    CreatedAt = now
                };
                var localSlotId = await keySlotRepo.CreateAsync(localSlot);

                var user = new User
                {
                    Username = req.AdminUsername.Trim(),
                    DisplayName = req.AdminUsername.Trim(),
                    PasswordHash = UserService.HashPassword(req.Password),
                    Role = UserRoles.Superadmin,
                    KeySlotId = localSlotId,
                    IsActive = true,
                    CreatedAt = now
                };
                await userRepo.CreateAsync(user);

                var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
                await nodeRepo.StoreSentinelAsync(sentinel);

                using (var conn = dbConnFactory.CreateConnection())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
                        VALUES (@k, '1', @ts)";
                    var p1 = cmd.CreateParameter();
                    p1.ParameterName = "k";
                    p1.Value = "legacy_password_unified";
                    cmd.Parameters.Add(p1);
                    var p2 = cmd.CreateParameter();
                    p2.ParameterName = "ts";
                    p2.Value = DateTime.UtcNow.ToString("O");
                    cmd.Parameters.Add(p2);
                    cmd.ExecuteNonQuery();
                }

                Array.Clear(masterDek);

                foreach (var entry in joinResponse.Whitelist ?? [])
                {
                    if (entry.NodeId == nodeId) continue;
                    if (entry.NodeId == joinResponse.RemoteNode.NodeId) continue;

                    try
                    {
                        var existing = await whitelistRepo.GetByNodeIdAsync(entry.NodeId);
                        if (existing != null) continue;

                        await whitelistRepo.CreateAsync(new WhitelistEntry
                        {
                            NodeId = entry.NodeId,
                            DisplayName = entry.DisplayName,
                            Ed25519PublicKey = Convert.FromBase64String(entry.Ed25519PublicKeyB64),
                            ApiAddress = entry.ApiAddress,
                            Status = "A",
                            CreatedAt = now,
                            UpdatedAt = now,
                            // Propagate IsSuperadmin from the bootstrap node's whitelist so this
                            // new node knows which transitively-discovered peers are Superadmins.
                            // Without this, every other Superadmin in the cluster would be demoted
                            // to plain peer locally → their whitelist_*/hard_delete/restore_network
                            // events would be rejected once a 3rd node joins.
                            IsSuperadmin = entry.IsSuperadmin
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to import whitelist entry for node {NodeId}", entry.NodeId);
                    }
                }

                var remote = joinResponse.RemoteNode;
                await whitelistRepo.CreateAsync(new WhitelistEntry
                {
                    NodeId = remote.NodeId,
                    DisplayName = remote.DisplayName,
                    Ed25519PublicKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64),
                    ApiAddress = req.RemoteUrl.TrimEnd('/'),
                    Status = "A",
                    CreatedAt = now,
                    UpdatedAt = now,
                    // Trust-on-join: the node we joined is implicitly Superadmin (we trusted them
                    // with our master password, they verified it). Mirrors JoinEndpoints.cs.
                    IsSuperadmin = true
                });

                try
                {
                    var challengeResp = await http.PostAsync(
                        $"{req.RemoteUrl.TrimEnd('/')}/api/sync/challenge", null);
                    challengeResp.EnsureSuccessStatusCode();
                    var challenge = await challengeResp.Content.ReadFromJsonAsync<ChallengeResponseDto>(JsonOptions)
                        ?? throw new InvalidOperationException("No challenge from remote");

                    var challengeBytes = Convert.FromBase64String(challenge.Challenge);
                    // V2: bound to the audience node's id. /api/sync/authenticate verifies against
                    // its own recorded identity and no longer accepts the unbound V1 tag, so a V1
                    // signature here would simply 401. First contact, so the anchor is the id the
                    // remote just declared — trust-on-first-use, same as the operator typing this
                    // URL and master password. See SnapshotJoinClient for the same reasoning.
                    var domainTag = "BMB-CHALLENGE-V2\0"u8.ToArray();
                    var challengePayload = domainTag
                        .Concat(challenge.ServerNodeId.ToByteArray())
                        .Concat(challengeBytes)
                        .ToArray();
                    var challengeSig = Ed25519Signer.Sign(privateKey, challengePayload);
                    Array.Clear(privateKey);

                    var authResp = await http.PostAsJsonAsync(
                        $"{req.RemoteUrl.TrimEnd('/')}/api/sync/authenticate",
                        new
                        {
                            NodeId = nodeId,
                            ChallengeB64 = challenge.Challenge,
                            SignatureB64 = Convert.ToBase64String(challengeSig)
                        }, JsonOptions);
                    authResp.EnsureSuccessStatusCode();
                    var authToken = (await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOptions))?.Token
                        ?? throw new InvalidOperationException("No token from remote");

                    using var snapReq = new HttpRequestMessage(HttpMethod.Get,
                        $"{req.RemoteUrl.TrimEnd('/')}/api/sync/snapshot/for-join");
                    snapReq.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

                    var snapResp = await http.SendAsync(snapReq);
                    snapResp.EnsureSuccessStatusCode();

                    var signatureB64 = snapResp.Headers.GetValues("X-BMB-Snapshot-Signature").FirstOrDefault()
                        ?? throw new InvalidOperationException("Missing signature header");
                    var sigBytes = Convert.FromBase64String(signatureB64);

                    var cpSeqHeader = snapResp.Headers.GetValues("X-BMB-Snapshot-CP-Seq").FirstOrDefault();
                    logger.LogInformation("Downloading snapshot for join (CP={Cp})", cpSeqHeader);

                    var tempTarGz = Path.Combine(Path.GetTempPath(), $"bmb-join-{Guid.NewGuid():N}.tar.gz");
                    try
                    {
                        await using (var fs = File.Create(tempTarGz))
                            await snapResp.Content.CopyToAsync(fs);

                        var producerPubKey = Convert.FromBase64String(remote.Ed25519PublicKeyB64);
                        var (cpSeq, lamportTs) = await snapshotService.RestoreForJoinAsync(
                            tempTarGz, sigBytes, producerPubKey);

                        await syncPositionRepo.UpsertAsync(new SyncPosition
                        {
                            RemoteNodeId = remote.NodeId,
                            LastSequenceNum = cpSeq,
                            UpdatedAt = DateTime.UtcNow
                        });

                        const long MAX_CLOCK_ADVANCE = 1_000_000;
                        var cappedLamport = Math.Min(lamportTs, lamportClock.Current + MAX_CLOCK_ADVANCE);
                        if (lamportTs > cappedLamport)
                            logger.LogWarning(
                                "Producer lamport_ts {Producer} exceeds local+MAX_CLOCK_ADVANCE, capping at {Capped}.",
                                lamportTs, cappedLamport);
                        lamportClock.Update(cappedLamport);

                        await nodeRepo.MarkInitialSyncCompletedAsync();

                        logger.LogInformation("Snapshot join complete. CP={Cp}, Lamport={Lamport}",
                            cpSeq, cappedLamport);
                    }
                    finally
                    {
                        if (File.Exists(tempTarGz)) File.Delete(tempTarGz);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Snapshot join failed after key setup. Node is in partial state.");
                    return Results.Json(
                        new ErrorResponse(
                            $"Key exchange succeeded but snapshot import failed: {ex.Message}. Node may need wipe & retry."),
                        statusCode: 500);
                }

                return Results.Ok(new { success = true });
            }
            finally
            {
                _initLock.Release();
            }
        });

        group.MapPost("/reset", async (
            ResetRequest req,
            HttpContext ctx,
            SessionService session,
            INodeIdentityRepository nodeRepo,
            DbConnectionFactory connFactory,
            MaintenanceModeService maintenance,
            Services.ChatDbConnectionFactory chatDbConnFactory,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("BeeMemoryBank.Api.InitEndpoints.Reset");

            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.BadRequest(new ErrorResponse("Node is not initialized — nothing to reset"));

            var unlockOk = await session.UnlockAsync(req.MasterPassword);
            if (!unlockOk)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

            var dataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH") ?? "/app/data";

            // AUDIT: this is the single most destructive operation in the product, and the wipe
            // below deletes tbl_audit_log along with everything else — a record that lived only
            // inside beememorybank.db could never survive the event it describes. Write a durable
            // trail BEFORE touching anything: an append-only file in the data directory (which the
            // wipe never touches — it's outside the SQLite file entirely) plus a Warning-level
            // structured log line so it also reaches whatever log sink/aggregator this deployment
            // has configured (journald, `docker logs`, a file sink, ...). Best-effort — a failure to
            // record the trail must never block the reset itself, or the audit mechanism becomes a
            // new way to lock an admin out of resetting a compromised node.
            var resetAt = DateTime.UtcNow;
            var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            try
            {
                var auditLine = $"{resetAt:O} node_reset old_node_id={identity.NodeId} " +
                    $"old_display_name=\"{identity.DisplayName}\" old_node_created_at={identity.CreatedAt:O} " +
                    $"remote_ip={remoteIp}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(dataPath, "reset-audit.log"), auditLine);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write reset-audit.log (continuing with the reset)");
            }
            logger.LogWarning(
                "NODE RESET initiated: old_node_id={NodeId} old_display_name={DisplayName} remote_ip={RemoteIp}",
                identity.NodeId, identity.DisplayName, remoteIp);

            maintenance.Enter("Resetting node...");
            session.Lock();

            try
            {
                using var conn = connFactory.CreateConnection();
                using (var pragmaOff = conn.CreateCommand())
                {
                    pragmaOff.CommandText = "PRAGMA foreign_keys = OFF";
                    pragmaOff.ExecuteNonQuery();
                }
                using (var tx = conn.BeginTransaction())
                {
                    // Enumerate every real content table from the LIVE schema instead of
                    // hand-maintaining a list here. A hand list silently rots: it can name a table
                    // that never existed (this used to list "tbl_agent_access", wrapped in an empty
                    // catch — invisible) and, worse, it can OMIT a table a later migration added —
                    // which is exactly how tbl_remote_api_token was left out, handing a pre-reset
                    // bmbrt_ remote token a live read path into the NEW vault once /Setup reassigns
                    // its user_id. Excluded on purpose:
                    //   - sqlite_%      — SQLite's own internal bookkeeping tables.
                    //   - fts_%         — FTS5 index tables and their shadow tables (fts_article,
                    //                     fts_article_data, ...). AFTER INSERT/UPDATE/DELETE triggers
                    //                     on tbl_article/tbl_folder/tbl_concept_tag (see
                    //                     005_fts5_metadata_index.sql) keep these in sync, so
                    //                     clearing the content tables below empties them too as a
                    //                     side effect — writing to an FTS5 shadow table directly is
                    //                     unsupported and unnecessary.
                    //   - tbl_migration — migration-applied bookkeeping. Wiping it would make
                    //                     MigrationRunner try to re-run every migration against a
                    //                     schema that (structurally) still exists — harmless in
                    //                     principle (CREATE TABLE "already exists" is treated as
                    //                     idempotent) but pointless and only slows down recovery.
                    var tablesToWipe = new List<string>();
                    using (var listCmd = conn.CreateCommand())
                    {
                        listCmd.Transaction = tx;
                        listCmd.CommandText = @"
                            SELECT name FROM sqlite_master
                            WHERE type = 'table'
                              AND name NOT LIKE 'sqlite_%'
                              AND name NOT LIKE 'fts_%'
                              AND name <> 'tbl_migration'
                            ORDER BY name";
                        using var reader = listCmd.ExecuteReader();
                        while (reader.Read())
                            tablesToWipe.Add(reader.GetString(0));
                    }

                    foreach (var table in tablesToWipe)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        // tbl_role is the one deliberate exception: DELETE FROM would take the two
                        // seeded system roles (superadmin/user) with it, and nothing re-seeds them
                        // (migrations only run once — 009_custom_roles.sql's INSERT OR IGNORE is a
                        // no-op on a second run). Every other table is cleared unconditionally.
                        delCmd.CommandText = table == "tbl_role"
                            ? "DELETE FROM tbl_role WHERE is_system = 0"
                            : $"DELETE FROM [{table}]";
                        try
                        {
                            delCmd.ExecuteNonQuery();
                        }
                        catch (Exception ex)
                        {
                            // Visible now instead of an empty catch — a failure here means the reset
                            // did NOT fully clear that table, which is exactly what an admin relying
                            // on "go to /Setup to rejoin" being a truly clean slate needs to know.
                            logger.LogWarning(ex, "Reset: failed to clear table {Table}", table);
                        }
                    }
                    tx.Commit();
                }
                using (var pragmaOn = conn.CreateCommand())
                {
                    pragmaOn.CommandText = "PRAGMA foreign_keys = ON";
                    pragmaOn.ExecuteNonQuery();
                }

                var mediaDir = Path.Combine(dataPath, "media");
                if (Directory.Exists(mediaDir))
                    foreach (var f in Directory.GetFiles(mediaDir, "*.enc")) File.Delete(f);

                using (var vacuumCmd = conn.CreateCommand())
                {
                    vacuumCmd.CommandText = "VACUUM";
                    vacuumCmd.ExecuteNonQuery();
                }

                // chat.db sits in the same data directory and holds this node's AI-chat history —
                // conversation transcripts and tool-result JSON that can include decrypted article
                // bodies the AI read during a turn (see McpResponseManager / ChatEndpoints). None of
                // that belongs to the NEW vault created after this reset, so clear it too. Best-effort
                // in its own try/catch: a chat.db failure must never abort or appear to partially
                // undo the vault wipe above, which has already committed. chat_model/chat_settings
                // are left alone — they're node-local operational config (the configured model
                // catalogue, the global chat toggle), not vault content.
                try
                {
                    using var chatConn = chatDbConnFactory.CreateConnection();
                    using var chatTx = chatConn.BeginTransaction();
                    foreach (var chatTable in new[] { "chat_attachment", "chat_message", "chat_conversation", "chat_api_key" })
                    {
                        using var chatDel = chatConn.CreateCommand();
                        chatDel.Transaction = chatTx;
                        chatDel.CommandText = $"DELETE FROM [{chatTable}]";
                        chatDel.ExecuteNonQuery();
                    }
                    chatTx.Commit();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Reset: failed to clear chat.db conversation history (non-fatal)");
                }

                // The folder-ACL cache is process-wide and keyed by (database, user id). The
                // database path does not change across a reset but the user ids do — they restart
                // at 1 — so a node re-initialized inside the cache TTL would hand the new account
                // the wiped account's permissions.
                FolderAccessService.InvalidateAll();

                maintenance.Exit();
                return Results.Ok(new { success = true, message = "Node reset — go to /Setup to rejoin" });
            }
            catch (Exception ex)
            {
                maintenance.Exit();
                return Results.Json(new ErrorResponse($"Reset failed: {ex.Message}"), statusCode: 500);
            }
        });
    }

    private sealed record JoinResponseDto(
        JoinRemoteNodeDto RemoteNode,
        JoinKeySlotDto KeySlot,
        List<JoinWhitelistEntryDto>? Whitelist);

    private sealed record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64, int ProtocolVersion);

    private sealed record JoinWhitelistEntryDto(
        Guid NodeId,
        string DisplayName,
        string Ed25519PublicKeyB64,
        string? ApiAddress,
        bool IsSuperadmin = false);

    private sealed record JoinKeySlotDto(
        string EncryptedMasterDekB64,
        string IvB64,
        string SaltB64,
        int ArgonMemory,
        int ArgonIterations,
        int ArgonParallelism);

    private sealed record ChallengeResponseDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}

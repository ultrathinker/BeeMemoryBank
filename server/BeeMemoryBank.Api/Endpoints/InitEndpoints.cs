using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
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
        var group = app.MapGroup("/api/init").WithTags("Init");

        group.MapGet("/status", async (InitializationService initSvc) =>
        {
            var initialized = await initSvc.IsInitializedAsync();
            return Results.Ok(new { initialized });
        });

        // POST /api/init/standalone — first-time node initialization (new network).
        // AUDIT NOTE: This endpoint is only callable from the Web UI server (localhost or X-Internal-Key).
        // It is NOT exposed to external clients. This prevents unauthorized initialization of the node
        // by external actors, while still allowing the setup flow before auth is configured.
        group.MapPost("/standalone", async (
            InitStandaloneRequest req,
            HttpContext ctx,
            InitializationService initSvc) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

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

            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

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
                    var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
                    var challengePayload = domainTag.Concat(challengeBytes).ToArray();
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
            MaintenanceModeService maintenance) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.BadRequest(new ErrorResponse("Node is not initialized — nothing to reset"));

            var unlockOk = await session.UnlockAsync(req.MasterPassword);
            if (!unlockOk)
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 403);

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
                    var tablesToWipe = new[]
                    {
                        "tbl_article_concept_tag", "tbl_concept_tag_edge", "tbl_concept_tag",
                        "tbl_article_body", "tbl_conflict_version", "tbl_tombstone",
                        "tbl_article", "tbl_media", "tbl_folder",
                        "tbl_node_identity", "tbl_key_slot", "tbl_whitelist",
                        "tbl_agent", "tbl_agent_access",
                        "tbl_sync_position", "tbl_sync_push_position",
                        "tbl_event", "tbl_compaction_log",
                        "tbl_audit_log", "tbl_hard_delete_audit",
                        "tbl_projection_matrix",
                        "tbl_user", "tbl_folder_acl_entry"
                    };
                    foreach (var table in tablesToWipe)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = $"DELETE FROM [{table}]";
                        try { delCmd.ExecuteNonQuery(); } catch { }
                    }
                    tx.Commit();
                }
                using (var pragmaOn = conn.CreateCommand())
                {
                    pragmaOn.CommandText = "PRAGMA foreign_keys = ON";
                    pragmaOn.ExecuteNonQuery();
                }

                var mediaDir = Path.Combine(
                    Environment.GetEnvironmentVariable("BMB_DATA_PATH") ?? "/app/data",
                    "media");
                if (Directory.Exists(mediaDir))
                    foreach (var f in Directory.GetFiles(mediaDir, "*.enc")) File.Delete(f);

                using (var vacuumCmd = conn.CreateCommand())
                {
                    vacuumCmd.CommandText = "VACUUM";
                    vacuumCmd.ExecuteNonQuery();
                }

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

    private sealed record JoinRemoteNodeDto(Guid NodeId, string DisplayName, string Ed25519PublicKeyB64);

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

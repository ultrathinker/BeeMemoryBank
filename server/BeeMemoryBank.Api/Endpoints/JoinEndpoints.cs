using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Endpoints;

public static class JoinEndpoints
{
    public static void MapJoinEndpoints(this WebApplication app)
    {
        // POST /api/join — a new node joins the network.
        // Validates the master password, adds the node to the whitelist,
        // returns a key slot for obtaining the Master DEK.
        // AUDIT NOTE: The master password is sent in the request body. This is a known limitation.
        // The bootstrap node is the user's own server, not a third party. The password is needed
        // to derive the KEK and transfer the master DEK. A SPAKE2/SRP zero-knowledge protocol
        // would eliminate this but is a significant engineering effort for a self-hosted system.
        app.MapPost("/api/join", async (
            JoinRequest req,
            IKeySlotRepository keySlotRepo,
            INodeIdentityRepository nodeRepo,
            IWhitelistRepository whitelistRepo,
            IEventLogger eventLogger) =>
        {
            // 1. Verify that this node is initialized
            var identity = await nodeRepo.GetAsync();
            if (identity == null)
                return Results.Json(new ErrorResponse("Node is not initialized"), statusCode: 500);

            // A node must never join itself.
            if (req.NodeId == identity.NodeId)
                return Results.BadRequest(new ErrorResponse("A node cannot join itself"));

            // 2. Validate password: take the first password-bearing slot, try to decrypt Master DEK.
            // After A2, fresh nodes have a "user" slot instead of legacy "password". Accept both
            // types so multi-node join works on post-A2 nodes. Found by E2E test on 2026-04-26.
            var slots = await keySlotRepo.GetAllAsync();
            var passwordSlot = slots.FirstOrDefault(s => s.SlotType == "user")
                              ?? slots.FirstOrDefault(s => s.SlotType == "password");
            if (passwordSlot == null)
                return Results.Json(new ErrorResponse("No password-bearing key slot found on this node"), statusCode: 500);

            try
            {
                var kek = KeyDerivation.DeriveKek(
                    req.MasterPassword,
                    passwordSlot.Salt!,
                    passwordSlot.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    passwordSlot.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    passwordSlot.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism);
                // Attempt to decrypt — if password is wrong, an exception will be thrown
                MasterKeyManager.UnwrapMasterDek(passwordSlot.EncryptedMasterDek, passwordSlot.IV, kek);
            }
            catch
            {
                return Results.Json(new ErrorResponse("Invalid master password"), statusCode: 401);
            }

            // 3. Validate the public key of the new node
            byte[] publicKey;
            try { publicKey = Convert.FromBase64String(req.Ed25519PublicKeyB64); }
            catch { return Results.BadRequest(new ErrorResponse("Invalid Ed25519PublicKeyB64 format")); }

            if (publicKey.Length != CryptoConstants.Ed25519PublicKeySize)
                return Results.BadRequest(new ErrorResponse("Ed25519 public key must be 32 bytes"));

            // 4. Add the new node to the whitelist (or update if already exists)
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

                existing.DisplayName = req.DisplayName;
                existing.ApiAddress = req.ApiAddress;
                existing.UpdatedAt = DateTime.UtcNow;
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
                    ApiAddress = req.ApiAddress,
                    Status = "A",
                    CreatedAt = now,
                    UpdatedAt = now,
                    // Trust-on-join: a peer that successfully joined via /api/join (provided the
                    // master password — proves they belong to the team vault) is implicitly trusted
                    // as a Superadmin in the team-vault model. Admin can later demote via UI if a
                    // peer should be limited to read/write only. Without this, the gate I added in
                    // EventApplier (gemini #1/#2/#3) would block ALL legitimate cross-node sync of
                    // whitelist/hard-delete/restore events between joined peers.
                    IsSuperadmin = true
                };
                await whitelistRepo.CreateAsync(entry);
                await eventLogger.LogWhitelistAddAsync(entry);
            }

            // 5. Return this node's identity + key slot + the full whitelist (for bootstrap of the new node)
            var allEntries = await whitelistRepo.GetAllActiveAsync();
            var whitelistDto = allEntries
                .Where(e => e.Status == "A")
                .Select(e => new JoinWhitelistEntry(
                    e.NodeId,
                    e.DisplayName,
                    Convert.ToBase64String(e.Ed25519PublicKey),
                    e.ApiAddress,
                    e.IsSuperadmin))
                .ToList();

            return Results.Ok(new JoinResponse(
                RemoteNode: new JoinRemoteIdentity(
                    identity.NodeId,
                    identity.DisplayName,
                    Convert.ToBase64String(identity.Ed25519PublicKey)),
                KeySlot: new JoinKeySlot(
                    Convert.ToBase64String(passwordSlot.EncryptedMasterDek),
                    Convert.ToBase64String(passwordSlot.IV),
                    Convert.ToBase64String(passwordSlot.Salt!),
                    passwordSlot.ArgonMemory ?? CryptoConstants.DefaultArgonMemory,
                    passwordSlot.ArgonIterations ?? CryptoConstants.DefaultArgonIterations,
                    passwordSlot.ArgonParallelism ?? CryptoConstants.DefaultArgonParallelism),
                Whitelist: whitelistDto));
        }).WithTags("Join");
    }
}
